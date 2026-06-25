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
using Avalonia.Threading;
using gated.Models;

namespace gated.Controls;

public sealed class FlowPlotView : Control
{
    private const double large_dot_radius = 1.75;
    private const double large_dot_opacity = 0.6;

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

    public static readonly StyledProperty<DotColorSettings?> DotColorProperty =
        AvaloniaProperty.Register<FlowPlotView, DotColorSettings?>(nameof(DotColor));

    public static readonly StyledProperty<PlotMode> PlotModeProperty =
        AvaloniaProperty.Register<FlowPlotView, PlotMode>(nameof(PlotMode), PlotMode.Density);

    public static readonly StyledProperty<bool> ShowOutlierPointsProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(ShowOutlierPoints), true);

    public static readonly StyledProperty<bool> DrawLargeDotsProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(DrawLargeDots));

    public static readonly StyledProperty<bool> ShowGridlinesProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(ShowGridlines), true);

    public static readonly StyledProperty<bool> ShowGateAnnotationsProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(ShowGateAnnotations), true);

    public static readonly StyledProperty<bool> ShowGateAnnotationNamesProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(ShowGateAnnotationNames));

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

    public static readonly StyledProperty<bool> TransformPreparingProperty =
        AvaloniaProperty.Register<FlowPlotView, bool>(nameof(TransformPreparing));

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
    private DotColorSettings? subscribed_dot_color;
    private string? cached_contour_key;
    private ContourGeometry? cached_contour_geometry;
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
            DotColorProperty,
            PlotModeProperty,
            ShowOutlierPointsProperty,
            DrawLargeDotsProperty,
            ShowGridlinesProperty,
            ShowGateAnnotationsProperty,
            ShowGateAnnotationNamesProperty,
            ContourLevelCountProperty,
            DensitySmoothingProperty,
            ActiveToolProperty,
            GateCreatedCommandProperty,
            GateEditedCommandProperty,
            TransformPreparingProperty);
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

    public DotColorSettings? DotColor
    {
        get => GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
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

    public bool DrawLargeDots
    {
        get => GetValue(DrawLargeDotsProperty);
        set => SetValue(DrawLargeDotsProperty, value);
    }

    public bool ShowGridlines
    {
        get => GetValue(ShowGridlinesProperty);
        set => SetValue(ShowGridlinesProperty, value);
    }

    public bool ShowGateAnnotations
    {
        get => GetValue(ShowGateAnnotationsProperty);
        set => SetValue(ShowGateAnnotationsProperty, value);
    }

    public bool ShowGateAnnotationNames
    {
        get => GetValue(ShowGateAnnotationNamesProperty);
        set => SetValue(ShowGateAnnotationNamesProperty, value);
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

    public bool TransformPreparing
    {
        get => GetValue(TransformPreparingProperty);
        set => SetValue(TransformPreparingProperty, value);
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
        if (change.Property == DotColorProperty)
            resubscribe_dot_color(DotColor);
        if (change.Property == ActiveToolProperty)
            clear_pending_gate();
        if (change.Property == XAxisProperty || change.Property == YAxisProperty)
            clear_pending_gate();
        if (change.Property == TransformPreparingProperty && !TransformPreparing)
            invalidate_plot_cache();
        if (change.Property == GroupProperty
            || change.Property == SampleProperty
            || change.Property == GateProperty
            || change.Property == GatesProperty
            || change.Property == PopulationProperty
            || change.Property == DotColorProperty
            || change.Property == PlotModeProperty
            || change.Property == ShowOutlierPointsProperty
            || change.Property == DrawLargeDotsProperty
            || change.Property == DensitySmoothingProperty
            || change.Property == ContourLevelCountProperty)
            invalidate_plot_cache();
        if ((change.Property == XAxisProperty || change.Property == YAxisProperty) && !TransformPreparing)
            invalidate_plot_cache();
        if (change.Property == ShowGridlinesProperty)
            InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pointer = e.GetPosition(this);
        if (dragging_pending_vertex_index >= 0 && pending_gate is not null)
        {
            var pending_data_point = one_dimensional_point_if_needed(pending_gate.Kind, screen_to_data(pointer));
            pending_gate.Vertices[dragging_pending_vertex_index] = pending_data_point;
            has_pending_preview_point = false;
            InvalidateVisual();
            return;
        }

        if (plot_rect.Contains(pointer) && (pending_gate is not null || ActiveTool is not GatingTool.View))
        {
            pending_preview_point = screen_to_data(pointer);
            has_pending_preview_point = true;
            InvalidateVisual();
        }

        if (dragging_vertex_index < 0 || dragging_gate is null || XAxis is null || YAxis is null)
        {
            Cursor = find_nearest_vertex(pointer, out _) >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            return;
        }

        var data_point = screen_to_data(pointer);
        if (dragging_vertex_index < dragging_gate.Vertices.Count)
        {
            dragging_gate.Vertices[dragging_vertex_index] = adjusted_drag_vertex(dragging_gate, dragging_vertex_index, data_point);
            gate_was_edited = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gate_was_edited && dragging_gate is not null && GateEditedCommand?.CanExecute(dragging_gate) == true)
        {
            invalidate_plot_cache();
            GateEditedCommand.Execute(dragging_gate);
        }

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
        else draw_density(context);
        if (PlotMode == PlotMode.Contour)
            draw_contours(context);

        draw_axes(context);
        draw_gates(context);
        draw_active_tool_preview(context);
        draw_pending_gate(context);
    }

    private void draw_density(DrawingContext context)
    {
        draw_plot_bitmap(context, false);
    }

    private static void add_filled_contour_pixels(byte[] pixels, double[,] normalized_density, double[] levels)
    {
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

    private void draw_contours(DrawingContext context)
    {
        var geometry = get_contour_geometry();
        if (geometry is null)
            return;

        var pen = new Pen(Brushes.Black, 0.8);
        foreach (var segment in geometry.Segments)
            context.DrawLine(pen, density_to_screen(segment.Start), density_to_screen(segment.End));
    }

    private ContourGeometry? get_contour_geometry()
    {
        string key = contour_geometry_key();
        if (cached_contour_geometry is not null && cached_contour_key == key)
            return cached_contour_geometry;

        var grid = create_density_grid();
        if (grid is null)
        {
            cached_contour_key = key;
            cached_contour_geometry = null;
            return null;
        }

        var normalized = smooth_density(normalized_density_grid(grid.Value.density, grid.Value.max_density), DensitySmoothing);
        var segments = create_contour_segments(normalized, contour_levels(ContourLevelCount));
        cached_contour_key = key;
        cached_contour_geometry = new ContourGeometry(segments);
        return cached_contour_geometry;
    }

    private string contour_geometry_key() =>
        string.Join("|",
            PlotMode,
            DensitySmoothing,
            ContourLevelCount,
            XAxis?.ChannelName,
            XAxis?.Minimum,
            XAxis?.Maximum,
            XAxis?.ScaleKind,
            XAxis?.LogicleTopOfScale,
            XAxis?.LogicleDecades,
            XAxis?.LogicleLinearizationWidth,
            XAxis?.LogicleNegativeDecades,
            YAxis?.ChannelName,
            YAxis?.Minimum,
            YAxis?.Maximum,
            YAxis?.ScaleKind,
            YAxis?.LogicleTopOfScale,
            YAxis?.LogicleDecades,
            YAxis?.LogicleLinearizationWidth,
            YAxis?.LogicleNegativeDecades,
            Sample?.Id,
            Gate?.Id,
            Population?.Region);

    private static IReadOnlyList<ContourSegment> create_contour_segments(double[,] density, double[] levels)
    {
        var segments = new List<ContourSegment>();
        foreach (double level in levels)
            add_contour_level_segments(segments, density, level);
        return segments;
    }

    private static void add_contour_level_segments(List<ContourSegment> segments, double[,] density, double level)
    {
        for (int x = 0; x < density_size - 1; x++)
        for (int y = 0; y < density_size - 1; y++)
        {
            var points = contour_cell_points(density[x, y], density[x + 1, y], density[x + 1, y + 1], density[x, y + 1], x, y, level);
            for (int index = 0; index + 1 < points.Count; index += 2)
                segments.Add(new ContourSegment(points[index], points[index + 1]));
        }
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
            return create_dotplot_bitmap(grid.Value.density, grid.Value.colors, grid.Value.total_count, DrawLargeDots);

        var normalized_density = normalized_density_grid(grid.Value.density, grid.Value.max_density);
        var pixels = new byte[density_size * density_size * 4];
        if (PlotMode == PlotMode.Contour)
        {
            if (ShowOutlierPoints)
                add_dotplot_pixels(pixels, grid.Value.density, grid.Value.colors, grid.Value.total_count, DrawLargeDots);

            normalized_density = smooth_density(normalized_density, DensitySmoothing);
            double[] levels = contour_levels(ContourLevelCount);
            add_filled_contour_pixels(pixels, normalized_density, levels);
            return create_bitmap(pixels);
        }

        if (PlotMode == PlotMode.Zebra)
            normalized_density = smooth_density(normalized_density, DensitySmoothing);
        if (PlotMode == PlotMode.Zebra && ShowOutlierPoints)
            add_dotplot_pixels(pixels, grid.Value.density, grid.Value.colors, grid.Value.total_count, DrawLargeDots);

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

    private (int[,] density, Color?[,] colors, int max_density, int total_count)? create_density_grid()
    {
        var samples = resolve_samples().ToArray();
        if (samples.Length == 0 || XAxis is null || YAxis is null)
            return null;

        var density = new int[density_size, density_size];
        var category_colors = should_color_dots() ? create_category_colors(samples, DotColor!.ChannelName, DotColor.Palette) : null;
        var color_accumulator = should_color_dots() ? new DotColorAccumulator(DotColor!.Palette, category_colors) : null;
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
            int[] event_indices = resolve_event_indices(sample);
            var x_values = sample.GetChannelValues(XAxis.ChannelName, event_indices);
            var y_values = sample.GetChannelValues(YAxis.ChannelName, event_indices);
            var color_values = should_color_dots() ? sample.GetChannelValues(DotColor!.ChannelName, event_indices) : [];
            var color_embedding = should_color_dots() && sample.Embeddings.TryGetValue(DotColor!.ChannelName, out var embedding)
                ? embedding
                : null;
            if (x_values.Length == 0 || y_values.Length == 0)
                continue;

            color_accumulator?.ConfigureCategories(color_embedding);
            for (int index = 0; index < event_indices.Length; index++)
            {
                int x_bin = to_bin(x_values[index], XAxis, x_minimum, x_span);
                int y_bin = to_bin(y_values[index], YAxis, y_minimum, y_span);
                if (x_bin < 0 || y_bin < 0)
                    continue;

                total_count++;
                int value = ++density[x_bin, y_bin];
                if (value > max_density)
                    max_density = value;
                if (index >= 0 && index < color_values.Length)
                    color_accumulator?.Add(x_bin, y_bin, color_values[index], DotColor!.UseLogScale);
            }
        }

        return max_density == 0 ? null : (density, color_accumulator?.CreateColors() ?? new Color?[density_size, density_size], max_density, total_count);
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

    private bool should_color_dots() =>
        DotColor is { HasChannel: true } && PlotMode == PlotMode.Dotplot;

    private static double transform_dot_color_value(double value, bool use_log_scale) =>
        use_log_scale ? Math.Log10(1 + Math.Max(0, value)) : value;

    private static Dictionary<string, Color>? create_category_colors(
        IEnumerable<FlowSample> samples,
        string embedding_name,
        PlotColorPalette palette)
    {
        var labels = samples
            .Select(sample => sample.Embeddings.TryGetValue(embedding_name, out var embedding) ? embedding : null)
            .Where(embedding => embedding is { Kind: EmbeddingValueKind.Integer })
            .SelectMany(embedding => embedding!.Categories.Values)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();
        if (labels.Length == 0)
            return null;

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (int index = 0; index < labels.Length; index++)
        {
            double normalized = labels.Length == 1 ? 0.5 : index / (double)(labels.Length - 1);
            colors[labels[index]] = palette_color(normalized, palette);
        }

        return colors;
    }

    private sealed class DotColorAccumulator
    {
        private readonly PlotColorPalette palette;
        private readonly IReadOnlyDictionary<string, Color>? category_colors;
        private readonly double[,] continuous_sums = new double[density_size, density_size];
        private readonly int[,] continuous_counts = new int[density_size, density_size];
        private readonly Dictionary<string, int>?[] category_counts = new Dictionary<string, int>?[density_size * density_size];
        private Dictionary<int, string>? current_category_labels;

        public DotColorAccumulator(PlotColorPalette palette, IReadOnlyDictionary<string, Color>? category_colors)
        {
            this.palette = palette;
            this.category_colors = category_colors;
        }

        public void ConfigureCategories(EmbeddingData? embedding)
        {
            if (embedding is not { Kind: EmbeddingValueKind.Integer } || embedding.Categories.Count == 0)
            {
                current_category_labels = null;
                return;
            }

            current_category_labels = new Dictionary<int, string>(embedding.Categories);
        }

        public void Add(int x_bin, int y_bin, float value, bool use_log_scale)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return;

            if (current_category_labels is not null && category_colors is not null)
            {
                int category_id = Convert.ToInt32(value);
                if (!current_category_labels.TryGetValue(category_id, out string? label))
                    return;

                int index = y_bin * density_size + x_bin;
                var counts = category_counts[index] ??= new Dictionary<string, int>(StringComparer.Ordinal);
                counts[label] = counts.TryGetValue(label, out int count) ? count + 1 : 1;
                return;
            }

            continuous_sums[x_bin, y_bin] += transform_dot_color_value(value, use_log_scale);
            continuous_counts[x_bin, y_bin]++;
        }

        public Color?[,] CreateColors()
        {
            var colors = create_continuous_colors();
            for (int index = 0; index < category_counts.Length; index++)
            {
                var counts = category_counts[index];
                if (counts is null || counts.Count == 0)
                    continue;

                var label = counts
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .First()
                    .Key;
                int x = index % density_size;
                int y = index / density_size;
                colors[x, y] = category_color(label);
            }

            return colors;
        }

        private Color?[,] create_continuous_colors()
        {
            double minimum = double.PositiveInfinity;
            double maximum = double.NegativeInfinity;
            for (int x = 0; x < density_size; x++)
            for (int y = 0; y < density_size; y++)
            {
                if (continuous_counts[x, y] <= 0)
                    continue;
                double value = continuous_sums[x, y] / continuous_counts[x, y];
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }

            var colors = new Color?[density_size, density_size];
            if (double.IsInfinity(minimum) || maximum <= minimum)
                return colors;

            for (int x = 0; x < density_size; x++)
            for (int y = 0; y < density_size; y++)
            {
                if (continuous_counts[x, y] <= 0)
                    continue;
                double value = continuous_sums[x, y] / continuous_counts[x, y];
                double normalized = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
                colors[x, y] = palette_color(normalized, palette);
            }

            return colors;
        }

        private Color category_color(string label)
        {
            if (category_colors is not null && category_colors.TryGetValue(label, out var color))
                return color;
            return Colors.Black;
        }
    }

    private static WriteableBitmap create_dotplot_bitmap(int[,] density, Color?[,]? colors, int total_count, bool large_dots)
    {
        var pixels = new byte[density_size * density_size * 4];
        add_dotplot_pixels(pixels, density, colors, total_count, large_dots);
        return create_bitmap(pixels);
    }

    private static void add_dotplot_pixels(byte[] pixels, int[,] density, Color?[,]? colors, int total_count, bool large_dots)
    {
        double threshold = large_dots ? 0 : total_count * 0.00001;
        for (int x_bin = 0; x_bin < density_size; x_bin++)
        for (int y_bin = 0; y_bin < density_size; y_bin++)
        {
            if (density[x_bin, y_bin] <= threshold)
                continue;

            var color = colors?[x_bin, y_bin] ?? Colors.Black;
            if (large_dots)
                set_large_dot(pixels, x_bin, density_size - 1 - y_bin, color, density[x_bin, y_bin]);
            else
                set_pixel(pixels, x_bin, density_size - 1 - y_bin, color);
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
            int[] event_indices = resolve_event_indices(sample);
            var x_values = sample.GetChannelValues(XAxis.ChannelName, event_indices);
            if (x_values.Length == 0)
                continue;

            for (int index = 0; index < event_indices.Length; index++)
            {
                int bin = to_bin(x_values[index], XAxis, x_minimum, x_span);
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
                if (!is_gate_hidden_by_plot_mode(gate))
                    yield return gate;
        else if (Gate is not null)
        {
            if (!is_gate_hidden_by_plot_mode(Gate))
                yield return Gate;
        }
    }

    private bool is_gate_hidden_by_plot_mode(GateDefinition gate)
    {
        if (XAxis is null || gate.XChannel != XAxis.ChannelName)
            return true;
        if (gate.IsOneDimensional || string.IsNullOrWhiteSpace(gate.YChannel))
            return PlotMode != PlotMode.Histogram;
        return PlotMode == PlotMode.Histogram || YAxis is null || gate.YChannel != YAxis.ChannelName;
    }

    private void draw_gate(DrawingContext context, GateDefinition gate)
    {
        if (gate.Vertices.Count == 0 || XAxis is null || YAxis is null)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 0, 0)), 2);
        var handle_fill = Brushes.White;
        var handle_stroke = new Pen(new SolidColorBrush(Color.FromRgb(0, 0, 0)), 1.2);

        if (gate.Kind == GateKind.Rectangle && gate.Vertices.Count >= 2)
        {
            var first = data_to_screen(gate.Vertices[0]);
            var second = data_to_screen(gate.Vertices[1]);
            context.DrawRectangle(null, pen, make_rect(first, second));
        }
        else if (gate.Kind is GateKind.Threshold or GateKind.Range)
        {
            draw_histogram_gate_indicator(context, gate.Vertices, Color.FromRgb(0, 0, 0), gate.Kind == GateKind.Range);
        }
        else if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
            draw_quadrant_gate(context, gate, pen);
        else
        {
            for (int index = 0; index < gate.Vertices.Count; index++)
            {
                var current = data_to_screen(gate.Vertices[index]);
                var next = data_to_screen(gate.Vertices[(index + 1) % gate.Vertices.Count]);
                context.DrawLine(pen, current, next);
            }
        }

        if (gate.Kind is GateKind.Threshold or GateKind.Range or GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
        {
            if (ShowGateAnnotations)
                draw_gate_annotation(context, gate);
            return;
        }

        foreach (var vertex in gate.Vertices)
        {
            var point = data_to_screen(vertex);
            var rect = new Rect(point.X - 4, point.Y - 4, 8, 8);
            context.FillRectangle(handle_fill, rect);
            context.DrawRectangle(null, handle_stroke, rect);
        }

        if (ShowGateAnnotations)
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
            var vertices = pending_gate.Vertices.ToList();
            if (has_pending_preview_point)
                vertices.Add(pending_preview_point);
            draw_histogram_gate_indicator(context, vertices, Color.FromRgb(20, 133, 255), true);
        }
        else if (pending_gate.Kind is GateKind.Threshold)
        {
            draw_histogram_gate_indicator(context, pending_gate.Vertices, Color.FromRgb(20, 133, 255), false);
        }
        else if (pending_gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
            draw_quadrant_gate(context, pending_gate, pen);

        if (pending_gate.Kind is GateKind.Threshold or GateKind.Range or GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
            return;

        foreach (var vertex in pending_gate.Vertices)
        {
            var point = data_to_screen(vertex);
            var rect = new Rect(point.X - 4, point.Y - 4, 8, 8);
            context.FillRectangle(handle_fill, rect);
            context.DrawRectangle(null, handle_stroke, rect);
        }
    }

    private void draw_active_tool_preview(DrawingContext context)
    {
        if (!has_pending_preview_point || XAxis is null || YAxis is null)
            return;
        if (ActiveTool is GatingTool.View or GatingTool.Polygon or GatingTool.Rectangle or GatingTool.Range)
            return;

        var gate = create_empty_gate(active_tool_gate_kind());
        gate.Vertices.Add(one_dimensional_point_if_needed(gate.Kind, pending_preview_point));
        if (gate.Kind == GateKind.OffsetQuadrant)
        {
            gate.Vertices.Add(pending_preview_point);
            gate.Vertices.Add(pending_preview_point);
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 20, 133, 255)), 1.2, DashStyle.Dash);
        if (gate.Kind == GateKind.Threshold)
        {
            draw_histogram_gate_indicator(context, gate.Vertices, Color.FromArgb(180, 20, 133, 255), false);
        }
        else
            draw_quadrant_gate(context, gate, pen);
    }

    private void draw_histogram_gate_indicator(
        DrawingContext context,
        IEnumerable<Point> vertices,
        Color color,
        bool connect_handles)
    {
        var points = vertices.Select(data_to_screen).ToList();
        if (points.Count == 0)
            return;

        var brush = new SolidColorBrush(color);
        var guide_pen = new Pen(brush, 1.2, DashStyle.Dash);
        var handle_stroke = new Pen(brush, 1.6);
        var center_y = plot_rect.Top + plot_rect.Height / 2;

        foreach (var point in points)
            context.DrawLine(guide_pen, new Point(point.X, plot_rect.Top), new Point(point.X, plot_rect.Bottom));

        if (connect_handles && points.Count >= 2)
        {
            double left = points.Min(point => point.X);
            double right = points.Max(point => point.X);
            context.DrawLine(handle_stroke, new Point(left, center_y), new Point(right, center_y));
        }

        foreach (var point in points)
        {
            if (!connect_handles)
                context.DrawLine(handle_stroke, new Point(point.X - 12, center_y), new Point(point.X + 12, center_y));

            var handle = new Rect(point.X - 5, center_y - 5, 10, 10);
            context.FillRectangle(Brushes.White, handle);
            context.DrawRectangle(null, handle_stroke, handle);
        }
    }

    private void draw_quadrant_gate(DrawingContext context, GateDefinition gate, Pen pen)
    {
        if (gate.Vertices.Count == 0)
            return;

        var center = data_to_screen(gate.Vertices[0]);
        if (gate.Kind == GateKind.CurlyQuadrant)
        {
            draw_curly_quadrant(context, center, pen);
            return;
        }

        if (gate.Kind == GateKind.OffsetQuadrant)
        {
            double top_x = data_to_screen(gate.Vertices.Count > 1 ? gate.Vertices[1] : gate.Vertices[0]).X;
            double bottom_x = data_to_screen(gate.Vertices.Count > 2 ? gate.Vertices[2] : gate.Vertices[0]).X;
            context.DrawLine(pen, new Point(top_x, plot_rect.Top), new Point(top_x, center.Y));
            context.DrawLine(pen, new Point(bottom_x, center.Y), new Point(bottom_x, plot_rect.Bottom));
            context.DrawLine(pen, new Point(plot_rect.Left, center.Y), new Point(plot_rect.Right, center.Y));
            return;
        }

        context.DrawLine(pen, new Point(center.X, plot_rect.Top), new Point(center.X, plot_rect.Bottom));
        context.DrawLine(pen, new Point(plot_rect.Left, center.Y), new Point(plot_rect.Right, center.Y));
    }

    private void draw_curly_quadrant(DrawingContext context, Point center, Pen pen)
    {
        if (XAxis is null || YAxis is null)
        {
            context.DrawLine(pen, new Point(center.X, center.Y), new Point(center.X, plot_rect.Bottom));
            context.DrawLine(pen, new Point(plot_rect.Left, center.Y), new Point(center.X, center.Y));
            return;
        }

        var data_center = screen_to_data(center);
        context.DrawLine(pen, data_to_screen(new Point(data_center.X, data_center.Y)), data_to_screen(new Point(data_center.X, YAxis.Minimum)));
        context.DrawLine(pen, data_to_screen(new Point(XAxis.Minimum, data_center.Y)), data_to_screen(new Point(data_center.X, data_center.Y)));
        draw_sampled_log_slope_curve(
            context,
            pen,
            data_center.Y,
            YAxis.Maximum,
            value => new Point(swapped_log_slope_boundary(data_center.X, data_center.Y, value, 0.1), value));
        draw_sampled_log_slope_curve(
            context,
            pen,
            data_center.X,
            XAxis.Maximum,
            value => new Point(value, log_slope_boundary(data_center.X, data_center.Y, value, 0.1)));
    }

    private void draw_sampled_log_slope_curve(DrawingContext context, Pen pen, double minimum, double maximum, Func<double, Point> point_factory)
    {
        const int steps = 48;
        Point? previous = null;
        for (int index = 0; index <= steps; index++)
        {
            double t = index / (double)steps;
            double value = minimum + (maximum - minimum) * t;
            var screen = data_to_screen(point_factory(value));
            if (!plot_rect.Intersects(new Rect(screen.X - 1, screen.Y - 1, 2, 2)))
            {
                previous = null;
                continue;
            }

            if (previous is { } start)
                context.DrawLine(pen, start, screen);
            previous = screen;
        }
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

    private void draw_gate_annotation(DrawingContext context, GateDefinition gate)
    {
        if (Group is null || gate.Vertices.Count == 0)
            return;

        foreach (var region in annotation_regions(gate))
            draw_gate_region_annotation(context, gate, region);
    }

    private void draw_gate_region_annotation(DrawingContext context, GateDefinition gate, PopulationRegion region)
    {
        if (Group is null)
            return;

        int event_count = 0;
        double parent_frequency = 0;
        int sample_count = 0;
        foreach (var sample in resolve_samples())
        {
            var population = find_population(sample.Populations, gate, gate.HasLinkedPopulations ? region : null);
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

        string name = gate.PopulationName(region);
        string statistics = $"{event_count:N0}  {parent_frequency:0.#}%";
        string label = ShowGateAnnotationNames ? $"{name}  {statistics}" : statistics;
        var text = new FormattedText(
            label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            current_typeface(),
            12,
            Brushes.White);
        var origin = gate_annotation_origin(gate, region, text);
        var background = new Rect(origin.X - 6, origin.Y - 4, text.Width + 12, text.Height + 8);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(210, 31, 111, 235)), background, 4);
        context.DrawText(text, origin);
    }

    private Point gate_annotation_origin(GateDefinition gate, PopulationRegion region, FormattedText text)
    {
        const double margin = 8;
        double width = text.Width + 12;
        double height = text.Height + 8;
        var anchor = data_to_screen(gate.Vertices[0]);
        double x = anchor.X + 10;
        double y = anchor.Y - 24;

        if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
        {
            x = region is PopulationRegion.TopRight or PopulationRegion.BottomRight
                ? plot_rect.Right - width - margin + 6
                : plot_rect.Left + margin + 6;
            y = region is PopulationRegion.BottomRight or PopulationRegion.BottomLeft
                ? plot_rect.Bottom - height - margin + 4
                : plot_rect.Top + margin + 4;
        }
        else if (gate.Kind == GateKind.Range)
        {
            x = region switch
            {
                PopulationRegion.BelowRange => plot_rect.Left + margin + 6,
                PopulationRegion.AboveRange => plot_rect.Right - width - margin + 6,
                _ => plot_rect.Left + (plot_rect.Width - width) / 2 + 6
            };
            y = plot_rect.Top + margin + 4;
        }
        else if (gate.Kind == GateKind.Threshold)
        {
            x = region == PopulationRegion.More
                ? plot_rect.Right - width - margin + 6
                : plot_rect.Left + margin + 6;
            y = plot_rect.Top + margin + 4;
        }

        return new Point(
            clamp_to_range(x, plot_rect.Left + margin, plot_rect.Right - width + 6),
            clamp_to_range(y, plot_rect.Top + margin, plot_rect.Bottom - height + 4));
    }

    private static double clamp_to_range(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private static IReadOnlyList<PopulationRegion> annotation_regions(GateDefinition gate) =>
        gate.HasLinkedPopulations ? gate.PopulationRegions : [PopulationRegion.Primary];

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

            draw_vertical_centered_text(context, axis_title(YAxis), new Point(plot_rect.Left - 62, plot_rect.Top + plot_rect.Height / 2), 14, Color.FromRgb(190, 198, 210));
        }

        draw_centered_text(
            context,
            axis_title(XAxis),
            new Point(plot_rect.Left + plot_rect.Width / 2, plot_rect.Bottom + 34),
            14,
            Color.FromRgb(190, 198, 210),
            bolded: true);
    }

    private string axis_title(AxisSettings axis)
    {
        string name = axis.ChannelName ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var channel = Sample?.Channels.FirstOrDefault(item => item.Name == name) ??
                      Group?.Channels.FirstOrDefault(item => item.Name == name);
        string label = channel?.Label?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(label) || string.Equals(label, name, StringComparison.Ordinal)
            ? name
            : $"{label} ({name})";
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

    private int[] resolve_event_indices(FlowSample sample)
    {
        if (Population is not null && Sample == sample)
            return Population.GetPlotEventIndices();

        if (Gate is not null)
        {
            if (Gate.HasLinkedPopulations)
                return resolve_parent_event_indices(sample, Gate);

            var population = find_population(sample.Populations, Gate);
            if (population is not null)
                return population.GetPlotEventIndices();
        }

        return sample.GetPlotEventIndices();
    }

    private static int[] resolve_parent_event_indices(FlowSample sample, GateDefinition gate)
    {
        if (gate.Parent is null)
            return sample.GetPlotEventIndices();

        var parent = find_population(sample.Populations, gate.Parent, gate.ParentPopulationRegion);
        return parent?.GetPlotEventIndices() ?? sample.GetPlotEventIndices();
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, GateDefinition gate, PopulationRegion? region = null)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate && (region is null || population.Region == region))
                return population;
            var child = find_population(population.Children, gate, region);
            if (child is not null)
                return child;
        }

        return null;
    }

    private int to_bin(double value, AxisSettings axis, double transformed_minimum, double transformed_span)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return -1;
        double normalized = (axis.Scale.Transform(value) - transformed_minimum) / transformed_span;
        if (double.IsNaN(normalized) || double.IsInfinity(normalized))
            return -1;
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

        if (pending_gate.Kind == GateKind.OffsetQuadrant)
        {
            pending_gate.Vertices.Clear();
            pending_gate.Vertices.Add(data_point);
            pending_gate.Vertices.Add(data_point);
            pending_gate.Vertices.Add(data_point);
            commit_created_gate(pending_gate);
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
            YChannel = kind is GateKind.Threshold or GateKind.Range or GateKind.Merge or GateKind.Exclude or GateKind.Overlap ? null : YAxis?.ChannelName,
            Kind = kind
        };

    private GateKind active_tool_gate_kind() =>
        ActiveTool switch
        {
            GatingTool.Polygon => GateKind.Polygon,
            GatingTool.Rectangle => GateKind.Rectangle,
            GatingTool.Quadrant => GateKind.Quadrant,
            GatingTool.CurlyQuadrant => GateKind.CurlyQuadrant,
            GatingTool.OffsetQuadrant => GateKind.OffsetQuadrant,
            GatingTool.Threshold => GateKind.Threshold,
            GatingTool.Range => GateKind.Range,
            _ => GateKind.Rectangle
        };

    private static Point one_dimensional_point_if_needed(GateKind kind, Point point) =>
        kind is GateKind.Threshold or GateKind.Range ? new Point(point.X, 0) : point;

    private static Point adjusted_drag_vertex(GateDefinition gate, int index, Point point)
    {
        if (gate.Kind is GateKind.Threshold or GateKind.Range)
            return new Point(point.X, 0);
        if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant)
            return point;
        if (gate.Kind == GateKind.OffsetQuadrant)
        {
            if (index == 0)
                return new Point(gate.Vertices[0].X, point.Y);
            return new Point(point.X, gate.Vertices[0].Y);
        }

        return point;
    }

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
        {
            invalidate_plot_cache();
            GateCreatedCommand.Execute(gate);
        }
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
                double distance = line_gate_distance(gate, index, point, vertex);
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

    private double line_gate_distance(GateDefinition gate, int index, Point pointer, Point vertex)
    {
        if (gate.Kind is GateKind.Threshold or GateKind.Range)
            return Math.Abs(vertex.X - pointer.X);
        if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant)
            return Math.Min(Math.Abs(vertex.X - pointer.X), Math.Abs(vertex.Y - pointer.Y));
        if (gate.Kind == GateKind.OffsetQuadrant)
        {
            var center = data_to_screen(gate.Vertices[0]);
            return index == 0
                ? Math.Abs(center.Y - pointer.Y)
                : Math.Abs(vertex.X - pointer.X);
        }

        return Math.Sqrt(Math.Pow(vertex.X - pointer.X, 2) + Math.Pow(vertex.Y - pointer.Y, 2));
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
                Color.FromRgb(255, 255, 255),
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

    private static Color palette_color(double value, PlotColorPalette palette)
    {
        value = Math.Clamp(value, 0, 1);
        return palette switch
        {
            PlotColorPalette.Gray => Color.FromRgb(to_byte(30 + value * 220), to_byte(30 + value * 220), to_byte(30 + value * 220)),
            PlotColorPalette.Plasma => interpolate_palette(value, [
                Color.FromRgb(13, 8, 135),
                Color.FromRgb(126, 3, 168),
                Color.FromRgb(204, 71, 120),
                Color.FromRgb(248, 149, 64),
                Color.FromRgb(240, 249, 33)
            ]),
            PlotColorPalette.Turbo => interpolate_palette(value, [
                Color.FromRgb(48, 18, 59),
                Color.FromRgb(61, 111, 225),
                Color.FromRgb(48, 204, 90),
                Color.FromRgb(246, 214, 69),
                Color.FromRgb(180, 31, 35)
            ]),
            _ => interpolate_palette(value, [
                Color.FromRgb(68, 1, 84),
                Color.FromRgb(59, 82, 139),
                Color.FromRgb(33, 145, 140),
                Color.FromRgb(94, 201, 98),
                Color.FromRgb(253, 231, 37)
            ])
        };
    }

    private static Color interpolate_palette(double value, Color[] colors)
    {
        if (colors.Length == 0)
            return Colors.Black;
        if (colors.Length == 1)
            return colors[0];
        double scaled = value * (colors.Length - 1);
        int index = Math.Clamp((int)Math.Floor(scaled), 0, colors.Length - 2);
        double t = scaled - index;
        var first = colors[index];
        var second = colors[index + 1];
        return Color.FromRgb(
            to_byte(first.R + (second.R - first.R) * t),
            to_byte(first.G + (second.G - first.G) * t),
            to_byte(first.B + (second.B - first.B) * t));
    }

    private static byte to_byte(double value) =>
        Convert.ToByte(Math.Clamp((int)Math.Round(value), 0, 255));

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

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color, bool bolded = false)
    {
        var formatted = create_formatted_text(text, size, color, bolded);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private void draw_right_aligned_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color, bolded: true);
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
        return Configuration.FormatAxisValue(value);
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
        return Configuration.MajorAxisTicks(axis).Where(value => is_tick_in_range(value, axis));
    }

    private static IEnumerable<double> minor_axis_ticks(AxisSettings axis)
    {
        if (axis.Maximum <= axis.Minimum)
            yield break;

        foreach (double value in Configuration.MinorAxisTicks(axis))
            if (is_tick_in_range(value, axis))
                yield return value;
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
        run_on_ui_thread(() =>
        {
            if (e.PropertyName is nameof(FlowSample.CompensatedEvents) or null or "")
                invalidate_plot_cache();
            else if (e.PropertyName == nameof(FlowSample.Populations))
                InvalidateVisual();
        });
    }

    private void resubscribe_axis(ref AxisSettings? old_axis, AxisSettings? new_axis)
    {
        if (old_axis is not null)
            old_axis.PropertyChanged -= axis_property_changed;

        old_axis = new_axis;
        if (old_axis is not null)
            old_axis.PropertyChanged += axis_property_changed;
    }

    private void axis_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        run_on_ui_thread(() =>
        {
            if (TransformPreparing)
            {
                InvalidateVisual();
                return;
            }

            invalidate_plot_cache();
        });
    }

    private void resubscribe_dot_color(DotColorSettings? new_dot_color)
    {
        if (subscribed_dot_color is not null)
            subscribed_dot_color.PropertyChanged -= dot_color_property_changed;

        subscribed_dot_color = new_dot_color;
        if (subscribed_dot_color is not null)
            subscribed_dot_color.PropertyChanged += dot_color_property_changed;
    }

    private void dot_color_property_changed(object? sender, PropertyChangedEventArgs e) =>
        run_on_ui_thread(invalidate_plot_cache);

    private void invalidate_plot_cache()
    {
        cached_plot_bitmap = null;
        cached_contour_key = null;
        cached_contour_geometry = null;
        InvalidateVisual();
    }

    private static void run_on_ui_thread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
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
        if (x < 0 || x >= density_size || y < 0 || y >= density_size)
            return;

        int offset = (y * density_size + x) * 4;
        pixels[offset] = premultiply(color.B, color.A);
        pixels[offset + 1] = premultiply(color.G, color.A);
        pixels[offset + 2] = premultiply(color.R, color.A);
        pixels[offset + 3] = color.A;
    }

    private static void set_large_dot(byte[] pixels, int center_x, int center_y, Color color, int count)
    {
        if (count <= 0)
            return;

        byte alpha = to_alpha(color.A * (1 - Math.Pow(1 - large_dot_opacity, count)));
        int radius = (int)Math.Ceiling(large_dot_radius);
        for (int y = center_y - radius; y <= center_y + radius; y++)
        for (int x = center_x - radius; x <= center_x + radius; x++)
        {
            double distance = Math.Sqrt((x - center_x) * (x - center_x) + (y - center_y) * (y - center_y));
            if (distance > large_dot_radius)
                continue;

            blend_pixel(pixels, x, y, Color.FromArgb(alpha, color.R, color.G, color.B));
        }
    }

    private static void blend_pixel(byte[] pixels, int x, int y, Color color)
    {
        if (x < 0 || x >= density_size || y < 0 || y >= density_size)
            return;

        int offset = (y * density_size + x) * 4;
        byte source_alpha = color.A;
        double inverse_alpha = 1 - source_alpha / 255.0;
        pixels[offset] = to_alpha(premultiply(color.B, source_alpha) + pixels[offset] * inverse_alpha);
        pixels[offset + 1] = to_alpha(premultiply(color.G, source_alpha) + pixels[offset + 1] * inverse_alpha);
        pixels[offset + 2] = to_alpha(premultiply(color.R, source_alpha) + pixels[offset + 2] * inverse_alpha);
        pixels[offset + 3] = to_alpha(source_alpha + pixels[offset + 3] * inverse_alpha);
    }

    private static byte premultiply(byte channel, byte alpha) =>
        Convert.ToByte(channel * alpha / 255);

    private static byte to_alpha(double value) =>
        Convert.ToByte(Math.Clamp((int)Math.Round(value), 0, 255));

    private sealed record ContourGeometry(IReadOnlyList<ContourSegment> Segments);

    private readonly record struct ContourSegment(Point Start, Point End);
}
