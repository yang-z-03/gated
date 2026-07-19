using System;
using System.Collections.Generic;
using System.Linq;
using gated.Models;

namespace gated.Services;

internal sealed record SpectracleSignatureResult(float[] Signature, float[,] CorrectedEvents);

internal static class SpectracleSignatureDerivation
{
    public const int NeighborCount = 2;

    public static string ResolvePeak(
        ControlSample stained,
        int[] stained_rows,
        ControlSample background,
        int[] background_rows,
        IReadOnlyList<string> detectors)
    {
        if (detectors.Count == 0)
            return "";

        int[] stained_columns = detectors.Select(stained.GetChannelIndex).ToArray();
        int[] background_columns = detectors.Select(background.GetChannelIndex).ToArray();
        double[] af_mean = column_means(background, background_rows, background_columns);
        double af_norm = Math.Sqrt(af_mean.Where(double.IsFinite).Sum(value => value * value));
        if (!double.IsFinite(af_norm) || af_norm <= double.Epsilon)
            return raw_peak(stained, stained_rows, detectors, stained_columns);

        double[] af_unit = af_mean.Select(value => double.IsFinite(value) ? value / af_norm : 0).ToArray();
        var cleaned_sums = new double[detectors.Count];
        var cleaned_counts = new int[detectors.Count];

        foreach (int row in stained_rows)
        {
            double projection = 0;
            for (int detector = 0; detector < stained_columns.Length; detector++)
            {
                int column = stained_columns[detector];
                if (column < 0)
                    continue;
                double value = stained.RawEvents[row, column];
                if (double.IsFinite(value))
                    projection += value * af_unit[detector];
            }

            for (int detector = 0; detector < stained_columns.Length; detector++)
            {
                int column = stained_columns[detector];
                if (column < 0)
                    continue;
                double value = stained.RawEvents[row, column];
                if (!double.IsFinite(value))
                    continue;
                cleaned_sums[detector] += value - projection * af_unit[detector];
                cleaned_counts[detector]++;
            }
        }

        int best = 0;
        double best_mean = double.NegativeInfinity;
        for (int detector = 0; detector < detectors.Count; detector++)
        {
            double mean = cleaned_counts[detector] == 0
                ? double.NegativeInfinity
                : cleaned_sums[detector] / cleaned_counts[detector];
            if (mean > best_mean)
            {
                best = detector;
                best_mean = mean;
            }
        }
        return detectors[best];
    }

    public static SpectracleSignatureResult DeriveFluorophoreSignature(
        ControlSample stained,
        int[] selected_rows,
        ControlSample background,
        int[] background_rows,
        IReadOnlyList<string> detectors)
    {
        if (selected_rows.Length == 0)
            throw new InvalidOperationException("At least one selected spectral event is required.");
        if (background_rows.Length < NeighborCount)
            throw new InvalidOperationException($"At least {NeighborCount} gated blank events are required for scatter matching.");

        string[] scatter_channels = common_scatter_channels(stained, background);
        if (scatter_channels.Length == 0)
            throw new InvalidOperationException("Spectracle derivation requires at least one common FSC or SSC channel for blank scatter matching.");

        int[] stained_detectors = detectors.Select(stained.GetChannelIndex).ToArray();
        int[] background_detectors = detectors.Select(background.GetChannelIndex).ToArray();
        int[] stained_scatter = scatter_channels.Select(stained.GetChannelIndex).ToArray();
        int[] background_scatter = scatter_channels.Select(background.GetChannelIndex).ToArray();
        var corrected = new float[selected_rows.Length, detectors.Count];

        for (int selected = 0; selected < selected_rows.Length; selected++)
        {
            int stained_row = selected_rows[selected];
            int[] neighbors = nearest_neighbors(
                stained, stained_row, stained_scatter,
                background, background_rows, background_scatter,
                NeighborCount);

            for (int detector = 0; detector < detectors.Count; detector++)
            {
                double positive = stained.RawEvents[stained_row, stained_detectors[detector]];
                double background_sum = 0;
                int background_count = 0;
                foreach (int neighbor in neighbors)
                {
                    double value = background.RawEvents[neighbor, background_detectors[detector]];
                    if (!double.IsFinite(value))
                        continue;
                    background_sum += value;
                    background_count++;
                }
                corrected[selected, detector] = double.IsFinite(positive) && background_count > 0
                    ? (float)(positive - background_sum / background_count)
                    : float.NaN;
            }
        }

        float[] signature = aggregate_and_normalize(corrected, clip_negative: true);
        return new SpectracleSignatureResult(signature, corrected);
    }

    public static float[] DeriveAfSignature(
        ControlSample background,
        int[] background_rows,
        IReadOnlyList<string> detectors)
    {
        int[] columns = detectors.Select(background.GetChannelIndex).ToArray();
        var events = new float[background_rows.Length, detectors.Count];
        for (int row = 0; row < background_rows.Length; row++)
            for (int detector = 0; detector < detectors.Count; detector++)
                events[row, detector] = background.RawEvents[background_rows[row], columns[detector]];
        return aggregate_and_normalize(events, clip_negative: false);
    }

    private static float[] aggregate_and_normalize(float[,] events, bool clip_negative)
    {
        var linear = new double[events.GetLength(1)];
        for (int detector = 0; detector < events.GetLength(1); detector++)
        {
            var values = new double[events.GetLength(0)];
            int count = 0;
            for (int row = 0; row < events.GetLength(0); row++)
            {
                double value = events[row, detector];
                if (double.IsFinite(value))
                    values[count++] = value;
            }
            if (count == 0)
                throw new InvalidOperationException("A spectral detector has no finite selected values.");
            Array.Sort(values, 0, count);
            int middle = count / 2;
            linear[detector] = count % 2 == 0
                ? (values[middle - 1] + values[middle]) / 2
                : values[middle];
        }

        double maximum = linear.Max();
        if (!double.IsFinite(maximum) || maximum <= double.Epsilon)
            throw new InvalidOperationException("The derived spectral signature has a non-positive maximum.");
        return linear.Select(value => (float)(clip_negative ? Math.Max(0, value / maximum) : value / maximum)).ToArray();
    }

    private static int[] nearest_neighbors(
        ControlSample query_sample,
        int query_row,
        int[] query_columns,
        ControlSample background,
        int[] background_rows,
        int[] background_columns,
        int count)
    {
        int best_row = -1;
        int second_row = -1;
        double best_distance = double.PositiveInfinity;
        double second_distance = double.PositiveInfinity;

        foreach (int background_row in background_rows)
        {
            double distance = 0;
            bool valid = true;
            for (int scatter = 0; scatter < query_columns.Length; scatter++)
            {
                double query = query_sample.RawEvents[query_row, query_columns[scatter]];
                double candidate = background.RawEvents[background_row, background_columns[scatter]];
                if (!double.IsFinite(query) || !double.IsFinite(candidate))
                {
                    valid = false;
                    break;
                }
                double delta = query - candidate;
                distance += delta * delta;
            }
            if (!valid)
                continue;
            if (distance < best_distance)
            {
                second_distance = best_distance;
                second_row = best_row;
                best_distance = distance;
                best_row = background_row;
            }
            else if (distance < second_distance)
            {
                second_distance = distance;
                second_row = background_row;
            }
        }

        if (best_row < 0)
            throw new InvalidOperationException("No finite blank event was available for scatter matching.");
        if (count == 1 || second_row < 0)
            return [best_row];
        return [best_row, second_row];
    }

    private static string[] common_scatter_channels(ControlSample stained, ControlSample background) =>
        stained.Channels
            .Select(channel => channel.Name)
            .Where(name => Configuration.IsFscChannel(name) || Configuration.IsSscChannel(name))
            .Where(name => background.GetChannelIndex(name) >= 0)
            .ToArray();

    private static double[] column_means(ControlSample sample, int[] rows, int[] columns)
    {
        var means = new double[columns.Length];
        for (int detector = 0; detector < columns.Length; detector++)
        {
            double sum = 0;
            int count = 0;
            foreach (int row in rows)
            {
                double value = sample.RawEvents[row, columns[detector]];
                if (!double.IsFinite(value))
                    continue;
                sum += value;
                count++;
            }
            means[detector] = count == 0 ? double.NaN : sum / count;
        }
        return means;
    }

    private static string raw_peak(ControlSample sample, int[] rows, IReadOnlyList<string> detectors, int[] columns)
    {
        double[] means = column_means(sample, rows, columns);
        int best = Enumerable.Range(0, means.Length).OrderByDescending(index => means[index]).First();
        return detectors[best];
    }
}
