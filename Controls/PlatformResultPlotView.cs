using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using gated.Models;

namespace gated.Controls;

public sealed class PlatformResultPlotView : Control
{
    public static readonly StyledProperty<Platform?> PlatformProperty =
        AvaloniaProperty.Register<PlatformResultPlotView, Platform?>(nameof(Platform));
    private Platform? subscribed_platform;
    private IntegrationJobPopulationSelection[] subscribed_rows = [];

    static PlatformResultPlotView()
    {
        AffectsRender<PlatformResultPlotView>(PlatformProperty);
    }

    public Platform? Platform
    {
        get => GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlatformProperty)
            resubscribe();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        var plot = new Rect(bounds.Left + 68, bounds.Top + 18, Math.Max(1, bounds.Width - 88), Math.Max(1, bounds.Height - 66));
        var axis_pen = new Pen(new SolidColorBrush(Color.FromRgb(166, 172, 184)), 1);
        var major_grid_pen = new Pen(new SolidColorBrush(Color.FromArgb(46, 124, 132, 148)), 1);
        var minor_grid_pen = new Pen(new SolidColorBrush(Color.FromArgb(26, 124, 132, 148)), 1);
        var tick_pen = new Pen(new SolidColorBrush(Color.FromRgb(146, 152, 164)), 1);
        context.DrawLine(axis_pen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        context.DrawLine(axis_pen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));

        var observed_series = Platform?.Kind == PlatformKind.Kinetics
            ? preview_kinetics_series(Platform)
            : preview_histogram_series(Platform);
        var fit_series = fit_curve_series(Platform);
        var scripted_model_series = scripted_model_plot_series(Platform);
        var series = observed_series.Concat(fit_series).Concat(scripted_model_series).ToArray();
        if (series.Length == 0)
            series = filter_visible_series(Platform, Platform?.PlotSeries.ToArray() ?? []);
        bool has_data = series.Length > 0;
        if (series.Length == 0)
            series = dummy_series(Platform);

        series = normalize_plot_series(Platform, series);
        var all_x = series.SelectMany(item => item.X).Where(double.IsFinite).ToArray();
        var all_y = series.SelectMany(item => item.Y).Where(double.IsFinite).Select(value => Math.Max(0, value)).ToArray();
        if (all_x.Length == 0 || all_y.Length == 0)
            return;
        var x_range = platform_x_range(Platform, all_x);
        double min_x = x_range.Minimum;
        double max_x = x_range.Maximum;
        double min_y = Math.Min(0, all_y.Min());
        double max_y = all_y.Max();
        if (max_x <= min_x) max_x = min_x + 1;
        if (max_y <= min_y) max_y = min_y + 1;
        max_y *= 1.08;

        foreach (double tick in minor_y_ticks(min_y, max_y))
        {
            double y = plot.Bottom - (tick - min_y) / (max_y - min_y) * plot.Height;
            context.DrawLine(minor_grid_pen, new Point(plot.Left, y), new Point(plot.Right, y));
            context.DrawLine(tick_pen, new Point(plot.Left - 4, y), new Point(plot.Left, y));
        }
        foreach (double tick in major_y_ticks(min_y, max_y))
        {
            double y = plot.Bottom - (tick - min_y) / (max_y - min_y) * plot.Height;
            context.DrawLine(major_grid_pen, new Point(plot.Left, y), new Point(plot.Right, y));
            context.DrawLine(tick_pen, new Point(plot.Left - 8, y), new Point(plot.Left, y));
            draw_right_aligned_text(context, tick.ToString("0.###", CultureInfo.InvariantCulture), new Point(plot.Left - 12, y - 8), 11, Color.FromRgb(176, 182, 192), false);
        }

        foreach (var tick in x_ticks(Platform, min_x, max_x, major: false))
        {
            double x = plot.Left + (tick.Position - min_x) / (max_x - min_x) * plot.Width;
            context.DrawLine(minor_grid_pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(tick_pen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 5));
        }
        foreach (var tick in x_ticks(Platform, min_x, max_x, major: true))
        {
            double x = plot.Left + (tick.Position - min_x) / (max_x - min_x) * plot.Width;
            context.DrawLine(major_grid_pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(tick_pen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 8));
            draw_centered_text(context, tick.Label, new Point(x, plot.Bottom + 11), 11, Color.FromRgb(176, 182, 192), false);
        }

        for (int series_index = 0; series_index < series.Length; series_index++)
        {
            if (!has_data)
                break;
            var item = series[series_index];
            int count = Math.Min(item.X.Length, item.Y.Length);
            var pen = plot_pen(Platform, item);
            Point? previous = null;
            for (int index = 0; index < count; index++)
            {
                if (!double.IsFinite(item.X[index]) || !double.IsFinite(item.Y[index]))
                    continue;
                var point = new Point(
                    plot.Left + (item.X[index] - min_x) / (max_x - min_x) * plot.Width,
                    plot.Bottom - (Math.Max(0, item.Y[index]) - min_y) / (max_y - min_y) * plot.Height);
                if (previous is not null)
                    context.DrawLine(pen, previous.Value, point);
                previous = point;
            }
        }

        draw_centered_text(context, series[0].XLabel, new Point(plot.Left + plot.Width / 2, bounds.Bottom - 18), 13, Color.FromRgb(218, 222, 230), false);
        draw_vertical_centered_text(context, Platform?.Kind == PlatformKind.Kinetics ? series[0].YLabel : "Frequency", new Point(bounds.Left + 18, plot.Top + plot.Height / 2), 13, Color.FromRgb(218, 222, 230), false);
    }

    public static PlatformPlotSeries[] CreateDisplaySeries(Platform? platform)
    {
        var observed_series = platform?.Kind == PlatformKind.Kinetics
            ? preview_kinetics_series(platform)
            : preview_histogram_series(platform);
        var fit_series = fit_curve_series(platform);
        var scripted_model_series = scripted_model_plot_series(platform);
        var series = observed_series.Concat(fit_series).Concat(scripted_model_series).ToArray();
        if (series.Length == 0)
            series = filter_visible_series(platform, platform?.PlotSeries.ToArray() ?? []);
        return normalize_plot_series(platform, series);
    }

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color, bool bold)
    {
        var formatted = create_text(text, size, color, bold);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private void draw_right_aligned_text(DrawingContext context, string text, Point origin, double size, Color color, bool bold)
    {
        var formatted = create_text(text, size, color, bold);
        context.DrawText(formatted, new Point(origin.X - formatted.Width, origin.Y));
    }

    private void draw_vertical_centered_text(DrawingContext context, string text, Point center, double size, Color color, bool bold)
    {
        var formatted = create_text(text, size, color, bold);
        var transform =
            Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(-Math.PI / 2)
            * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(transform))
            context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private FormattedText create_text(string text, double size, Color color, bool bold) =>
        new(
            text ?? "",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                TextElement.GetFontFamily(this),
                FontStyle.Normal,
                bold ? FontWeight.SemiBold : FontWeight.Normal),
            size,
            new SolidColorBrush(color));

    private static PlatformPlotSeries[] preview_histogram_series(Platform? platform)
    {
        if (platform is null || platform.Kind == PlatformKind.Integration)
            return [];
        var matrix = platform.Transformed ?? platform.Compensated;
        if (matrix is null || matrix.GetLength(0) == 0 || matrix.GetLength(1) == 0 || platform.RowMap.SourceIds.Length == 0)
            return [];

        string x_label = platform.SelectedFeatureNames.FirstOrDefault() ?? "Intensity";
        var selected_keys = platform.Populations
            .Where(row => row.IsPopulation && row.IsPlatformDropped && row.IsSelected)
            .Select(row => (row.GroupId, row.SampleId, row.GateId, row.Region))
            .ToHashSet();
        if (selected_keys.Count == 0)
            return [];

        var result = new List<PlatformPlotSeries>();
        for (int source_index = 0; source_index < platform.RowMap.Sources.Count; source_index++)
        {
            var source = platform.RowMap.Sources[source_index];
            if (!selected_keys.Contains((source.GroupId, source.SampleId, source.GateId, source.Region)))
                continue;
            var values = Enumerable.Range(0, platform.RowMap.SourceIds.Length)
                .Where(row => platform.RowMap.SourceIds[row] == source_index)
                .Select(row => (double)matrix[row, 0])
                .Where(double.IsFinite)
                .ToArray();
            if (values.Length == 0)
                continue;
            int bins = 400;
            var range = platform_x_range(platform, values);
            double minimum = range.Minimum;
            double maximum = range.Maximum;
            if (maximum <= minimum)
                maximum = minimum + 1;
            var counts = new double[bins];
            var centers = new double[bins];
            double width = (maximum - minimum) / bins;
            for (int bin = 0; bin < bins; bin++)
                centers[bin] = minimum + (bin + 0.5) * width;
            foreach (double value in values)
            {
                int bin = Math.Clamp((int)((value - minimum) / width), 0, bins - 1);
                counts[bin]++;
            }
            var smoothing = platform_smoothing(platform);
            if (smoothing.Enabled)
                counts = smooth(counts, smoothing.HalfWindow);
            counts = counts.Select(count => count / Math.Max(values.Length, 1)).ToArray();
            result.Add(new PlatformPlotSeries
            {
                Key = $"preview_{source_index}",
                Title = "Preview histogram",
                XLabel = x_label,
                YLabel = "Normalized frequency",
                X = centers,
                Y = counts
            });
        }

        return result.ToArray();
    }

    private static PlatformPlotSeries[] preview_kinetics_series(Platform? platform)
    {
        if (platform is not KineticsPlatform kinetics || platform.Compensated is null || platform.RowMap.SourceIds.Length == 0 || kinetics.TimeValues.Length != platform.RowMap.SourceIds.Length)
            return [];
        string y_label = platform.SelectedFeatureNames.FirstOrDefault() ?? "Signal";
        var selected_source_ids = platform.Populations
            .Where(row => row.IsPopulation && row.IsPlatformDropped && row.IsSelected)
            .Select(row => source_index_for_row(platform, row))
            .Where(index => index >= 0)
            .ToHashSet();
        if (selected_source_ids.Count == 0)
            return [];
        var result = new List<PlatformPlotSeries>();
        for (int source_index = 0; source_index < platform.RowMap.Sources.Count; source_index++)
        {
            if (!selected_source_ids.Contains(source_index))
                continue;
            var x_values = new List<double>();
            var y_values = new List<double>();
            for (int row = 0; row < platform.RowMap.SourceIds.Length; row++)
            {
                if (platform.RowMap.SourceIds[row] != source_index)
                    continue;
                double time = kinetics.TimeValues[row];
                double value = platform.Compensated[row, 0];
                if (double.IsFinite(time) && double.IsFinite(value))
                {
                    x_values.Add(time);
                    y_values.Add(value);
                }
            }
            if (x_values.Count == 0)
                continue;
            var order = x_values.Select((value, index) => (value, index)).OrderBy(item => item.value).ToArray();
            double min = order.First().value;
            double max = order.Last().value;
            if (max <= min)
                max = min + 1;
            int bins = Math.Clamp(kinetics.TimeWindowCount, 4, 1000);
            var centers = new double[bins];
            var sums = new double[bins];
            var counts = new int[bins];
            double width = (max - min) / bins;
            for (int bin = 0; bin < bins; bin++)
                centers[bin] = min + (bin + 0.5) * width;
            foreach (var item in order)
            {
                int bin = Math.Clamp((int)((item.value - min) / width), 0, bins - 1);
                sums[bin] += y_values[item.index];
                counts[bin]++;
            }
            var x = new List<double>();
            var y = new List<double>();
            for (int bin = 0; bin < bins; bin++)
            {
                if (counts[bin] == 0)
                    continue;
                x.Add(centers[bin]);
                y.Add(Math.Max(0, sums[bin] / counts[bin]));
            }
            if (x.Count > 0)
                result.Add(new PlatformPlotSeries { Key = $"trend_{source_index}", Title = "Kinetics preview", XLabel = "Time", YLabel = y_label, X = x.ToArray(), Y = y.ToArray() });
        }

        return result.ToArray();
    }

    private static PlatformPlotSeries[] dummy_series(Platform? platform)
    {
        double minimum = platform?.Kind == PlatformKind.Kinetics ? 0 : platform_x_range(platform, [0]).Minimum;
        double maximum = platform?.Kind == PlatformKind.Kinetics ? 1 : platform_x_range(platform, [1]).Maximum;
        return
        [
            new PlatformPlotSeries
            {
                Key = "dummy",
                XLabel = platform?.Kind == PlatformKind.Kinetics ? "Time" : platform?.SelectedFeatureNames.FirstOrDefault() ?? "Intensity",
                YLabel = platform?.Kind == PlatformKind.Kinetics ? platform.SelectedFeatureNames.FirstOrDefault() ?? "Signal" : "Frequency",
                X = [minimum, maximum],
                Y = [0, 1]
            }
        ];
    }

    private static PlatformPlotSeries[] normalize_plot_series(Platform? platform, PlatformPlotSeries[] series)
    {
        if (platform?.Kind == PlatformKind.Kinetics)
            return series;
        var all_x = series.SelectMany(item => item.X).Where(double.IsFinite).ToArray();
        var range = all_x.Length == 0 ? (0, 1) : platform_x_range(platform, all_x);
        return series.Select(item =>
        {
            var item_x = item.X;
            var item_y = item.Y;
            if (item_x.Length > 0 && item_y.Length > 0)
            {
                var x_values = item_x.ToList();
                var y_values = item_y.Select(value => double.IsFinite(value) ? Math.Max(0, value) : value).ToList();
                if (range.Item1 < x_values.First())
                {
                    x_values.Insert(0, range.Item1);
                    y_values.Insert(0, 0);
                }
                if (range.Item2 > x_values.Last())
                {
                    x_values.Add(range.Item2);
                    y_values.Add(0);
                }
                item_x = x_values.ToArray();
                item_y = y_values.ToArray();
            }
            if (string.Equals(item.YLabel, "Normalized frequency", StringComparison.OrdinalIgnoreCase))
                return new PlatformPlotSeries { Key = item.Key, Title = item.Title, XLabel = item.XLabel, YLabel = item.YLabel, X = item_x, Y = item_y };
            double total = item_y.Where(double.IsFinite).Sum();
            if (total <= 0)
                return new PlatformPlotSeries { Key = item.Key, Title = item.Title, XLabel = item.XLabel, YLabel = item.YLabel, X = item_x, Y = item_y };
            return new PlatformPlotSeries
            {
                Key = item.Key,
                Title = item.Title,
                XLabel = item.XLabel,
                YLabel = "Normalized frequency",
                X = item_x,
                Y = item_y.Select(value => double.IsFinite(value) ? value / total : value).ToArray()
            };
        }).ToArray();
    }

    private static double[] smooth(double[] values, int half_window)
    {
        if (half_window <= 0)
            return values.ToArray();
        var result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            int start = Math.Max(0, index - half_window);
            int end = Math.Min(values.Length - 1, index + half_window);
            double sum = 0;
            for (int current = start; current <= end; current++)
                sum += values[current];
            result[index] = sum / (end - start + 1);
        }
        return result;
    }

    private static PlatformSmoothingOptions platform_smoothing(Platform platform) =>
        platform switch
        {
            UnivariatePlatform univariate => univariate.Smoothing,
            BivariatePlatform bivariate => bivariate.Smoothing,
            _ => new PlatformSmoothingOptions()
        };

    private static bool is_component_series(string key) =>
        key.Contains("component", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("generation", StringComparison.OrdinalIgnoreCase);

    private static bool is_component_series(Platform? platform, string key) =>
        is_component_series(key) ||
        platform?.Components.Values.Any(curves => curves.Any(curve => curve.Key == key)) == true;

    private static bool is_model_sum_series(string key) =>
        key.StartsWith("fit_", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("_fit_", StringComparison.OrdinalIgnoreCase);

    private static bool is_model_sum_series(Platform? platform, string key) =>
        is_model_sum_series(key) ||
        platform?.Models.ContainsKey(key) == true;

    private static PlatformPlotSeries[] fit_curve_series(Platform? platform)
    {
        if (platform is null || platform.FitCurves.Count == 0)
            return [];

        var visible_source_ids = platform.Populations
            .Where(row => row.IsPopulation && row.IsPlatformDropped && row.IsSelected)
            .Select(row => source_index_for_row(platform, row))
            .Where(index => index >= 0)
            .ToHashSet();
        if (visible_source_ids.Count == 0)
            return [];

        bool draw_sum = platform.Kind switch
        {
            PlatformKind.CellCycle => platform is not CellCyclePlatform cell_cycle || cell_cycle.DrawModelSum,
            PlatformKind.Proliferation => platform is not ProliferationPlatform proliferation || proliferation.DrawModelSum,
            _ => true
        };
        bool draw_components = platform.Kind switch
        {
            PlatformKind.CellCycle => platform is not CellCyclePlatform cell_cycle || cell_cycle.DrawComponents,
            PlatformKind.Proliferation => platform is not ProliferationPlatform proliferation || proliferation.DrawComponents,
            _ => true
        };

        var result = new List<PlatformPlotSeries>();
        foreach (var curve in platform.FitCurves)
        {
            if (curve.SourceId >= 0 && !visible_source_ids.Contains(curve.SourceId))
                continue;
            if (is_component_series(platform, curve.Key) && !draw_components)
                continue;
            if (is_model_sum_series(platform, curve.Key) && !draw_sum)
                continue;

            var series = evaluate_fit_curve(platform, curve);
            if (series is not null)
                result.Add(series);
        }

        return result.ToArray();
    }

    private static PlatformPlotSeries[] scripted_model_plot_series(Platform? platform)
    {
        if (platform is null || platform.PlotSeries.Count == 0)
            return [];
        return filter_visible_series(platform, platform.PlotSeries.ToArray())
            .Where(item => is_model_sum_series(platform, item.Key) || is_component_series(platform, item.Key))
            .ToArray();
    }

    private static PlatformPlotSeries? evaluate_fit_curve(Platform platform, PlatformFitCurve curve)
    {
        int resolution = 400;
        double raw_minimum;
        double raw_maximum;
        if (platform.Kind == PlatformKind.Kinetics)
        {
            var domain = kinetics_curve_domain(curve, platform);
            raw_minimum = domain.Minimum;
            raw_maximum = domain.Maximum;
        }
        else
        {
            raw_minimum = platform.Axis.Minimum;
            raw_maximum = platform.Axis.Maximum;
        }
        if (!double.IsFinite(raw_minimum) || !double.IsFinite(raw_maximum) || raw_maximum <= raw_minimum)
            return null;

        var x = new double[resolution];
        var y = new double[resolution];
        var current_transform = platform.Axis.Transform == PlatformTransformationKind.Logicle
            ? new LogicleTransform(platform.Axis.Logicle)
            : null;
        var fit_transform = curve.FitTransformation == PlatformTransformationKind.Logicle
            ? new LogicleTransform(curve.FitLogicle)
            : null;
        for (int index = 0; index < resolution; index++)
        {
            double raw = raw_minimum + (raw_maximum - raw_minimum) * index / (resolution - 1);
            double fit_x = fit_transform?.Transform(raw) ?? raw;
            x[index] = platform.Kind == PlatformKind.Kinetics ? raw : current_transform?.Transform(raw) ?? raw;
            y[index] = evaluate_curve_y(platform, curve, fit_x);
        }

        return new PlatformPlotSeries
        {
            Key = curve.Key,
            Title = curve.Title,
            XLabel = curve.XLabel,
            YLabel = curve.YLabel,
            X = x,
            Y = y
        };
    }

    private static (double Minimum, double Maximum) kinetics_curve_domain(PlatformFitCurve curve, Platform platform)
    {
        if (curve.Parameters.Length >= 4 &&
            double.IsFinite(curve.Parameters[2]) &&
            double.IsFinite(curve.Parameters[3]) &&
            curve.Parameters[3] > curve.Parameters[2])
            return (curve.Parameters[2], curve.Parameters[3]);
        if (platform is KineticsPlatform kinetics && kinetics.TimeValues.Length > 0)
            return (kinetics.TimeValues.Min(), kinetics.TimeValues.Max());
        return (0, 1);
    }

    private static double evaluate_curve_y(Platform platform, PlatformFitCurve curve, double x)
    {
        double normalizer = curve.Normalizer > 0 ? curve.Normalizer : 1.0;
        return curve.Kind switch
        {
            PlatformFitCurveKind.Gaussian => gaussian_y(curve.Parameters, 0, x) / normalizer,
            PlatformFitCurveKind.GaussianSum => gaussian_sum_y(curve.Parameters, x) / normalizer,
            PlatformFitCurveKind.CellCycleSum => cell_cycle_sum_y(curve.Parameters, x) / normalizer,
            PlatformFitCurveKind.Linear => curve.Parameters.Length >= 2 ? curve.Parameters[0] * x + curve.Parameters[1] : 0,
            PlatformFitCurveKind.Exponential => exponential_y(curve.Parameters, x) / normalizer,
            PlatformFitCurveKind.Gamma => gamma_y(curve.Parameters, x) / normalizer,
            PlatformFitCurveKind.Addition => addition_y(platform, curve, x) / normalizer,
            _ => 0
        };
    }

    private static double addition_y(Platform platform, PlatformFitCurve curve, double x)
    {
        double result = curve.Intercept;
        int count = Math.Min(curve.ModelKeys.Length, curve.Weights.Length);
        for (int index = 0; index < count; index++)
        {
            string key = curve.ModelKeys[index];
            var model = platform.Models.TryGetValue(key, out var fit)
                ? fit
                : platform.Components.Values.SelectMany(item => item).FirstOrDefault(item => item.Key == key) ??
                  platform.FitCurves.FirstOrDefault(item => item.Key == key);
            if (model is null || ReferenceEquals(model, curve))
                continue;
            result += curve.Weights[index] * evaluate_curve_y(platform, model, x);
        }
        return result;
    }

    private static double exponential_y(double[] parameters, double x)
    {
        if (parameters.Length >= 3)
            return parameters[2] + parameters[1] * Math.Exp(parameters[0] * x);
        return parameters.Length >= 2 ? parameters[1] * Math.Exp(parameters[0] * x) : 0;
    }

    private static double gamma_y(double[] parameters, double x)
    {
        if (parameters.Length < 3 || x < 0)
            return 0;
        double alpha = parameters[0];
        double beta = parameters[1];
        double amplitude = parameters[2];
        if (!double.IsFinite(alpha) || !double.IsFinite(beta) || alpha <= 0 || beta <= 0)
            return 0;
        if (x == 0)
            return alpha == 1 ? amplitude / beta : 0;
        double log_density = (alpha - 1) * Math.Log(x) - x / beta - log_gamma(alpha) - alpha * Math.Log(beta);
        return amplitude * Math.Exp(log_density);
    }

    private static double log_gamma(double z)
    {
        double[] coefficients =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];
        if (z < 0.5)
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * z)) - log_gamma(1 - z);
        z -= 1;
        double x = 0.99999999999980993;
        for (int index = 0; index < coefficients.Length; index++)
            x += coefficients[index] / (z + index + 1);
        double t = z + coefficients.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    private static double gaussian_sum_y(double[] parameters, double x)
    {
        double result = 0;
        for (int index = 0; index + 2 < parameters.Length; index += 3)
            result += gaussian_y(parameters, index, x);
        return result;
    }

    private static double cell_cycle_sum_y(double[] parameters, double x)
    {
        if (parameters.Length < 7)
            return 0;
        double g1 = gaussian_y(parameters, 0, x);
        double g2 = gaussian_y(parameters, 3, x);
        double m1 = parameters[1];
        double m2 = parameters[4];
        double s_amp = Math.Max(0, parameters[6]);
        double bridge = 0;
        if (m2 > m1 && x >= m1 && x <= m2)
            bridge = (x - m1) / (m2 - m1) * s_amp;
        return g1 + g2 + bridge;
    }

    private static double gaussian_y(double[] parameters, int offset, double x)
    {
        if (parameters.Length <= offset + 2)
            return 0;
        double sigma = Math.Max(Math.Abs(parameters[offset + 2]), 1e-9);
        double z = (x - parameters[offset + 1]) / sigma;
        return parameters[offset] * Math.Exp(-0.5 * z * z);
    }

    private static Pen plot_pen(Platform? platform, PlatformPlotSeries item)
    {
        Color color = PlatformPalette.ColorForSeriesKey(item.Key);
        if (is_model_sum_series(platform, item.Key))
            color = Color.FromRgb(
                (byte)Math.Max(0, color.R - 42),
                (byte)Math.Max(0, color.G - 42),
                (byte)Math.Max(0, color.B - 42));
        var pen = new Pen(
            new SolidColorBrush(color),
            is_component_series(platform, item.Key) ? 1.6 : is_model_sum_series(platform, item.Key) ? 2.7 : 1.8);
        if (is_component_series(platform, item.Key))
            pen.DashStyle = DashStyle.Dash;
        return pen;
    }

    private static PlatformPlotSeries[] filter_visible_series(Platform? platform, PlatformPlotSeries[] series)
    {
        if (platform is null || series.Length == 0)
            return series;

        var visible_source_ids = platform.Populations
            .Where(row => row.IsPopulation && row.IsPlatformDropped && row.IsSelected)
            .Select(row => source_index_for_row(platform, row))
            .Where(index => index >= 0)
            .ToHashSet();
        if (visible_source_ids.Count == 0)
            return [];

        bool draw_sum = platform.Kind switch
        {
            PlatformKind.CellCycle => platform is not CellCyclePlatform cell_cycle || cell_cycle.DrawModelSum,
            PlatformKind.Proliferation => platform is not ProliferationPlatform proliferation || proliferation.DrawModelSum,
            _ => true
        };
        bool draw_components = platform.Kind switch
        {
            PlatformKind.CellCycle => platform is not CellCyclePlatform cell_cycle || cell_cycle.DrawComponents,
            PlatformKind.Proliferation => platform is not ProliferationPlatform proliferation || proliferation.DrawComponents,
            _ => true
        };

        return series
            .Where(item =>
            {
                int source_id = source_index_from_series_key(item.Key);
                if (source_id >= 0 && !visible_source_ids.Contains(source_id))
                    return false;
                if (is_component_series(platform, item.Key))
                    return draw_components;
                if (is_model_sum_series(platform, item.Key))
                    return draw_sum;
                return true;
            })
            .ToArray();
    }

    private static int source_index_for_row(Platform platform, IntegrationJobPopulationSelection row)
    {
        for (int index = 0; index < platform.RowMap.Sources.Count; index++)
        {
            var source = platform.RowMap.Sources[index];
            if (source.GroupId == row.GroupId &&
                source.SampleId == row.SampleId &&
                source.GateId == row.GateId &&
                source.Region == row.Region)
                return index;
        }

        return -1;
    }

    private static int source_index_from_series_key(string key)
    {
        int underscore = key.LastIndexOf('_');
        if (underscore < 0 || underscore == key.Length - 1)
            return -1;
        return int.TryParse(key[(underscore + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : -1;
    }

    private static (double Minimum, double Maximum) platform_x_range(Platform? platform, double[] all_x)
    {
        if (platform?.Kind == PlatformKind.Kinetics)
            return (all_x.Min(), all_x.Max());
        double raw_maximum = platform?.Axis.Maximum > 0 ? platform.Axis.Maximum : all_x.Max();
        double raw_minimum = platform is null ? all_x.Min() : platform.Axis.Minimum;
        if (platform?.Axis.Transform == PlatformTransformationKind.Logicle)
        {
            var transform = new LogicleTransform(platform.Axis.Logicle);
            return (transform.Transform(raw_minimum), transform.Transform(raw_maximum));
        }

        return (raw_minimum, raw_maximum);
    }

    private static IEnumerable<double> major_y_ticks(double minimum, double maximum)
    {
        var axis = new AxisSettings { Minimum = minimum, Maximum = maximum, ScaleKind = CoordinateScaleKind.Linear };
        return Configuration.MajorAxisTicks(axis);
    }

    private static IEnumerable<double> minor_y_ticks(double minimum, double maximum)
    {
        var axis = new AxisSettings { Minimum = minimum, Maximum = maximum, ScaleKind = CoordinateScaleKind.Linear };
        return Configuration.MinorAxisTicks(axis);
    }

    private static IEnumerable<(double Position, string Label)> x_ticks(Platform? platform, double minimum, double maximum, bool major)
    {
        if (platform?.Axis.Transform == PlatformTransformationKind.Logicle)
        {
            var transform = new LogicleTransform(platform.Axis.Logicle);
            var axis = new AxisSettings
            {
                Minimum = platform.Axis.Minimum,
                Maximum = platform.Axis.Maximum,
                ScaleKind = CoordinateScaleKind.Logicle
            };
            var ticks = major ? Configuration.MajorAxisTicks(axis) : Configuration.MinorAxisTicks(axis);
            foreach (double raw in ticks)
            {
                double transformed = transform.Transform(raw);
                if (transformed >= minimum && transformed <= maximum)
                    yield return (transformed, Configuration.FormatAxisValue(raw));
            }
            yield break;
        }

        var linear_axis = new AxisSettings { Minimum = minimum, Maximum = maximum, ScaleKind = CoordinateScaleKind.Linear };
        var linear_ticks = major ? Configuration.MajorAxisTicks(linear_axis) : Configuration.MinorAxisTicks(linear_axis);
        foreach (double value in linear_ticks)
            yield return (value, Configuration.FormatAxisValue(value));
    }

    private void resubscribe()
    {
        if (subscribed_platform is not null)
        {
            subscribed_platform.PropertyChanged -= platform_changed;
            subscribed_platform.PlotSeries.CollectionChanged -= collection_changed;
            subscribed_platform.FitCurves.CollectionChanged -= collection_changed;
            subscribed_platform.Populations.CollectionChanged -= collection_changed;
            foreach (var row in subscribed_rows)
                row.PropertyChanged -= row_changed;
        }
        subscribed_platform = Platform;
        subscribed_rows = [];
        if (subscribed_platform is not null)
        {
            subscribed_platform.PropertyChanged += platform_changed;
            subscribed_platform.PlotSeries.CollectionChanged += collection_changed;
            subscribed_platform.FitCurves.CollectionChanged += collection_changed;
            subscribed_platform.Populations.CollectionChanged += collection_changed;
            subscribed_rows = subscribed_platform.Populations.ToArray();
            foreach (var row in subscribed_rows)
                row.PropertyChanged += row_changed;
        }
        invalidate_on_ui_thread();
    }

    private void platform_changed(object? sender, PropertyChangedEventArgs e) => invalidate_on_ui_thread();

    private void collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, subscribed_platform?.Populations))
            resubscribe();
        else
            invalidate_on_ui_thread();
    }

    private void row_changed(object? sender, PropertyChangedEventArgs e) => invalidate_on_ui_thread();

    private void invalidate_on_ui_thread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
            return;
        }

        Dispatcher.UIThread.Post(InvalidateVisual);
    }
}
