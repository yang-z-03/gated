using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using gated.Models;

namespace gated.Services;

public sealed record MassNormalizationProgress(double Fraction, string Detail);
public sealed record MassNormalizationPreparation(
    IReadOnlyList<FlowSample> Samples,
    IReadOnlyDictionary<string, AxisSettings> DataImpliedViewOptions,
    IReadOnlyDictionary<Guid, Guid> GeneratedSampleIds);

public sealed class MassNormalizationService
{
    private const int target_beads_per_bin = 50;

    public void Annotate(FlowSample sample, MassNormalizationRow row)
    {
        var context = validate_context(sample, row);
        row.Gates.Clear();

        foreach (var reference in row.References.OrderBy(item => item.MassNumber))
        {
            string channel = context.MassChannels[reference.MassNumber];
            float[] values = sample.GetChannelValues(channel);
            int[] finite = Enumerable.Range(0, sample.EventCount)
                .Where(index => float.IsFinite(values[index]))
                .ToArray();
            if (finite.Length < 3)
                throw new InvalidOperationException($"Could not find a positive bead population in {channel}.");

            // Bead isotopes occupy the extreme positive end of their channel.
            // Start with the upper half of the observed raw range, then mirror
            // its median around the channel maximum to define the X window.
            double maximum = finite.Max(index => (double)values[index]);
            if (!double.IsFinite(maximum) || maximum <= 0)
                throw new InvalidOperationException($"{channel} has no finite positive isotope signal.");
            double half_maximum = maximum / 2.0;
            double[] upper_half = finite
                .Where(index => values[index] >= half_maximum && values[index] <= maximum)
                .Select(index => (double)values[index])
                .OrderBy(value => value)
                .ToArray();
            if (upper_half.Length < 3)
                throw new InvalidOperationException($"Could not find at least three events between half-maximum and maximum in {channel}.");
            double upper_median = quantile(upper_half, 0.5);
            double minimum = upper_median - (maximum - upper_median);

            int[] isotope_high = finite
                .Where(index => values[index] >= minimum && values[index] <= maximum && float.IsFinite(context.DnaValues[index]))
                .ToArray();
            if (isotope_high.Length < 3)
                throw new InvalidOperationException($"Could not find enough isotope-high events in {channel} to define the DNA-low region.");
            double dna_minimum = isotope_high.Min(index => (double)context.DnaValues[index]);
            double dna_upper = dna_low_cdf_plateau(context.DnaValues, isotope_high);

            var gate = new MassBeadGateState { MassNumber = reference.MassNumber, ChannelName = channel };
            set_rectangle(gate.Vertices, minimum, maximum, dna_minimum, dna_upper);
            row.Gates.Add(gate);
        }

        rebuild_caches(sample, row, context);
    }

    public void RebuildCaches(FlowSample sample, MassNormalizationRow row)
    {
        var context = validate_context(sample, row);
        if (row.Gates.Count != row.References.Count || row.Gates.Any(gate => gate.Vertices.Count < 3))
        {
            Annotate(sample, row);
            return;
        }
        rebuild_caches(sample, row, context);
    }

    public IReadOnlyList<int> ExtrapolatedMasses(FlowSample sample, MassNormalizationRow row)
    {
        if (row.References.Count < 2) return [];
        int minimum = row.References.Min(reference => reference.MassNumber);
        int maximum = row.References.Max(reference => reference.MassNumber);
        string cytometer = Configuration.CytometerNameForSample(sample);
        return sample.Channels
            .Where(channel => Configuration.ChannelKind(channel.Name, cytometer) == ChannelSemanticKind.Mass)
            .Select(channel => Configuration.MassNumberForChannel(channel.Name, cytometer))
            .Where(mass => mass.HasValue && (mass.Value < minimum || mass.Value > maximum))
            .Select(mass => mass!.Value).Distinct().OrderBy(mass => mass).ToArray();
    }

    public MassNormalizationPreparation PrepareApply(
        FlowGroup source,
        FlowGroup target,
        IProgress<MassNormalizationProgress>? progress = null,
        CancellationToken cancellation_token = default)
    {
        var configured = source.MassNormalization.Rows
            .Select(row => (Row: row, Sample: source.Samples.FirstOrDefault(sample => sample.Id == row.SampleId)))
            .Where(item => item.Sample is not null)
            .Select(item => (item.Row, Sample: item.Sample!))
            .OrderBy(item => source.Samples.IndexOf(item.Sample))
            .ToArray();
        if (configured.Length == 0) throw new InvalidOperationException("Drop at least one sample into Mass normalization.");
        if (target.MassNormalizationSourceGroupId is { } owner && owner != source.Id)
            throw new InvalidOperationException("The selected output group belongs to another mass-normalization setup.");
        if (target.Samples.Count > 0 && target.MassNormalizationSourceGroupId != source.Id)
            throw new InvalidOperationException("The selected output group must be empty.");

        var generated_ids = new Dictionary<Guid, Guid>(source.MassNormalization.GeneratedSampleIds);
        long total_events = Math.Max(1, configured.Sum(item => (long)item.Sample.EventCount));
        long completed = 0;
        var generated = new List<FlowSample>(configured.Length);
        for (int sample_index = 0; sample_index < configured.Length; sample_index++)
        {
            cancellation_token.ThrowIfCancellationRequested();
            var (row, sample) = configured[sample_index];
            RebuildCaches(sample, row);
            if (!string.IsNullOrWhiteSpace(row.CacheError)) throw new InvalidOperationException($"{sample.Name}: {row.CacheError}");
            if (row.QcBeadIndices.Length < 20 || row.TimeDynamics is null)
                throw new InvalidOperationException($"{sample.Name} does not have enough QC-passed beads.");
            progress?.Report(new MassNormalizationProgress((double)completed / total_events * 0.85, $"Normalizing {sample.Name} ({sample_index + 1}/{configured.Length})"));

            var removed = source.MassNormalization.RemoveBeadsAndDoublets
                ? row.RemovedEventIndices.ToHashSet()
                : new HashSet<int>();
            int[] retained_rows = Enumerable.Range(0, sample.EventCount).Where(index => !removed.Contains(index)).ToArray();
            var channels = sample.Channels.Select((channel, index) => new ChannelDefinition(index, channel.Name, channel.Label, channel.Maximum, channel.Gain)).ToArray();
            var values = new float[retained_rows.Length, channels.Length];
            string cytometer = Configuration.CytometerNameForSample(sample);
            string time_channel = row.TimeDynamics.TimeChannel;
            int time_index = sample.GetChannelIndex(time_channel);
            var series = row.TimeDynamics.Series.OrderBy(item => item.MassNumber).ToArray();
            if (series.Length < 2) throw new InvalidOperationException($"{sample.Name} requires at least two bead isotopes.");

            for (int output_row = 0; output_row < retained_rows.Length; output_row++)
            {
                if ((output_row & 4095) == 0) cancellation_token.ThrowIfCancellationRequested();
                int source_row = retained_rows[output_row];
                double time = sample.RawEvents[source_row, time_index];
                for (int column = 0; column < channels.Length; column++)
                {
                    float raw = sample.RawEvents[source_row, column];
                    if (Configuration.ChannelKind(channels[column].Name, cytometer) != ChannelSemanticKind.Mass ||
                        Configuration.MassNumberForChannel(channels[column].Name, cytometer) is not { } mass)
                    {
                        values[output_row, column] = raw;
                        continue;
                    }
                    double factor = factor_at_mass(series, time, mass);
                    if (!double.IsFinite(factor) || factor <= 0)
                        throw new InvalidOperationException($"{sample.Name}: normalization produced an invalid factor for mass {mass}.");
                    values[output_row, column] = (float)(raw * factor);
                }
            }

            if (!generated_ids.TryGetValue(sample.Id, out Guid generated_id)) generated_ids[sample.Id] = generated_id = Guid.NewGuid();
            var derived = new FlowSample(sample.Name, channels, values) { Id = generated_id };
            foreach (var pair in sample.Metadata) derived.Metadata[pair.Key] = pair.Value;
            derived.Metadata["MassNormalizationSourceSampleId"] = sample.Id.ToString();
            derived.Metadata["MassNormalizationBeadType"] = row.BeadTypeName;
            derived.Metadata["MassNormalizationBeadLot"] = row.BeadLotName;
            derived.Metadata["MassNormalizationQcBeads"] = row.QcBeadIndices.Length.ToString();
            generated.Add(derived);
            completed += sample.EventCount;
        }

        var calculation_group = new FlowGroup();
        foreach (var gate in target.Gates) calculation_group.Gates.Add(gate);
        foreach (var statistic in target.Statistics) calculation_group.Statistics.Add(statistic);
        foreach (var sample in generated) calculation_group.AddSample(sample, recalculate: false);
        calculation_group.ResetIdentityCompensation();
        for (int index = 0; index < generated.Count; index++)
        {
            cancellation_token.ThrowIfCancellationRequested();
            progress?.Report(new MassNormalizationProgress(0.85 + 0.1 * index / Math.Max(1, generated.Count), $"Preparing {generated[index].Name} ({index + 1}/{generated.Count})"));
            generated[index].Recalculate(calculation_group, cancellation_token: cancellation_token);
        }
        calculation_group.RecalculateDataImpliedViewOptions(cancellation_token);
        progress?.Report(new MassNormalizationProgress(1, "Finalizing normalized samples"));
        return new MassNormalizationPreparation(generated, calculation_group.DataImpliedViewOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), generated_ids);
    }

    public void CommitApply(FlowGroup source, FlowGroup target, MassNormalizationPreparation preparation)
    {
        target.Samples.Clear();
        foreach (var sample in preparation.Samples) target.AddSample(sample, recalculate: false);
        target.MassNormalizationSourceGroupId = source.Id;
        target.ResetIdentityCompensation();
        target.DataImpliedViewOptions.Clear();
        foreach (var pair in preparation.DataImpliedViewOptions) target.DataImpliedViewOptions[pair.Key] = pair.Value;
        foreach (var pair in preparation.GeneratedSampleIds) source.MassNormalization.GeneratedSampleIds[pair.Key] = pair.Value;
    }

    public FlowGroup EnsureOutputGroup(FlowWorkspace workspace, FlowGroup source, FlowGroup? selected)
    {
        if (selected is not null) return selected;
        if (source.MassNormalization.LinkedOutputGroupId is { } linked && workspace.Groups.FirstOrDefault(group => group.Id == linked) is { } existing)
            return existing;
        string preferred = $"{source.Name} normalized";
        string name = preferred;
        int suffix = 2;
        while (workspace.Groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{preferred} {suffix++}";
        var target = new FlowGroup { Name = name, MassNormalizationSourceGroupId = source.Id };
        workspace.Groups.Add(target);
        source.MassNormalization.LinkedOutputGroupId = target.Id;
        return target;
    }

    private static void rebuild_caches(FlowSample sample, MassNormalizationRow row, Context context)
    {
        row.CacheError = "";
        try
        {
            int[] qc = gated_indices(sample, row);
            if (qc.Length < 20) throw new InvalidOperationException($"Only {qc.Length:N0} QC-passed beads were found; at least 20 are required.");
            row.QcBeadIndices = qc;
            row.RemovedEventIndices = bead_associated_indices(sample, row);
            row.TimeDynamics = build_time_dynamics(sample, row, context.TimeChannel, qc);
        }
        catch (Exception exception)
        {
            row.QcBeadIndices = [];
            row.RemovedEventIndices = [];
            row.TimeDynamics = null;
            row.CacheError = exception.Message;
        }
    }

    private static Context validate_context(FlowSample sample, MassNormalizationRow row)
    {
        if (row.References.Count < 2) throw new InvalidOperationException("At least two bead isotope references are required.");
        if (row.References.Any(reference => !double.IsFinite(reference.ReferenceIntensity) || reference.ReferenceIntensity <= 0))
            throw new InvalidOperationException("Every bead reference intensity must be finite and positive.");
        string cytometer = Configuration.CytometerNameForSample(sample);
        var mass_groups = sample.Channels
            .Select(channel => (channel.Name, Mass: Configuration.MassNumberForChannel(channel.Name, cytometer)))
            .Where(item => item.Mass.HasValue)
            .GroupBy(item => item.Mass!.Value)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Name).ToArray());
        foreach (int mass in row.References.Select(reference => reference.MassNumber))
        {
            if (!mass_groups.TryGetValue(mass, out var names)) throw new InvalidOperationException($"The sample does not contain mass {mass}.");
            if (names.Length != 1) throw new InvalidOperationException($"Mass {mass} maps to more than one sample channel.");
        }
        if (string.IsNullOrWhiteSpace(row.DnaChannel) || sample.GetChannelIndex(row.DnaChannel) < 0)
            throw new InvalidOperationException("Select a DNA channel.");
        string time = sample.Channels.FirstOrDefault(channel => Configuration.ChannelKind(channel.Name, cytometer) == ChannelSemanticKind.Time)?.Name ?? "";
        if (string.IsNullOrWhiteSpace(time)) throw new InvalidOperationException("The sample has no registered Time channel.");
        return new Context(
            row.DnaChannel,
            time,
            sample.GetChannelValues(row.DnaChannel),
            row.References.ToDictionary(reference => reference.MassNumber, reference => mass_groups[reference.MassNumber][0]));
    }

    private static MassTimeDynamicsData build_time_dynamics(FlowSample sample, MassNormalizationRow row, string time_channel, int[] qc)
    {
        float[] time_values = sample.GetChannelValues(time_channel);
        int[] ordered = qc.Where(index => float.IsFinite(time_values[index])).OrderBy(index => time_values[index]).ToArray();
        if (ordered.Length < 20 || time_values[ordered[0]] == time_values[ordered[^1]])
            throw new InvalidOperationException("QC beads do not span at least two distinct Time values.");
        int bin_count = Math.Max(2, (int)Math.Round(ordered.Length / (double)target_beads_per_bin));
        bin_count = Math.Min(200, Math.Min(bin_count, Math.Max(2, ordered.Length / 10)));
        var bins = new List<int[]>();
        int bin_size = (int)Math.Ceiling(ordered.Length / (double)bin_count);
        for (int start = 0; start < ordered.Length; start += bin_size)
            bins.Add(ordered.Skip(start).Take(bin_size).ToArray());
        if (bins.Count > 1 && bins[^1].Length < 25)
        {
            bins[^2] = bins[^2].Concat(bins[^1]).ToArray();
            bins.RemoveAt(bins.Count - 1);
        }
        if (bins.Count < 2)
        {
            int middle = ordered.Length / 2;
            bins = [ordered[..middle], ordered[middle..]];
        }
        double[] times = bins.Select(bin => median(bin.Select(index => (double)time_values[index]))).ToArray();
        var series = new List<MassTimeSeries>();
        double observed_maximum = 1;
        foreach (var reference in row.References.OrderBy(reference => reference.MassNumber))
        {
            var gate = row.Gates.First(item => item.MassNumber == reference.MassNumber);
            float[] values = sample.GetChannelValues(gate.ChannelName);
            double[] medians = bins.Select(bin => median(bin.Select(index => (double)values[index]).Where(double.IsFinite))).ToArray();
            if (medians.Any(value => !double.IsFinite(value) || value <= 0))
                throw new InvalidOperationException($"A time bin has no positive {gate.ChannelName} bead signal.");
            observed_maximum = Math.Max(observed_maximum, medians.Max());
            series.Add(new MassTimeSeries(reference.MassNumber, gate.ChannelName, times, medians,
                Enumerable.Repeat(reference.ReferenceIntensity, times.Length).ToArray(), reference.ReferenceIntensity));
        }
        double channel_maximum = row.Gates
            .Select(gate => sample.Channels.FirstOrDefault(channel => channel.Name == gate.ChannelName)?.Maximum)
            .Where(value => value.HasValue && float.IsFinite(value.Value) && value.Value > 0)
            .Select(value => (double)value!.Value)
            .DefaultIfEmpty(observed_maximum)
            .Max();
        return new MassTimeDynamicsData(time_channel, series, times.Min(), times.Max(), channel_maximum);
    }

    private static double factor_at_mass(IReadOnlyList<MassTimeSeries> series, double time, int mass)
    {
        var points = series.Select(item => (Mass: item.MassNumber, Factor: item.ReferenceIntensity / interpolate(item.Times, item.RawMedians, time))).ToArray();
        int upper = Array.FindIndex(points, point => point.Mass >= mass);
        if (upper == 0) return lerp_mass(points[0], points[1], mass);
        if (upper < 0) return lerp_mass(points[^2], points[^1], mass);
        if (points[upper].Mass == mass) return points[upper].Factor;
        return lerp_mass(points[upper - 1], points[upper], mass);
    }

    private static double lerp_mass((int Mass, double Factor) left, (int Mass, double Factor) right, int mass) =>
        left.Factor + (right.Factor - left.Factor) * (mass - left.Mass) / (double)(right.Mass - left.Mass);

    private static double interpolate(IReadOnlyList<double> x, IReadOnlyList<double> y, double value)
    {
        if (value <= x[0]) return y[0];
        if (value >= x[^1]) return y[^1];
        int upper = 1;
        while (upper < x.Count && x[upper] < value) upper++;
        double fraction = (value - x[upper - 1]) / (x[upper] - x[upper - 1]);
        return y[upper - 1] + (y[upper] - y[upper - 1]) * fraction;
    }

    private static int[] gated_indices(FlowSample sample, MassNormalizationRow row)
    {
        var result = new List<int>();
        float[] dna = sample.GetChannelValues(row.DnaChannel);
        var channel_values = row.Gates.ToDictionary(gate => gate.MassNumber, gate => sample.GetChannelValues(gate.ChannelName));
        for (int index = 0; index < sample.EventCount; index++)
            if (row.Gates.All(gate => contains(gate.Vertices, channel_values[gate.MassNumber][index], dna[index]))) result.Add(index);
        return result.ToArray();
    }

    private static int[] bead_associated_indices(FlowSample sample, MassNormalizationRow row)
    {
        var lower = row.Gates.ToDictionary(gate => gate.MassNumber, gate => gate.Vertices.Min(vertex => vertex.X));
        var values = row.Gates.ToDictionary(gate => gate.MassNumber, gate => sample.GetChannelValues(gate.ChannelName));
        return Enumerable.Range(0, sample.EventCount)
            .Where(index => row.Gates.All(gate => values[gate.MassNumber][index] >= lower[gate.MassNumber]))
            .ToArray();
    }

    private static bool contains(IReadOnlyList<Point> vertices, double x, double y)
    {
        if (vertices.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
            if (((vertices[i].Y > y) != (vertices[j].Y > y)) && x < (vertices[j].X - vertices[i].X) * (y - vertices[i].Y) / (vertices[j].Y - vertices[i].Y) + vertices[i].X) inside = !inside;
        return inside;
    }

    private static void set_rectangle(ICollection<Point> vertices, double x_min, double x_max, double y_min, double y_max)
    {
        vertices.Clear();
        vertices.Add(new Point(x_min, y_min)); vertices.Add(new Point(x_max, y_min));
        vertices.Add(new Point(x_max, y_max)); vertices.Add(new Point(x_min, y_max));
    }

    private static double dna_low_cdf_plateau(float[] dna_values, int[] isotope_high_indices)
    {
        double[] sorted = isotope_high_indices
            .Select(index => transform(dna_values[index]))
            .Where(double.IsFinite)
            .OrderBy(value => value)
            .ToArray();
        if (sorted.Length < 3) return inverse_transform(sorted.LastOrDefault());

        // A plateau in the empirical CDF is an interval in DNA intensity with
        // no observations.  Use the widest such interval after a supported
        // low-DNA subset and place the gate boundary at its midpoint.
        int minimum_low_count = Math.Max(2, (int)Math.Ceiling(sorted.Length * 0.05));
        int minimum_high_count = Math.Max(1, (int)Math.Ceiling(sorted.Length * 0.01));
        int first_split = minimum_low_count - 1;
        int last_split = sorted.Length - minimum_high_count - 1;
        if (last_split < first_split) return inverse_transform(quantile(sorted, 0.25));

        int plateau = first_split;
        double plateau_width = double.NegativeInfinity;
        for (int index = first_split; index <= last_split; index++)
        {
            double width = sorted[index + 1] - sorted[index];
            if (width > plateau_width)
            {
                plateau_width = width;
                plateau = index;
            }
        }
        if (!double.IsFinite(plateau_width) || plateau_width <= 0)
            return inverse_transform(quantile(sorted, 0.25));
        return inverse_transform((sorted[plateau] + sorted[plateau + 1]) / 2.0);
    }

    private static double transform(double value) => double.IsFinite(value) ? Math.Asinh(value / 5.0) : double.NaN;
    private static double inverse_transform(double value) => 5.0 * Math.Sinh(value);
    private static double median(IEnumerable<double> source)
    {
        double[] values = source.Where(double.IsFinite).OrderBy(value => value).ToArray();
        return values.Length == 0 ? double.NaN : quantile(values, 0.5);
    }
    private static double quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        double position = Math.Clamp(q, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private sealed record Context(string DnaChannel, string TimeChannel, float[] DnaValues, IReadOnlyDictionary<int, string> MassChannels);
}
