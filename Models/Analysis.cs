using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace gated.Models;

public static class GateEvaluator
{
    public static int[] Apply(FlowSample sample, GateDefinition gate, int[] parent_indices)
    {
        if (gate.Kind is GateKind.Merge or GateKind.Exclude or GateKind.Overlap)
            return parent_indices.ToArray();

        var x_values = sample.GetChannelValues(gate.XChannel);
        if (x_values.Length == 0)
            return Array.Empty<int>();

        float[]? y_values = null;
        if (!gate.IsOneDimensional && gate.YChannel is not null)
            y_values = sample.GetChannelValues(gate.YChannel);

        var selected = new List<int>(parent_indices.Length);
        foreach (int row in parent_indices)
        {
            double x_value = x_values[row];
            double y_value = y_values is null ? 0 : y_values[row];
            if (Contains(gate, x_value, y_value))
                selected.Add(row);
        }

        return selected.ToArray();
    }

    public static bool Contains(GateDefinition gate, double x_value, double y_value)
    {
        if (gate.Vertices.Count == 0)
            return true;

        double transformed_x = gate.XScale.Transform(x_value);
        double transformed_y = gate.IsOneDimensional ? 0 : gate.YScale.Transform(y_value);
        var transformed_vertices = transformed_gate_vertices(gate);
        return gate.Kind switch
        {
            GateKind.Polygon => contains_polygon(transformed_vertices, transformed_x, transformed_y),
            GateKind.Rectangle => contains_rectangle(transformed_vertices, transformed_x, transformed_y),
            GateKind.Threshold => transformed_x >= transformed_vertices[0].X,
            GateKind.Range => contains_range(transformed_vertices, transformed_x),
            GateKind.Quadrant => transformed_x >= transformed_vertices[0].X && transformed_y >= transformed_vertices[0].Y,
            GateKind.CurlyQuadrant => transformed_x >= transformed_vertices[0].X && transformed_y >= transformed_vertices[0].Y,
            _ => true
        };
    }

    private static Point[] transformed_gate_vertices(GateDefinition gate) =>
        gate.Vertices
            .Select(vertex => new Point(
                gate.XScale.Transform(vertex.X),
                gate.IsOneDimensional ? 0 : gate.YScale.Transform(vertex.Y)))
            .ToArray();

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

    private static bool contains_range(IReadOnlyList<Point> vertices, double x_value)
    {
        if (vertices.Count < 2)
            return x_value >= vertices[0].X;

        double min_x = Math.Min(vertices[0].X, vertices[1].X);
        double max_x = Math.Max(vertices[0].X, vertices[1].X);
        return x_value >= min_x && x_value <= max_x;
    }
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
            _ => calculate_channel_statistic(sample, definition, event_indices)
        };

        return new StatisticResult { Kind = definition.Kind, ChannelName = definition.ChannelName, Value = value };
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
