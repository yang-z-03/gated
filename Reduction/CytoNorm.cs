using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Reduction;

public sealed class CytoNormFitResult
{
    internal CytoNormFitResult(
        FlowSomClusterer clusterer,
        ExpressionQuantiles expression_quantiles,
        CytoNormSpline[,,] splines,
        int[] batches,
        int[] clusters,
        int[] reference_clusters,
        CytoNormOptions options)
    {
        Clusterer = clusterer;
        ExpressionQuantiles = expression_quantiles;
        Splines = splines;
        Batches = batches;
        Clusters = clusters;
        ReferenceClusters = reference_clusters;
        Options = options;
    }

    public FlowSomClusterer Clusterer { get; }
    public ExpressionQuantiles ExpressionQuantiles { get; }
    public CytoNormSpline[,,] Splines { get; }
    public int[] Batches { get; }
    public int[] Clusters { get; }
    public int[] ReferenceClusters { get; }
    public CytoNormOptions Options { get; }
}

public static class CytoNorm
{
    public static CytoNormFitResult Fit(float[,] reference_data, int[] reference_batches, CytoNormOptions? options = null)
    {
        options ??= new CytoNormOptions();
        MatrixUtilities.Validate(reference_data, nameof(reference_data));
        if (reference_batches.Length != reference_data.GetLength(0))
            throw new ArgumentException("Batch IDs must have one entry per observation.", nameof(reference_batches));
        if (options.MinimumCellsPerCluster < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumCellsPerCluster), "Minimum cells per cluster must be positive.");

        var clusterer = new FlowSomClusterer(options.FlowSom);
        clusterer.Train(reference_data);
        var clusters = clusterer.Predict(reference_data);
        return Fit(reference_data, reference_batches, clusters, clusterer, options);
    }

    public static CytoNormFitResult Fit(
        float[,] reference_data,
        int[] reference_batches,
        int[] reference_clusters,
        FlowSomClusterer clusterer,
        CytoNormOptions? options = null)
    {
        options ??= new CytoNormOptions();
        MatrixUtilities.Validate(reference_data, nameof(reference_data));
        int rows = reference_data.GetLength(0);
        int channels = reference_data.GetLength(1);
        if (reference_batches.Length != rows || reference_clusters.Length != rows)
            throw new ArgumentException("Batch and cluster IDs must have one entry per observation.");

        var batches = MatrixUtilities.SortedUnique(reference_batches);
        var clusters = MatrixUtilities.SortedUnique(reference_clusters);
        var batch_lookup = batches.Select((batch, index) => (batch, index)).ToDictionary(static item => item.batch, static item => item.index);
        var cluster_lookup = clusters.Select((cluster, index) => (cluster, index)).ToDictionary(static item => item.cluster, static item => item.index);

        var quantiles = options.Quantiles is { Length: > 0 }
            ? (double[])options.Quantiles.Clone()
            : MatrixUtilities.DefaultQuantiles(options.QuantileCount);
        validate_quantiles(quantiles);

        var expression_quantiles = new ExpressionQuantiles(batches.Length, clusters.Length, channels, quantiles);
        var buckets = new Dictionary<(int BatchIndex, int ClusterIndex), List<int>>();
        for (int row = 0; row < rows; row++)
        {
            var key = (batch_lookup[reference_batches[row]], cluster_lookup[reference_clusters[row]]);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
            }

            bucket.Add(row);
        }

        for (int batch = 0; batch < batches.Length; batch++)
        for (int cluster = 0; cluster < clusters.Length; cluster++)
        {
            if (buckets.TryGetValue((batch, cluster), out var rows_for_bucket) &&
                rows_for_bucket.Count >= options.MinimumCellsPerCluster)
            {
                expression_quantiles.AddQuantiles(reference_data, rows_for_bucket.ToArray(), batch, cluster);
            }
            else
            {
                expression_quantiles.AddNanSlice(batch, cluster);
            }
        }

        var splines = new CytoNormSpline[batches.Length, clusters.Length, channels];
        for (int batch = 0; batch < batches.Length; batch++)
        for (int cluster = 0; cluster < clusters.Length; cluster++)
        for (int channel = 0; channel < channels; channel++)
        {
            var current = expression_quantiles.GetQuantiles(batch, cluster, channel);
            var goal = expression_quantiles.GetGoalQuantiles(cluster, channel, options);
            splines[batch, cluster, channel] = CytoNormSpline.Fit(current, goal, options.Limits);
        }

        return new CytoNormFitResult(
            clusterer,
            expression_quantiles,
            splines,
            batches,
            clusters,
            (int[])reference_clusters.Clone(),
            options);
    }

    public static float[,] Normalize(CytoNormFitResult model, float[,] data, int[] batches)
    {
        MatrixUtilities.Validate(data, nameof(data));
        if (batches.Length != data.GetLength(0))
            throw new ArgumentException("Batch IDs must have one entry per observation.", nameof(batches));
        if (data.GetLength(1) != model.ExpressionQuantiles.ChannelCount)
            throw new ArgumentException("Data must have the same number of variables as the CytoNorm model.", nameof(data));

        var result = MatrixUtilities.Copy(data);
        var clusters = model.Clusterer.Predict(data);
        NormalizeInPlace(model, result, batches, clusters);
        return result;
    }

    public static void NormalizeInPlace(CytoNormFitResult model, float[,] data, int[] batches, int[] clusters)
    {
        int rows = data.GetLength(0);
        int channels = data.GetLength(1);
        if (batches.Length != rows || clusters.Length != rows)
            throw new ArgumentException("Batch and cluster IDs must have one entry per observation.");

        var batch_lookup = model.Batches.Select((batch, index) => (batch, index)).ToDictionary(static item => item.batch, static item => item.index);
        var cluster_lookup = model.Clusters.Select((cluster, index) => (cluster, index)).ToDictionary(static item => item.cluster, static item => item.index);

        for (int row = 0; row < rows; row++)
        {
            if (!batch_lookup.TryGetValue(batches[row], out int batch))
                throw new ArgumentException($"Batch {batches[row]} is not present in the CytoNorm model.", nameof(batches));
            if (!cluster_lookup.TryGetValue(clusters[row], out int cluster))
                throw new ArgumentException($"Cluster {clusters[row]} is not present in the CytoNorm model.", nameof(clusters));

            for (int channel = 0; channel < channels; channel++)
                model.Splines[batch, cluster, channel].TransformInPlace(data, row, channel);
        }
    }

    private static void validate_quantiles(double[] quantiles)
    {
        foreach (double quantile in quantiles)
            if (quantile < 0 || quantile > 1)
                throw new ArgumentOutOfRangeException(nameof(quantiles), "Quantiles must be between 0 and 1.");
    }
}
