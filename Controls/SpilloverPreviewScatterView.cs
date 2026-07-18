using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using gated.Models;
using gated.ViewModels;

namespace gated.Controls;

public sealed class SpilloverPreviewScatterView : Control
{
    public static readonly StyledProperty<SpilloverPreviewCell?> CellProperty =
        AvaloniaProperty.Register<SpilloverPreviewScatterView, SpilloverPreviewCell?>(nameof(Cell));

    private Rect plot_rect;

    static SpilloverPreviewScatterView()
    {
        AffectsRender<SpilloverPreviewScatterView>(CellProperty);
    }

    public SpilloverPreviewCell? Cell
    {
        get => GetValue(CellProperty);
        set => SetValue(CellProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 150 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(Brushes.Transparent, bounds);

        const double left_axis_space = 14;
        const double right_space = 4;
        const double top_space = 4;
        const double bottom_axis_space = 14;
        double size = Math.Max(1, Math.Min(
            bounds.Width - left_axis_space - right_space,
            bounds.Height - top_space - bottom_axis_space));
        plot_rect = new Rect(
            bounds.Left + left_axis_space + Math.Max(0, bounds.Width - left_axis_space - right_space - size) / 2,
            bounds.Top + top_space,
            size,
            size);

        if (Cell is not { } cell)
        {
            context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background4")), plot_rect);
            draw_axes(context, null);
            return;
        }

        context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")), plot_rect);
        draw_grid(context, cell);
        draw_points(context, cell);
        draw_fit(context, cell);
        draw_axes(context, cell);
    }

    private void draw_grid(DrawingContext context, SpilloverPreviewCell cell)
    {
        var major = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMajor")), 1);
        var minor = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMinor")), 1);
        foreach (double value in minor_axis_ticks(x_axis(cell)))
        {
            double x = data_to_screen_x(cell, value);
            context.DrawLine(minor, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }
        foreach (double value in major_axis_ticks(x_axis(cell)))
        {
            double x = data_to_screen_x(cell, value);
            context.DrawLine(major, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }
        foreach (double value in minor_axis_ticks(y_axis(cell)))
        {
            double y = data_to_screen_y(cell, value);
            context.DrawLine(minor, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
        }
        foreach (double value in major_axis_ticks(y_axis(cell)))
        {
            double y = data_to_screen_y(cell, value);
            context.DrawLine(major, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
        }
    }

    private void draw_points(DrawingContext context, SpilloverPreviewCell cell)
    {
        if (cell.Points.Count == 0)
            return;

        int step = Math.Max(1, cell.Points.Count / 2200);
        var brush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayStrong"));
        for (int index = 0; index < cell.Points.Count; index += step)
        {
            var point = data_to_screen(cell, cell.Points[index].X, cell.Points[index].Y);
            if (plot_rect.Contains(point))
                context.FillRectangle(brush, new Rect(point.X, point.Y, 1.1, 1.1));
        }
    }

    private void draw_fit(DrawingContext context, SpilloverPreviewCell cell)
    {
        if (cell.FitLine is not { } fit)
            return;

        var geometry = new StreamGeometry();
        bool has_started = false;
        using (var stream = geometry.Open())
        {
            for (int index = 0; index <= 80; index++)
            {
                double x = cell.XMinimum + (cell.XMaximum - cell.XMinimum) * index / 80.0;
                double y = fit.Intercept + fit.Slope * x;
                if (!double.IsFinite(y))
                    continue;
                var point = data_to_screen(cell, x, y);
                if (!has_started)
                {
                    stream.BeginFigure(point, false);
                    has_started = true;
                }
                else
                {
                    stream.LineTo(point);
                }
            }
        }

        if (has_started)
            context.DrawGeometry(null, new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayDanger")), 2.2), geometry);
    }

    private void draw_axes(DrawingContext context, SpilloverPreviewCell? cell)
    {
        var pen = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text5")), 1);
        context.DrawLine(pen, new Point(plot_rect.Left, plot_rect.Bottom), new Point(plot_rect.Right, plot_rect.Bottom));
        context.DrawLine(pen, new Point(plot_rect.Left, plot_rect.Top), new Point(plot_rect.Left, plot_rect.Bottom));
        if (cell is null)
            return;

        var text = gated.Shared.ThemeResources.AppColor("Text5");
        foreach (double value in minor_axis_ticks(x_axis(cell)))
        {
            double x = data_to_screen_x(cell, value);
            context.DrawLine(pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 3));
        }
        foreach (double value in major_axis_ticks(x_axis(cell)))
        {
            double x = data_to_screen_x(cell, value);
            context.DrawLine(pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 6));
        }
        foreach (double value in minor_axis_ticks(y_axis(cell)))
        {
            double y = data_to_screen_y(cell, value);
            context.DrawLine(pen, new Point(plot_rect.Left - 3, y), new Point(plot_rect.Left, y));
        }
        foreach (double value in major_axis_ticks(y_axis(cell)))
        {
            double y = data_to_screen_y(cell, value);
            var formatted = create_text(Configuration.FormatAxisValue(value), 7.5, text);
            context.DrawLine(pen, new Point(plot_rect.Left - 6, y), new Point(plot_rect.Left, y));
        }
    }

    private Point data_to_screen(SpilloverPreviewCell cell, double x, double y)
    {
        return new Point(data_to_screen_x(cell, x), data_to_screen_y(cell, y));
    }

    private double data_to_screen_x(SpilloverPreviewCell cell, double x) =>
        plot_rect.Left + normalize(x, cell.XMinimum, cell.XMaximum, cell.XScale) * plot_rect.Width;

    private double data_to_screen_y(SpilloverPreviewCell cell, double y) =>
        plot_rect.Bottom - normalize(y, cell.YMinimum, cell.YMaximum, cell.YScale) * plot_rect.Height;

    private static double normalize(double value, double minimum, double maximum, AxisScale scale)
    {
        double transformed_minimum = scale.Transform(minimum);
        double transformed_maximum = scale.Transform(maximum);
        double transformed = scale.Transform(value);
        if (!double.IsFinite(transformed_minimum) ||
            !double.IsFinite(transformed_maximum) ||
            !double.IsFinite(transformed) ||
            transformed_maximum <= transformed_minimum)
            return 0;
        return Math.Clamp((transformed - transformed_minimum) / (transformed_maximum - transformed_minimum), 0, 1);
    }

    private static AxisSettings x_axis(SpilloverPreviewCell cell) =>
        new()
        {
            ChannelName = cell.XChannel,
            Minimum = cell.XMinimum,
            Maximum = cell.XMaximum,
            Scale = cell.XScale.Clone()
        };

    private static AxisSettings y_axis(SpilloverPreviewCell cell) =>
        new()
        {
            ChannelName = cell.YChannel,
            Minimum = cell.YMinimum,
            Maximum = cell.YMaximum,
            Scale = cell.YScale.Clone()
        };

    private static double[] major_axis_ticks(AxisSettings axis) =>
        Configuration.MajorAxisTicks(axis)
            .Where(value => value >= axis.Minimum && value <= axis.Maximum)
            .ToArray();

    private static IEnumerable<double> minor_axis_ticks(AxisSettings axis)
    {
        var major = major_axis_ticks(axis);
        foreach (double value in Configuration.MinorAxisTicks(axis))
            if (value >= axis.Minimum && value <= axis.Maximum && !major.Any(item => Math.Abs(item - value) < 1e-9))
                yield return value;
    }

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private FormattedText create_text(string text, double size, Color color) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this)),
            size,
            new SolidColorBrush(color));
}
