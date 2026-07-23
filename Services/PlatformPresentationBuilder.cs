using System;
using System.Collections.Generic;
using System.Linq;
using gated.Models;
using gated.Reduction;

namespace gated.Services;

public static class PlatformPresentationBuilder
{
    public static PlatformPresentation Integration(IntegrationPlatform platform)
    {
        string channel = platform.SelectedFeatureNames.FirstOrDefault() ?? "";
        int column = Array.IndexOf(platform.SelectedFeatureNames, channel);
        var matrix = platform.Normalized ?? platform.Compensated;
        var series = column >= 0 && matrix is not null
            ? distributions(platform, matrix, column, normalized_input: platform.Normalized is not null)
            : [];
        var plot = plot_document(platform, "integration-histogram", "Integration histogram", channel, series);
        return presentation(platform, plot, default_table_key: "");
    }

    public static PlatformPresentation Univariate(
        UnivariatePlatform platform,
        string plot_key,
        string plot_title,
        string default_table_key,
        bool default_plot,
        bool include_fit,
        bool include_components,
        bool include_observed = true)
    {
        var observed = !include_observed || platform.Transformed is null
            ? Array.Empty<PlatformPlotSeries>()
            : distributions(platform, platform.Transformed, 0, normalized_input: true);
        var scripted = platform.PlotSeries
            .Where(item => source_visible(platform, item.SourceId))
            .Where(item => item.Role != PlatformSeriesRole.Fit || include_fit)
            .Where(item => item.Role != PlatformSeriesRole.Component || include_components)
            .Select(item => raw_x_series(platform, item))
            .ToArray();
        var plot = plot_document(platform, plot_key, plot_title, platform.SelectedFeatureNames.FirstOrDefault() ?? "Intensity", observed.Concat(scripted).ToArray());
        return presentation(platform, plot, default_table_key, default_plot);
    }

    private static PlatformPresentation presentation(Platform platform, PlatformPlotDocument plot, string default_table_key, bool default_plot = true)
    {
        var tables = platform.ResultTables.Select(table => new PlatformTableDocument
        {
            Key = table.Key,
            Title = table.Title,
            Columns = table.Columns,
            Rows = table.Rows.ToArray()
        }).ToArray();
        var outputs = new List<PlatformLayoutOutput>();
        if (plot.Series.Count > 0)
            outputs.Add(new PlatformLayoutOutput(plot.Key, plot.Title, PlatformLayoutOutputKind.Plot, default_plot));
        foreach (var table in tables)
            outputs.Add(new PlatformLayoutOutput(table.Key, table.Title, PlatformLayoutOutputKind.Table,
                string.Equals(table.Key, default_table_key, StringComparison.Ordinal)));
        return new PlatformPresentation { Plots = plot.Series.Count > 0 ? [plot] : [], Tables = tables, Outputs = outputs };
    }

    private static PlatformPlotDocument plot_document(Platform platform, string key, string title, string x_label, IReadOnlyList<PlatformPlotSeries> series) =>
        new()
        {
            Key = key,
            Title = title,
            XLabel = string.IsNullOrWhiteSpace(x_label) ? "Intensity" : x_label,
            YLabel = "Normalized frequency",
            XTransform = platform.Axis.Transform,
            Minimum = platform.Axis.Minimum,
            Maximum = platform.Axis.Maximum,
            Logicle = platform.Axis.Logicle,
            Series = series
        };

    private static PlatformPlotSeries[] distributions(Platform platform, float[,] matrix, int column, bool normalized_input)
    {
        if (column < 0 || column >= matrix.GetLength(1) || platform.RowMap.Count == 0)
            return [];
        var result = new List<PlatformPlotSeries>();
        for (int source_id = 0; source_id < platform.RowMap.Sources.Count; source_id++)
        {
            if (!source_visible(platform, source_id))
                continue;
            var values = new List<double>();
            for (int row = 0; row < platform.RowMap.SourceIds.Length && row < matrix.GetLength(0); row++)
            {
                if (platform.RowMap.SourceIds[row] != source_id)
                    continue;
                double value = matrix[row, column];
                if (normalized_input)
                    value = inverse_transform(platform, value);
                if (double.IsFinite(value))
                    values.Add(value);
            }
            if (values.Count == 0)
                continue;
            result.Add(histogram(platform, source_id, values));
        }
        return result.ToArray();
    }

    private static PlatformPlotSeries histogram(Platform platform, int source_id, IReadOnlyList<double> values)
    {
        const int bins = 400;
        double transformed_minimum = transform(platform, platform.Axis.Minimum);
        double transformed_maximum = transform(platform, platform.Axis.Maximum);
        if (!double.IsFinite(transformed_minimum) || !double.IsFinite(transformed_maximum) || transformed_maximum <= transformed_minimum)
        {
            transformed_minimum = values.Select(value => transform(platform, value)).Where(double.IsFinite).DefaultIfEmpty(0).Min();
            transformed_maximum = values.Select(value => transform(platform, value)).Where(double.IsFinite).DefaultIfEmpty(1).Max();
        }
        if (transformed_maximum <= transformed_minimum)
            transformed_maximum = transformed_minimum + 1;
        var x = new double[bins];
        var y = new double[bins];
        double width = (transformed_maximum - transformed_minimum) / bins;
        for (int bin = 0; bin < bins; bin++)
            x[bin] = inverse_transform(platform, transformed_minimum + (bin + 0.5) * width);
        foreach (double raw in values)
        {
            double value = transform(platform, raw);
            int bin = (int)Math.Floor((value - transformed_minimum) / width);
            if (bin >= 0 && bin < bins)
                y[bin]++;
        }
        double total = y.Sum();
        if (total > 0)
            for (int index = 0; index < y.Length; index++)
                y[index] /= total;
        return new PlatformPlotSeries
        {
            Key = $"observed:{source_id}",
            Title = source_label(platform, source_id),
            XLabel = platform.SelectedFeatureNames.FirstOrDefault() ?? "Intensity",
            YLabel = "Normalized frequency",
            SourceId = source_id,
            Role = PlatformSeriesRole.Observed,
            X = x,
            Y = y
        };
    }

    private static PlatformPlotSeries raw_x_series(Platform platform, PlatformPlotSeries series) => new()
    {
        Key = series.Key,
        Title = series.Title,
        XLabel = series.XLabel,
        YLabel = series.YLabel,
        SourceId = series.SourceId,
        Role = series.Role,
        X = series.X.Select(value => inverse_transform(platform, value)).ToArray(),
        Y = series.Y
    };

    private static bool source_visible(Platform platform, int source_id)
    {
        if (source_id < 0)
            return true;
        if (source_id >= platform.RowMap.Sources.Count)
            return false;
        var source = platform.RowMap.Sources[source_id];
        return platform.Populations.Any(row =>
            row.GroupId == source.GroupId && row.SampleId == source.SampleId && row.GateId == source.GateId &&
            row.Region == source.Region && row.IsSelected);
    }

    private static string source_label(Platform platform, int source_id)
    {
        if (source_id < 0 || source_id >= platform.RowMap.Sources.Count)
            return "Population";
        var source = platform.RowMap.Sources[source_id];
        var row = platform.Populations.FirstOrDefault(item =>
            item.GroupId == source.GroupId && item.SampleId == source.SampleId && item.GateId == source.GateId && item.Region == source.Region);
        return row?.DisplayName ?? $"Population {source_id + 1}";
    }

    private static double transform(Platform platform, double value) => platform.Axis.Transform switch
    {
        PlatformTransformationKind.Logicle => new LogicleTransform(platform.Axis.Logicle).Transform(value),
        PlatformTransformationKind.Logarithm => Math.Sign(value) * Math.Log10(1 + Math.Abs(value)),
        PlatformTransformationKind.Arcsinh => Math.Asinh(value / 5.0),
        _ => value
    };

    private static double inverse_transform(Platform platform, double value) => platform.Axis.Transform switch
    {
        PlatformTransformationKind.Logicle => new LogicleTransform(platform.Axis.Logicle).InverseTransform(value),
        PlatformTransformationKind.Logarithm => Math.Sign(value) * (Math.Pow(10, Math.Abs(value)) - 1),
        PlatformTransformationKind.Arcsinh => Math.Sinh(value) * 5.0,
        _ => value
    };
}
