using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using gated.Models;
using gated.Python;

namespace gated.Services;

public sealed record SpectralFitOutcome(string Warning, int Rank, int PositiveEventCount, int UnstainedEventCount);
public sealed record SpectralApplyProgress(double Fraction, string Detail);
public sealed record SpectralApplyPreparation(
    IReadOnlyList<FlowSample> Samples,
    IReadOnlyDictionary<string, AxisSettings> DataImpliedViewOptions,
    IReadOnlyDictionary<Guid, Guid> GeneratedSampleIds);

public sealed class SpectralUnmixingService
{
    public SpectralFitOutcome Fit(FlowGroup group)
    {
        var configured = group.SpectralUnmixing.Rows
            .Select(row => (Row: row, Sample: group.ControlSamples.FirstOrDefault(sample => sample.Id == row.ControlSampleId)))
            .Where(item => item.Sample is not null).Select(item => (item.Row, Sample: item.Sample!)).ToArray();
        string[] detectors = group.SpectralUnmixing.DetectorNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (detectors.Length == 0)
            throw new InvalidOperationException("This spectral model has no detector snapshot. Add a spectral control before fitting.");
        validate_control_profiles(configured.Select(item => item.Sample), detectors);

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
        var derived_signatures = new List<float[]>();
        int positive_count = 0;

        foreach (var item in stained)
        {
            int[] indices = gated_indices(item.Sample, gate_for(group.SpectralUnmixing, item.Row));
            if (indices.Length < 3) throw new InvalidOperationException($"The gate for {item.Row.MoleculeName} contains fewer than three events.");
            string peak = !item.Row.UseAutomaticPeak && detectors.Contains(item.Row.PeakChannel, StringComparer.Ordinal)
                ? item.Row.PeakChannel
                : SpectracleSignatureDerivation.ResolvePeak(
                    item.Sample,
                    deterministic_sample(indices, 5000),
                    background.Sample,
                    sampled_background,
                    detectors);
            if (item.Row.UseAutomaticPeak)
                item.Row.CachedPeakChannel = peak;
            else
                item.Row.PeakChannel = peak;
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
            derived_signatures.Add(
                SpectracleSignatureDerivation.DeriveFluorophoreSignature(
                    item.Sample,
                    positives,
                    background.Sample,
                    sampled_background,
                    detectors).Signature);
        }

        derived_signatures.Add(
            SpectracleSignatureDerivation.DeriveAfSignature(
                background.Sample,
                sampled_background,
                detectors));
        var signature_matrix = new float[derived_signatures.Count, detectors.Length];
        for (int signature = 0; signature < derived_signatures.Count; signature++)
            for (int detector = 0; detector < detectors.Length; detector++)
                signature_matrix[signature, detector] = derived_signatures[signature][detector];

        var fit = PythonExtensionRuntime.FitSpectralUnmixing(signature_matrix);
        string[] names = stained.Select(item => item.Row.MoleculeName.Trim()).Append("AF").ToArray();
        group.SpectralUnmixing.SetFit(detectors, names, fit.Signatures, fit.Similarity, fit.Coefficients);
        return new SpectralFitOutcome(fit.Warning, fit.Rank, positive_count, sampled_background.Length);
    }

    public SpectralApplyPreparation PrepareApply(
        FlowGroup source,
        FlowGroup target,
        IProgress<SpectralApplyProgress>? progress = null,
        CancellationToken cancellation_token = default)
    {
        var state = source.SpectralUnmixing;
        if (state.IsStale || state.Coefficients.Length == 0) throw new InvalidOperationException("Fit the spectral model before applying it.");
        string[] detector_names = state.DetectorNames.ToArray();
        string[] molecule_names = state.SignatureNames.Where(name => !string.Equals(name, "AF", StringComparison.OrdinalIgnoreCase)).ToArray();
        float[,] coefficients = (float[,])state.Coefficients.Clone();
        var generated_sample_ids = new Dictionary<Guid, Guid>(state.GeneratedSampleIds);
        if (molecule_names.Length == 0) throw new InvalidOperationException("The spectral model has no molecule outputs.");
        if (target.SpectralSourceGroupId is { } owner && owner != source.Id)
            throw new InvalidOperationException("The selected output group belongs to another spectral model.");
        if (target.Samples.Count > 0 && target.SpectralSourceGroupId != source.Id)
            throw new InvalidOperationException("The selected output group must be empty.");

        var spectral_names = detector_names.ToHashSet(StringComparer.Ordinal);
        var source_channels = source.Channels;
        var retained = source_channels.Where(channel => !spectral_names.Contains(channel.Name)).ToArray();
        string[] expected = retained.Select(channel => channel.Name).Concat(molecule_names).ToArray();
        if (target.Samples.Count > 0 && !target.Channels.Select(channel => channel.Name).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("The fitted output schema changed; link a new empty output group before applying.");

        var source_samples = source.Samples.ToArray();
        long total_events = Math.Max(1, source_samples.Sum(sample => (long)sample.EventCount));
        long completed_events = 0;
        var generated = new List<FlowSample>(source_samples.Length);
        for (int sample_index = 0; sample_index < source_samples.Length; sample_index++)
        {
            cancellation_token.ThrowIfCancellationRequested();
            var sample = source_samples[sample_index];
            progress?.Report(new SpectralApplyProgress(
                0.85 * completed_events / total_events,
                $"Unmixing {sample.Name} ({sample_index + 1}/{source_samples.Length})"));
            int[] detector_indices = detector_names.Select(sample.GetChannelIndex).ToArray();
            if (detector_indices.Any(index => index < 0)) throw new InvalidOperationException($"{sample.Name} is missing configured spectral detectors.");
            var channels = new List<ChannelDefinition>();
            foreach (var channel in retained) channels.Add(new ChannelDefinition(channels.Count, channel.Name, channel.Label, channel.Maximum, channel.Gain));
            foreach (string molecule in molecule_names) channels.Add(new ChannelDefinition(channels.Count, molecule, "", maximum_for_output(sample, detector_indices), 1));
            var values = new float[sample.EventCount, channels.Count];
            for (int row = 0; row < sample.EventCount; row++)
            {
                if ((row & 4095) == 0)
                {
                    cancellation_token.ThrowIfCancellationRequested();
                    progress?.Report(new SpectralApplyProgress(
                        0.85 * (completed_events + row) / total_events,
                        $"Unmixing {sample.Name} ({sample_index + 1}/{source_samples.Length})"));
                }
                for (int column = 0; column < retained.Length; column++) values[row, column] = sample.RawEvents[row, sample.GetChannelIndex(retained[column].Name)];
                for (int molecule = 0; molecule < molecule_names.Length; molecule++)
                {
                    double value = 0;
                    for (int detector = 0; detector < detector_indices.Length; detector++) value += sample.RawEvents[row, detector_indices[detector]] * coefficients[molecule, detector];
                    values[row, retained.Length + molecule] = (float)value;
                }
            }
            if (!generated_sample_ids.TryGetValue(sample.Id, out Guid generated_id)) generated_sample_ids[sample.Id] = generated_id = Guid.NewGuid();
            var derived = new FlowSample(sample.Name, channels, values) { Id = generated_id };
            foreach (var pair in sample.Metadata) derived.Metadata[pair.Key] = pair.Value;
            derived.Metadata["SpectralSourceSampleId"] = sample.Id.ToString();
            generated.Add(derived);
            completed_events += sample.EventCount;
        }

        var calculation_group = new FlowGroup();
        foreach (var gate in target.Gates)
            calculation_group.Gates.Add(gate);
        foreach (var statistic in target.Statistics)
            calculation_group.Statistics.Add(statistic);
        foreach (var sample in generated)
            calculation_group.AddSample(sample, recalculate: false);
        calculation_group.ResetIdentityCompensation();
        for (int index = 0; index < generated.Count; index++)
        {
            cancellation_token.ThrowIfCancellationRequested();
            progress?.Report(new SpectralApplyProgress(
                0.85 + 0.10 * index / Math.Max(1, generated.Count),
                $"Preparing {generated[index].Name} ({index + 1}/{generated.Count})"));
            generated[index].Recalculate(calculation_group, cancellation_token: cancellation_token);
        }
        progress?.Report(new SpectralApplyProgress(0.95, "Calculating output ranges"));
        calculation_group.RecalculateDataImpliedViewOptions(cancellation_token);
        progress?.Report(new SpectralApplyProgress(1, "Finalizing unmixed samples"));
        return new SpectralApplyPreparation(
            generated,
            calculation_group.DataImpliedViewOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            generated_sample_ids);
    }

    public void CommitApply(FlowGroup source, FlowGroup target, SpectralApplyPreparation preparation)
    {
        if (target.SpectralSourceGroupId is { } owner && owner != source.Id)
            throw new InvalidOperationException("The selected output group belongs to another spectral model.");
        target.Samples.Clear();
        foreach (var sample in preparation.Samples)
            target.AddSample(sample, recalculate: false);
        target.SpectralSourceGroupId = source.Id;
        target.ResetIdentityCompensation();
        target.DataImpliedViewOptions.Clear();
        foreach (var pair in preparation.DataImpliedViewOptions)
            target.DataImpliedViewOptions[pair.Key] = pair.Value;
        foreach (var pair in preparation.GeneratedSampleIds)
            source.SpectralUnmixing.GeneratedSampleIds[pair.Key] = pair.Value;
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

    private static void validate_control_profiles(IEnumerable<ControlSample> samples, string[] detectors)
    {
        foreach (var sample in samples)
            if (detectors.Any(detector => sample.GetChannelIndex(detector) < 0))
                throw new InvalidOperationException($"{sample.Name} does not contain every detector stored by this spectral model.");
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
