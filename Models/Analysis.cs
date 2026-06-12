using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using gated.Python;

namespace gated.Models;

public static class GateEvaluator
{
    public static int[] Apply(FlowSample sample, GateDefinition gate, int[] parent_indices)
    {
        return Apply(sample, gate, PopulationRegion.Primary, parent_indices, CancellationToken.None);
    }

    public static int[] Apply(
        FlowSample sample,
        GateDefinition gate,
        PopulationRegion region,
        int[] parent_indices,
        CancellationToken cancellation_token = default)
    {
        return Apply(sample, null, gate, region, parent_indices, cancellation_token);
    }

    public static int[] Apply(
        FlowSample sample,
        FlowGroup? group,
        GateDefinition gate,
        PopulationRegion region,
        int[] parent_indices,
        CancellationToken cancellation_token = default)
    {
        return Apply(sample, group, gate, region, parent_indices, parent_population: null, cancellation_token);
    }

    public static int[] Apply(
        FlowSample sample,
        FlowGroup? group,
        GateDefinition gate,
        PopulationRegion region,
        int[] parent_indices,
        PopulationResult? parent_population,
        CancellationToken cancellation_token = default)
    {
        cancellation_token.ThrowIfCancellationRequested();
        if (gate.Kind is GateKind.Merge or GateKind.Exclude or GateKind.Overlap)
            return apply_boolean_gate(sample, group, gate, parent_indices, cancellation_token);

        if (gate.Kind == GateKind.CurlyQuadrant)
            return apply_curly_quadrant(sample, gate, region, parent_indices, cancellation_token);

        var x_values = parent_population is null
            ? sample.GetNormalizedChannelValues(gate.XChannel, gate.XMinimum, gate.XMaximum, gate.XScale, cancellation_token)
            : parent_population.GetNormalizedChannelValues(sample, gate.XChannel, gate.XMinimum, gate.XMaximum, gate.XScale, cancellation_token);
        if (x_values.Length == 0)
            return Array.Empty<int>();

        float[]? y_values = null;
        if (!gate.IsOneDimensional && gate.YChannel is not null)
        {
            y_values = parent_population is null
                ? sample.GetNormalizedChannelValues(gate.YChannel, gate.YMinimum, gate.YMaximum, gate.YScale, cancellation_token)
                : parent_population.GetNormalizedChannelValues(sample, gate.YChannel, gate.YMinimum, gate.YMaximum, gate.YScale, cancellation_token);
            if (y_values.Length == 0)
                return Array.Empty<int>();
        }

        var normalized_vertices = normalized_gate_vertices(gate);

        var selected = new List<int>(parent_indices.Length);
        for (int index = 0; index < parent_indices.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();
            int row = parent_indices[index];
            int value_index = parent_population is null ? row : index;
            if (value_index < 0 || value_index >= x_values.Length || y_values is not null && value_index >= y_values.Length)
                continue;
            double x_value = x_values[value_index];
            double y_value = y_values is null ? 0 : y_values[value_index];
            if (contains_normalized(gate, region, normalized_vertices, x_value, y_value))
                selected.Add(row);
        }

        return selected.ToArray();
    }

    public static bool Contains(GateDefinition gate, double x_value, double y_value)
    {
        return Contains(gate, PopulationRegion.Primary, x_value, y_value);
    }

    public static bool Contains(GateDefinition gate, PopulationRegion region, double x_value, double y_value)
    {
        if (gate.Vertices.Count == 0)
            return true;

        if (gate.Kind == GateKind.CurlyQuadrant)
            return contains_curly_quadrant(gate.Vertices[0], x_value, y_value, region);

        double normalized_x = normalize_x(gate, x_value);
        double normalized_y = gate.IsOneDimensional ? 0 : normalize_y(gate, y_value);
        var normalized_vertices = normalized_gate_vertices(gate);
        return contains_normalized(gate, region, normalized_vertices, normalized_x, normalized_y);
    }

    private static bool contains_normalized(
        GateDefinition gate,
        PopulationRegion region,
        IReadOnlyList<Point> normalized_vertices,
        double normalized_x,
        double normalized_y)
    {
        if (normalized_vertices.Count == 0)
            return true;

        return gate.Kind switch
        {
            GateKind.Polygon => contains_polygon(normalized_vertices, normalized_x, normalized_y),
            GateKind.Rectangle => contains_rectangle(normalized_vertices, normalized_x, normalized_y),
            GateKind.Threshold => contains_threshold(normalized_vertices, normalized_x, region),
            GateKind.Range => contains_range(normalized_vertices, normalized_x, region),
            GateKind.Quadrant => contains_quadrant(normalized_vertices[0], normalized_x, normalized_y, region),
            GateKind.OffsetQuadrant => contains_offset_quadrant(normalized_vertices, normalized_x, normalized_y, region),
            _ => true
        };
    }

    private static int[] apply_curly_quadrant(
        FlowSample sample,
        GateDefinition gate,
        PopulationRegion region,
        int[] parent_indices,
        CancellationToken cancellation_token)
    {
        if (gate.YChannel is null || gate.Vertices.Count == 0)
            return Array.Empty<int>();

        var x_values = sample.GetChannelValues(gate.XChannel);
        var y_values = sample.GetChannelValues(gate.YChannel);
        if (x_values.Length == 0 || y_values.Length == 0)
            return Array.Empty<int>();

        var selected = new List<int>(parent_indices.Length);
        for (int index = 0; index < parent_indices.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();
            int row = parent_indices[index];
            if (row < 0 || row >= x_values.Length || row >= y_values.Length)
                continue;
            if (contains_curly_quadrant(gate.Vertices[0], x_values[row], y_values[row], region))
                selected.Add(row);
        }

        return selected.ToArray();
    }

    private static int[] apply_boolean_gate(
        FlowSample sample,
        FlowGroup? group,
        GateDefinition gate,
        int[] parent_indices,
        CancellationToken cancellation_token)
    {
        if (group is null || gate.BooleanFirstGateId is null || gate.BooleanSecondGateId is null)
            return Array.Empty<int>();

        var first_gate = find_gate(group.Gates, gate.BooleanFirstGateId.Value);
        var second_gate = find_gate(group.Gates, gate.BooleanSecondGateId.Value);
        if (first_gate is null || second_gate is null)
            return Array.Empty<int>();

        var all_indices = Enumerable.Range(0, sample.EventCount).ToArray();
        var first = evaluate_gate_population(sample, group, first_gate, gate.BooleanFirstRegion, all_indices, [], cancellation_token);
        var second = evaluate_gate_population(sample, group, second_gate, gate.BooleanSecondRegion, all_indices, [], cancellation_token);
        cancellation_token.ThrowIfCancellationRequested();
        var first_set = first.ToHashSet();
        var second_set = second.ToHashSet();
        var parent_set = parent_indices.ToHashSet();
        IEnumerable<int> result = gate.Kind switch
        {
            GateKind.Exclude => first_set.Where(index => !second_set.Contains(index)),
            GateKind.Overlap => first_set.Where(second_set.Contains),
            _ => first_set.Concat(second_set).Distinct()
        };

        return result.Where(parent_set.Contains).OrderBy(index => index).ToArray();
    }

    private static int[] evaluate_gate_population(
        FlowSample sample,
        FlowGroup group,
        GateDefinition gate,
        PopulationRegion region,
        int[] root_indices,
        HashSet<Guid> active,
        CancellationToken cancellation_token)
    {
        cancellation_token.ThrowIfCancellationRequested();
        if (!active.Add(gate.Id))
            return Array.Empty<int>();

        int[] parent_indices = root_indices;
        if (gate.Parent is not null)
            parent_indices = evaluate_gate_population(sample, group, gate.Parent, gate.ParentPopulationRegion, root_indices, active, cancellation_token);

        active.Remove(gate.Id);
        return Apply(sample, group, gate, region, parent_indices, cancellation_token);
    }

    private static GateDefinition? find_gate(IEnumerable<GateDefinition> gates, Guid id)
    {
        foreach (var gate in gates)
        {
            if (gate.Id == id)
                return gate;
            var child = find_gate(gate.Children, id);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static Point[] normalized_gate_vertices(GateDefinition gate) =>
        gate.Vertices
            .Select(vertex => new Point(
                normalize_x(gate, vertex.X),
                gate.IsOneDimensional ? 0 : normalize_y(gate, vertex.Y)))
            .ToArray();

    private static double normalize_x(GateDefinition gate, double value) =>
        normalize(value, gate.XMinimum, gate.XMaximum, gate.XScale);

    private static double normalize_y(GateDefinition gate, double value) =>
        normalize(value, gate.YMinimum, gate.YMaximum, gate.YScale);

    private static double normalize(double value, double minimum, double maximum, AxisScale scale)
    {
        double transformed_minimum = scale.Transform(minimum);
        double transformed_maximum = scale.Transform(maximum);
        double transformed_span = transformed_maximum - transformed_minimum;
        if (transformed_span <= 0)
            return 0;

        return (scale.Transform(value) - transformed_minimum) / transformed_span;
    }

    private static bool contains_polygon(IReadOnlyList<Point> vertices, double x_value, double y_value)
    {
        bool inside = false;
        int previous = vertices.Count - 1;
        for (int current = 0; current < vertices.Count; current++)
        {
            var current_point = vertices[current];
            var previous_point = vertices[previous];
            bool crosses = current_point.Y > y_value != previous_point.Y > y_value;
            if (crosses)
            {
                double intersection = (previous_point.X - current_point.X) * (y_value - current_point.Y) /
                    (previous_point.Y - current_point.Y) + current_point.X;
                if (x_value < intersection)
                    inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    private static bool contains_rectangle(IReadOnlyList<Point> vertices, double x_value, double y_value)
    {
        if (vertices.Count < 2)
            return false;

        double min_x = Math.Min(vertices[0].X, vertices[1].X);
        double max_x = Math.Max(vertices[0].X, vertices[1].X);
        double min_y = Math.Min(vertices[0].Y, vertices[1].Y);
        double max_y = Math.Max(vertices[0].Y, vertices[1].Y);
        return x_value >= min_x && x_value <= max_x && y_value >= min_y && y_value <= max_y;
    }

    private static bool contains_threshold(IReadOnlyList<Point> vertices, double x_value, PopulationRegion region)
    {
        bool more = x_value >= vertices[0].X;
        return region == PopulationRegion.Less ? !more : more;
    }

    private static bool contains_range(IReadOnlyList<Point> vertices, double x_value, PopulationRegion region)
    {
        if (vertices.Count < 2)
            return contains_threshold(vertices, x_value, region);

        double min_x = Math.Min(vertices[0].X, vertices[1].X);
        double max_x = Math.Max(vertices[0].X, vertices[1].X);
        return region switch
        {
            PopulationRegion.BelowRange => x_value < min_x,
            PopulationRegion.AboveRange => x_value > max_x,
            _ => x_value >= min_x && x_value <= max_x
        };
    }

    private static bool contains_quadrant(Point center, double x_value, double y_value, PopulationRegion region)
    {
        return region switch
        {
            PopulationRegion.TopLeft => x_value < center.X && y_value >= center.Y,
            PopulationRegion.BottomRight => x_value >= center.X && y_value < center.Y,
            PopulationRegion.BottomLeft => x_value < center.X && y_value < center.Y,
            _ => x_value >= center.X && y_value >= center.Y
        };
    }

    private static bool contains_offset_quadrant(IReadOnlyList<Point> vertices, double x_value, double y_value, PopulationRegion region)
    {
        var center = vertices[0];
        double top_x = vertices.Count > 1 ? vertices[1].X : center.X;
        double bottom_x = vertices.Count > 2 ? vertices[2].X : center.X;
        double x_boundary = y_value >= center.Y ? top_x : bottom_x;
        return region switch
        {
            PopulationRegion.TopLeft => x_value < x_boundary && y_value >= center.Y,
            PopulationRegion.BottomRight => x_value >= x_boundary && y_value < center.Y,
            PopulationRegion.BottomLeft => x_value < x_boundary && y_value < center.Y,
            _ => x_value >= x_boundary && y_value >= center.Y
        };
    }

    private static bool contains_curly_quadrant(Point center, double x_value, double y_value, PopulationRegion region)
    {
        double x_boundary = y_value >= center.Y
            ? swapped_log_slope_boundary(center.X, center.Y, y_value, 0.1)
            : center.X;
        double y_boundary = x_value >= center.X
            ? log_slope_boundary(center.X, center.Y, x_value, 0.1)
            : center.Y;

        return region switch
        {
            PopulationRegion.TopLeft => x_value < x_boundary && y_value >= center.Y,
            PopulationRegion.BottomRight => x_value >= center.X && y_value < y_boundary,
            PopulationRegion.BottomLeft => x_value < center.X && y_value < center.Y,
            _ => x_value >= x_boundary && y_value >= y_boundary
        };
    }

    private static double log_slope_boundary(double anchor_x, double anchor_y, double x_value, double slope)
    {
        double x0 = positive(anchor_x);
        double y0 = positive(anchor_y);
        double x = positive(x_value);
        double intercept = Math.Log(y0) - slope * Math.Log(x0);
        return Math.Exp(slope * Math.Log(x) + intercept);
    }

    private static double swapped_log_slope_boundary(double anchor_x, double anchor_y, double y_value, double slope)
    {
        double x0 = positive(anchor_x);
        double y0 = positive(anchor_y);
        double y = positive(y_value);
        double intercept = Math.Log(x0) - slope * Math.Log(y0);
        return Math.Exp(slope * Math.Log(y) + intercept);
    }

    private static double positive(double value) =>
        Math.Max(value, 1e-6);
}

public static class StatisticsCalculator
{
    public static StatisticResult Calculate(
        FlowSample sample,
        StatisticDefinition definition,
        int[] event_indices,
        int parent_count,
        int all_count)
    {
        double value = definition.Kind switch
        {
            StatisticKind.NumberOfEvents => event_indices.Length,
            StatisticKind.FrequencyOfParent => parent_count == 0 ? 0 : event_indices.Length * 100.0 / parent_count,
            StatisticKind.FrequencyOfAll => all_count == 0 ? 0 : event_indices.Length * 100.0 / all_count,
            StatisticKind.Python => calculate_python_statistic(sample, definition, event_indices),
            _ => calculate_channel_statistic(sample, definition, event_indices)
        };

        return new StatisticResult
        {
            Kind = definition.Kind,
            ChannelName = definition.ChannelName,
            Value = value,
            PythonDisplayName = definition.PythonDisplayName
        };
    }

    private static double calculate_python_statistic(FlowSample sample, StatisticDefinition definition, int[] event_indices)
    {
        try
        {
            var matrix = select_rows(sample.CompensatedEvents, event_indices);
            var channels = sample.Channels.Select(channel => channel.Name).ToArray();
            return PythonExtensionRuntime.CalculateStatistic(definition, matrix, channels);
        }
        catch (Exception exception)
        {
            PythonExtensionRuntime.Log($"Python statistic failed: {exception.Message}");
            return double.NaN;
        }
    }

    private static float[,] select_rows(float[,] source, int[] event_indices)
    {
        int columns = source.GetLength(1);
        var selected = new float[event_indices.Length, columns];
        for (int row = 0; row < event_indices.Length; row++)
        {
            int source_row = event_indices[row];
            if (source_row < 0 || source_row >= source.GetLength(0))
                continue;
            for (int column = 0; column < columns; column++)
                selected[row, column] = source[source_row, column];
        }
        return selected;
    }

    private static double calculate_channel_statistic(FlowSample sample, StatisticDefinition definition, int[] event_indices)
    {
        var values = sample.GetChannelValues(definition.ChannelName, event_indices)
            .Where(value => !float.IsNaN(value) && !float.IsInfinity(value))
            .Select(value => (double)value)
            .ToArray();

        if (values.Length == 0)
            return 0;

        return definition.Kind switch
        {
            StatisticKind.Mean => values.Average(),
            StatisticKind.Median => median(values),
            StatisticKind.GeometricMean => geometric_mean(values),
            StatisticKind.StandardDeviation => standard_deviation(values),
            StatisticKind.CoefficientOfVariation => coefficient_of_variation(values),
            _ => 0
        };
    }

    private static double median(double[] values)
    {
        Array.Sort(values);
        int middle = values.Length / 2;
        if (values.Length % 2 == 1)
            return values[middle];

        return (values[middle - 1] + values[middle]) / 2.0;
    }

    private static double geometric_mean(double[] values)
    {
        var positive_values = values.Where(value => value > 0).ToArray();
        if (positive_values.Length == 0)
            return 0;

        return Math.Exp(positive_values.Select(value => Math.Log(value)).Average());
    }

    private static double standard_deviation(double[] values)
    {
        if (values.Length < 2)
            return 0;

        double mean = values.Average();
        double variance = values.Select(value => Math.Pow(value - mean, 2)).Sum() / (values.Length - 1);
        return Math.Sqrt(variance);
    }

    private static double coefficient_of_variation(double[] values)
    {
        double mean = values.Average();
        if (Math.Abs(mean) < double.Epsilon)
            return 0;

        return standard_deviation(values) / mean * 100.0;
    }
}
