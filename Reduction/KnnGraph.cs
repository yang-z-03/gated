using System;
using System.Collections.Generic;
using System.Linq;
using gated.Network;

namespace gated.Reduction;

public enum KnnDistanceMetric
{
    Euclidean,
    Cosine
}

public enum KnnSearchMethod
{
    NNDescent,
    Exact
}

public sealed class KnnGraphOptions
{
    public int NeighborCount { get; init; } = 15;
    public KnnDistanceMetric Distance { get; init; } = KnnDistanceMetric.Euclidean;
    public KnnSearchMethod SearchMethod { get; init; } = KnnSearchMethod.NNDescent;
    public bool Mutual { get; init; }
    public int? IterationCount { get; init; }
    public int? MaxCandidates { get; init; }
    public IProvideRandomValues? Random { get; init; }
}

public sealed class KnnGraphResult
{
    internal KnnGraphResult(int[][] indices, float[][] distances, gated.Network.Network network)
    {
        Indices = indices;
        Distances = distances;
        Network = network;
    }

    public int[][] Indices { get; }
    public float[][] Distances { get; }
    public gated.Network.Network Network { get; }
}

public static class KnnGraphBuilder
{
    public static KnnGraphResult Build(float[,] data, KnnGraphOptions? options = null)
    {
        options ??= new KnnGraphOptions();
        MatrixUtilities.Validate(data, nameof(data));
        if (options.NeighborCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.NeighborCount), "Neighbor count must be positive.");

        int rows = data.GetLength(0);
        int k = Math.Min(options.NeighborCount, Math.Max(0, rows - 1));
        var (indices, distances) = options.SearchMethod == KnnSearchMethod.Exact
            ? exact_neighbors(data, k, options.Distance)
            : nn_descent_neighbors(data, k, options);

        return new KnnGraphResult(indices, distances, BuildNetwork(indices, distances, rows, options.Mutual));
    }

    private static (int[][] Indices, float[][] Distances) exact_neighbors(float[,] data, int k, KnnDistanceMetric metric)
    {
        int rows = data.GetLength(0);
        var indices = new int[rows][];
        var distances = new float[rows][];
        for (int row = 0; row < rows; row++)
        {
            var candidates = new (int Index, float Distance)[rows - 1];
            int write = 0;
            for (int other = 0; other < rows; other++)
            {
                if (row == other)
                    continue;

                candidates[write++] = (other, distance(data, row, other, metric));
            }

            Array.Sort(candidates, static (left, right) =>
                left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : left.Index.CompareTo(right.Index));

            indices[row] = new int[k];
            distances[row] = new float[k];
            for (int neighbor = 0; neighbor < k; neighbor++)
            {
                indices[row][neighbor] = candidates[neighbor].Index;
                distances[row][neighbor] = candidates[neighbor].Distance;
            }
        }

        return (indices, distances);
    }

    private static (int[][] Indices, float[][] Distances) nn_descent_neighbors(float[,] data, int k, KnnGraphOptions options)
    {
        int rows = data.GetLength(0);
        if (k == 0)
            return (Enumerable.Range(0, rows).Select(static _ => Array.Empty<int>()).ToArray(),
                Enumerable.Range(0, rows).Select(static _ => Array.Empty<float>()).ToArray());

        var points = MatrixUtilities.ToJagged(data).Select(static row => new RawVectorArrayUmapDataPoint(row)).ToArray();
        var random = options.Random ?? DefaultRandomGenerator.DisableThreading;
        DistanceCalculation<RawVectorArrayUmapDataPoint> distance_fn = options.Distance == KnnDistanceMetric.Cosine
            ? Umap<RawVectorArrayUmapDataPoint>.DistanceFunctions.Cosine
            : Umap<RawVectorArrayUmapDataPoint>.DistanceFunctions.Euclidean;

        int nn_descent_k = Math.Min(rows, k + 1);
        int tree_count = 5 + round(MathF.Sqrt(rows) / 20f);
        int iteration_count = options.IterationCount ?? Math.Max(5, (int)Math.Floor(Math.Round(Math.Log2(rows))));
        int leaf_size = Math.Max(10, nn_descent_k);
        var forest = Enumerable.Range(0, tree_count)
            .Select(index => Tree<RawVectorArrayUmapDataPoint>.FlattenTree(
                Tree<RawVectorArrayUmapDataPoint>.MakeTree(points, leaf_size, index, random),
                leaf_size))
            .ToArray();
        var leaf_array = Tree<RawVectorArrayUmapDataPoint>.MakeLeafArray(forest);
        var nn_descent = NNDescent<RawVectorArrayUmapDataPoint>.MakeNNDescent(distance_fn, random);
        var (raw_indices, raw_distances) = nn_descent(
            points,
            leaf_array,
            nn_descent_k,
            nIters: iteration_count,
            maxCandidates: options.MaxCandidates ?? 50);

        return remove_self_and_fill(data, raw_indices, raw_distances, k, options.Distance);
    }

    private static (int[][] Indices, float[][] Distances) remove_self_and_fill(
        float[,] data,
        int[][] raw_indices,
        float[][] raw_distances,
        int k,
        KnnDistanceMetric metric)
    {
        int rows = data.GetLength(0);
        var indices = new int[rows][];
        var distances = new float[rows][];
        for (int row = 0; row < rows; row++)
        {
            var selected = new List<(int Index, float Distance)>(k);
            var seen = new HashSet<int>();
            for (int index = 0; index < raw_indices[row].Length && selected.Count < k; index++)
            {
                int neighbor = raw_indices[row][index];
                if (neighbor < 0 || neighbor == row || !seen.Add(neighbor))
                    continue;

                selected.Add((neighbor, raw_distances[row][index]));
            }

            if (selected.Count < k)
                fill_missing_exact(data, row, k, metric, selected, seen);

            selected.Sort(static (left, right) =>
                left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : left.Index.CompareTo(right.Index));
            indices[row] = selected.Select(static item => item.Index).ToArray();
            distances[row] = selected.Select(static item => item.Distance).ToArray();
        }

        return (indices, distances);
    }

    public static gated.Network.Network BuildNetwork(int[][] indices, float[][] distances, int node_count, bool mutual = false)
    {
        if (indices.Length != node_count || distances.Length != node_count)
            throw new ArgumentException("KNN arrays must have one row per node.");

        var edge_weights = new Dictionary<(int Source, int Target), double>();
        var directed = new HashSet<(int Source, int Target)>();
        for (int source = 0; source < node_count; source++)
        {
            if (indices[source].Length != distances[source].Length)
                throw new ArgumentException("Each KNN index row must match its distance row length.");

            for (int neighbor = 0; neighbor < indices[source].Length; neighbor++)
            {
                int target = indices[source][neighbor];
                if (target < 0 || target >= node_count || target == source)
                    continue;

                directed.Add((source, target));
            }
        }

        for (int source = 0; source < node_count; source++)
        for (int neighbor = 0; neighbor < indices[source].Length; neighbor++)
        {
            int target = indices[source][neighbor];
            if (target < 0 || target >= node_count || target == source)
                continue;
            if (mutual && !directed.Contains((target, source)))
                continue;

            int left = Math.Min(source, target);
            int right = Math.Max(source, target);
            double weight = 1.0 / (1.0 + distances[source][neighbor]);
            edge_weights[(left, right)] = Math.Max(edge_weights.GetValueOrDefault((left, right)), weight);
        }

        var edge_array = edge_weights.Keys.ToArray();
        var sources = new LargeIntArray(edge_array.Select(static edge => edge.Source).ToArray());
        var targets = new LargeIntArray(edge_array.Select(static edge => edge.Target).ToArray());
        var weights = new LargeDoubleArray(edge_array.Select(edge => edge_weights[edge]).ToArray());
        return new gated.Network.Network(node_count, true, [sources, targets], weights, false, true);
    }

    private static float distance(float[,] data, int left, int right, KnnDistanceMetric metric) =>
        metric == KnnDistanceMetric.Cosine ? cosine(data, left, right) : MathF.Sqrt(MatrixUtilities.SquaredEuclidean(data, left, right));

    private static float cosine(float[,] data, int left, int right)
    {
        int columns = data.GetLength(1);
        double numerator = 0;
        double left_norm = 0;
        double right_norm = 0;
        for (int column = 0; column < columns; column++)
        {
            double left_value = data[left, column];
            double right_value = data[right, column];
            numerator += left_value * right_value;
            left_norm += left_value * left_value;
            right_norm += right_value * right_value;
        }

        double denominator = Math.Sqrt(left_norm) * Math.Sqrt(right_norm);
        return denominator == 0 ? 1 : (float)(1 - numerator / denominator);
    }

    private static void fill_missing_exact(
        float[,] data,
        int row,
        int k,
        KnnDistanceMetric metric,
        List<(int Index, float Distance)> selected,
        HashSet<int> seen)
    {
        var candidates = new List<(int Index, float Distance)>();
        for (int other = 0; other < data.GetLength(0); other++)
        {
            if (other == row || seen.Contains(other))
                continue;

            candidates.Add((other, distance(data, row, other, metric)));
        }

        candidates.Sort(static (left, right) =>
            left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : left.Index.CompareTo(right.Index));
        foreach (var candidate in candidates)
        {
            selected.Add(candidate);
            if (selected.Count == k)
                break;
        }
    }

    private static int round(double value) => value == 0.5 ? 0 : (int)Math.Floor(Math.Round(value));
}
