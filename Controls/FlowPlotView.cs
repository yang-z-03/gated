using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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

namespace gated.Controls;

public sealed class FlowPlotView : Control
{
    public static readonly StyledProperty<FlowGroup?> GroupProperty =
        AvaloniaProperty.Register<FlowPlotView, FlowGroup?>(nameof(Group));

    public static readonly StyledProperty<FlowSample?> SampleProperty =
        AvaloniaProperty.Register<FlowPlotView, FlowSample?>(nameof(Sample));

    public static readonly StyledProperty<GateDefinition?> GateProperty =
        AvaloniaProperty.Register<FlowPlotView, GateDefinition?>(nameof(Gate));

    public static readonly StyledProperty<IReadOnlyList<GateDefinition>?> GatesProperty =
        AvaloniaProperty.Register<FlowPlotView, IReadOnlyList<GateDefinition>?>(nameof(Gates));

    public static readonly StyledProperty<PopulationResult?> PopulationProperty =
        AvaloniaProperty.Register<FlowPlotView, PopulationResult?>(nameof(Population));

    public static readonly StyledProperty<AxisSettings?> XAxisProperty =
        AvaloniaProperty.Register<FlowPlotView, AxisSettings?>(nameof(XAxis));

    public static readonly StyledProperty<AxisSettings?> YAxisProperty =
        AvaloniaProperty.Register<FlowPlotView, AxisSettings?>(nameof(YAxis));

    public static readonly StyledProperty<PlotMode> PlotModeProperty =
        AvaloniaProperty.Register<FlowPlotView, PlotMode>(nameof(PlotMode), PlotMode.Density);

    public static readonly StyledProperty<bool> ShowOutlierPointsProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(ShowOutlierPoints), true);

    public static readonly StyledProperty<bool> ShowGridlinesProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(ShowGridlines), true);

    public static readonly StyledProperty<int> ContourLevelCountProperty =
        AvaloniaProperty.Register<FlowPlotView, int>(nameof(ContourLevelCount), 10);

    public static readonly StyledProperty<int> DensitySmoothingProperty =
        AvaloniaProperty.Register<FlowPlotView, int>(nameof(DensitySmoothing), 9);

    public static readonly StyledProperty<GatingTool> ActiveToolProperty =
        AvaloniaProperty.Register<FlowPlotView, GatingTool>(nameof(ActiveTool), GatingTool.View);

    public static readonly StyledProperty<ICommand?> GateCreatedCommandProperty =
        AvaloniaProperty.Register<FlowPlotView, ICommand?>(nameof(GateCreatedCommand));

    public static readonly StyledProperty<ICommand?> GateEditedCommandProperty =
        AvaloniaProperty.Register<FlowPlotView, ICommand?>(nameof(GateEditedCommand));

    private const int density_size = 300;
    private int dragging_vertex_index = -1;
    private int dragging_pending_vertex_index = -1;
    private GateDefinition? dragging_gate;
    private bool gate_was_edited;
    private GateDefinition? pending_gate;
    private Point pending_preview_point;
    private bool has_pending_preview_point;
    private Rect plot_rect;
    private FlowGroup? subscribed_group;
    private FlowSample? subscribed_sample;
    private readonly List<FlowSample> subscribed_group_samples = new();
    private AxisSettings? subscribed_x_axis;
    private AxisSettings? subscribed_y_axis;
    private WriteableBitmap? cached_plot_bitmap;
    private PlotMode cached_plot_mode;
    private bool cached_histogram;

    static FlowPlotView()
    {
        AffectsRender<FlowPlotView>(
            GroupProperty,
            SampleProperty,
            GateProperty,
            GatesProperty,
            PopulationProperty,
            XAxisProperty,
            YAxisProperty,
            PlotModeProperty,
            ShowOutlierPointsProperty,
            ShowGridlinesProperty,
            ContourLevelCountProperty,
            DensitySmoothingProperty,
            ActiveToolProperty,
            GateCreatedCommandProperty,
            GateEditedCommandProperty);
    }

    public FlowGroup? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    public FlowSample? Sample
    {
        get => GetValue(SampleProperty);
        set => SetValue(SampleProperty, value);
    }

    public GateDefinition? Gate
    {
        get => GetValue(GateProperty);
        set => SetValue(GateProperty, value);
    }

    public IReadOnlyList<GateDefinition>? Gates
    {
        get => GetValue(GatesProperty);
        set => SetValue(GatesProperty, value);
    }

    public PopulationResult? Population
    {
        get => GetValue(PopulationProperty);
        set => SetValue(PopulationProperty, value);
    }

    public AxisSettings? XAxis
    {
        get => GetValue(XAxisProperty);
        set => SetValue(XAxisProperty, value);
    }

    public AxisSettings? YAxis
    {
        get => GetValue(YAxisProperty);
        set => SetValue(YAxisProperty, value);
    }

    public PlotMode PlotMode
    {
        get => GetValue(PlotModeProperty);
        set => SetValue(PlotModeProperty, value);
    }

    public bool ShowOutlierPoints
    {
        get => GetValue(ShowOutlierPointsProperty);
        set => SetValue(ShowOutlierPointsProperty, value);
    }

    public bool ShowGridlines
    {
        get => GetValue(ShowGridlinesProperty);
        set => SetValue(ShowGridlinesProperty, value);
    }

    public int ContourLevelCount
    {
        get => GetValue(ContourLevelCountProperty);
        set => SetValue(ContourLevelCountProperty, value);
    }

    public int DensitySmoothing
    {
        get => GetValue(DensitySmoothingProperty);
        set => SetValue(DensitySmoothingProperty, value);
    }

    public GatingTool ActiveTool
    {
        get => GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    public ICommand? GateCreatedCommand
    {
        get => GetValue(GateCreatedCommandProperty);
        set => SetValue(GateCreatedCommandProperty, value);
    }

    public ICommand? GateEditedCommand
    {
        get => GetValue(GateEditedCommandProperty);
        set => SetValue(GateEditedCommandProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (XAxis is null || YAxis is null)
            return;

        var point = e.GetPosition(this);
        dragging_vertex_index = find_nearest_vertex(point, out var drag_gate);
        if (dragging_vertex_index >= 0)
        {
            dragging_gate = drag_gate;
            gate_was_edited = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (ActiveTool != GatingTool.View && plot_rect.Contains(point))
        {
            begin_gate_creation_point(screen_to_data(point), e.ClickCount);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GroupProperty)
            resubscribe_group(Group);
        if (change.Property == SampleProperty)
            resubscribe_sample(Sample);
        if (change.Property == XAxisProperty)
            resubscribe_axis(ref subscribed_x_axis, XAxis);
        if (change.Property == YAxisProperty)
            resubscribe_axis(ref subscribed_y_axis, YAxis);
        if (change.Property == ActiveToolProperty)
            clear_pending_gate();
        if (change.Property == XAxisProperty || change.Property == YAxisProperty)
            clear_pending_gate();
        if (change.Property == GroupProperty
            || change.Property == SampleProperty
            || change.Property == GateProperty
            || change.Property == GatesProperty
            || change.Property == PopulationProperty
            || change.Property == XAxisProperty
            || change.Property == YAxisProperty
            || change.Property == PlotModeProperty
            || change.Property == ShowOutlierPointsProperty
            || change.Property == DensitySmoothingProperty
            || change.Property == ContourLevelCountProperty)
            invalidate_plot_cache();
        if (change.Property == ShowGridlinesProperty)
            InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging_pending_vertex_index >= 0 && pending_gate is not null)
        {
            var pending_data_point = one_dimensional_point_if_needed(pending_gate.Kind, screen_to_data(e.GetPosition(this)));
            pending_gate.Vertices[dragging_pending_vertex_index] = pending_data_point;
            has_pending_preview_point = false;
            InvalidateVisual();
            return;
        }

        if (pending_gate is not null && plot_rect.Contains(e.GetPosition(this)))
        {
            pending_preview_point = screen_to_data(e.GetPosition(this));
            has_pending_preview_point = true;
            InvalidateVisual();
        }

        if (dragging_vertex_index < 0 || dragging_gate is null || XAxis is null || YAxis is null)
            return;

        var pointer = e.GetPosition(this);
        var data_point = screen_to_data(pointer);
        if (dragging_vertex_index < dragging_gate.Vertices.Count)
        {
            dragging_gate.Vertices[dragging_vertex_index] = data_point;
            gate_was_edited = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gate_was_edited && dragging_gate is not null && GateEditedCommand?.CanExecute(dragging_gate) == true)
            GateEditedCommand.Execute(dragging_gate);

        if (dragging_pending_vertex_index >= 0 && pending_gate is not null)
            settle_pending_gate_point();

        dragging_vertex_index = -1;
        dragging_pending_vertex_index = -1;
        dragging_gate = null;
        gate_was_edited = false;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(37, 37, 37)), bounds);

        const double left_axis_space = 78;
        const double right_space = 20;
        const double top_space = 18;
        const double bottom_axis_space = 56;
        double available_width = bounds.Width - left_axis_space - right_space;
        double available_height = bounds.Height - top_space - bottom_axis_space;
        double size = Math.Min(available_width, available_height);
        if (size < 120)
            return;

        plot_rect = new Rect(
            left_axis_space + (available_width - size) / 2,
            top_space + (available_height - size) / 2,
            size,
            size);
        context.FillRectangle(Brushes.White, plot_rect);

        if (XAxis is null || YAxis is null)
            return;

        if (ShowGridlines)
            draw_grid(context);

        if (is_histogram_mode())
            draw_plot_bitmap(context, true);
        else if (PlotMode == PlotMode.Contour)
            draw_contour_plot(context);
        else draw_density(context);

        draw_axes(context);
        draw_gates(context);
        draw_pending_gate(context);
    }

    private void draw_density(DrawingContext context)
    {
        draw_plot_bitmap(context, false);
    }

    private void draw_contour_plot(DrawingContext context)
    {
        var grid = create_density_grid();
        if (grid is null)
            return;

        if (ShowOutlierPoints)
            context.DrawImage(create_dotplot_bitmap(grid.Value.density, grid.Value.total_count), plot_rect);

        var normalized_density = smooth_density(normalized_density_grid(grid.Value.density, grid.Value.max_density), DensitySmoothing);
        double[] levels = contour_levels(ContourLevelCount);
        context.DrawImage(create_filled_contour_bitmap(normalized_density, levels), plot_rect);

        var pen = new Pen(Brushes.Black, 0.8);
        foreach (double level in levels)
            draw_contour_level(context, normalized_density, level, pen);
    }

    private static WriteableBitmap create_filled_contour_bitmap(double[,] normalized_density, double[] levels)
    {
        var pixels = new byte[density_size * density_size * 4];
        for (int x_bin = 0; x_bin < density_size; x_bin++)
        for (int y_bin = 0; y_bin < density_size; y_bin++)
        {
            double value = normalized_density[x_bin, y_bin];
            int level_index = Array.FindLastIndex(levels, level => value >= level);
            if (level_index < 0)
                continue;

            double normalized_level = (level_index + 1.0) / levels.Length;
            byte shade = Convert.ToByte(250 - normalized_level * 210);
            set_pixel(pixels, x_bin, density_size - 1 - y_bin, Color.FromRgb(shade, shade, shade));
        }

        return create_bitmap(pixels);
    }

    private void draw_contour_level(DrawingContext context, double[,] density, double level, Pen pen)
    {
        for (int x = 0; x < density_size - 1; x++)
        for (int y = 0; y < density_size - 1; y++)
        {
            var points = contour_cell_points(density[x, y], density[x + 1, y], density[x + 1, y + 1], density[x, y + 1], x, y, level);
            for (int index = 0; index + 1 < points.Count; index += 2)
                context.DrawLine(pen, density_to_screen(points[index]), density_to_screen(points[index + 1]));
        }
    }

    private static List<Point> contour_cell_points(double bottom_left, double bottom_right, double top_right, double top_left, int x, int y, double level)
    {
        var points = new List<Point>(4);
        add_contour_intersection(points, bottom_left, bottom_right, new Point(x, y), new Point(x + 1, y), level);
        add_contour_intersection(points, bottom_right, top_right, new Point(x + 1, y), new Point(x + 1, y + 1), level);
        add_contour_intersection(points, top_right, top_left, new Point(x + 1, y + 1), new Point(x, y + 1), level);
        add_contour_intersection(points, top_left, bottom_left, new Point(x, y + 1), new Point(x, y), level);
        if (points.Count == 4)
            return [points[0], points[1], points[2], points[3]];

        return points;
    }

    private static void add_contour_intersection(List<Point> points, double first_value, double second_value, Point first, Point second, double level)
    {
        if ((first_value < level && second_value < level) || (first_value > level && second_value > level) || Math.Abs(first_value - second_value) < double.Epsilon)
            return;

        double t = Math.Clamp((level - first_value) / (second_value - first_value), 0, 1);
        points.Add(new Point(first.X + (second.X - first.X) * t, first.Y + (second.Y - first.Y) * t));
    }

    private Point density_to_screen(Point point) =>
        new(
            plot_rect.Left + point.X / (density_size - 1) * plot_rect.Width,
            plot_rect.Bottom - point.Y / (density_size - 1) * plot_rect.Height);

    private void draw_plot_bitmap(DrawingContext context, bool histogram)
    {
        if (cached_plot_bitmap is null || cached_plot_mode != PlotMode || cached_histogram != histogram)
        {
            cached_plot_mode = PlotMode;
            cached_histogram = histogram;
            cached_plot_bitmap = histogram ? create_histogram_bitmap() : create_density_bitmap();
        }

        if (cached_plot_bitmap is not null)
            context.DrawImage(cached_plot_bitmap, plot_rect);
    }

    private WriteableBitmap? create_density_bitmap()
    {
        var grid = create_density_grid();
        if (grid is null)
            return null;

        if (PlotMode == PlotMode.Dotplot)
            return create_dotplot_bitmap(grid.Value.density, grid.Value.total_count);

        var normalized_density = normalized_density_grid(grid.Value.density, grid.Value.max_density);
        if (PlotMode == PlotMode.Zebra)
            normalized_density = smooth_density(normalized_density, DensitySmoothing);
        var pixels = new byte[density_size * density_size * 4];
        if (PlotMode == PlotMode.Zebra && ShowOutlierPoints)
            add_dotplot_pixels(pixels, grid.Value.density, grid.Value.total_count);

        for (int x_bin = 0; x_bin < density_size; x_bin++)
        for (int y_bin = 0; y_bin < density_size; y_bin++)
        {
            double normalized = normalized_density[x_bin, y_bin];
            if (normalized <= 0)
                continue;

            set_pixel(pixels, x_bin, density_size - 1 - y_bin, plot_color(normalized, PlotMode, ContourLevelCount));
        }

        return create_bitmap(pixels);
    }

    private (int[,] density, int max_density, int total_count)? create_density_grid()
    {
        var samples = resolve_samples().ToArray();
        if (samples.Length == 0 || XAxis is null || YAxis is null)
            return null;

        var density = new int[density_size, density_size];
        int max_density = 0;
        int total_count = 0;
        double x_minimum = XAxis.Scale.Transform(XAxis.Minimum);
        double x_span = XAxis.Scale.Transform(XAxis.Maximum) - x_minimum;
        double y_minimum = YAxis.Scale.Transform(YAxis.Minimum);
        double y_span = YAxis.Scale.Transform(YAxis.Maximum) - y_minimum;
        if (x_span <= 0 || y_span <= 0)
            return null;

        foreach (var sample in samples)
        {
            int x_index = sample.GetChannelIndex(XAxis.ChannelName);
            int y_index = sample.GetChannelIndex(YAxis.ChannelName);
            if (x_index < 0 || y_index < 0)
                continue;

            foreach (int index in resolve_event_indices(sample))
            {
                int x_bin = to_bin(sample.CompensatedEvents[index, x_index], XAxis, x_minimum, x_span);
                int y_bin = to_bin(sample.CompensatedEvents[index, y_index], YAxis, y_minimum, y_span);
                if (x_bin < 0 || y_bin < 0)
                    continue;

                total_count++;
                int value = ++density[x_bin, y_bin];
                if (value > max_density)
                    max_density = value;
            }
        }

        return max_density == 0 ? null : (density, max_density, total_count);
    }

    private static double[,] normalized_density_grid(int[,] density, int max_density)
    {
        var normalized_density = new double[density_size, density_size];
        double denominator = Math.Log(1 + max_density);
        for (int x_bin = 0; x_bin < density_size; x_bin++)
        for (int y_bin = 0; y_bin < density_size; y_bin++)
        {
            int value = density[x_bin, y_bin];
            if (value > 0)
                normalized_density[x_bin, y_bin] = Math.Log(1 + value) / denominator;
        }

        return normalized_density;
    }

    private static WriteableBitmap create_dotplot_bitmap(int[,] density, int total_count)
    {
        var pixels = new byte[density_size * density_size * 4];
        add_dotplot_pixels(pixels, density, total_count);
        return create_bitmap(pixels);
    }

    private static void add_dotplot_pixels(byte[] pixels, int[,] density, int total_count)
    {
        double threshold = total_count * 0.00001;
        for (int x_bin = 0; x_bin < density_size; x_bin++)
        for (int y_bin = 0; y_bin < density_size; y_bin++)
        {
            if (density[x_bin, y_bin] <= threshold)
                continue;

            set_pixel(pixels, x_bin, density_size - 1 - y_bin, Colors.Black);
        }
    }

    private WriteableBitmap? create_histogram_bitmap()
    {
        var samples = resolve_samples().ToArray();
        if (samples.Length == 0 || XAxis is null)
            return null;

        int[] bins = new int[density_size];
        int max_count = 0;
        double x_minimum = XAxis.Scale.Transform(XAxis.Minimum);
        double x_span = XAxis.Scale.Transform(XAxis.Maximum) - x_minimum;
        if (x_span <= 0)
            return null;

        foreach (var sample in samples)
        {
            int x_index = sample.GetChannelIndex(XAxis.ChannelName);
            if (x_index < 0)
                continue;

            foreach (int index in resolve_event_indices(sample))
            {
                int bin = to_bin(sample.CompensatedEvents[index, x_index], XAxis, x_minimum, x_span);
                if (bin < 0)
                    continue;

                bins[bin]++;
                if (bins[bin] > max_count)
                    max_count = bins[bin];
            }
        }

        if (max_count == 0)
            return null;

        var pixels = new byte[density_size * density_size * 4];
        var fill = Colors.Black;
        for (int bin = 0; bin < density_size; bin++)
        {
            int height = Math.Clamp((int)Math.Round(bins[bin] / (double)max_count * density_size), 0, density_size);
            for (int y = density_size - height; y < density_size; y++)
                set_pixel(pixels, bin, y, fill);
        }

        return create_bitmap(pixels);
    }

    private void draw_gates(DrawingContext context)
    {
        foreach (var gate in resolve_plot_gates())
            draw_gate(context, gate);
    }

    private IEnumerable<GateDefinition> resolve_plot_gates()
    {
        if (Gates is not null)
            foreach (var gate in Gates)
                yield return gate;
        else if (Gate is not null)
            yield return Gate;
    }

    private void draw_gate(DrawingContext context, GateDefinition gate)
    {
        if (gate.Vertices.Count == 0 || XAxis is null || YAxis is null)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(20, 133, 255)), 2);
        var handle_fill = Brushes.White;
        var handle_stroke = new Pen(new SolidColorBrush(Color.FromRgb(20, 133, 255)), 1.2);

        if (gate.Kind == GateKind.Rectangle && gate.Vertices.Count >= 2)
        {
            var first = data_to_screen(gate.Vertices[0]);
            var second = data_to_screen(gate.Vertices[1]);
            context.DrawRectangle(null, pen, make_rect(first, second));
        }
        else if (gate.Kind is GateKind.Threshold or GateKind.Range)
        {
            foreach (var vertex in gate.Vertices)
            {
                var point = data_to_screen(vertex);
                context.DrawLine(pen, new Point(point.X, plot_rect.Top), new Point(point.X, plot_rect.Bottom));
            }
        }
        else if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant)
        {
            var point = data_to_screen(gate.Vertices[0]);
            context.DrawLine(pen, new Point(point.X, plot_rect.Top), new Point(point.X, plot_rect.Bottom));
            context.DrawLine(pen, new Point(plot_rect.Left, point.Y), new Point(plot_rect.Right, point.Y));
        }
        else
        {
            for (int index = 0; index < gate.Vertices.Count; index++)
            {
                var current = data_to_screen(gate.Vertices[index]);
                var next = data_to_screen(gate.Vertices[(index + 1) % gate.Vertices.Count]);
                context.DrawLine(pen, current, next);
            }
        }

        foreach (var vertex in gate.Vertices)
        {
            var point = data_to_screen(vertex);
            var rect = new Rect(point.X - 4, point.Y - 4, 8, 8);
            context.FillRectangle(handle_fill, rect);
            context.DrawRectangle(null, handle_stroke, rect);
        }

        draw_gate_annotation(context, gate);
    }

    private void draw_pending_gate(DrawingContext context)
    {
        if (pending_gate is null || XAxis is null || YAxis is null || pending_gate.Vertices.Count == 0)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(20, 133, 255)), 1.2, DashStyle.Dash);
        var handle_fill = Brushes.White;
        var handle_stroke = new Pen(new SolidColorBrush(Color.FromRgb(20, 133, 255)), 1.2);

        if (pending_gate.Kind == GateKind.Polygon)
        {
            for (int index = 0; index < pending_gate.Vertices.Count - 1; index++)
                context.DrawLine(pen, data_to_screen(pending_gate.Vertices[index]), data_to_screen(pending_gate.Vertices[index + 1]));

            if (has_pending_preview_point)
                context.DrawLine(pen, data_to_screen(pending_gate.Vertices[^1]), data_to_screen(pending_preview_point));
        }
        else if (pending_gate.Kind == GateKind.Rectangle)
        {
            var first = data_to_screen(pending_gate.Vertices[0]);
            var second_source = pending_gate.Vertices.Count > 1
                ? pending_gate.Vertices[1]
                : has_pending_preview_point ? pending_preview_point : pending_gate.Vertices[0];
            var second = data_to_screen(second_source);
            context.DrawRectangle(null, pen, make_rect(first, second));
        }
        else if (pending_gate.Kind == GateKind.Range)
        {
            foreach (var vertex in pending_gate.Vertices)
            {
                var point = data_to_screen(vertex);
                context.DrawLine(pen, new Point(point.X, plot_rect.Top), new Point(point.X, plot_rect.Bottom));
            }

            if (has_pending_preview_point)
            {
                var preview = data_to_screen(pending_preview_point);
                context.DrawLine(pen, new Point(preview.X, plot_rect.Top), new Point(preview.X, plot_rect.Bottom));
            }
        }

        foreach (var vertex in pending_gate.Vertices)
        {
            var point = data_to_screen(vertex);
            var rect = new Rect(point.X - 4, point.Y - 4, 8, 8);
            context.FillRectangle(handle_fill, rect);
            context.DrawRectangle(null, handle_stroke, rect);
        }
    }

    private void draw_gate_annotation(DrawingContext context, GateDefinition gate)
    {
        if (Group is null || gate.Vertices.Count == 0)
            return;

        int event_count = 0;
        double parent_frequency = 0;
        int sample_count = 0;
        foreach (var sample in Group.Samples)
        {
            var population = find_population(sample.Populations, gate);
            if (population is null)
                continue;

            event_count += population.EventCount;
            var statistic = population.Statistics.FirstOrDefault(item => item.Kind == StatisticKind.FrequencyOfParent);
            if (statistic is not null)
                parent_frequency += statistic.Value;
            sample_count++;
        }

        if (sample_count > 0)
            parent_frequency /= sample_count;

        var anchor = data_to_screen(gate.Vertices[0]);
        var origin = new Point(
            Math.Clamp(anchor.X + 10, plot_rect.Left + 8, plot_rect.Right - 120),
            Math.Clamp(anchor.Y - 24, plot_rect.Top + 8, plot_rect.Bottom - 28));
        string label = $"{gate.Name}  {event_count:N0}  {parent_frequency:0.#}%";
        var text = new FormattedText(
            label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            Brushes.White);
        var background = new Rect(origin.X - 6, origin.Y - 4, text.Width + 12, text.Height + 8);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(210, 31, 111, 235)), background, 4);
        context.DrawText(text, origin);
    }

    private void draw_grid(DrawingContext context)
    {
        if (XAxis is null || YAxis is null)
            return;

        var major_pen = new Pen(new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)), 1);
        var minor_pen = new Pen(new SolidColorBrush(Color.FromArgb(18, 0, 0, 0)), 1);
        foreach (double value in minor_axis_ticks(XAxis))
            draw_vertical_grid_line(context, value, minor_pen);
        foreach (double value in major_axis_ticks(XAxis))
            draw_vertical_grid_line(context, value, major_pen);

        if (is_histogram_mode())
            return;

        foreach (double value in minor_axis_ticks(YAxis))
            draw_horizontal_grid_line(context, value, minor_pen);
        foreach (double value in major_axis_ticks(YAxis))
            draw_horizontal_grid_line(context, value, major_pen);
    }

    private void draw_axes(DrawingContext context)
    {
        if (XAxis is null || YAxis is null)
            return;

        var axis_pen = new Pen(new SolidColorBrush(Color.FromRgb(94, 94, 94)), 1);
        var tick_pen = new Pen(new SolidColorBrush(Color.FromRgb(120, 120, 120)), 1);
        context.DrawLine(axis_pen, new Point(plot_rect.Left, plot_rect.Bottom), new Point(plot_rect.Right, plot_rect.Bottom));
        context.DrawLine(axis_pen, new Point(plot_rect.Left, plot_rect.Top), new Point(plot_rect.Left, plot_rect.Bottom));

        foreach (double value in minor_axis_ticks(XAxis))
        {
            double x = data_to_screen(new Point(value, YAxis.Minimum)).X;
            context.DrawLine(tick_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 3));
        }

        foreach (double value in major_axis_ticks(XAxis))
        {
            double x = data_to_screen(new Point(value, YAxis.Minimum)).X;
            context.DrawLine(tick_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 5));
            draw_centered_text(context, format_axis_value(value), new Point(x, plot_rect.Bottom + 8), 10, Color.FromRgb(128, 128, 128));
        }

        if (!is_histogram_mode())
        {
            foreach (double value in minor_axis_ticks(YAxis))
            {
                double y = data_to_screen(new Point(XAxis.Minimum, value)).Y;
                context.DrawLine(tick_pen, new Point(plot_rect.Left - 3, y), new Point(plot_rect.Left, y));
            }

            foreach (double value in major_axis_ticks(YAxis))
            {
                double y = data_to_screen(new Point(XAxis.Minimum, value)).Y;
                context.DrawLine(tick_pen, new Point(plot_rect.Left - 5, y), new Point(plot_rect.Left, y));
                draw_right_aligned_text(context, format_axis_value(value), new Point(plot_rect.Left - 10, y - 7), 10, Color.FromRgb(128, 128, 128));
            }

            draw_vertical_centered_text(context, YAxis.ChannelName ?? "", new Point(plot_rect.Left - 62, plot_rect.Top + plot_rect.Height / 2), 14, Color.FromRgb(190, 198, 210));
        }

        draw_text(
            context, XAxis.ChannelName ?? "", 
            new Point(plot_rect.Left + plot_rect.Width / 2 - 18, plot_rect.Bottom + 34), 14, 
            Color.FromRgb(190, 198, 210), bolded: true
        );
    }

    private IEnumerable<FlowSample> resolve_samples()
    {
        if (Sample is not null)
        {
            yield return Sample;
            yield break;
        }

        if (Gate is not null && Group is not null)
            foreach (var sample in Group.Samples)
                yield return sample;
        else if (Group is not null)
            foreach (var sample in Group.Samples)
                yield return sample;
    }

    private IEnumerable<int> resolve_event_indices(FlowSample sample)
    {
        if (Population is not null && Sample == sample)
            return Population.EventIndices;

        if (Gate is not null)
        {
            var population = find_population(sample.Populations, Gate);
            if (population is not null)
                return population.EventIndices;
        }

        return Enumerable.Range(0, sample.EventCount);
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, GateDefinition gate)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate)
                return population;
            var child = find_population(population.Children, gate);
            if (child is not null)
                return child;
        }

        return null;
    }

    private int to_bin(double value, AxisSettings axis, double transformed_minimum, double transformed_span)
    {
        double normalized = (axis.Scale.Transform(value) - transformed_minimum) / transformed_span;
        if (normalized < 0 || normalized > 1)
            return -1;

        return Math.Clamp((int)(normalized * (density_size - 1)), 0, density_size - 1);
    }

    private void begin_gate_creation_point(Point data_point, int click_count)
    {
        if (XAxis is null || YAxis is null)
            return;

        if (pending_gate is not null && pending_gate.Kind != active_tool_gate_kind())
            clear_pending_gate();

        if (pending_gate?.Kind == GateKind.Polygon && click_count > 1 && pending_gate.Vertices.Count >= 3)
        {
            commit_created_gate(pending_gate);
            return;
        }

        pending_gate ??= create_empty_gate(active_tool_gate_kind());

        if (pending_gate.Kind is GateKind.Threshold or GateKind.Quadrant or GateKind.CurlyQuadrant)
        {
            pending_gate.Vertices.Clear();
            pending_gate.Vertices.Add(one_dimensional_point_if_needed(pending_gate.Kind, data_point));
            dragging_pending_vertex_index = 0;
            has_pending_preview_point = false;
            InvalidateVisual();
            return;
        }

        if (pending_gate.Kind is GateKind.Rectangle or GateKind.Range && pending_gate.Vertices.Count == 0)
        {
            pending_gate.Vertices.Add(one_dimensional_point_if_needed(pending_gate.Kind, data_point));
            pending_gate.Vertices.Add(one_dimensional_point_if_needed(pending_gate.Kind, data_point));
            dragging_pending_vertex_index = 1;
            has_pending_preview_point = false;
            InvalidateVisual();
            return;
        }

        pending_gate.Vertices.Add(one_dimensional_point_if_needed(pending_gate.Kind, data_point));
        dragging_pending_vertex_index = pending_gate.Vertices.Count - 1;
        has_pending_preview_point = false;
        InvalidateVisual();
    }

    private void settle_pending_gate_point()
    {
        if (pending_gate is null)
            return;

        if (pending_gate.Kind == GateKind.Polygon)
        {
            InvalidateVisual();
            return;
        }

        if (pending_gate.Kind is GateKind.Rectangle or GateKind.Range)
        {
            if (pending_gate.Vertices.Count >= 2 && !points_are_nearly_equal(pending_gate.Vertices[0], pending_gate.Vertices[1]))
                commit_created_gate(pending_gate);
            else clear_pending_gate();
            return;
        }

        if (pending_gate.Kind is GateKind.Threshold or GateKind.Quadrant or GateKind.CurlyQuadrant)
            commit_created_gate(pending_gate);
    }

    private GateDefinition create_empty_gate(GateKind kind) =>
        new()
        {
            XChannel = XAxis?.ChannelName ?? "",
            YChannel = kind is GateKind.Threshold or GateKind.Range ? null : YAxis?.ChannelName,
            Kind = kind
        };

    private GateKind active_tool_gate_kind() =>
        ActiveTool switch
        {
            GatingTool.Polygon => GateKind.Polygon,
            GatingTool.Rectangle => GateKind.Rectangle,
            GatingTool.Quadrant => GateKind.Quadrant,
            GatingTool.CurlyQuadrant => GateKind.CurlyQuadrant,
            GatingTool.Threshold => GateKind.Threshold,
            GatingTool.Range => GateKind.Range,
            _ => GateKind.Rectangle
        };

    private static Point one_dimensional_point_if_needed(GateKind kind, Point point) =>
        kind is GateKind.Threshold or GateKind.Range ? new Point(point.X, 0) : point;

    private static bool points_are_nearly_equal(Point first, Point second) =>
        Math.Abs(first.X - second.X) < 0.000001 && Math.Abs(first.Y - second.Y) < 0.000001;

    private static Rect make_rect(Point first, Point second) =>
        new(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(first.X - second.X),
            Math.Abs(first.Y - second.Y));

    private void commit_created_gate(GateDefinition gate)
    {
        pending_gate = null;
        has_pending_preview_point = false;
        if (GateCreatedCommand?.CanExecute(gate) == true)
            GateCreatedCommand.Execute(gate);
        InvalidateVisual();
    }

    private void clear_pending_gate()
    {
        pending_gate = null;
        has_pending_preview_point = false;
        InvalidateVisual();
    }

    private Point data_to_screen(Point data)
    {
        if (XAxis is null || YAxis is null)
            return default;

        double x = normalize(data.X, XAxis);
        double y = normalize(data.Y, YAxis);
        return new Point(plot_rect.Left + x * plot_rect.Width, plot_rect.Bottom - y * plot_rect.Height);
    }

    private Point screen_to_data(Point screen)
    {
        if (XAxis is null || YAxis is null)
            return default;

        double x_normalized = Math.Clamp((screen.X - plot_rect.Left) / plot_rect.Width, 0, 1);
        double y_normalized = Math.Clamp((plot_rect.Bottom - screen.Y) / plot_rect.Height, 0, 1);
        double x = denormalize(x_normalized, XAxis);
        double y = denormalize(y_normalized, YAxis);
        return new Point(x, y);
    }

    private static double normalize(double value, AxisSettings axis)
    {
        double minimum = axis.Scale.Transform(axis.Minimum);
        double maximum = axis.Scale.Transform(axis.Maximum);
        double transformed = axis.Scale.Transform(value);
        if (maximum <= minimum)
            return 0;

        return Math.Clamp((transformed - minimum) / (maximum - minimum), 0, 1);
    }

    private static double denormalize(double normalized, AxisSettings axis)
    {
        double minimum = axis.Scale.Transform(axis.Minimum);
        double maximum = axis.Scale.Transform(axis.Maximum);
        return axis.Scale.InverseTransform(minimum + normalized * (maximum - minimum));
    }

    private int find_nearest_vertex(Point point, out GateDefinition? found_gate)
    {
        found_gate = null;
        double best_distance = 12;
        int best_index = -1;
        foreach (var gate in resolve_plot_gates())
        {
            for (int index = 0; index < gate.Vertices.Count; index++)
            {
                var vertex = data_to_screen(gate.Vertices[index]);
                double distance = gate.Kind is GateKind.Threshold or GateKind.Range
                    ? Math.Abs(vertex.X - point.X)
                    : gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant
                    ? Math.Min(Math.Abs(vertex.X - point.X), Math.Abs(vertex.Y - point.Y))
                    : Math.Sqrt(Math.Pow(vertex.X - point.X, 2) + Math.Pow(vertex.Y - point.Y, 2));
                if (distance < best_distance)
                {
                    best_distance = distance;
                    best_index = index;
                    found_gate = gate;
                }
            }
        }

        return best_index;
    }

    private static Color density_color(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (value < 0.25)
            return Color.FromRgb(21, 35, Convert.ToByte(110 + value * 360));
        if (value < 0.5)
            return Color.FromRgb(0, Convert.ToByte(80 + value * 260), 220);
        if (value < 0.75)
            return Color.FromRgb(Convert.ToByte((value - 0.5) * 720), 230, 95);

        return Color.FromRgb(255, Convert.ToByte(220 - (value - 0.75) * 260), 40);
    }

    private static double[,] smooth_density(double[,] source, int passes)
    {
        passes = Math.Clamp(passes, 0, 12);
        var result = source;
        for (int pass = 0; pass < passes; pass++)
            result = smooth_density_once(result);

        return result;
    }

    private static double[,] smooth_density_once(double[,] source)
    {
        double[] kernel = [1, 4, 6, 4, 1];
        var horizontal = new double[density_size, density_size];
        var result = new double[density_size, density_size];

        for (int x = 0; x < density_size; x++)
        for (int y = 0; y < density_size; y++)
        {
            double sum = 0;
            double weight = 0;
            for (int offset = -2; offset <= 2; offset++)
            {
                int sample_x = x + offset;
                if (sample_x < 0 || sample_x >= density_size)
                    continue;

                double kernel_weight = kernel[offset + 2];
                sum += source[sample_x, y] * kernel_weight;
                weight += kernel_weight;
            }

            horizontal[x, y] = weight == 0 ? 0 : sum / weight;
        }

        for (int x = 0; x < density_size; x++)
        for (int y = 0; y < density_size; y++)
        {
            double sum = 0;
            double weight = 0;
            for (int offset = -2; offset <= 2; offset++)
            {
                int sample_y = y + offset;
                if (sample_y < 0 || sample_y >= density_size)
                    continue;

                double kernel_weight = kernel[offset + 2];
                sum += horizontal[x, sample_y] * kernel_weight;
                weight += kernel_weight;
            }

            result[x, y] = weight == 0 ? 0 : sum / weight;
        }

        return result;
    }

    private static double[] contour_levels(int count)
    {
        count = Math.Clamp(count, 2, 80);
        var levels = new double[count];
        for (int index = 0; index < count; index++)
            levels[index] = 0.08 + 0.88 * (index + 1) / (count + 1);

        return levels;
    }

    private static Color plot_color(double value, PlotMode mode, int level_count)
    {
        if (mode == PlotMode.Zebra)
        {
            Color[] cycle =
            [
                Color.FromRgb(220, 220, 220),
                Color.FromRgb(150, 150, 150),
                Color.FromRgb(85, 85, 85),
                Color.FromRgb(35, 35, 35),
                Colors.Black
            ];
            int bands = Math.Clamp(level_count * 5, 10, 400);
            int band = Math.Clamp((int)Math.Floor(value * bands), 0, bands - 1);
            return cycle[band % cycle.Length];
        }

        return density_color(value);
    }

    private void draw_text(DrawingContext context, string text, Point origin, double size, Color color, bool bolded = false)
    {
        var typeface = current_typeface();
        if (bolded) typeface = current_typeface_bolded();
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            new SolidColorBrush(color));
        context.DrawText(formatted, origin);
    }

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private void draw_right_aligned_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width, origin.Y));
    }

    private void draw_vertical_centered_text(DrawingContext context, string text, Point center, double size, Color color, bool bolded = false)
    {
        var formatted = create_formatted_text(text, size, color, bolded);
        var transform =
            Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(-Math.PI / 2)
            * Matrix.CreateTranslation(center.X, center.Y);

        using (context.PushTransform(transform))
            context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private FormattedText create_formatted_text(string text, double size, Color color, bool bolded = false) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            bolded ? current_typeface_bolded() : current_typeface(),
            size,
            new SolidColorBrush(color));

    private Typeface current_typeface() =>
        new(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this),
            TextElement.GetFontStretch(this));
    
    private Typeface current_typeface_bolded() =>
        new(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            FontWeight.Bold,
            TextElement.GetFontStretch(this));

    private static string format_axis_value(double value)
    {
        if (Math.Abs(value) >= 1000)
            return $"{value / 1000:0.#}k";

        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private bool is_histogram_mode() =>
        PlotMode == PlotMode.Histogram;

    private void draw_vertical_grid_line(DrawingContext context, double value, Pen pen)
    {
        if (YAxis is null || !is_tick_in_range(value, XAxis))
            return;

        double x = data_to_screen(new Point(value, YAxis.Minimum)).X;
        context.DrawLine(pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
    }

    private void draw_horizontal_grid_line(DrawingContext context, double value, Pen pen)
    {
        if (XAxis is null || !is_tick_in_range(value, YAxis))
            return;

        double y = data_to_screen(new Point(XAxis.Minimum, value)).Y;
        context.DrawLine(pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
    }

    private static IEnumerable<double> major_axis_ticks(AxisSettings axis)
    {
        double[] values = axis.ScaleKind == CoordinateScaleKind.Logicle
            ? [0, 1000, 10000, 100000]
            : [0, 50000, 100000, 150000, 200000];

        return values.Where(value => is_tick_in_range(value, axis));
    }

    private static IEnumerable<double> minor_axis_ticks(AxisSettings axis)
    {
        if (axis.Maximum <= axis.Minimum)
            yield break;

        if (axis.ScaleKind == CoordinateScaleKind.Logicle)
        {
            for (int multiplier = 2; multiplier <= 9; multiplier++)
            {
                double value = multiplier * 1000;
                if (is_tick_in_range(value, axis))
                    yield return value;
            }

            for (int multiplier = 2; multiplier <= 9; multiplier++)
            {
                double value = multiplier * 10000;
                if (is_tick_in_range(value, axis))
                    yield return value;
            }

            yield break;
        }

        for (double value = 10000; value < 200000; value += 10000)
        {
            if (value % 50000 == 0)
                continue;
            if (is_tick_in_range(value, axis))
                yield return value;
        }
    }

    private static bool is_tick_in_range(double value, AxisSettings? axis) =>
        axis is not null && value >= axis.Minimum && value <= axis.Maximum;

    private void resubscribe_group(FlowGroup? group)
    {
        if (subscribed_group is not null)
        {
            subscribed_group.Samples.CollectionChanged -= group_samples_collection_changed;
            unsubscribe_group_samples();
        }

        subscribed_group = group;
        if (subscribed_group is null)
            return;

        subscribed_group.Samples.CollectionChanged += group_samples_collection_changed;
        subscribe_group_samples();
    }

    private void resubscribe_sample(FlowSample? sample)
    {
        if (subscribed_sample is not null)
            subscribed_sample.PropertyChanged -= sample_property_changed;

        subscribed_sample = sample;
        if (subscribed_sample is not null)
            subscribed_sample.PropertyChanged += sample_property_changed;
        if (subscribed_group is not null)
        {
            unsubscribe_group_samples();
            subscribe_group_samples();
        }
    }

    private void group_samples_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        unsubscribe_group_samples();
        subscribe_group_samples();
        invalidate_plot_cache();
    }

    private void subscribe_group_samples()
    {
        if (subscribed_group is null)
            return;

        foreach (var sample in subscribed_group.Samples)
        {
            if (ReferenceEquals(sample, subscribed_sample))
                continue;

            sample.PropertyChanged += sample_property_changed;
            subscribed_group_samples.Add(sample);
        }
    }

    private void unsubscribe_group_samples()
    {
        foreach (var sample in subscribed_group_samples)
            sample.PropertyChanged -= sample_property_changed;
        subscribed_group_samples.Clear();
    }

    private void sample_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FlowSample.CompensatedEvents) or null or "")
            invalidate_plot_cache();
    }

    private void resubscribe_axis(ref AxisSettings? old_axis, AxisSettings? new_axis)
    {
        if (old_axis is not null)
            old_axis.PropertyChanged -= axis_property_changed;

        old_axis = new_axis;
        if (old_axis is not null)
            old_axis.PropertyChanged += axis_property_changed;
    }

    private void axis_property_changed(object? sender, PropertyChangedEventArgs e) =>
        invalidate_plot_cache();

    private void invalidate_plot_cache()
    {
        cached_plot_bitmap = null;
        InvalidateVisual();
    }

    private static WriteableBitmap create_bitmap(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(density_size, density_size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        int row_bytes = density_size * 4;
        if (frame.RowBytes == row_bytes)
        {
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            return bitmap;
        }

        for (int y = 0; y < density_size; y++)
            Marshal.Copy(pixels, y * row_bytes, IntPtr.Add(frame.Address, y * frame.RowBytes), row_bytes);

        return bitmap;
    }

    private static void set_pixel(byte[] pixels, int x, int y, Color color)
    {
        int offset = (y * density_size + x) * 4;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
        pixels[offset + 3] = color.A;
    }
}
