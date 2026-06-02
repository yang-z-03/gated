using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Reduction;

public enum CytoNormGoal
{
    BatchMean,
    BatchMedian
}

public sealed class CytoNormOptions
{
    public int QuantileCount { get; set; } = 99;
    public double[]? Quantiles { get; set; }
    public int MinimumCellsPerCluster { get; set; } = 50;
    public CytoNormGoal Goal { get; set; } = CytoNormGoal.BatchMean;
    public int? GoalBatch { get; set; }
    public double[]? Limits { get; set; }
    public FlowSomClustererOptions FlowSom { get; set; } = new();
}

public sealed class ExpressionQuantiles
{
    private readonly double[,,,] expr_quantiles;

    public ExpressionQuantiles(int batch_count, int cluster_count, int channel_count, double[] quantiles)
    {
        if (batch_count <= 0 || cluster_count <= 0 || channel_count <= 0)
            throw new ArgumentOutOfRangeException(nameof(batch_count), "Quantile dimensions must be positive.");

        BatchCount = batch_count;
        ClusterCount = cluster_count;
        ChannelCount = channel_count;
        Quantiles = (double[])quantiles.Clone();
        expr_quantiles = new double[batch_count, cluster_count, Quantiles.Length, channel_count];
    }

    public int BatchCount { get; }
    public int ClusterCount { get; }
    public int ChannelCount { get; }
    public double[] Quantiles { get; }

    public void AddQuantiles(float[,] data, int[] rows, int batch_index, int cluster_index)
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            var values = new double[rows.Length];
            for (int index = 0; index < rows.Length; index++)
                values[index] = data[rows[index], channel];
            Array.Sort(values);

            for (int quantile = 0; quantile < Quantiles.Length; quantile++)
                expr_quantiles[batch_index, cluster_index, quantile, channel] =
                    MatrixUtilities.PercentileInSorted(values, Quantiles[quantile]);
        }
    }

    public void AddNanSlice(int batch_index, int cluster_index)
    {
        for (int quantile = 0; quantile < Quantiles.Length; quantile++)
        for (int channel = 0; channel < ChannelCount; channel++)
            expr_quantiles[batch_index, cluster_index, quantile, channel] = double.NaN;
    }

    public double[] GetQuantiles(int batch_index, int cluster_index, int channel_index)
    {
        var result = new double[Quantiles.Length];
        for (int quantile = 0; quantile < Quantiles.Length; quantile++)
            result[quantile] = expr_quantiles[batch_index, cluster_index, quantile, channel_index];
        return result;
    }

    public double[] GetGoalQuantiles(int cluster_index, int channel_index, CytoNormOptions options)
    {
        if (options.GoalBatch is int batch_index)
            return GetQuantiles(batch_index, cluster_index, channel_index);

        var result = new double[Quantiles.Length];
        for (int quantile = 0; quantile < Quantiles.Length; quantile++)
        {
            var values = new List<double>(BatchCount);
            for (int batch = 0; batch < BatchCount; batch++)
            {
                double value = expr_quantiles[batch, cluster_index, quantile, channel_index];
                if (!double.IsNaN(value))
                    values.Add(value);
            }

            result[quantile] = options.Goal == CytoNormGoal.BatchMedian ? median(values) : mean(values);
        }

        return result;
    }

    private static double mean(List<double> values) => values.Count == 0 ? double.NaN : values.Sum() / values.Count;

    private static double median(List<double> values)
    {
        if (values.Count == 0)
            return double.NaN;
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2.0;
    }
}
