using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using gated.Models;

namespace gated.Controls;

public sealed class TimeDynamicsView : Control
{
    private const double tick_font_size = 11;
    private const double axis_title_font_size = 12;
    private const double legend_font_size = 11;
    private const double minimum_tick_spacing = 32;
    private static readonly Color[] light_theme_series_colors =
    [
        Color.FromRgb(91, 58, 158), Color.FromRgb(8, 126, 164), Color.FromRgb(46, 125, 50),
        Color.FromRgb(199, 91, 18), Color.FromRgb(166, 27, 60), Color.FromRgb(90, 103, 33)
    ];
    private static readonly Color[] dark_theme_series_colors =
    [
        Color.FromRgb(184, 154, 244), Color.FromRgb(78, 201, 232), Color.FromRgb(111, 209, 122),
        Color.FromRgb(242, 166, 90), Color.FromRgb(240, 120, 147), Color.FromRgb(199, 211, 103)
    ];

    public static readonly StyledProperty<MassTimeDynamicsData?> DataProperty =
        AvaloniaProperty.Register<TimeDynamicsView, MassTimeDynamicsData?>(nameof(Data));

    static TimeDynamicsView() => AffectsRender<TimeDynamicsView>(DataProperty);

    public MassTimeDynamicsData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double left_axis_space = 72;
        const double right_space = 18;
        const double top_space = 34;
        const double bottom_axis_space = 42;
        var plot = new Rect(
            left_axis_space,
            top_space,
            Math.Max(1, Bounds.Width - left_axis_space - right_space),
            Math.Max(1, Bounds.Height - top_space - bottom_axis_space));
        var axis = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text5")), 1);
        var major_grid = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMajor")), 1);
        var minor_grid = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMinor")), 1);
        var tick_brush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text5"));
        var title_brush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text1"));
        context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")), plot);

        if (Data is { Series.Count: > 0 } data && data.MaximumTime > data.MinimumTime)
        {
            double maximum = Math.Max(1, data.MaximumIntensity);
            TickSet x_ticks = linear_ticks(data.MinimumTime, data.MaximumTime, plot.Width);
            TickSet y_ticks = logarithmic_ticks(maximum, plot.Height);

            foreach (double tick in x_ticks.Minor)
            {
                double x = time_x(plot, tick, data.MinimumTime, data.MaximumTime);
                context.DrawLine(minor_grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
            foreach (double tick in y_ticks.Minor)
            {
                double y = intensity_y(plot, tick, maximum);
                context.DrawLine(minor_grid, new Point(plot.Left, y), new Point(plot.Right, y));
            }
            foreach (double tick in x_ticks.Major)
            {
                double x = time_x(plot, tick, data.MinimumTime, data.MaximumTime);
                context.DrawLine(major_grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
            foreach (double tick in y_ticks.Major)
            {
                double y = intensity_y(plot, tick, maximum);
                context.DrawLine(major_grid, new Point(plot.Left, y), new Point(plot.Right, y));
            }

            for (int series_index = 0; series_index < data.Series.Count; series_index++)
            {
                var series = data.Series[series_index];
                Color color = series_color(series_index, data.Series.Count);
                var raw_pen = new Pen(new SolidColorBrush(color), 2.2, lineCap: PenLineCap.Round);
                draw_line(context, plot, raw_pen, series.Times, series.RawMedians, data.MinimumTime, data.MaximumTime, maximum);
                var normalized_pen = new Pen(new SolidColorBrush(Color.FromArgb(115, color.R, color.G, color.B)), 1.2, dashStyle: DashStyle.Dash);
                draw_line(context, plot, normalized_pen, series.Times, series.NormalizedMedians, data.MinimumTime, data.MaximumTime, maximum);
            }
            draw_legend(context, plot, data, title_brush);

            context.DrawLine(axis, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            context.DrawLine(axis, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
            draw_ticks(context, plot, axis, tick_brush, data, maximum, x_ticks, y_ticks);
        }
        else
        {
            context.DrawLine(axis, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            context.DrawLine(axis, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        }
        var x_title = formatted("Time", axis_title_font_size, title_brush);
        context.DrawText(x_title, new Point(plot.Center.X - x_title.Width / 2, Bounds.Height - 6 - x_title.Height / 2));
        draw_vertical_centered_text(context, "Bead intensity", new Point(20, plot.Center.Y), axis_title_font_size, title_brush);
    }

    private void draw_ticks(DrawingContext context, Rect plot, Pen pen, IBrush brush, MassTimeDynamicsData data,
        double maximum, TickSet x_ticks, TickSet y_ticks)
    {
        foreach (double tick in x_ticks.Minor)
        {
            double x = time_x(plot, tick, data.MinimumTime, data.MaximumTime);
            context.DrawLine(pen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 3));
        }
        foreach (double tick in x_ticks.Major)
        {
            double x = time_x(plot, tick, data.MinimumTime, data.MaximumTime);
            context.DrawLine(pen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 5));
            draw_centered_text(context, Configuration.FormatAxisValue(tick), new Point(x, plot.Bottom + 15), tick_font_size, brush, plot.Left, plot.Right);
        }
        foreach (double tick in y_ticks.Minor)
        {
            double y = intensity_y(plot, tick, maximum);
            context.DrawLine(pen, new Point(plot.Left - 3, y), new Point(plot.Left, y));
        }
        foreach (double tick in y_ticks.Major)
        {
            double y = intensity_y(plot, tick, maximum);
            context.DrawLine(pen, new Point(plot.Left - 5, y), new Point(plot.Left, y));
            draw_right_text(context, Configuration.FormatAxisValue(tick), new Point(plot.Left - 10, y), tick_font_size, brush);
        }
    }

    private void draw_legend(DrawingContext context, Rect plot, MassTimeDynamicsData data, IBrush brush)
    {
        string[] labels = data.Series.Select(series => $"{series.MassNumber} {series.ChannelName}").ToArray();
        double total_width = legend_width(labels, brush);
        if (total_width > plot.Width)
        {
            labels = data.Series.Select(series => series.MassNumber.ToString(CultureInfo.CurrentCulture)).ToArray();
            total_width = legend_width(labels, brush);
        }
        double x = Math.Max(plot.Left, plot.Center.X - total_width / 2);
        const double top = 7;
        for (int index = 0; index < labels.Length; index++)
        {
            var label = formatted(labels[index], legend_font_size, brush);
            Color color = series_color(index, labels.Length);
            double line_y = top + label.Height / 2;
            context.DrawLine(new Pen(new SolidColorBrush(color), 2.2, lineCap: PenLineCap.Round),
                new Point(x, line_y), new Point(x + 14, line_y));
            x += 19;
            context.DrawText(formatted(labels[index], legend_font_size, new SolidColorBrush(color)), new Point(x, top));
            x += label.Width + 12;
        }
    }

    private double legend_width(IEnumerable<string> labels, IBrush brush) =>
        labels.Sum(label => 19 + formatted(label, legend_font_size, brush).Width + 12);

    private static TickSet linear_ticks(double minimum, double maximum, double width)
    {
        int target_count = Math.Clamp((int)Math.Floor(width / 76), 2, 10);
        double rough_step = (maximum - minimum) / Math.Max(1, target_count - 1);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough_step)));
        double normalized = rough_step / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        double step = nice * magnitude;
        double first = Math.Ceiling(minimum / step) * step;
        var major = new List<double>();
        for (double value = first; value <= maximum + step * 1e-9 && major.Count < 100; value += step)
            major.Add(Math.Abs(value) < step * 1e-9 ? 0 : value);
        if (major.Count == 0) major.Add(minimum);

        int subdivisions = width / Math.Max(1, major.Count - 1) >= 120 ? 5 : 2;
        double minor_step = step / subdivisions;
        double first_minor = Math.Ceiling(minimum / minor_step) * minor_step;
        var minor = new List<double>();
        for (double value = first_minor; value <= maximum + minor_step * 1e-9 && minor.Count < 500; value += minor_step)
            if (!major.Any(item => Math.Abs(item - value) < minor_step * 0.01))
                minor.Add(Math.Abs(value) < minor_step * 1e-9 ? 0 : value);
        return new TickSet(major, minor);
    }

    private static TickSet logarithmic_ticks(double maximum, double height)
    {
        var candidates = new List<double> { 0 };
        for (double value = 1; value < maximum && double.IsFinite(value); value *= 10)
            candidates.Add(value);
        if (candidates.Count == 0 || Math.Abs(candidates[^1] - maximum) > maximum * 1e-9)
            candidates.Add(maximum);

        var major = new List<double>();
        double previous_y = double.PositiveInfinity;
        foreach (double value in candidates.Take(candidates.Count - 1))
        {
            double y = (1 - log_fraction(value, maximum)) * height;
            if (major.Count == 0 || Math.Abs(y - previous_y) >= minimum_tick_spacing)
            {
                major.Add(value);
                previous_y = y;
            }
        }
        double maximum_y = 0;
        if (major.Count > 1 && Math.Abs(maximum_y - previous_y) < minimum_tick_spacing)
            major.RemoveAt(major.Count - 1);
        major.Add(maximum);

        double pixels_per_decade = height / Math.Max(1, Math.Log10(1 + maximum));
        int[] multipliers = pixels_per_decade >= 72 ? [2, 3, 4, 5, 6, 7, 8, 9]
            : pixels_per_decade >= 34 ? [2, 5]
            : pixels_per_decade >= 20 ? [5]
            : [];
        var minor = new List<double>();
        for (double decade = 1; decade < maximum && double.IsFinite(decade); decade *= 10)
            foreach (int multiplier in multipliers)
            {
                double value = decade * multiplier;
                if (value >= maximum) break;
                if (!major.Any(item => Math.Abs(item - value) < value * 1e-9)) minor.Add(value);
            }
        return new TickSet(major, minor);
    }

    private static void draw_line(DrawingContext context, Rect plot, Pen pen, IReadOnlyList<double> times,
        IReadOnlyList<double> values, double minimum_time, double maximum_time, double maximum_value)
    {
        Point? previous = null;
        for (int index = 0; index < Math.Min(times.Count, values.Count); index++)
        {
            if (!double.IsFinite(times[index]) || !double.IsFinite(values[index])) continue;
            var point = new Point(
                time_x(plot, times[index], minimum_time, maximum_time),
                intensity_y(plot, values[index], maximum_value));
            if (previous is { } from) context.DrawLine(pen, from, point);
            previous = point;
        }
    }

    private static double time_x(Rect plot, double value, double minimum, double maximum) =>
        plot.Left + (value - minimum) / (maximum - minimum) * plot.Width;

    private static double intensity_y(Rect plot, double value, double maximum) =>
        plot.Bottom - log_fraction(value, maximum) * plot.Height;

    private static double log_fraction(double value, double maximum) =>
        Math.Clamp(Math.Log10(1 + Math.Max(0, value)) / Math.Log10(1 + Math.Max(1, maximum)), 0, 1);

    private FormattedText formatted(string value, double size, IBrush brush) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this)), size, brush);

    private void draw_centered_text(DrawingContext context, string value, Point point, double size, IBrush brush, double left, double right)
    {
        var text = formatted(value, size, brush);
        double x = Math.Clamp(point.X - text.Width / 2, left, Math.Max(left, right - text.Width));
        context.DrawText(text, new Point(x, point.Y - text.Height / 2));
    }

    private void draw_right_text(DrawingContext context, string value, Point point, double size, IBrush brush)
    {
        var text = formatted(value, size, brush);
        context.DrawText(text, new Point(point.X - text.Width, point.Y - text.Height / 2));
    }

    private void draw_vertical_centered_text(DrawingContext context, string value, Point center, double size, IBrush brush)
    {
        var text = formatted(value, size, brush);
        using (context.PushTransform(Matrix.CreateTranslation(-text.Width / 2, -text.Height / 2) *
                                     Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
            context.DrawText(text, new Point());
    }

    private static Color series_color(int index, int count)
    {
        Color background = gated.Shared.ThemeResources.AppColor("Background3");
        double luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
        Color[] palette = luminance >= 0.5 ? light_theme_series_colors : dark_theme_series_colors;
        if (count <= palette.Length) return palette[index % palette.Length];
        return palette[index % palette.Length];
    }

    private sealed record TickSet(IReadOnlyList<double> Major, IReadOnlyList<double> Minor);
}
