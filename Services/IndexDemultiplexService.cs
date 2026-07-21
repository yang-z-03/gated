using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using gated.Models;

namespace gated.Services;

public sealed record IndexDemultiplexFitResult(
    double[] SampledValues,
    double[] YieldX,
    double[] YieldY,
    double? Cutoff,
    double FitMaximum,
    double LinearSlope,
    double LinearIntercept,
    double LinearRss,
    double LogLogisticSlope,
    double LogLogisticUpper,
    double LogLogisticMidpoint,
    double LogLogisticRss,
    string Error);

public sealed record IndexDemultiplexProgress(double Fraction, string Detail);

public sealed record IndexDemultiplexPreparation(
    IReadOnlyList<FlowSample> Samples,
    IReadOnlyDictionary<string, AxisSettings> DataImpliedViewOptions,
    IReadOnlyDictionary<string, Guid> GeneratedSampleIds,
    CompensationMatrix? Compensation,
    IReadOnlyList<string> Warnings);

public sealed class IndexDemultiplexService
{
    public const int MaximumIndexChannels = 7;
    public const int MaximumFitEvents = 20_000;
    public const double FitArcsinhCofactor = 5.0;

    public IndexDemultiplexFitResult FitCutoff(
        FlowSample sample,
        string channel_name,
        CancellationToken cancellation_token = default)
    {
        int channel = sample.GetChannelIndex(channel_name);
        if (channel < 0)
            return failed($"{sample.Name} does not contain {channel_name}.");

        var indices = deterministic_sample(sample.EventCount, MaximumFitEvents, HashCode.Combine(sample.Id, channel_name));
        var values = new List<double>(indices.Length);
        foreach (int index in indices)
        {
            cancellation_token.ThrowIfCancellationRequested();
            double value = sample.RawEvents[index, channel];
            if (double.IsFinite(value) && value >= 0)
                values.Add(value);
        }
        if (values.Count < 3)
            return failed($"{channel_name} has fewer than three finite non-negative events.", values.ToArray());

        double maximum = values.Max();
        if (!double.IsFinite(maximum) || maximum <= 0)
            return failed($"{channel_name} has no positive fitting range.", values.ToArray());
        double transformed_maximum = Math.Asinh(maximum / FitArcsinhCofactor);
        if (!double.IsFinite(transformed_maximum) || transformed_maximum <= 0)
            return failed($"{channel_name} has no positive transformed fitting range.", values.ToArray());

        values.Sort();
        var xs = new double[101];
        var transformed_xs = new double[101];
        var ys = new double[101];
        for (int index = 0; index <= 100; index++)
        {
            cancellation_token.ThrowIfCancellationRequested();
            transformed_xs[index] = index / 100.0 * transformed_maximum;
            xs[index] = FitArcsinhCofactor * Math.Sinh(transformed_xs[index]);
            int first = lower_bound(values, xs[index]);
            ys[index] = (values.Count - first) / (double)values.Count;
        }

        var linear = fit_linear(transformed_xs, ys);
        var logistic = fit_log_logistic(transformed_xs, ys, cancellation_token);
        double? linear_cutoff = linear.Slope < -1e-12
            ? valid_range(-linear.Intercept / (2 * linear.Slope), transformed_maximum)
            : null;
        double? logistic_cutoff = logistic.Valid ? log_logistic_cutoff(logistic, transformed_xs) : null;

        double? transformed_cutoff;
        if (linear_cutoff.HasValue && logistic_cutoff.HasValue)
        {
            double denominator = linear.Rss + logistic.Rss;
            double weight = denominator > 1e-20 && double.IsFinite(denominator)
                ? logistic.Rss / denominator
                : 0.5;
            transformed_cutoff = valid_range(weight * linear_cutoff.Value + (1 - weight) * logistic_cutoff.Value, transformed_maximum);
        }
        else
        {
            transformed_cutoff = linear_cutoff ?? logistic_cutoff;
        }

        double? raw_cutoff = transformed_cutoff.HasValue
            ? FitArcsinhCofactor * Math.Sinh(transformed_cutoff.Value)
            : null;
        string error = raw_cutoff.HasValue ? "" : "Automatic cutoff fitting did not produce a valid threshold; place it manually.";
        return new IndexDemultiplexFitResult(
            values.ToArray(),
            xs,
            ys,
            raw_cutoff,
            maximum,
            linear.Slope,
            linear.Intercept,
            linear.Rss,
            logistic.Slope,
            logistic.Upper,
            logistic.Midpoint,
            logistic.Rss,
            error);
    }

    public int[] Classify(FlowSample sample, IndexDemultiplexSampleRow row, IReadOnlyList<string> channels, CancellationToken cancellation_token = default)
    {
        if (channels.Count is < 1 or > MaximumIndexChannels)
            throw new InvalidOperationException($"Select between one and {MaximumIndexChannels} index channels.");
        var cutoffs = channels.Select(channel => row.Cutoffs.FirstOrDefault(item => item.ChannelName == channel)?.Cutoff).ToArray();
        if (cutoffs.Any(cutoff => !cutoff.HasValue || !double.IsFinite(cutoff.Value)))
            throw new InvalidOperationException($"Every index channel in {sample.Name} requires a finite cutoff.");
        var indices = channels.Select(sample.GetChannelIndex).ToArray();
        if (indices.Any(index => index < 0))
            throw new InvalidOperationException($"{sample.Name} is missing one or more selected index channels.");

        var result = new int[sample.EventCount];
        for (int event_index = 0; event_index < sample.EventCount; event_index++)
        {
            if ((event_index & 4095) == 0) cancellation_token.ThrowIfCancellationRequested();
            int mask = 0;
            bool finite = true;
            for (int channel = 0; channel < indices.Length; channel++)
            {
                double value = sample.RawEvents[event_index, indices[channel]];
                if (!double.IsFinite(value))
                {
                    finite = false;
                    break;
                }
                if (value >= cutoffs[channel]!.Value)
                    mask |= 1 << channel;
            }
            result[event_index] = finite ? mask : -1;
        }
        return result;
    }

    public IndexDemultiplexPreparation PrepareApply(
        FlowGroup source,
        FlowGroup target,
        IProgress<IndexDemultiplexProgress>? progress = null,
        CancellationToken cancellation_token = default,
        IReadOnlySet<int>? allowed_subset_masks = null)
    {
        var state = source.IndexDemultiplex;
        string[] channels = state.SelectedChannels.ToArray();
        if (channels.Length is < 1 or > MaximumIndexChannels)
            throw new InvalidOperationException($"Select between one and {MaximumIndexChannels} index channels.");
        if (target.IndexDemultiplexSourceGroupId is { } owner && owner != source.Id)
            throw new InvalidOperationException("The selected output group belongs to another demultiplex model.");
        if (target.Samples.Count > 0 && target.IndexDemultiplexSourceGroupId != source.Id)
            throw new InvalidOperationException("The selected output group must be empty.");
        if (target.Samples.Count > 0 && !target.Channels.Select(item => item.Name).SequenceEqual(source.Channels.Select(item => item.Name), StringComparer.Ordinal))
            throw new InvalidOperationException("The selected output group has an incompatible channel schema.");

        var included = state.Subsets
            .Where(subset => subset.IsIncluded && (allowed_subset_masks is null || allowed_subset_masks.Contains(subset.Mask)))
            .OrderBy(subset => subset.Mask)
            .ToArray();
        if (included.Length == 0)
            throw new InvalidOperationException("Select at least one subset to split.");
        if (included.Any(subset => string.IsNullOrWhiteSpace(subset.Name)) ||
            included.GroupBy(subset => subset.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Included subset names must be non-empty and unique.");

        var configured = source.Samples
            .Select(sample => (Sample: sample, Row: state.Rows.FirstOrDefault(row => row.SampleId == sample.Id)))
            .Where(item => item.Row is not null)
            .Select(item => (item.Sample, Row: item.Row!))
            .ToArray();
        if (configured.Length == 0)
            throw new InvalidOperationException("Drop at least one sample before splitting.");

        long total_events = Math.Max(1, configured.Sum(item => (long)item.Sample.EventCount));
        long completed = 0;
        var generated = new List<FlowSample>();
        var generated_ids = new Dictionary<string, Guid>(state.GeneratedSampleIds, StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var item in configured)
        {
            cancellation_token.ThrowIfCancellationRequested();
            progress?.Report(new IndexDemultiplexProgress(0.8 * completed / total_events, $"Classifying {item.Sample.Name}"));
            int[] assignments = Classify(item.Sample, item.Row, channels, cancellation_token);
            int unassigned = assignments.Count(mask => mask < 0);
            if (unassigned > 0) warnings.Add($"{item.Sample.Name}: {unassigned:N0} events had non-finite index values and were not assigned.");
            foreach (var subset in included)
            {
                cancellation_token.ThrowIfCancellationRequested();
                int[] selected = Enumerable.Range(0, assignments.Length).Where(index => assignments[index] == subset.Mask).ToArray();
                if (selected.Length == 0)
                {
                    warnings.Add($"{item.Sample.Name}: {subset.Name} contained no events and was skipped.");
                    continue;
                }
                var values = new float[selected.Length, item.Sample.ChannelCount];
                for (int row = 0; row < selected.Length; row++)
                {
                    if ((row & 4095) == 0) cancellation_token.ThrowIfCancellationRequested();
                    int source_row = selected[row];
                    for (int column = 0; column < item.Sample.ChannelCount; column++)
                        values[row, column] = item.Sample.RawEvents[source_row, column];
                }
                var output_channels = item.Sample.Channels.Select((channel, index) =>
                    new ChannelDefinition(index, channel.Name, channel.Label, channel.Maximum, channel.Gain)).ToArray();
                string identity = generated_key(item.Sample.Id, subset.Signature);
                if (!generated_ids.TryGetValue(identity, out Guid generated_id))
                    generated_ids[identity] = generated_id = Guid.NewGuid();
                var derived = new FlowSample($"{item.Sample.Name}: {subset.Name.Trim()}", output_channels, values) { Id = generated_id };
                foreach (var pair in item.Sample.Metadata) derived.Metadata[pair.Key] = pair.Value;
                derived.Metadata["IndexDemultiplexSourceSampleId"] = item.Sample.Id.ToString();
                derived.Metadata["IndexDemultiplexSubset"] = subset.Signature;
                derived.Metadata["IndexDemultiplexSubsetName"] = subset.Name.Trim();
                derived.Metadata["IndexDemultiplexCutoffs"] = string.Join(";", channels.Select(channel =>
                    $"{channel}={item.Row.Cutoffs.First(cutoff => cutoff.ChannelName == channel).Cutoff:R}"));
                generated.Add(derived);
            }
            completed += item.Sample.EventCount;
        }
        if (generated.Count == 0)
            throw new InvalidOperationException("None of the checked subsets contained events.");

        CompensationMatrix? compensation = source.AppliedCompensation is { } applied
            ? CompensationMatrix.Create(applied.Name, applied.ChannelNames, applied.Values)
            : null;
        var calculation_group = new FlowGroup();
        foreach (var gate in target.Gates) calculation_group.Gates.Add(gate);
        foreach (var statistic in target.Statistics) calculation_group.Statistics.Add(statistic);
        foreach (var sample in generated) calculation_group.AddSample(sample, recalculate: false);
        if (compensation is not null)
        {
            var registered = calculation_group.RegisterCompensation(compensation, make_applied_if_first: false);
            calculation_group.SetAppliedCompensation(registered, manual: true, recalculate: false);
        }
        for (int index = 0; index < generated.Count; index++)
        {
            cancellation_token.ThrowIfCancellationRequested();
            progress?.Report(new IndexDemultiplexProgress(0.8 + 0.15 * index / Math.Max(1, generated.Count), $"Preparing {generated[index].Name}"));
            generated[index].Recalculate(calculation_group, cancellation_token: cancellation_token);
        }
        calculation_group.RecalculateDataImpliedViewOptions(cancellation_token);
        progress?.Report(new IndexDemultiplexProgress(1, "Finalizing demultiplexed samples"));
        return new IndexDemultiplexPreparation(
            generated,
            calculation_group.DataImpliedViewOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            generated_ids,
            compensation is null ? null : CompensationMatrix.Create(compensation.Name, compensation.ChannelNames, compensation.Values),
            warnings);
    }

    public void CommitApply(FlowGroup source, FlowGroup target, IndexDemultiplexPreparation preparation)
    {
        if (target.IndexDemultiplexSourceGroupId is { } owner && owner != source.Id)
            throw new InvalidOperationException("The selected output group belongs to another demultiplex model.");
        target.Samples.Clear();
        target.ResetIdentityCompensation();
        foreach (var sample in preparation.Samples) target.AddSample(sample, recalculate: false);
        target.ResetIdentityCompensation();
        if (preparation.Compensation is { } snapshot)
        {
            var registered = target.RegisterCompensation(snapshot, make_applied_if_first: false);
            target.SetAppliedCompensation(registered, manual: true, recalculate: false);
        }
        target.IndexDemultiplexSourceGroupId = source.Id;
        target.DataImpliedViewOptions.Clear();
        foreach (var pair in preparation.DataImpliedViewOptions) target.DataImpliedViewOptions[pair.Key] = pair.Value;
        source.IndexDemultiplex.GeneratedSampleIds.Clear();
        foreach (var pair in preparation.GeneratedSampleIds) source.IndexDemultiplex.GeneratedSampleIds[pair.Key] = pair.Value;
    }

    public FlowGroup EnsureOutputGroup(FlowWorkspace workspace, FlowGroup source)
    {
        var state = source.IndexDemultiplex;
        if (state.LinkedOutputGroupId is { } linked && workspace.Groups.FirstOrDefault(group => group.Id == linked) is { } existing)
            return existing;
        string preferred = $"{source.Name} demultiplexed";
        string name = preferred;
        for (int suffix = 2; workspace.Groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{preferred} {suffix}";
        var target = new FlowGroup { Name = name, IndexDemultiplexSourceGroupId = source.Id };
        workspace.Groups.Add(target);
        state.LinkedOutputGroupId = target.Id;
        return target;
    }

    public static string Signature(IReadOnlyList<string> channels, int mask) =>
        string.Join("|", channels.Select((channel, index) => $"{channel}={(((mask >> index) & 1) != 0 ? "+" : "-")}"));

    private static string generated_key(Guid sample_id, string signature) => $"{sample_id:N}|{signature}";

    private static IndexDemultiplexFitResult failed(string error, double[]? values = null) =>
        new(values ?? [], [], [], null, 0, 0, 0, double.NaN, 0, 0, 0, double.NaN, error);

    private static int[] deterministic_sample(int count, int maximum, int seed)
    {
        if (count <= maximum) return Enumerable.Range(0, count).ToArray();
        var result = Enumerable.Range(0, maximum).ToArray();
        var random = new Random(seed);
        for (int index = maximum; index < count; index++)
        {
            int replacement = random.Next(index + 1);
            if (replacement < maximum) result[replacement] = index;
        }
        Array.Sort(result);
        return result;
    }

    private static int lower_bound(List<double> values, double target)
    {
        int low = 0, high = values.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (values[middle] < target) low = middle + 1; else high = middle;
        }
        return low;
    }

    private static double? valid_range(double value, double maximum) =>
        double.IsFinite(value) && value >= 0 && value <= maximum ? value : null;

    private static LinearFit fit_linear(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average();
        double denominator = 0, numerator = 0;
        for (int index = 0; index < x.Length; index++)
        {
            denominator += (x[index] - mx) * (x[index] - mx);
            numerator += (x[index] - mx) * (y[index] - my);
        }
        double slope = denominator > 0 ? numerator / denominator : 0;
        double intercept = my - slope * mx;
        double rss = 0;
        for (int index = 0; index < x.Length; index++)
        {
            double residual = y[index] - (slope * x[index] + intercept);
            rss += residual * residual;
        }
        return new LinearFit(slope, intercept, rss);
    }

    private static LogisticFit fit_log_logistic(double[] x, double[] y, CancellationToken cancellation_token)
    {
        LogisticFit best = default;
        foreach (double seed in new[] { 1.0, 2.0, 5.0, 10.0, 20.0 })
        {
            double upper = Math.Max(1e-6, y.Max());
            int midpoint_index = Enumerable.Range(1, 100).OrderBy(index => Math.Abs(y[index] - upper / 2)).First();
            var p = new[] { Math.Log(seed), Math.Log(upper), Math.Log(x[midpoint_index]) };
            double lambda = 1e-3;
            double rss = logistic_rss(p, x, y);
            for (int iteration = 0; iteration < 100; iteration++)
            {
                cancellation_token.ThrowIfCancellationRequested();
                var normal = new double[3, 3];
                var right = new double[3];
                for (int index = 1; index <= 100; index++)
                {
                    logistic_value_and_jacobian(x[index], p, out double value, out var jacobian);
                    double residual = y[index] - value;
                    for (int row = 0; row < 3; row++)
                    {
                        right[row] += jacobian[row] * residual;
                        for (int column = 0; column < 3; column++) normal[row, column] += jacobian[row] * jacobian[column];
                    }
                }
                for (int index = 0; index < 3; index++) normal[index, index] += lambda;
                if (!solve3(normal, right, out var delta)) break;
                var candidate = new[] { p[0] + delta[0], p[1] + delta[1], p[2] + delta[2] };
                if (candidate.Any(value => !double.IsFinite(value) || Math.Abs(value) > 30)) { lambda *= 10; continue; }
                double candidate_rss = logistic_rss(candidate, x, y);
                if (candidate_rss < rss)
                {
                    p = candidate; rss = candidate_rss; lambda = Math.Max(1e-9, lambda / 3);
                    if (delta.Max(Math.Abs) < 1e-8) break;
                }
                else lambda = Math.Min(1e12, lambda * 10);
            }
            var fit = new LogisticFit(Math.Exp(p[0]), Math.Exp(p[1]), Math.Exp(p[2]), rss,
                double.IsFinite(rss) && Math.Exp(p[0]) > 0 && Math.Exp(p[1]) > 0 &&
                Math.Exp(p[2]) > 0 && Math.Exp(p[2]) <= x[^1] * 2);
            if (fit.Valid && (!best.Valid || fit.Rss < best.Rss)) best = fit;
        }
        return best;
    }

    private static double? log_logistic_cutoff(LogisticFit fit, double[] x)
    {
        for (int index = 1; index <= 100; index++)
        {
            double value = x[index];
            double z = Math.Exp(fit.Slope * (Math.Log(value) - Math.Log(fit.Midpoint)));
            double ratio = fit.Slope * z / (value * (1 + z));
            if (double.IsFinite(ratio) && Math.Abs(ratio) > 0.1) return value;
        }
        return null;
    }

    private static double logistic_rss(double[] p, double[] x, double[] y)
    {
        double rss = 0;
        for (int index = 1; index <= 100; index++)
        {
            logistic_value_and_jacobian(x[index], p, out double value, out _);
            double residual = y[index] - value;
            rss += residual * residual;
        }
        return rss;
    }

    private static void logistic_value_and_jacobian(double x, double[] p, out double value, out double[] jacobian)
    {
        double slope = Math.Exp(p[0]), upper = Math.Exp(p[1]), midpoint = Math.Exp(p[2]);
        double log_ratio = Math.Log(x) - Math.Log(midpoint);
        double z = Math.Exp(Math.Clamp(slope * log_ratio, -60, 60));
        double denominator = 1 + z;
        value = upper / denominator;
        double common = upper * z / (denominator * denominator);
        jacobian = new[] { -common * log_ratio * slope, value, common * slope };
    }

    private static bool solve3(double[,] matrix, double[] vector, out double[] result)
    {
        var a = new double[3, 4];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++) a[row, column] = matrix[row, column];
            a[row, 3] = vector[row];
        }
        for (int pivot = 0; pivot < 3; pivot++)
        {
            int selected = pivot;
            for (int row = pivot + 1; row < 3; row++) if (Math.Abs(a[row, pivot]) > Math.Abs(a[selected, pivot])) selected = row;
            if (Math.Abs(a[selected, pivot]) < 1e-14) { result = []; return false; }
            if (selected != pivot) for (int column = pivot; column < 4; column++) (a[pivot, column], a[selected, column]) = (a[selected, column], a[pivot, column]);
            double divisor = a[pivot, pivot];
            for (int column = pivot; column < 4; column++) a[pivot, column] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = a[row, pivot];
                for (int column = pivot; column < 4; column++) a[row, column] -= factor * a[pivot, column];
            }
        }
        result = new[] { a[0, 3], a[1, 3], a[2, 3] };
        return result.All(double.IsFinite);
    }

    private readonly record struct LinearFit(double Slope, double Intercept, double Rss);
    private readonly record struct LogisticFit(double Slope, double Upper, double Midpoint, double Rss, bool Valid);
}
