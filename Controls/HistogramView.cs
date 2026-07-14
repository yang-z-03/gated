using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using gated.Models;

namespace gated.Controls;

public enum HistogramAxisScaleKind
{
    Linear,
    Log,
    Logicle,
    Arcsinh
}

public sealed record HistogramRangeSelection(double Minimum, double Maximum);

public sealed class HistogramSeries : NotifyBase
{
    private IReadOnlyList<double> values = [];
    private IReadOnlyList<double>? sorted_values;
    private int bin_count = 256;
    private Color color = Color.FromRgb(20, 133, 255);
    private string name = "";

    public IReadOnlyList<double> Values
    {
        get => values;
        set
        {
            if (SetField(ref values, value ?? []))
                SortedValues = null;
        }
    }

    public IReadOnlyList<double>? SortedValues
    {
        get => sorted_values;
        set => SetField(ref sorted_values, value);
    }

    public int BinCount
    {
        get => bin_count;
        set => SetField(ref bin_count, Math.Max(1, value));
    }

    public Color Color
    {
        get => color;
        set => SetField(ref color, value);
    }

    public string Name
    {
        get => name;
        set => SetField(ref name, value ?? "");
    }
}

public sealed class HistogramView : Control
{
    public static readonly StyledProperty<IReadOnlyList<HistogramSeries>?> SeriesProperty =
        AvaloniaProperty.Register<HistogramView, IReadOnlyList<HistogramSeries>?>(nameof(Series));

    public static readonly StyledProperty<double?> MinimumProperty =
        AvaloniaProperty.Register<HistogramView, double?>(nameof(Minimum));

    public static readonly StyledProperty<double?> MaximumProperty =
        AvaloniaProperty.Register<HistogramView, double?>(nameof(Maximum));

    public static readonly StyledProperty<HistogramAxisScaleKind> AxisScaleProperty =
        AvaloniaProperty.Register<HistogramView, HistogramAxisScaleKind>(nameof(AxisScale), HistogramAxisScaleKind.Linear);

    public static readonly StyledProperty<double> LogicleTopOfScaleProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleTopOfScale), new LogicleParameters().T);

    public static readonly StyledProperty<double> LogicleLinearizationWidthProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleLinearizationWidth), new LogicleParameters().W);

    public static readonly StyledProperty<double> LogicleDecadesProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleDecades), new LogicleParameters().M);

    public static readonly StyledProperty<double> LogicleNegativeDecadesProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleNegativeDecades), new LogicleParameters().A);

    public static readonly StyledProperty<double> ArcsinhCofactorProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(ArcsinhCofactor), 5.0);

    public static readonly StyledProperty<int> BinSmoothingProperty =
        AvaloniaProperty.Register<HistogramView, int>(nameof(BinSmoothing), 2);

    public static readonly StyledProperty<double> YMaximumFactorProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(YMaximumFactor), 1.1);

    public static readonly StyledProperty<bool> IsGatingEnabledProperty =
        AvaloniaProperty.Register<HistogramView, bool>(nameof(IsGatingEnabled));

    public static readonly StyledProperty<HistogramRangeSelection?> SelectionProperty =
        AvaloniaProperty.Register<HistogramView, HistogramRangeSelection?>(nameof(Selection), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private Rect plot_rect;
    private List<PreparedSeries>? prepared_series;
    private PreparedAxis? prepared_axis;
    private INotifyCollectionChanged? subscribed_series_collection;
    private readonly List<HistogramSeries> subscribed_series = new();
    private DragTarget drag_target = DragTarget.None;
    private HistogramRangeSelection? draft_selection;

    static HistogramView()
    {
        AffectsRender<HistogramView>(
            SeriesProperty,
            MinimumProperty,
            MaximumProperty,
            AxisScaleProperty,
            LogicleTopOfScaleProperty,
            LogicleLinearizationWidthProperty,
            LogicleDecadesProperty,
            LogicleNegativeDecadesProperty,
            ArcsinhCofactorProperty,
            BinSmoothingProperty,
            YMaximumFactorProperty,
            IsGatingEnabledProperty,
            SelectionProperty);
    }

    public IReadOnlyList<HistogramSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double? Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double? Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public HistogramAxisScaleKind AxisScale
    {
        get => GetValue(AxisScaleProperty);
        set => SetValue(AxisScaleProperty, value);
    }

    public double LogicleTopOfScale
    {
        get => GetValue(LogicleTopOfScaleProperty);
        set => SetValue(LogicleTopOfScaleProperty, value);
    }

    public double LogicleLinearizationWidth
    {
        get => GetValue(LogicleLinearizationWidthProperty);
        set => SetValue(LogicleLinearizationWidthProperty, value);
    }

    public double LogicleDecades
    {
        get => GetValue(LogicleDecadesProperty);
        set => SetValue(LogicleDecadesProperty, value);
    }

    public double LogicleNegativeDecades
    {
        get => GetValue(LogicleNegativeDecadesProperty);
        set => SetValue(LogicleNegativeDecadesProperty, value);
    }

    public double ArcsinhCofactor
    {
        get => GetValue(ArcsinhCofactorProperty);
        set => SetValue(ArcsinhCofactorProperty, value);
    }

    public int BinSmoothing
    {
        get => GetValue(BinSmoothingProperty);
        set => SetValue(BinSmoothingProperty, value);
    }

    public double YMaximumFactor
    {
        get => GetValue(YMaximumFactorProperty);
        set => SetValue(YMaximumFactorProperty, value);
    }

    public bool IsGatingEnabled
    {
        get => GetValue(IsGatingEnabledProperty);
        set => SetValue(IsGatingEnabledProperty, value);
    }

    public HistogramRangeSelection? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 140 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeriesProperty)
            resubscribe_series();
        if (change.Property == SeriesProperty
            || change.Property == MinimumProperty
            || change.Property == MaximumProperty
            || change.Property == AxisScaleProperty
            || change.Property == LogicleTopOfScaleProperty
            || change.Property == LogicleLinearizationWidthProperty
            || change.Property == LogicleDecadesProperty
            || change.Property == LogicleNegativeDecadesProperty
            || change.Property == ArcsinhCofactorProperty
            || change.Property == BinSmoothingProperty
            || change.Property == YMaximumFactorProperty)
            invalidate_bins();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsGatingEnabled || !plot_rect.Contains(e.GetPosition(this)))
            return;

        var point = e.GetPosition(this);
        double value = screen_to_data(point.X);
        drag_target = nearest_selection_edge(point.X);
        if (drag_target == DragTarget.None)
        {
            draft_selection = new HistogramRangeSelection(value, value);
            drag_target = DragTarget.Maximum;
        }
        else
        {
            draft_selection = Selection;
        }
        update_selection(value);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (drag_target != DragTarget.None)
        {
            update_selection(screen_to_data(point.X));
            e.Handled = true;
            return;
        }

        Cursor = IsGatingEnabled && plot_rect.Contains(point) && nearest_selection_edge(point.X) != DragTarget.None
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (draft_selection is { } selection)
            Selection = normalized_selection(selection);
        draft_selection = null;
        drag_target = DragTarget.None;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(37, 37, 37)), bounds);

        const double left_axis_space = 72;
        const double right_space = 18;
        const double top_space = 10;
        const double bottom_axis_space = 42;
        plot_rect = new Rect(
            bounds.Left + left_axis_space,
            bounds.Top + top_space,
            Math.Max(1, bounds.Width - left_axis_space - right_space),
            Math.Max(1, bounds.Height - top_space - bottom_axis_space));

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(37, 37, 37)), plot_rect);
        draw_grid(context);
        draw_series(context);
        draw_selection(context);
        draw_axes(context);
    }

    private void draw_series(DrawingContext context)
    {
        var series = prepared_series ??= prepare_series();
        foreach (var item in series)
        {
            if (item.Points.Count == 0)
                continue;

            var fill = new SolidColorBrush(Color.FromArgb(42, item.Color.R, item.Color.G, item.Color.B));
            var stroke = new Pen(new SolidColorBrush(item.Color), 1.8);
            var fill_geometry = new StreamGeometry();
            using (var geometry = fill_geometry.Open())
            {
                geometry.BeginFigure(new Point(item.Points[0].X, plot_rect.Bottom), true);
                foreach (var point in item.Points)
                    geometry.LineTo(point);
                geometry.LineTo(new Point(item.Points[^1].X, plot_rect.Bottom));
                geometry.EndFigure(true);
            }
            context.DrawGeometry(fill, null, fill_geometry);

            var line_geometry = new StreamGeometry();
            using (var geometry = line_geometry.Open())
            {
                geometry.BeginFigure(item.Points[0], false);
                foreach (var point in item.Points.Skip(1))
                    geometry.LineTo(point);
            }
            context.DrawGeometry(null, stroke, line_geometry);
        }
    }

    private List<PreparedSeries> prepare_series()
    {
        var result = new List<PreparedSeries>();
        if (Series is null || prepare_axis() is not { } axis)
            return result;

        double transformed_span = axis.TransformedMaximum - axis.TransformedMinimum;
        if (transformed_span <= 0)
            return result;

        foreach (var source in Series)
        {
            int bins = Math.Max(1, source.BinCount);
            var counts = new double[bins];
            foreach (double value in source.Values)
            {
                if (!double.IsFinite(value) || !try_transform(value, out double transformed))
                    continue;

                double normalized = (transformed - axis.TransformedMinimum) / transformed_span;
                if (normalized < 0 || normalized > 1)
                    continue;

                int bin = Math.Clamp((int)(normalized * bins), 0, bins - 1);
                counts[bin]++;
            }

            counts = smooth(counts, BinSmoothing);
            double maximum = counts.DefaultIfEmpty(0).Max();
            double y_maximum = maximum * Math.Max(1.0, YMaximumFactor);
            var points = new List<Point>(bins);
            if (y_maximum > 0)
            {
                for (int bin = 0; bin < bins; bin++)
                {
                    double normalized_x = (bin + 0.5) / bins;
                    double normalized_y = counts[bin] / y_maximum;
                    points.Add(new Point(
                        plot_rect.Left + normalized_x * plot_rect.Width,
                        plot_rect.Bottom - normalized_y * plot_rect.Height));
                }
            }

            result.Add(new PreparedSeries(source.Color, points));
        }

        return result;
    }

    private void draw_selection(DrawingContext context)
    {
        if ((draft_selection ?? Selection) is not { } selection)
            return;

        var normalized = normalized_selection(selection);
        double left = data_to_screen(normalized.Minimum);
        double right = data_to_screen(normalized.Maximum);
        var fill = new SolidColorBrush(Color.FromArgb(34, 120, 160, 255));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(190, 200, 230)), 1.4, DashStyle.Dash);
        context.FillRectangle(fill, new Rect(left, plot_rect.Top, Math.Max(0, right - left), plot_rect.Height));
        context.DrawLine(pen, new Point(left, plot_rect.Top), new Point(left, plot_rect.Bottom));
        context.DrawLine(pen, new Point(right, plot_rect.Top), new Point(right, plot_rect.Bottom));

        if (!IsGatingEnabled)
            return;

        var center_y = plot_rect.Top + plot_rect.Height / 2;
        var handle_pen = new Pen(new SolidColorBrush(Color.FromRgb(210, 218, 244)), 1.6);
        context.DrawLine(handle_pen, new Point(left, center_y), new Point(right, center_y));
        draw_selection_handle(context, left, center_y, handle_pen);
        draw_selection_handle(context, right, center_y, handle_pen);
    }

    private static void draw_selection_handle(DrawingContext context, double x, double y, Pen pen)
    {
        var rect = new Rect(x - 5, y - 5, 10, 10);
        context.FillRectangle(Brushes.White, rect);
        context.DrawRectangle(null, pen, rect);
    }

    private void draw_grid(DrawingContext context)
    {
        var major_grid_pen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        var minor_grid_pen = new Pen(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), 1);
        for (int index = 1; index < 5; index++)
        {
            double y = plot_rect.Bottom - plot_rect.Height * index / 5.0;
            context.DrawLine(index == 5 ? major_grid_pen : minor_grid_pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
        }

        foreach (double tick in minor_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(minor_grid_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }

        foreach (double tick in major_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(major_grid_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }
    }

    private void draw_axes(DrawingContext context)
    {
        var axis_color = Color.FromRgb(142, 148, 160);
        var text_color = Color.FromRgb(230, 235, 245);
        var tick_text = Color.FromRgb(140, 148, 162);
        var axis_pen = new Pen(new SolidColorBrush(axis_color), 1);
        var tick_pen = new Pen(new SolidColorBrush(axis_color), 1);
        context.DrawLine(axis_pen, new Point(plot_rect.Left, plot_rect.Bottom), new Point(plot_rect.Right, plot_rect.Bottom));
        context.DrawLine(axis_pen, new Point(plot_rect.Left, plot_rect.Top), new Point(plot_rect.Left, plot_rect.Bottom));

        foreach (double tick in minor_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(tick_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 4));
        }

        foreach (double tick in major_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(tick_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 6));
            draw_centered_text(context, format_axis_value(tick), new Point(x, plot_rect.Bottom + 8), 11, tick_text);
        }

        string x_title = Series?.FirstOrDefault()?.Name ?? "";
        if (!string.IsNullOrWhiteSpace(x_title))
            draw_centered_text(context, x_title, new Point(plot_rect.Left + plot_rect.Width / 2, Bounds.Bottom - 15), 12, text_color);
        draw_vertical_centered_text(context, "Normalized Frequency", new Point(Bounds.Left + 45, plot_rect.Top + plot_rect.Height / 2), 12, text_color);
    }

    private IEnumerable<double> major_axis_ticks()
    {
        if (prepare_axis() is not { } prepared || prepared.Maximum <= prepared.Minimum)
            yield break;

        var axis = new AxisSettings
        {
            Minimum = prepared.Minimum,
            Maximum = prepared.Maximum,
            ScaleKind = coordinate_scale_kind()
        };
        
        axis.Scale.Logicle = new LogicleParameters(
            LogicleTopOfScale,
            LogicleLinearizationWidth,
            LogicleDecades,
            LogicleNegativeDecades);
        axis.ArcsinhCofactor = ArcsinhCofactor;

        foreach (double value in Configuration.MajorAxisTicks(axis))
            if (value >= prepared.Minimum && value <= prepared.Maximum)
                yield return value;
    }

    private IEnumerable<double> minor_axis_ticks()
    {
        if (prepare_axis() is not { } prepared || prepared.Maximum <= prepared.Minimum)
            yield break;

        var axis = new AxisSettings
        {
            Minimum = prepared.Minimum,
            Maximum = prepared.Maximum,
            ScaleKind = coordinate_scale_kind()
        };
        axis.Scale.Logicle = new LogicleParameters(
            LogicleTopOfScale,
            LogicleLinearizationWidth,
            LogicleDecades,
            LogicleNegativeDecades);
        axis.ArcsinhCofactor = ArcsinhCofactor;

        foreach (double value in Configuration.MinorAxisTicks(axis))
            if (value >= prepared.Minimum && value <= prepared.Maximum)
                yield return value;
    }

    private double data_to_screen(double value)
    {
        if (prepare_axis() is not { } axis || !try_transform(value, out double transformed))
            return plot_rect.Left;

        double span = axis.TransformedMaximum - axis.TransformedMinimum;
        if (span <= 0)
            return plot_rect.Left;
        return plot_rect.Left + Math.Clamp((transformed - axis.TransformedMinimum) / span, 0, 1) * plot_rect.Width;
    }

    private double screen_to_data(double x)
    {
        if (prepare_axis() is not { } axis)
            return effective_minimum();

        double normalized = Math.Clamp((x - plot_rect.Left) / plot_rect.Width, 0, 1);
        double transformed = axis.TransformedMinimum + normalized * (axis.TransformedMaximum - axis.TransformedMinimum);
        return try_inverse_transform(transformed, out double value) ? value : effective_minimum();
    }

    private PreparedAxis? prepare_axis()
    {
        if (prepared_axis is not null)
            return prepared_axis;

        var (minimum, maximum) = effective_bounds();
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return null;
        var scale = shared_scale();
        double transformed_minimum;
        double transformed_maximum;
        if (maximum <= minimum)
        {
            transformed_minimum = scale.Transform(minimum - 0.5);
            transformed_maximum = scale.Transform(maximum + 0.5);
            if (transformed_maximum <= transformed_minimum)
                return null;
            return prepared_axis = new PreparedAxis(minimum, maximum, transformed_minimum, transformed_maximum, scale);
        }
        transformed_minimum = scale.Transform(minimum);
        transformed_maximum = scale.Transform(maximum);
        if (!double.IsFinite(transformed_minimum) || !double.IsFinite(transformed_maximum) || transformed_maximum <= transformed_minimum)
            return null;
        return prepared_axis = new PreparedAxis(minimum, maximum, transformed_minimum, transformed_maximum, scale);
    }

    private bool try_transform(double value, out double transformed)
    {
        transformed = value;
        if (!double.IsFinite(value))
            return false;

        transformed = (prepared_axis?.Scale ?? shared_scale()).Transform(value);
        return double.IsFinite(transformed);
    }

    private bool try_inverse_transform(double transformed, out double value)
    {
        value = transformed;
        if (!double.IsFinite(transformed))
            return false;
        value = (prepared_axis?.Scale ?? shared_scale()).InverseTransform(transformed);
        return double.IsFinite(value);
    }

    private AxisScale shared_scale() => new()
    {
        Kind = coordinate_scale_kind(),
        Logicle = new LogicleParameters(LogicleTopOfScale, LogicleLinearizationWidth, LogicleDecades, LogicleNegativeDecades),
        ArcsinhCofactor = ArcsinhCofactor
    };

    private CoordinateScaleKind coordinate_scale_kind() => AxisScale switch
    {
        HistogramAxisScaleKind.Log => CoordinateScaleKind.Logarithmic,
        HistogramAxisScaleKind.Logicle => CoordinateScaleKind.Logicle,
        HistogramAxisScaleKind.Arcsinh => CoordinateScaleKind.Arcsinh,
        _ => CoordinateScaleKind.Linear
    };

    private void update_selection(double value)
    {
        var current = draft_selection ?? Selection;
        if (current is not { } selection)
            return;

        draft_selection = drag_target switch
        {
            DragTarget.Minimum => new HistogramRangeSelection(value, selection.Maximum),
            DragTarget.Maximum => new HistogramRangeSelection(selection.Minimum, value),
            _ => selection
        };
        InvalidateVisual();
    }

    private DragTarget nearest_selection_edge(double x)
    {
        if (Selection is not { } selection)
            return DragTarget.None;

        var normalized = normalized_selection(selection);
        double left = data_to_screen(normalized.Minimum);
        double right = data_to_screen(normalized.Maximum);
        double left_distance = Math.Abs(x - left);
        double right_distance = Math.Abs(x - right);
        double best = Math.Min(left_distance, right_distance);
        if (best > 12)
            return DragTarget.None;
        return left_distance <= right_distance ? DragTarget.Minimum : DragTarget.Maximum;
    }

    private static HistogramRangeSelection normalized_selection(HistogramRangeSelection selection) =>
        selection.Minimum <= selection.Maximum
            ? selection
            : new HistogramRangeSelection(selection.Maximum, selection.Minimum);

    private double effective_minimum() => prepare_axis()?.Minimum ?? effective_bounds().Minimum;

    private (double Minimum, double Maximum) effective_bounds()
    {
        var finite = finite_sorted_values();
        if (finite.Length == 0)
            return (Minimum ?? 0, Maximum ?? 1);
        double observed_minimum = percentile_in_sorted(finite, 0.0001);
        double observed_maximum = percentile_in_sorted(finite, 0.999);
        double minimum = Minimum is { } requested_minimum && double.IsFinite(requested_minimum)
            ? Math.Clamp(requested_minimum, observed_minimum, observed_maximum)
            : observed_minimum;
        double maximum = Maximum is { } requested_maximum && double.IsFinite(requested_maximum)
            ? Math.Clamp(requested_maximum, observed_minimum, observed_maximum)
            : observed_maximum;
        if (maximum < minimum)
            (minimum, maximum) = (maximum, minimum);
        return (minimum, maximum);
    }

    private double[] finite_sorted_values()
    {
        if (Series is null || Series.Count == 0)
            return [];

        if (Series.Count == 1 && Series[0].SortedValues is { } sorted)
        {
            if (sorted is double[] array)
                return array;
            return sorted.ToArray();
        }

        var finite = Series.SelectMany(item => item.SortedValues ?? item.Values)
            .Where(double.IsFinite)
            .ToArray();
        Array.Sort(finite);
        return finite;
    }

    private static double percentile_in_sorted(double[] values, double quantile)
    {
        if (values.Length == 1)
            return values[0];
        double position = Math.Clamp(quantile, 0, 1) * (values.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(values.Length - 1, lower + 1);
        double fraction = position - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }

    private static double[] smooth(double[] source, int passes)
    {
        passes = Math.Clamp(passes, 0, 16);
        var result = source;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = new double[result.Length];
            for (int index = 0; index < result.Length; index++)
            {
                double total = result[index] * 2;
                double weight = 2;
                if (index > 0)
                {
                    total += result[index - 1];
                    weight += 1;
                }
                if (index + 1 < result.Length)
                {
                    total += result[index + 1];
                    weight += 1;
                }
                next[index] = total / weight;
            }
            result = next;
        }

        return result;
    }

    private void invalidate_bins()
    {
        prepared_series = null;
        prepared_axis = null;
        InvalidateVisual();
    }

    private void resubscribe_series()
    {
        if (subscribed_series_collection is not null)
            subscribed_series_collection.CollectionChanged -= on_series_collection_changed;
        foreach (var series in subscribed_series)
            series.PropertyChanged -= on_series_property_changed;

        subscribed_series.Clear();
        subscribed_series_collection = Series as INotifyCollectionChanged;
        if (subscribed_series_collection is not null)
            subscribed_series_collection.CollectionChanged += on_series_collection_changed;

        if (Series is null)
            return;
        foreach (var series in Series)
        {
            subscribed_series.Add(series);
            series.PropertyChanged += on_series_property_changed;
        }
    }

    private void on_series_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        resubscribe_series();
        invalidate_bins();
    }

    private void on_series_property_changed(object? sender, PropertyChangedEventArgs e) =>
        invalidate_bins();

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private void draw_right_aligned_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width, origin.Y));
    }

    private void draw_vertical_centered_text(DrawingContext context, string text, Point center, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        using (context.PushTransform(Matrix.CreateTranslation(-formatted.Width / 2, -formatted.Height / 2) *
                                     Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
            context.DrawText(formatted, new Point());
    }

    private FormattedText create_text(string text, double size, Color color) =>
        new(
            text ?? "",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this)),
            size,
            new SolidColorBrush(color));

    private static string format_axis_value(double value)
    {
        double absolute = Math.Abs(value);
        if (absolute >= 10000 || absolute is > 0 and < 0.01)
            return value.ToString("0.##E0", CultureInfo.InvariantCulture);
        if (absolute >= 100)
            return value.ToString("0", CultureInfo.InvariantCulture);
        if (absolute >= 10)
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private sealed record PreparedSeries(Color Color, List<Point> Points);
    private sealed record PreparedAxis(double Minimum, double Maximum, double TransformedMinimum, double TransformedMaximum, AxisScale Scale);

    private enum DragTarget
    {
        None,
        Minimum,
        Maximum
    }
}
