using System;
using System.Collections.Generic;
using System.Linq;
using gated.Network;

namespace gated.Reduction;

public interface IMatrixClusterer
{
    void Train(float[,] data);
    int[] Predict(float[,] data);
}

public sealed class LeidenClusteringOptions
{
    public double Resolution { get; init; } = CPMClusteringAlgorithm.DefaultResolution;
    public int IterationCount { get; init; } = IterativeCPMClusteringAlgorithm.DefaultNIterations;
    public double Randomness { get; init; } = LeidenAlgorithm.DefaultRandomness;
    public int? Seed { get; init; } = 187;
}

public static class LeidenClustering
{
    public static int[] Cluster(gated.Network.Network network, LeidenClusteringOptions? options = null)
    {
        options ??= new LeidenClusteringOptions();
        var random = options.Seed is null ? new Random() : new Random(options.Seed.Value);
        var algorithm = new LeidenAlgorithm(options.Resolution, options.IterationCount, options.Randomness, random);
        var clustering = algorithm.FindClustering(network);
        return clustering.GetClusters();
    }
}

public sealed class FlowSomClustererOptions
{
    public FlowSomOptions Som { get; init; } = new();
    public LeidenClusteringOptions MetaClustering { get; init; } = new() { Resolution = 0.05 };
    public FlowSomDistance Distance { get; init; } = FlowSomDistance.Euclidean;
}

public sealed class FlowSomClusterer : IMatrixClusterer
{
    private readonly FlowSomClustererOptions options;
    private double[,]? codes;
    private int[] node_clusters = [];

    public FlowSomClusterer(FlowSomClustererOptions? options = null)
    {
        this.options = options ?? new FlowSomClustererOptions();
    }

    public double[,] Codes => codes is null ? throw new InvalidOperationException("FlowSOM has not been trained.") : (double[,])codes.Clone();
    public int[] NodeClusters => (int[])node_clusters.Clone();

    public void Train(float[,] data)
    {
        MatrixUtilities.Validate(data, nameof(data));
        codes = FlowSom.Som(data, options.Som);
        node_clusters = cluster_nodes(codes, options.Som.XDimension, options.Som.YDimension, options.MetaClustering);
    }

    public int[] Predict(float[,] data)
    {
        if (codes is null)
            throw new InvalidOperationException("FlowSOM has not been trained.");

        var mapped = FlowSom.MapDataToNodes(codes, data, options.Distance).NodeIds;
        var result = new int[mapped.Length];
        for (int row = 0; row < result.Length; row++)
            result[row] = node_clusters[mapped[row] - 1];
        return result;
    }

    public int[,] PredictMultiple(float[,] data, IReadOnlyList<double> resolutions)
    {
        if (codes is null)
            throw new InvalidOperationException("FlowSOM has not been trained.");
        if (resolutions.Count == 0)
            return new int[data.GetLength(0), 0];

        var mapped = FlowSom.MapDataToNodes(codes, data, options.Distance).NodeIds;
        var result = new int[data.GetLength(0), resolutions.Count];
        for (int index = 0; index < resolutions.Count; index++)
        {
            var clustering_options = new LeidenClusteringOptions
            {
                Resolution = resolutions[index],
                IterationCount = options.MetaClustering.IterationCount,
                Randomness = options.MetaClustering.Randomness,
                Seed = options.MetaClustering.Seed
            };
            var clusters = cluster_nodes(codes, options.Som.XDimension, options.Som.YDimension, clustering_options);
            for (int row = 0; row < mapped.Length; row++)
                result[row, index] = clusters[mapped[row] - 1];
        }

        return result;
    }

    private static int[] cluster_nodes(double[,] codes, int x_dimension, int y_dimension, LeidenClusteringOptions options)
    {
        int node_count = codes.GetLength(0);
        var edges = new List<(int Source, int Target, double Weight)>();
        for (int y = 0; y < y_dimension; y++)
        for (int x = 0; x < x_dimension; x++)
        {
            int node = y * x_dimension + x;
            if (x + 1 < x_dimension)
                edges.Add((node, node + 1, code_weight(codes, node, node + 1)));
            if (y + 1 < y_dimension)
                edges.Add((node, node + x_dimension, code_weight(codes, node, node + x_dimension)));
        }

        if (edges.Count == 0)
            return Enumerable.Range(0, node_count).ToArray();

        var sources = new LargeIntArray(edges.Select(static edge => edge.Source).ToArray());
        var targets = new LargeIntArray(edges.Select(static edge => edge.Target).ToArray());
        var weights = new LargeDoubleArray(edges.Select(static edge => edge.Weight).ToArray());
        var network = new gated.Network.Network(node_count, true, [sources, targets], weights, false, true);
        return LeidenClustering.Cluster(network, options);
    }

    private static double code_weight(double[,] codes, int left, int right)
    {
        int columns = codes.GetLength(1);
        double distance = 0;
        for (int column = 0; column < columns; column++)
        {
            double delta = codes[left, column] - codes[right, column];
            distance += delta * delta;
        }

        return 1.0 / (1.0 + Math.Sqrt(distance));
    }
}
