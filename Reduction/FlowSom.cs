using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Reduction;

public enum FlowSomDistance
{
    Manhattan = 1,
    Euclidean = 2,
    Chebyshev = 3,
    Cosine = 4
}

public sealed record FlowSomMapResult(int[] NodeIds, double[] Distances);

public sealed class FlowSomOptions
{
    public int XDimension { get; set; } = 10;
    public int YDimension { get; set; } = 10;
    public int IterationCount { get; set; } = 10;
    public double AlphaStart { get; set; } = 0.05;
    public double AlphaEnd { get; set; } = 0.01;
    public double? RadiusStart { get; set; }
    public double RadiusEnd { get; set; }
    public FlowSomDistance Distance { get; set; } = FlowSomDistance.Euclidean;
    public int? Seed { get; set; } = 42;
}

public static class FlowSom
{
    private const int c_rand_max = 32767;

    public static double[,] NeighborhoodDistance(int x_dimension, int y_dimension)
    {
        if (x_dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(x_dimension), "Grid width must be positive.");
        if (y_dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(y_dimension), "Grid height must be positive.");

        int node_count = checked(x_dimension * y_dimension);
        var coordinates = new (int X, int Y)[node_count];
        int index = 0;
        for (int y = 1; y <= y_dimension; y++)
        for (int x = 1; x <= x_dimension; x++)
            coordinates[index++] = (x, y);

        var distances = new double[node_count, node_count];
        for (int row = 0; row < node_count; row++)
        for (int column = 0; column < node_count; column++)
        {
            int x_distance = Math.Abs(coordinates[row].X - coordinates[column].X);
            int y_distance = Math.Abs(coordinates[row].Y - coordinates[column].Y);
            distances[row, column] = Math.Max(x_distance, y_distance);
        }

        return distances;
    }

    public static double[,] Som(double[,] data, FlowSomOptions? options = null, double[,]? initial_nodes = null)
    {
        options ??= new FlowSomOptions();
        validate_options(options);

        int row_count = data.GetLength(0);
        int column_count = data.GetLength(1);
        if (row_count == 0 || column_count == 0)
            throw new ArgumentException("SOM input data must contain at least one row and one column.", nameof(data));

        int node_count = checked(options.XDimension * options.YDimension);
        var nodes = initial_nodes is null
            ? initialize_nodes(data, node_count, options.Seed)
            : copy_nodes(initial_nodes, node_count, column_count);

        var neighborhood_distances = NeighborhoodDistance(options.XDimension, options.YDimension);
        double radius_start = options.RadiusStart ?? percentile(neighborhood_distances, 67.0);
        som_train(
            data,
            nodes,
            neighborhood_distances,
            options.AlphaStart,
            options.AlphaEnd,
            radius_start,
            options.RadiusEnd,
            options.IterationCount,
            options.Distance,
            options.Seed);

        return nodes;
    }

    public static double[,] Som(float[,] data, FlowSomOptions? options = null, double[,]? initial_nodes = null) =>
        Som(to_double_matrix(data), options, initial_nodes);

    public static FlowSomMapResult MapDataToNodes(double[,] nodes, double[,] data, FlowSomDistance distance = FlowSomDistance.Euclidean)
    {
        int data_rows = data.GetLength(0);
        int data_columns = data.GetLength(1);
        int node_count = nodes.GetLength(0);
        if (node_count == 0)
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        if (nodes.GetLength(1) != data_columns)
            throw new ArgumentException("Nodes and data must have the same column count.", nameof(nodes));

        var node_ids = new int[data_rows];
        var distances = new double[data_rows];
        for (int row = 0; row < data_rows; row++)
        {
            int best_node = -1;
            double best_distance = double.MaxValue;
            for (int node = 0; node < node_count; node++)
            {
                double current_distance = calculate_distance(data, row, nodes, node, data_columns, distance);
                if (current_distance >= best_distance)
                    continue;

                best_distance = current_distance;
                best_node = node;
            }

            node_ids[row] = best_node + 1;
            distances[row] = best_distance;
        }

        return new FlowSomMapResult(node_ids, distances);
    }

    public static FlowSomMapResult MapDataToNodes(double[,] nodes, float[,] data, FlowSomDistance distance = FlowSomDistance.Euclidean) =>
        MapDataToNodes(nodes, to_double_matrix(data), distance);

    private static void validate_options(FlowSomOptions options)
    {
        if (options.XDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.XDimension), "Grid width must be positive.");
        if (options.YDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.YDimension), "Grid height must be positive.");
        if (options.IterationCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.IterationCount), "Iteration count must be positive.");
        if (options.AlphaStart < 0 || options.AlphaEnd < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Learning rates must be non-negative.");
    }

    private static double[,] initialize_nodes(double[,] data, int node_count, int? seed)
    {
        int row_count = data.GetLength(0);
        int column_count = data.GetLength(1);
        if (node_count > row_count)
            throw new ArgumentException("The SOM node count cannot exceed the number of input rows when nodes are not supplied.", nameof(data));

        var random = seed is null ? new Random() : new Random(seed.Value);
        var rows = Enumerable.Range(0, row_count).ToArray();
        for (int index = 0; index < node_count; index++)
        {
            int swap_index = random.Next(index, row_count);
            (rows[index], rows[swap_index]) = (rows[swap_index], rows[index]);
        }

        var nodes = new double[node_count, column_count];
        for (int node = 0; node < node_count; node++)
        for (int column = 0; column < column_count; column++)
            nodes[node, column] = data[rows[node], column];

        return nodes;
    }

    private static double[,] copy_nodes(double[,] initial_nodes, int expected_rows, int expected_columns)
    {
        if (initial_nodes.GetLength(0) != expected_rows)
            throw new ArgumentException("Initial nodes must have XDimension * YDimension rows.", nameof(initial_nodes));
        if (initial_nodes.GetLength(1) != expected_columns)
            throw new ArgumentException("Initial nodes must have the same column count as the data.", nameof(initial_nodes));

        return (double[,])initial_nodes.Clone();
    }

    private static void som_train(
        double[,] data,
        double[,] nodes,
        double[,] neighborhood_distances,
        double alpha_start,
        double alpha_end,
        double radius_start,
        double radius_end,
        int iteration_count,
        FlowSomDistance distance,
        int? seed)
    {
        int row_count = data.GetLength(0);
        int column_count = data.GetLength(1);
        int node_count = nodes.GetLength(0);
        var x_distances = new double[node_count];

        var random = new CStyleRandom(seed is null
            ? unchecked((uint)Random.Shared.Next(1, 65535))
            : unchecked((uint)seed.Value));

        double total_iterations = iteration_count * row_count;
        double threshold = radius_start;
        double threshold_step = (radius_start - radius_end) / total_iterations;
        double change = 1.0;

        for (double step = 0; step < total_iterations; step++)
        {
            if (step % row_count == 0)
            {
                if (change < 1.0)
                    break;

                change = 0.0;
            }

            int row = (int)(row_count * random.NextUniform());
            int nearest_node = 0;
            for (int node = 0; node < node_count; node++)
            {
                x_distances[node] = calculate_distance(data, row, nodes, node, column_count, distance);
                if (x_distances[node] < x_distances[nearest_node])
                    nearest_node = node;
            }

            if (threshold < 1.0)
                threshold = 0.5;

            double alpha = alpha_start - (alpha_start - alpha_end) * step / total_iterations;
            for (int node = 0; node < node_count; node++)
            {
                if (neighborhood_distances[node, nearest_node] > threshold)
                    continue;

                for (int column = 0; column < column_count; column++)
                {
                    double delta = data[row, column] - nodes[node, column];
                    change += Math.Abs(delta);
                    nodes[node, column] += delta * alpha;
                }
            }

            threshold -= threshold_step;
        }
    }

    private static double calculate_distance(
        double[,] data,
        int data_row,
        double[,] nodes,
        int node_row,
        int column_count,
        FlowSomDistance distance)
    {
        return distance switch
        {
            FlowSomDistance.Manhattan => manhattan(data, data_row, nodes, node_row, column_count),
            FlowSomDistance.Chebyshev => chebyshev(data, data_row, nodes, node_row, column_count),
            FlowSomDistance.Cosine => cosine(data, data_row, nodes, node_row, column_count),
            _ => euclidean(data, data_row, nodes, node_row, column_count)
        };
    }

    private static double euclidean(double[,] data, int data_row, double[,] nodes, int node_row, int column_count)
    {
        double distance = 0.0;
        for (int column = 0; column < column_count; column++)
        {
            double delta = data[data_row, column] - nodes[node_row, column];
            distance += delta * delta;
        }

        return Math.Sqrt(distance);
    }

    private static double manhattan(double[,] data, int data_row, double[,] nodes, int node_row, int column_count)
    {
        double distance = 0.0;
        for (int column = 0; column < column_count; column++)
            distance += Math.Abs(data[data_row, column] - nodes[node_row, column]);

        return distance;
    }

    private static double chebyshev(double[,] data, int data_row, double[,] nodes, int node_row, int column_count)
    {
        double distance = 0.0;
        for (int column = 0; column < column_count; column++)
            distance = Math.Max(distance, Math.Abs(data[data_row, column] - nodes[node_row, column]));

        return distance;
    }

    private static double cosine(double[,] data, int data_row, double[,] nodes, int node_row, int column_count)
    {
        double numerator = 0.0;
        double data_norm = 0.0;
        double node_norm = 0.0;
        for (int column = 0; column < column_count; column++)
        {
            double data_value = data[data_row, column];
            double node_value = nodes[node_row, column];
            numerator += data_value * node_value;
            data_norm += data_value * data_value;
            node_norm += node_value * node_value;
        }

        return -numerator / (Math.Sqrt(data_norm) * Math.Sqrt(node_norm)) + 1.0;
    }

    private static double percentile(double[,] values, double percentile_value)
    {
        var flattened = new List<double>(values.Length);
        foreach (double value in values)
            flattened.Add(value);

        flattened.Sort();
        if (flattened.Count == 0)
            return 0.0;

        double index = (flattened.Count - 1) * percentile_value / 100.0;
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return flattened[lower];

        double weight = index - lower;
        return flattened[lower] * (1.0 - weight) + flattened[upper] * weight;
    }

    private static double[,] to_double_matrix(float[,] values)
    {
        int rows = values.GetLength(0);
        int columns = values.GetLength(1);
        var result = new double[rows, columns];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            result[row, column] = values[row, column];

        return result;
    }

    private sealed class CStyleRandom(uint seed)
    {
        private uint seed = seed;

        public double NextUniform() =>
            next() / (c_rand_max + 1.0);

        private int next()
        {
            seed = unchecked(seed * 1103515245 + 12345);
            return (int)((seed / 65536) % (c_rand_max + 1));
        }
    }
}
