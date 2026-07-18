using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using gated.Models;
using gated.Shared;

namespace gated.Controls;

public sealed class ScatterGateView : Control, IThemeResourceAware
{
    public static readonly StyledProperty<ControlSample?> SampleProperty =
        AvaloniaProperty.Register<ScatterGateView, ControlSample?>(nameof(Sample));

    public static readonly StyledProperty<string> XChannelProperty =
        AvaloniaProperty.Register<ScatterGateView, string>(nameof(XChannel), "FSC-A");

    public static readonly StyledProperty<string> YChannelProperty =
        AvaloniaProperty.Register<ScatterGateView, string>(nameof(YChannel), "SSC-A");

    public static readonly StyledProperty<ObservableCollection<Point>?> VerticesProperty =
        AvaloniaProperty.Register<ScatterGateView, ObservableCollection<Point>?>(nameof(Vertices));

    public static readonly StyledProperty<double> XMinimumProperty =
        AvaloniaProperty.Register<ScatterGateView, double>(nameof(XMinimum));

    public static readonly StyledProperty<double> XMaximumProperty =
        AvaloniaProperty.Register<ScatterGateView, double>(nameof(XMaximum), 262144);

    public static readonly StyledProperty<AxisScale?> XScaleProperty =
        AvaloniaProperty.Register<ScatterGateView, AxisScale?>(nameof(XScale));

    public static readonly StyledProperty<double> YMinimumProperty =
        AvaloniaProperty.Register<ScatterGateView, double>(nameof(YMinimum));

    public static readonly StyledProperty<double> YMaximumProperty =
        AvaloniaProperty.Register<ScatterGateView, double>(nameof(YMaximum), 262144);

    public static readonly StyledProperty<AxisScale?> YScaleProperty =
        AvaloniaProperty.Register<ScatterGateView, AxisScale?>(nameof(YScale));

    public static readonly StyledProperty<ICommand?> VerticesCommittedCommandProperty =
        AvaloniaProperty.Register<ScatterGateView, ICommand?>(nameof(VerticesCommittedCommand));

    private Rect plot_rect;
    private ObservableCollection<Point>? subscribed_vertices;
    private List<Point>? draft_vertices;
    private int dragged_vertex = -1;
    private WriteableBitmap? cached_event_bitmap;

    static ScatterGateView()
    {
        AffectsRender<ScatterGateView>(
            SampleProperty,
            XChannelProperty,
            YChannelProperty,
            VerticesProperty,
            XMinimumProperty,
            XMaximumProperty,
            XScaleProperty,
            YMinimumProperty,
            YMaximumProperty,
            YScaleProperty);
    }

    public ControlSample? Sample
    {
        get => GetValue(SampleProperty);
        set => SetValue(SampleProperty, value);
    }

    public string XChannel
    {
        get => GetValue(XChannelProperty);
        set => SetValue(XChannelProperty, value);
    }

    public string YChannel
    {
        get => GetValue(YChannelProperty);
        set => SetValue(YChannelProperty, value);
    }

    public ObservableCollection<Point>? Vertices
    {
        get => GetValue(VerticesProperty);
        set => SetValue(VerticesProperty, value);
    }

    public double XMinimum
    {
        get => GetValue(XMinimumProperty);
        set => SetValue(XMinimumProperty, value);
    }

    public double XMaximum
    {
        get => GetValue(XMaximumProperty);
        set => SetValue(XMaximumProperty, value);
    }

    public AxisScale? XScale
    {
        get => GetValue(XScaleProperty);
        set => SetValue(XScaleProperty, value);
    }

    public double YMinimum
    {
        get => GetValue(YMinimumProperty);
        set => SetValue(YMinimumProperty, value);
    }

    public double YMaximum
    {
        get => GetValue(YMaximumProperty);
        set => SetValue(YMaximumProperty, value);
    }

    public AxisScale? YScale
    {
        get => GetValue(YScaleProperty);
        set => SetValue(YScaleProperty, value);
    }

    public ICommand? VerticesCommittedCommand
    {
        get => GetValue(VerticesCommittedCommandProperty);
        set => SetValue(VerticesCommittedCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VerticesProperty)
            resubscribe_vertices();
        if (change.Property == SampleProperty || change.Property == XChannelProperty ||
            change.Property == YChannelProperty || change.Property == XMinimumProperty ||
            change.Property == XMaximumProperty || change.Property == XScaleProperty ||
            change.Property == YMinimumProperty || change.Property == YMaximumProperty ||
            change.Property == YScaleProperty)
            cached_event_bitmap = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (subscribed_vertices is not null)
            subscribed_vertices.CollectionChanged -= vertices_changed;
        subscribed_vertices = null;
    }

    public void RefreshThemeResources()
    {
        cached_event_bitmap = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Vertices is null || !plot_rect.Contains(e.GetPosition(this)))
            return;

        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            Vertices.Clear();
            draft_vertices = null;
            execute_vertices_committed();
            e.Handled = true;
            return;
        }

        draft_vertices = Vertices.ToList();
        dragged_vertex = nearest_vertex(point, draft_vertices);
        if (dragged_vertex < 0)
        {
            draft_vertices.Add(screen_to_data(point));
            dragged_vertex = draft_vertices.Count - 1;
        }

        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (draft_vertices is null || dragged_vertex < 0 || dragged_vertex >= draft_vertices.Count)
            return;

        draft_vertices[dragged_vertex] = screen_to_data(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Vertices is not null && draft_vertices is not null)
        {
            Vertices.Clear();
            foreach (var vertex in draft_vertices)
                Vertices.Add(vertex);
            execute_vertices_committed();
        }
        draft_vertices = null;
        dragged_vertex = -1;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")), bounds);

        const double left_axis_space = 72;
        const double right_space = 18;
        const double top_space = 10;
        const double bottom_axis_space = 42;
        plot_rect = new Rect(
            bounds.Left + left_axis_space,
            bounds.Top + top_space,
            Math.Max(1, bounds.Width - left_axis_space - right_space),
            Math.Max(1, bounds.Height - top_space - bottom_axis_space));

        context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")), plot_rect);
        draw_grid(context);
        draw_events(context);
        draw_polygon(context);
        draw_axes(context);
    }

    private void draw_grid(DrawingContext context)
    {
        var major = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMajor")), 1);
        var minor = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMinor")), 1);
        foreach (double value in minor_axis_ticks(x_axis()))
            draw_vertical_grid_line(context, value, minor);
        foreach (double value in major_axis_ticks(x_axis()))
            draw_vertical_grid_line(context, value, major);
        foreach (double value in minor_axis_ticks(y_axis()))
            draw_horizontal_grid_line(context, value, minor);
        foreach (double value in major_axis_ticks(y_axis()))
            draw_horizontal_grid_line(context, value, major);
    }

    private void draw_events(DrawingContext context)
    {
        if (Sample is null || string.IsNullOrWhiteSpace(XChannel) || string.IsNullOrWhiteSpace(YChannel))
            return;

        var x_values = Sample.GetChannelValues(XChannel);
        var y_values = Sample.GetChannelValues(YChannel);
        if (x_values.Length == 0 || y_values.Length == 0)
            return;

        if (cached_event_bitmap is not null)
        {
            context.DrawImage(cached_event_bitmap, plot_rect);
            return;
        }

        const int size = 192;
        int step = Math.Max(1, x_values.Length / 12000);
        var bins = new int[size, size];
        for (int index = 0; index < x_values.Length && index < y_values.Length; index += step)
        {
            if (!try_normalize(x_values[index], x_axis(), out double nx) ||
                !try_normalize(y_values[index], y_axis(), out double ny))
                continue;
            int bx = Math.Clamp((int)(nx * bins.GetLength(0)), 0, bins.GetLength(0) - 1);
            int by = Math.Clamp((int)(ny * bins.GetLength(1)), 0, bins.GetLength(1) - 1);
            bins[bx, by]++;
        }
        int max_count = bins.Cast<int>().DefaultIfEmpty(0).Max();

        var pixels = new byte[size * size * 4];
        for (int bx = 0; bx < size; bx++)
        for (int by = 0; by < size; by++)
        {
            int count = bins[bx, by];
            if (count == 0) continue;
            double density = max_count <= 0 ? 0 : count / (double)max_count;
            Color color = density > 0.35 ? gated.Shared.ThemeResources.AppColor("OverlayDensityHigh") :
                density > 0.12 ? gated.Shared.ThemeResources.AppColor("OverlayDensityMedium") : gated.Shared.ThemeResources.AppColor("OverlayDensityLow");
            int offset = ((size - 1 - by) * size + bx) * 4;
            pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = color.A;
        }
        cached_event_bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = cached_event_bitmap.Lock())
        {
            int row_bytes = size * 4;
            for (int row = 0; row < size; row++)
                Marshal.Copy(pixels, row * row_bytes, IntPtr.Add(frame.Address, row * frame.RowBytes), row_bytes);
        }
        context.DrawImage(cached_event_bitmap, plot_rect);
    }

    private void draw_polygon(DrawingContext context)
    {
        var vertices = draft_vertices ?? Vertices?.ToList();
        if (vertices is null || vertices.Count == 0)
            return;

        var points = vertices.Select(data_to_screen).ToArray();
        var line_pen = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme4")), 2.0);
        var fill = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayScrim"));
        if (points.Length > 2)
        {
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(points[0], true);
                foreach (var point in points.Skip(1))
                    stream.LineTo(point);
                stream.EndFigure(true);
            }
            context.DrawGeometry(fill, line_pen, geometry);
        }
        else if (points.Length == 2)
        {
            context.DrawLine(line_pen, points[0], points[1]);
        }

        foreach (var point in points)
        {
            var rect = new Rect(point.X - 4, point.Y - 4, 8, 8);
            context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background1")), rect);
            context.DrawRectangle(null, line_pen, rect);
        }
    }

    private void draw_axes(DrawingContext context)
    {
        var axis_color = gated.Shared.ThemeResources.AppColor("Text5");
        var text_color = gated.Shared.ThemeResources.AppColor("Text1");
        var tick_text = gated.Shared.ThemeResources.AppColor("Text5");
        var pen = new Pen(new SolidColorBrush(axis_color), 1);
        context.DrawLine(pen, new Point(plot_rect.Left, plot_rect.Bottom), new Point(plot_rect.Right, plot_rect.Bottom));
        context.DrawLine(pen, new Point(plot_rect.Left, plot_rect.Top), new Point(plot_rect.Left, plot_rect.Bottom));
        draw_ticks(context, pen, tick_text);
        draw_centered_text(context, XChannel, new Point(plot_rect.Left + plot_rect.Width / 2, Bounds.Bottom - 6), 12, text_color);
        draw_rotated_text(context, YChannel, new Point(20, plot_rect.Top + plot_rect.Height / 2), 12, text_color);
    }

    private void draw_ticks(DrawingContext context, Pen pen, Color color)
    {
        foreach (double value in minor_axis_ticks(x_axis()))
        {
            double x = data_to_screen_x(value);
            context.DrawLine(pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 3));
        }
        foreach (double value in major_axis_ticks(x_axis()))
        {
            double x = data_to_screen_x(value);
            context.DrawLine(pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 5));
            draw_centered_text(context, Configuration.FormatAxisValue(value), new Point(x, plot_rect.Bottom + 15), 11, color);
        }
        foreach (double value in minor_axis_ticks(y_axis()))
        {
            double y = data_to_screen_y(value);
            context.DrawLine(pen, new Point(plot_rect.Left - 3, y), new Point(plot_rect.Left, y));
        }
        foreach (double value in major_axis_ticks(y_axis()))
        {
            double y = data_to_screen_y(value);
            context.DrawLine(pen, new Point(plot_rect.Left - 5, y), new Point(plot_rect.Left, y));
            var formatted = create_formatted_text(Configuration.FormatAxisValue(value), 11, color);
            context.DrawText(formatted, new Point(plot_rect.Left - formatted.Width - 10, y - formatted.Height / 2));
        }
    }

    private int nearest_vertex(Point point, IReadOnlyList<Point>? vertices = null)
    {
        vertices ??= Vertices;
        if (vertices is null)
            return -1;

        int best_index = -1;
        double best_distance = 12;
        for (int index = 0; index < vertices.Count; index++)
        {
            var vertex = data_to_screen(vertices[index]);
            double distance = Math.Sqrt(Math.Pow(vertex.X - point.X, 2) + Math.Pow(vertex.Y - point.Y, 2));
            if (distance >= best_distance)
                continue;
            best_distance = distance;
            best_index = index;
        }
        return best_index;
    }

    private Point data_to_screen(Point point) => new(data_to_screen_x(point.X), data_to_screen_y(point.Y));

    private Point screen_to_data(Point point)
    {
        double x = denormalize(Math.Clamp((point.X - plot_rect.Left) / plot_rect.Width, 0, 1), x_axis());
        double y = denormalize(1 - Math.Clamp((point.Y - plot_rect.Top) / plot_rect.Height, 0, 1), y_axis());
        return new Point(x, y);
    }

    private double data_to_screen_x(double value)
    {
        return plot_rect.Left + normalize(value, x_axis()) * plot_rect.Width;
    }

    private double data_to_screen_y(double value)
    {
        return plot_rect.Bottom - normalize(value, y_axis()) * plot_rect.Height;
    }

    private static bool try_normalize(double value, AxisSettings axis, out double normalized)
    {
        normalized = 0;
        if (!double.IsFinite(value))
            return false;
        normalized = normalize(value, axis);
        return normalized >= 0 && normalized <= 1;
    }

    private AxisSettings x_axis() =>
        axis_for(XChannel, XMinimum, XMaximum, XScale);

    private AxisSettings y_axis() =>
        axis_for(YChannel, YMinimum, YMaximum, YScale);

    private AxisSettings axis_for(string channel_name, double minimum, double maximum, AxisScale? scale)
    {
        var channel = Sample?.Channels.FirstOrDefault(channel => channel.Name == channel_name);
        double fallback_maximum = channel?.Maximum ?? 262144;
        fallback_maximum = double.IsFinite(fallback_maximum) && fallback_maximum > 0 ? fallback_maximum : 262144;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = 0;
            maximum = fallback_maximum;
        }

        var axis = new AxisSettings
        {
            ChannelName = channel_name,
            Minimum = minimum,
            Maximum = maximum,
            Scale = scale?.Clone() ?? new AxisScale { Kind = CoordinateScaleKind.Linear }
        };
        return axis;
    }

    private void draw_vertical_grid_line(DrawingContext context, double value, Pen pen)
    {
        double x = data_to_screen_x(value);
        context.DrawLine(pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
    }

    private void draw_horizontal_grid_line(DrawingContext context, double value, Pen pen)
    {
        double y = data_to_screen_y(value);
        context.DrawLine(pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
    }

    private static IEnumerable<double> major_axis_ticks(AxisSettings axis) =>
        Configuration.MajorAxisTicks(axis).Where(value => value >= axis.Minimum && value <= axis.Maximum);

    private static IEnumerable<double> minor_axis_ticks(AxisSettings axis)
    {
        var major = major_axis_ticks(axis).ToArray();
        foreach (double value in Configuration.MinorAxisTicks(axis))
            if (value >= axis.Minimum && value <= axis.Maximum && !major.Any(item => Math.Abs(item - value) < 1e-9))
                yield return value;
    }

    private static double normalize(double value, AxisSettings axis)
    {
        double minimum = axis.Scale.Transform(axis.Minimum);
        double maximum = axis.Scale.Transform(axis.Maximum);
        double transformed = axis.Scale.Transform(value);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || !double.IsFinite(transformed) || maximum <= minimum)
            return 0;
        return Math.Clamp((transformed - minimum) / (maximum - minimum), 0, 1);
    }

    private static double denormalize(double normalized, AxisSettings axis)
    {
        double minimum = axis.Scale.Transform(axis.Minimum);
        double maximum = axis.Scale.Transform(axis.Maximum);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return axis.Minimum;
        return axis.Scale.InverseTransform(minimum + normalized * (maximum - minimum));
    }

    private void draw_centered_text(DrawingContext context, string text, Point center, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private void draw_rotated_text(DrawingContext context, string text, Point center, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        using (context.PushTransform(Matrix.CreateTranslation(-formatted.Width / 2, -formatted.Height / 2) *
                                     Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
            context.DrawText(formatted, new Point());
    }

    private FormattedText create_formatted_text(string text, double size, Color color) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                TextElement.GetFontFamily(this),
                TextElement.GetFontStyle(this),
                TextElement.GetFontWeight(this),
                TextElement.GetFontStretch(this)),
            size,
            new SolidColorBrush(color));

    private void resubscribe_vertices()
    {
        if (subscribed_vertices is not null)
            subscribed_vertices.CollectionChanged -= vertices_changed;
        subscribed_vertices = Vertices;
        if (subscribed_vertices is not null)
            subscribed_vertices.CollectionChanged += vertices_changed;
    }

    private void vertices_changed(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void execute_vertices_committed()
    {
        if (VerticesCommittedCommand?.CanExecute(null) == true)
            VerticesCommittedCommand.Execute(null);
    }
}
