using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using gated.Models;
using gated.Python;

namespace gated.Services;

public sealed record SpectralFitOutcome(string Warning, int Rank, int PositiveEventCount, int UnstainedEventCount);

public sealed class SpectralUnmixingService
{
    public SpectralFitOutcome Fit(FlowGroup group)
    {
        string cytometer = Configuration.CytometerNameForSample(group.Samples.FirstOrDefault());
        string? detector_error = Configuration.ValidateSpectralDetectors(cytometer);
        if (detector_error is not null) throw new InvalidOperationException(detector_error);
        var detector_preferences = Configuration.SpectralDetectors(cytometer);
        string[] detectors = detector_preferences.Select(item => item.ChannelName).ToArray();
        validate_channel_profiles(group, detectors);

        var configured = group.SpectralUnmixing.Rows
            .Select(row => (Row: row, Sample: group.ControlSamples.FirstOrDefault(sample => sample.Id == row.ControlSampleId)))
            .Where(item => item.Sample is not null).Select(item => (item.Row, Sample: item.Sample!)).ToArray();
        foreach (var item in configured) normalize_role(item.Row);
        var backgrounds = configured.Where(item => item.Row.Role == SpectralControlRole.UnstainedAf).ToArray();
        if (backgrounds.Length != 1) throw new InvalidOperationException("Exactly one Unstained/Blank/AF control is required.");
        var stained = configured.Where(item => item.Row.Role == SpectralControlRole.Molecule).ToArray();
        if (stained.Length == 0) throw new InvalidOperationException("At least one stained spectral control is required.");
        if (stained.Any(item => string.IsNullOrWhiteSpace(item.Row.MoleculeName)) ||
            stained.GroupBy(item => item.Row.MoleculeName.Trim(), StringComparer.OrdinalIgnoreCase).Any(grouping => grouping.Count() > 1))
            throw new InvalidOperationException("Stained molecule names must be non-empty and unique.");

        var background = backgrounds[0];
        int[] background_indices = gated_indices(background.Sample, gate_for(group.SpectralUnmixing, background.Row));
        if (background_indices.Length < 3) throw new InvalidOperationException("The Unstained/AF gate contains fewer than three events.");
        int[] sampled_background = deterministic_sample(background_indices, 3000);
        float[,] background_matrix = select_spectral(background.Sample, sampled_background, detectors);
        var positive_matrices = new List<float[,]>();
        var peak_indices = new List<int>();
        int positive_count = 0;

        foreach (var item in stained)
        {
            int[] indices = gated_indices(item.Sample, gate_for(group.SpectralUnmixing, item.Row));
            if (indices.Length < 3) throw new InvalidOperationException($"The gate for {item.Row.MoleculeName} contains fewer than three events.");
            string peak = resolve_peak(item.Row, item.Sample, indices, background.Sample, background_indices, detectors);
            item.Row.PeakChannel = peak;
            int detector_index = Array.IndexOf(detectors, peak);
            var background_values = background.Sample.GetChannelValues(peak, background_indices).Where(float.IsFinite).Select(value => (double)value).OrderBy(value => value).ToArray();
            if (background_values.Length == 0) throw new InvalidOperationException($"The Unstained control has no finite {peak} values.");
            double threshold = quantile(background_values, 0.99);
            var candidates = indices.Select(index => (Index: index, Value: (double)item.Sample.RawEvents[index, item.Sample.GetChannelIndex(peak)]))
                .Where(pair => double.IsFinite(pair.Value) && pair.Value > threshold).OrderByDescending(pair => pair.Value).ToArray();
            if (candidates.Length < 3) throw new InvalidOperationException($"Fewer than three positive events were found for {item.Row.MoleculeName}.");
            if (item.Row.PositiveSelection is null)
            {
                var selected_for_default = candidates.Take(3000).ToArray();
                item.Row.PositiveSelection = new SpilloverRangeSelection(selected_for_default.Min(pair => pair.Value), selected_for_default.Max(pair => pair.Value));
            }
            var range = item.Row.PositiveSelection!;
            int[] positives = candidates.Where(pair => pair.Value >= Math.Min(range.Minimum, range.Maximum) && pair.Value <= Math.Max(range.Minimum, range.Maximum))
                .Take(3000).Select(pair => pair.Index).ToArray();
            if (positives.Length < 3) throw new InvalidOperationException($"The positive range for {item.Row.MoleculeName} contains fewer than three events.");
            positive_count += positives.Length;
            positive_matrices.Add(select_spectral(item.Sample, positives, detectors));
            peak_indices.Add(detector_index);
        }

        var fit = PythonExtensionRuntime.FitSpectralUnmixing(positive_matrices, background_matrix, peak_indices);
        string[] names = stained.Select(item => item.Row.MoleculeName.Trim()).Append("AF").ToArray();
        group.SpectralUnmixing.SetFit(detectors, names, fit.Signatures, fit.Similarity, fit.Coefficients);
        return new SpectralFitOutcome(fit.Warning, fit.Rank, positive_count, sampled_background.Length);
    }

    public FlowGroup Apply(FlowWorkspace workspace, FlowGroup source)
    {
        var state = source.SpectralUnmixing;
        if (state.IsStale || state.Coefficients.Length == 0) throw new InvalidOperationException("Fit the spectral model before applying it.");
        string[] molecule_names = state.SignatureNames.Where(name => !string.Equals(name, "AF", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (molecule_names.Length == 0) throw new InvalidOperationException("The spectral model has no molecule outputs.");
        var target = EnsureOutputGroup(workspace, source);
        if (target.SpectralSourceGroupId is { } owner && owner != source.Id)
            throw new InvalidOperationException("The selected output group belongs to another spectral model.");
        if (target.Samples.Count > 0 && target.SpectralSourceGroupId != source.Id)
            throw new InvalidOperationException("The selected output group must be empty.");

        string cytometer = Configuration.CytometerNameForSample(source.Samples.FirstOrDefault());
        var spectral_names = Configuration.SpectralDetectors(cytometer).Select(item => item.ChannelName).ToHashSet(StringComparer.Ordinal);
        var source_channels = source.Channels;
        var retained = source_channels.Where(channel => !spectral_names.Contains(channel.Name)).ToArray();
        string[] expected = retained.Select(channel => channel.Name).Concat(molecule_names).ToArray();
        if (target.Samples.Count > 0 && !target.Channels.Select(channel => channel.Name).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("The fitted output schema changed; link a new empty output group before applying.");

        var generated = new List<FlowSample>();
        foreach (var sample in source.Samples)
        {
            int[] detector_indices = state.DetectorNames.Select(sample.GetChannelIndex).ToArray();
            if (detector_indices.Any(index => index < 0)) throw new InvalidOperationException($"{sample.Name} is missing configured spectral detectors.");
            var channels = new List<ChannelDefinition>();
            foreach (var channel in retained) channels.Add(new ChannelDefinition(channels.Count, channel.Name, channel.Label, channel.Maximum, channel.Gain));
            foreach (string molecule in molecule_names) channels.Add(new ChannelDefinition(channels.Count, molecule, "", maximum_for_output(sample, detector_indices), 1));
            var values = new float[sample.EventCount, channels.Count];
            for (int row = 0; row < sample.EventCount; row++)
            {
                for (int column = 0; column < retained.Length; column++) values[row, column] = sample.RawEvents[row, sample.GetChannelIndex(retained[column].Name)];
                for (int molecule = 0; molecule < molecule_names.Length; molecule++)
                {
                    double value = 0;
                    for (int detector = 0; detector < detector_indices.Length; detector++) value += sample.RawEvents[row, detector_indices[detector]] * state.Coefficients[molecule, detector];
                    values[row, retained.Length + molecule] = (float)value;
                }
            }
            if (!state.GeneratedSampleIds.TryGetValue(sample.Id, out Guid generated_id)) state.GeneratedSampleIds[sample.Id] = generated_id = Guid.NewGuid();
            var derived = new FlowSample(sample.Name, channels, values) { Id = generated_id };
            foreach (var pair in sample.Metadata) derived.Metadata[pair.Key] = pair.Value;
            derived.Metadata["SpectralSourceSampleId"] = sample.Id.ToString();
            generated.Add(derived);
        }

        target.Samples.Clear();
        foreach (var sample in generated) target.AddSample(sample, recalculate: false);
        target.SpectralSourceGroupId = source.Id;
        target.ResetIdentityCompensation();
        target.RecalculateSamples();
        return target;
    }

    public FlowGroup EnsureOutputGroup(FlowWorkspace workspace, FlowGroup source)
    {
        var state = source.SpectralUnmixing;
        var target = state.LinkedOutputGroupId is { } linked ? workspace.Groups.FirstOrDefault(group => group.Id == linked) : null;
        if (target is not null) return target;
        target = new FlowGroup { Name = unique_group_name(workspace, $"{source.Name} unmixed"), SpectralSourceGroupId = source.Id };
        workspace.Groups.Add(target); state.LinkedOutputGroupId = target.Id;
        return target;
    }

    private static void normalize_role(SpectralControlRow row)
    {
        if (row.MoleculeName.Trim() is { } name && (name.Equals("unstained", StringComparison.OrdinalIgnoreCase) 
            || name.Equals("blank", StringComparison.OrdinalIgnoreCase) || name.Equals("af", StringComparison.OrdinalIgnoreCase)))
            row.Role = SpectralControlRole.UnstainedAf;
    }

    private static ControlGatePreset gate_for(SpectralUnmixingState state, SpectralControlRow row) =>
        state.GatePresets.FirstOrDefault(preset => preset.Id == row.GatePresetId) ?? state.DefaultGatePreset;

    private static int[] gated_indices(ControlSample sample, ControlGatePreset preset)
    {
        if (preset.Vertices.Count < 3) return Enumerable.Range(0, sample.EventCount).ToArray();
        var x = sample.GetChannelValues(preset.XChannel); var y = sample.GetChannelValues(preset.YChannel); var selected = new List<int>();
        for (int index = 0; index < Math.Min(x.Length, y.Length); index++) if (contains(preset.Vertices, x[index], y[index])) selected.Add(index);
        return selected.ToArray();
    }

    private static bool contains(IReadOnlyList<Point> vertices, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
            if (((vertices[i].Y > y) != (vertices[j].Y > y)) && x < (vertices[j].X - vertices[i].X) * (y - vertices[i].Y) / (vertices[j].Y - vertices[i].Y) + vertices[i].X) inside = !inside;
        return inside;
    }

    private static string resolve_peak(SpectralControlRow row, ControlSample stained, int[] stained_indices, ControlSample background, int[] background_indices, string[] detectors)
    {
        if (!row.UseAutomaticPeak && detectors.Contains(row.PeakChannel, StringComparer.Ordinal)) return row.PeakChannel;
        return detectors.OrderByDescending(detector => median(stained.GetChannelValues(detector, stained_indices)) - median(background.GetChannelValues(detector, background_indices))).First();
    }

    private static double median(IEnumerable<float> source)
    {
        var values = source.Where(float.IsFinite).Select(value => (double)value).OrderBy(value => value).ToArray();
        if (values.Length == 0) return double.NegativeInfinity;
        int middle = values.Length / 2; return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    private static double quantile(double[] sorted, double q)
    {
        double position = Math.Clamp(q, 0, 1) * (sorted.Length - 1); int lower = (int)Math.Floor(position); int upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static int[] deterministic_sample(int[] source, int maximum)
    {
        if (source.Length <= maximum) return source;
        var result = new int[maximum];
        for (int index = 0; index < maximum; index++) result[index] = source[(int)((long)index * source.Length / maximum)];
        return result;
    }

    private static float[,] select_spectral(ControlSample sample, int[] rows, string[] detectors)
    {
        var result = new float[rows.Length, detectors.Length];
        int[] columns = detectors.Select(sample.GetChannelIndex).ToArray();
        for (int row = 0; row < rows.Length; row++) for (int column = 0; column < columns.Length; column++) result[row, column] = sample.RawEvents[rows[row], columns[column]];
        return result;
    }

    private static void validate_channel_profiles(FlowGroup group, string[] detectors)
    {
        foreach (var sample in group.Samples) if (detectors.Any(detector => sample.GetChannelIndex(detector) < 0)) throw new InvalidOperationException($"{sample.Name} does not contain every configured spectral detector.");
        foreach (var sample in group.ControlSamples) if (detectors.Any(detector => sample.GetChannelIndex(detector) < 0)) throw new InvalidOperationException($"{sample.Name} does not contain every configured spectral detector.");
    }

    private static string unique_group_name(FlowWorkspace workspace, string preferred)
    {
        if (workspace.Groups.All(group => !string.Equals(group.Name, preferred, StringComparison.OrdinalIgnoreCase))) return preferred;
        int index = 2; while (workspace.Groups.Any(group => string.Equals(group.Name, $"{preferred} {index}", StringComparison.OrdinalIgnoreCase))) index++;
        return $"{preferred} {index}";
    }

    private static float maximum_for_output(FlowSample sample, int[] detectors) => 
        detectors.Select(index => sample.Channels[index].Maximum).DefaultIfEmpty(262144).Max();
}
