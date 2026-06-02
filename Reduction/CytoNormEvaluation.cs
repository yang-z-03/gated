using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Reduction;

public static class CytoNormEvaluation
{
    public static double[,] MedianAbsoluteDeviation(float[,] data, int[] group_ids)
    {
        MatrixUtilities.Validate(data, nameof(data));
        if (group_ids.Length != data.GetLength(0))
            throw new ArgumentException("Group IDs must have one entry per observation.", nameof(group_ids));

        var groups = group_ids.Select((group, row) => (group, row))
            .GroupBy(static item => item.group)
            .OrderBy(static group => group.Key)
            .ToArray();
        int channels = data.GetLength(1);
        var result = new double[groups.Length, channels];
        for (int group_index = 0; group_index < groups.Length; group_index++)
        {
            var rows = groups[group_index].Select(static item => item.row).ToArray();
            for (int channel = 0; channel < channels; channel++)
            {
                var values = rows.Select(row => (double)data[row, channel]).OrderBy(static value => value).ToArray();
                double median = percentile(values, 0.5);
                var deviations = values.Select(value => Math.Abs(value - median)).OrderBy(static value => value).ToArray();
                result[group_index, channel] = percentile(deviations, 0.5) * 1.4826;
            }
        }

        return result;
    }

    public static double EarthMoverDistance(float[] left, float[] right)
    {
        if (left.Length == 0 || right.Length == 0)
            return double.NaN;

        double min = Math.Min(left.Min(), right.Min());
        double max = Math.Max(left.Max(), right.Max());
        if (min > -100)
            min = -99;
        if (max < 100)
            max = 99;

        double bin_size = calculate_bin_size(min, max);
        var left_bins = bin(left, min - 1, max + 1, bin_size);
        var right_bins = bin(right, min - 1, max + 1, bin_size);
        double cumulative = 0;
        double distance = 0;
        for (int index = 0; index < left_bins.Length; index++)
        {
            cumulative += left_bins[index] - right_bins[index];
            distance += Math.Abs(cumulative);
        }

        distance *= bin_size;
        return bin_size == 0.1 ? distance : distance * (bin_size / 0.1);
    }

    public static double[,] PairwiseEarthMoverDistance(float[,] data, int[] group_ids)
    {
        MatrixUtilities.Validate(data, nameof(data));
        if (group_ids.Length != data.GetLength(0))
            throw new ArgumentException("Group IDs must have one entry per observation.", nameof(group_ids));

        var groups = group_ids.Select((group, row) => (group, row))
            .GroupBy(static item => item.group)
            .OrderBy(static group => group.Key)
            .ToArray();
        int channels = data.GetLength(1);
        int pairs = groups.Length * (groups.Length - 1) / 2;
        var result = new double[pairs, channels];
        int pair = 0;
        for (int left = 0; left < groups.Length; left++)
        for (int right = left + 1; right < groups.Length; right++)
        {
            var left_rows = groups[left].Select(static item => item.row).ToArray();
            var right_rows = groups[right].Select(static item => item.row).ToArray();
            for (int channel = 0; channel < channels; channel++)
            {
                var left_values = left_rows.Select(row => data[row, channel]).ToArray();
                var right_values = right_rows.Select(row => data[row, channel]).ToArray();
                result[pair, channel] = EarthMoverDistance(left_values, right_values);
            }

            pair++;
        }

        return result;
    }

    public static double[] ClusterCoefficientOfVariation(int[] clusters, int[] sample_ids)
    {
        if (clusters.Length != sample_ids.Length)
            throw new ArgumentException("Cluster and sample IDs must have equal length.");

        var cluster_values = MatrixUtilities.SortedUnique(clusters);
        var sample_values = MatrixUtilities.SortedUnique(sample_ids);
        var sample_sizes = sample_values.ToDictionary(static sample => sample, sample => sample_ids.Count(id => id == sample));
        var result = new double[cluster_values.Length];
        for (int cluster_index = 0; cluster_index < cluster_values.Length; cluster_index++)
        {
            int cluster = cluster_values[cluster_index];
            var percentages = new double[sample_values.Length];
            for (int sample_index = 0; sample_index < sample_values.Length; sample_index++)
            {
                int sample = sample_values[sample_index];
                int count = 0;
                for (int row = 0; row < clusters.Length; row++)
                    if (clusters[row] == cluster && sample_ids[row] == sample)
                        count++;
                percentages[sample_index] = sample_sizes[sample] == 0 ? 0 : (double)count / sample_sizes[sample];
            }

            double mean = percentages.Average();
            double sd = sample_standard_deviation(percentages, mean);
            result[cluster_index] = mean == 0 ? 0 : sd / mean;
        }

        return result;
    }

    private static double[] bin(float[] values, double min, double max, double bin_size)
    {
        int bin_count = Math.Max(1, (int)Math.Ceiling((max - min) / bin_size));
        var counts = new double[bin_count];
        foreach (float value in values)
        {
            int index = (int)Math.Floor((value - min) / bin_size);
            index = Math.Clamp(index, 0, bin_count - 1);
            counts[index]++;
        }

        if (values.Length > 0)
            for (int index = 0; index < counts.Length; index++)
                counts[index] /= values.Length;

        return counts;
    }

    private static double calculate_bin_size(double min, double max)
    {
        double diff = Math.Max(double.Epsilon, max - min);
        double adjusted = Math.Ceiling(Math.Log10(diff));
        return Math.Max(0.1, 0.0001 * Math.Pow(10, adjusted));
    }

    private static double percentile(double[] sorted_values, double quantile) =>
        MatrixUtilities.PercentileInSorted(sorted_values, quantile);

    private static double sample_standard_deviation(double[] values, double mean)
    {
        if (values.Length < 2)
            return 0;
        double sum = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sum / (values.Length - 1));
    }
}
