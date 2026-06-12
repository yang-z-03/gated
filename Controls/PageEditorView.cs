using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using gated.Models;
using gated.ViewModels;

namespace gated.Controls;

public sealed class PageEditorView : Control
{
    public const string ProjectNodeDataFormat = "gated/project-node";
    public static ProjectNode? DraggedProjectNode { get; set; }

    public static readonly StyledProperty<IEnumerable?> ElementsProperty =
        AvaloniaProperty.Register<PageEditorView, IEnumerable?>(nameof(Elements));

    public static readonly StyledProperty<PagePlotElement?> SelectedElementProperty =
        AvaloniaProperty.Register<PageEditorView, PagePlotElement?>(nameof(SelectedElement));

    public static readonly StyledProperty<ICommand?> AddElementCommandProperty =
        AvaloniaProperty.Register<PageEditorView, ICommand?>(nameof(AddElementCommand));

    public static readonly StyledProperty<Size> ViewportSizeProperty =
        AvaloniaProperty.Register<PageEditorView, Size>(nameof(ViewportSize));

    private const double title_space = 38;
    private const double left_axis_label_space = 30;
    private const double left_tick_label_space = 28;
    private const double right_spine_space = 14;
    private const double bottom_axis_label_space = 30;
    private const double bottom_tick_label_space = 18;
    private const double workspace_margin = 60;
    private const double minimum_workspace_width = 1800;
    private const double minimum_workspace_height = 1200;
    private const int raster_size = 260;
    private PagePlotElement? captured_element;
    private Point drag_start_page;
    private double drag_start_x;
    private double drag_start_y;
    private double drag_start_size;
    private bool resizing;
    private INotifyCollectionChanged? subscribed_elements;
    private PagePlotElement[] subscribed_page_elements = [];
    private Size content_extent;
    private double? active_vertical_snap_guide;
    private double? active_horizontal_snap_guide;
    private readonly Dictionary<Guid, (string Key, DensityGrid? Grid)> density_grid_cache = new();
    private readonly Dictionary<Guid, (string Key, ContourGeometry? Geometry)> contour_geometry_cache = new();
    private readonly Dictionary<Guid, (string Key, WriteableBitmap? Bitmap)> plot_bitmap_cache = new();
    private readonly Dictionary<Guid, (string Key, RenderTargetBitmap Bitmap)> element_bitmap_cache = new();

    static PageEditorView()
    {
        AffectsMeasure<PageEditorView>(ViewportSizeProperty);
        AffectsRender<PageEditorView>(ElementsProperty, SelectedElementProperty);
    }

    public PageEditorView()
    {
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, drag_over);
        DragDrop.AddDropHandler(this, drop_node);
    }

    public IEnumerable? Elements
    {
        get => GetValue(ElementsProperty);
        set => SetValue(ElementsProperty, value);
    }

    public PagePlotElement? SelectedElement
    {
        get => GetValue(SelectedElementProperty);
        set => SetValue(SelectedElementProperty, value);
    }

    public ICommand? AddElementCommand
    {
        get => GetValue(AddElementCommandProperty);
        set => SetValue(AddElementCommandProperty, value);
    }

    public Size ViewportSize
    {
        get => GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(Brushes.White, bounds);
        foreach (var element in PageElements)
            draw_page_element(context, element, ReferenceEquals(element, SelectedElement));
        draw_snap_guides(context, bounds);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return workspace_size();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ElementsProperty)
            resubscribe_elements();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var element = PageElements.LastOrDefault(item => item.Bounds.Contains(point));
        SelectedElement = element;
        if (element is null)
        {
            InvalidateVisual();
            return;
        }

        captured_element = element;
        drag_start_page = point;
        drag_start_x = element.X;
        drag_start_y = element.Y;
        drag_start_size = element.Size;
        resizing = resize_handle_rect(element).Contains(point);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (captured_element is null)
            return;

        var point = e.GetPosition(this);
        if (resizing)
        {
            double requested = Math.Max(point.X - drag_start_x, point.Y - drag_start_y);
            captured_element.Size = snap_size(captured_element, requested);
        }
        else
        {
            captured_element.X = Math.Max(0, drag_start_x + point.X - drag_start_page.X);
            captured_element.Y = Math.Max(0, drag_start_y + point.Y - drag_start_page.Y);
            snap_position(captured_element);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        captured_element = null;
        resizing = false;
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        e.Pointer.Capture(null);
        update_content_extent();
        InvalidateVisual();
    }

    public void SaveBitmap(string file_path, bool transparent_background)
    {
        var export_size = export_size_for(PageElements);
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(export_size.Width), (int)Math.Ceiling(export_size.Height)), new Vector(96, 96));
        var visual = new ExportPageVisual(PageElements, transparent_background, export_size);
        visual.Measure(export_size);
        visual.Arrange(new Rect(export_size));
        bitmap.Render(visual);
        bitmap.Save(file_path);
    }

    public void SaveSvg(string file_path)
    {
        var svg = new StringBuilder();
        var export_size = export_size_for(PageElements);
        svg.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{export_size.Width.ToString(CultureInfo.InvariantCulture)}" height="{export_size.Height.ToString(CultureInfo.InvariantCulture)}" viewBox="0 0 {export_size.Width.ToString(CultureInfo.InvariantCulture)} {export_size.Height.ToString(CultureInfo.InvariantCulture)}">""");
        svg.AppendLine("""<rect width="100%" height="100%" fill="white"/>""");
        foreach (var element in PageElements)
        {
            svg.AppendLine($"""<g transform="translate({element.X.ToString(CultureInfo.InvariantCulture)} {element.Y.ToString(CultureInfo.InvariantCulture)})">""");
            svg.AppendLine($"""<rect width="{element.Size.ToString(CultureInfo.InvariantCulture)}" height="{element.Size.ToString(CultureInfo.InvariantCulture)}" fill="white"/>""");
            append_svg_plot_image(svg, element);
            append_svg_axes(svg, element);
            svg.AppendLine($"""<text x="{(element.Size / 2).ToString(CultureInfo.InvariantCulture)}" y="{(title_space / 2 + 5).ToString(CultureInfo.InvariantCulture)}" text-anchor="middle" font-family="Arial" font-size="13" font-weight="700" fill="black">{escape_xml(element.Title)}</text>""");
            if (element.ShowGates)
                append_svg_gates(svg, element);
            svg.AppendLine("</g>");
        }
        svg.AppendLine("</svg>");
        System.IO.File.WriteAllText(file_path, svg.ToString());
    }

    private void draw_page_element(DrawingContext context, PagePlotElement element, bool selected)
    {
        var bounds = element.Bounds;
        var bitmap = get_element_bitmap(element);
        context.DrawImage(bitmap, bounds);

        if (!selected)
            return;

        var selection_pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1.5);
        context.DrawRectangle(null, selection_pen, bounds);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0, 120, 215)), resize_handle_rect(element));
    }

    private void draw_page_element_content(DrawingContext context, PagePlotElement element, Rect bounds)
    {
        context.FillRectangle(Brushes.White, bounds);
        var plot_rect = plot_rect_for(bounds, element.ShowTickLabels);
        context.FillRectangle(Brushes.White, plot_rect);
        if (element.ShowGridlines)
            draw_grid(context, element, plot_rect);

        var plot_bitmap = get_plot_bitmap(element);
        if (plot_bitmap is not null)
            context.DrawImage(plot_bitmap, plot_rect);
        if (element.PlotMode == PlotMode.Contour)
            draw_contours(context, element, plot_rect);

        draw_axes(context, element, bounds, plot_rect);
        if (element.ShowGates)
            draw_gates(context, element, plot_rect);
        draw_centered_text_in_band(
            context,
            element.Title,
            new Rect(bounds.Left, bounds.Top, bounds.Width, title_space),
            13,
            Colors.Black,
            bolded: true);
    }

    private RenderTargetBitmap get_element_bitmap(PagePlotElement element)
    {
        string key = element_bitmap_key(element);
        if (element_bitmap_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Bitmap;

        int size = Math.Max(1, (int)Math.Ceiling(element.Size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        var visual = new ElementContentVisual(this, element, new Size(size, size));
        visual.Measure(new Size(size, size));
        visual.Arrange(new Rect(0, 0, size, size));
        bitmap.Render(visual);
        element_bitmap_cache[element.Id] = (key, bitmap);
        return bitmap;
    }

    private static string element_bitmap_key(PagePlotElement element) =>
        string.Join("|",
            plot_bitmap_key(element),
            element.Size,
            element.Title,
            element.ShowTickLabels,
            element.ShowGates,
            element.ShowGateAnnotations,
            element.XAxis.ChannelName,
            element.YAxis.ChannelName,
            element.DotColor.ChannelName,
            element.DotColor.Palette,
            element.DotColor.UseLogScale);

    private WriteableBitmap? create_density_bitmap(PagePlotElement element)
    {
        var grid = get_density_grid(element);
        if (grid is null)
            return null;

        var pixels = new byte[raster_size * raster_size * 4];
        if (element.PlotMode == PlotMode.Dotplot)
        {
            add_dotplot_pixels(pixels, grid.Density, grid.Colors, grid.TotalCount, element.DrawLargeDots);
            return create_bitmap(pixels);
        }

        var normalized = normalized_density_grid(grid.Density, grid.MaxDensity);
        if (element.PlotMode == PlotMode.Contour)
            return create_contour_bitmap(element, grid.Density, grid.TotalCount, normalized);

        if (element.PlotMode == PlotMode.Zebra)
            normalized = smooth_density(normalized, element.DensitySmoothing);
        if (element.PlotMode == PlotMode.Zebra && element.ShowOutlierPoints)
            add_dotplot_pixels(pixels, grid.Density, grid.Colors, grid.TotalCount, element.DrawLargeDots);

        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
        {
            double value = normalized[x, y];
            if (value <= 0)
                continue;

            set_pixel(pixels, x, raster_size - 1 - y, plot_color(value, element));
        }

        return create_bitmap(pixels);
    }

    private WriteableBitmap create_contour_bitmap(PagePlotElement element, int[,] density, int total_count, double[,] normalized)
    {
        var pixels = new byte[raster_size * raster_size * 4];
        if (element.ShowOutlierPoints)
            add_dotplot_pixels(pixels, density, null, total_count, element.DrawLargeDots);

        var smoothed = smooth_density(normalized, element.DensitySmoothing);
        double[] levels = contour_levels(element.ContourLevelCount);
        add_filled_contour_pixels(pixels, smoothed, levels);
        return create_bitmap(pixels);
    }

    private WriteableBitmap? get_plot_bitmap(PagePlotElement element)
    {
        string key = plot_bitmap_key(element);
        if (plot_bitmap_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Bitmap;

        var bitmap = element.IsHistogram ? create_histogram_bitmap(element) : create_density_bitmap(element);
        plot_bitmap_cache[element.Id] = (key, bitmap);
        return bitmap;
    }

    private static string plot_bitmap_key(PagePlotElement element) =>
        string.Join("|",
            element.IsHistogram,
            element.PlotMode,
            element.ShowOutlierPoints,
            element.DrawLargeDots,
            element.UsePseudocolor,
            element.ContourLevelCount,
            element.DensitySmoothing,
            density_grid_key(element));

    private static string contour_geometry_key(PagePlotElement element) =>
        string.Join("|",
            density_grid_key(element),
            element.ContourLevelCount,
            element.DensitySmoothing);

    private DensityGrid? get_density_grid(PagePlotElement element)
    {
        string key = density_grid_key(element);
        if (density_grid_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Grid;

        var grid = create_density_grid(element);
        density_grid_cache[element.Id] = (key, grid);
        return grid;
    }

    private void draw_contours(DrawingContext context, PagePlotElement element, Rect plot_rect)
    {
        var geometry = get_contour_geometry(element);
        if (geometry is null)
            return;

        var pen = new Pen(Brushes.Black, 0.8);
        foreach (var segment in geometry.Segments)
            context.DrawLine(pen, density_to_screen(segment.Start, plot_rect), density_to_screen(segment.End, plot_rect));
    }

    private ContourGeometry? get_contour_geometry(PagePlotElement element)
    {
        string key = contour_geometry_key(element);
        if (contour_geometry_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Geometry;

        var grid = get_density_grid(element);
        if (grid is null)
        {
            contour_geometry_cache[element.Id] = (key, null);
            return null;
        }

        var normalized = smooth_density(normalized_density_grid(grid.Density, grid.MaxDensity), element.DensitySmoothing);
        var segments = create_contour_segments(normalized, contour_levels(element.ContourLevelCount));
        var geometry = new ContourGeometry(segments);
        contour_geometry_cache[element.Id] = (key, geometry);
        return geometry;
    }

    private static IReadOnlyList<ContourSegment> create_contour_segments(double[,] density, double[] levels)
    {
        var segments = new List<ContourSegment>();
        foreach (double level in levels)
            add_contour_level_segments(segments, density, level);
        return segments;
    }

    private static void add_contour_level_segments(List<ContourSegment> segments, double[,] density, double level)
    {
        for (int x = 0; x < raster_size - 1; x++)
        for (int y = 0; y < raster_size - 1; y++)
        {
            var points = contour_cell_points(density[x, y], density[x + 1, y], density[x + 1, y + 1], density[x, y + 1], x, y, level);
            for (int index = 0; index + 1 < points.Count; index += 2)
                segments.Add(new ContourSegment(points[index], points[index + 1]));
        }
    }

    private static Point density_to_screen(Point point, Rect plot_rect) =>
        new(
            plot_rect.Left + point.X / (raster_size - 1) * plot_rect.Width,
            plot_rect.Bottom - point.Y / (raster_size - 1) * plot_rect.Height);

    private static string density_grid_key(PagePlotElement element) =>
        string.Join("|",
            element.IsHistogram,
            element.XAxis.ChannelName,
            element.XAxis.Minimum,
            element.XAxis.Maximum,
            element.XAxis.ScaleKind,
            element.XAxis.LogicleTopOfScale,
            element.XAxis.LogicleDecades,
            element.XAxis.LogicleLinearizationWidth,
            element.XAxis.LogicleNegativeDecades,
            element.YAxis.ChannelName,
            element.YAxis.Minimum,
            element.YAxis.Maximum,
            element.YAxis.ScaleKind,
            element.YAxis.LogicleTopOfScale,
            element.YAxis.LogicleDecades,
            element.YAxis.LogicleLinearizationWidth,
            element.YAxis.LogicleNegativeDecades,
            should_color_dots(element) ? element.DotColor.ChannelName : "",
            should_color_dots(element) ? element.DotColor.UseLogScale : false,
            element.Sample?.Id,
            element.Gate?.Id,
            element.Population?.Region);

    private WriteableBitmap? create_histogram_bitmap(PagePlotElement element)
    {
        var samples = resolve_samples(element).ToArray();
        if (samples.Length == 0)
            return null;

        int[] bins = new int[raster_size];
        int max_count = 0;
        double x_minimum = element.XAxis.Scale.Transform(element.XAxis.Minimum);
        double x_span = element.XAxis.Scale.Transform(element.XAxis.Maximum) - x_minimum;
        if (x_span <= 0)
            return null;

        foreach (var sample in samples)
        {
            int[] event_indices = resolve_event_indices(element, sample);
            var x_values = sample.GetChannelValues(element.XAxis.ChannelName, event_indices);
            if (x_values.Length == 0)
                continue;
            for (int index = 0; index < event_indices.Length; index++)
            {
                int bin = to_bin(x_values[index], element.XAxis, x_minimum, x_span);
                if (bin < 0)
                    continue;
                max_count = Math.Max(max_count, ++bins[bin]);
            }
        }

        if (max_count == 0)
            return null;

        var pixels = new byte[raster_size * raster_size * 4];
        for (int bin = 0; bin < raster_size; bin++)
        {
            int height = Math.Clamp((int)Math.Round(bins[bin] / (double)max_count * raster_size), 0, raster_size);
            for (int y = raster_size - height; y < raster_size; y++)
                set_pixel(pixels, bin, y, Colors.Black);
        }
        return create_bitmap(pixels);
    }

    private DensityGrid? create_density_grid(PagePlotElement element)
    {
        var samples = resolve_samples(element).ToArray();
        if (samples.Length == 0)
            return null;

        var density = new int[raster_size, raster_size];
        var color_sums = should_color_dots(element) ? new double[raster_size, raster_size] : null;
        var color_counts = should_color_dots(element) ? new int[raster_size, raster_size] : null;
        int max_density = 0;
        int total_count = 0;
        double x_minimum = element.XAxis.Scale.Transform(element.XAxis.Minimum);
        double x_span = element.XAxis.Scale.Transform(element.XAxis.Maximum) - x_minimum;
        double y_minimum = element.YAxis.Scale.Transform(element.YAxis.Minimum);
        double y_span = element.YAxis.Scale.Transform(element.YAxis.Maximum) - y_minimum;
        if (x_span <= 0 || y_span <= 0)
            return null;

        foreach (var sample in samples)
        {
            int[] event_indices = resolve_event_indices(element, sample);
            var x_values = sample.GetChannelValues(element.XAxis.ChannelName, event_indices);
            var y_values = sample.GetChannelValues(element.YAxis.ChannelName, event_indices);
            var color_values = should_color_dots(element) ? sample.GetChannelValues(element.DotColor.ChannelName, event_indices) : [];
            if (x_values.Length == 0 || y_values.Length == 0)
                continue;

            for (int index = 0; index < event_indices.Length; index++)
            {
                int x_bin = to_bin(x_values[index], element.XAxis, x_minimum, x_span);
                int y_bin = to_bin(y_values[index], element.YAxis, y_minimum, y_span);
                if (x_bin < 0 || y_bin < 0)
                    continue;
                total_count++;
                max_density = Math.Max(max_density, ++density[x_bin, y_bin]);
                if (color_sums is not null &&
                    color_counts is not null &&
                    index >= 0 &&
                    index < color_values.Length &&
                    !float.IsNaN(color_values[index]) &&
                    !float.IsInfinity(color_values[index]))
                {
                    color_sums[x_bin, y_bin] += transform_dot_color_value(color_values[index], element.DotColor.UseLogScale);
                    color_counts[x_bin, y_bin]++;
                }
            }
        }

        return max_density == 0 ? null : new DensityGrid(density, create_dot_colors(element, color_sums, color_counts), max_density, total_count);
    }

    private void draw_axes(DrawingContext context, PagePlotElement element, Rect bounds, Rect plot_rect)
    {
        var spine_pen = new Pen(Brushes.Black, 1);
        var major_pen = new Pen(Brushes.Black, 1);
        var minor_pen = new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90)), 0.8);
        context.DrawRectangle(null, spine_pen, plot_rect);
        foreach (double value in minor_axis_ticks(element.XAxis))
        {
            double x = data_to_screen(new Point(value, element.YAxis.Minimum), element, plot_rect).X;
            context.DrawLine(minor_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 3));
            context.DrawLine(minor_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Top - 3));
        }
        foreach (double value in major_axis_ticks(element.XAxis))
        {
            double x = data_to_screen(new Point(value, element.YAxis.Minimum), element, plot_rect).X;
            context.DrawLine(major_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 5));
            context.DrawLine(major_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Top - 5));
            if (element.ShowTickLabels)
                draw_centered_text(context, format_axis_value(value), new Point(x, plot_rect.Bottom + 7), 9, Colors.Black);
        }
        if (!element.IsHistogram)
        {
            foreach (double value in minor_axis_ticks(element.YAxis))
            {
                double y = data_to_screen(new Point(element.XAxis.Minimum, value), element, plot_rect).Y;
                context.DrawLine(minor_pen, new Point(plot_rect.Left - 3, y), new Point(plot_rect.Left, y));
                context.DrawLine(minor_pen, new Point(plot_rect.Right, y), new Point(plot_rect.Right + 3, y));
            }
            foreach (double value in major_axis_ticks(element.YAxis))
            {
                double y = data_to_screen(new Point(element.XAxis.Minimum, value), element, plot_rect).Y;
                context.DrawLine(major_pen, new Point(plot_rect.Left - 5, y), new Point(plot_rect.Left, y));
                context.DrawLine(major_pen, new Point(plot_rect.Right, y), new Point(plot_rect.Right + 5, y));
                if (element.ShowTickLabels)
                    draw_right_aligned_text(context, format_axis_value(value), new Point(plot_rect.Left - 7, y - 6), 9, Colors.Black);
            }
            double y_axis_label_x = bounds.Left + left_axis_label_space / 2;
            draw_vertical_centered_text(context, element.YAxis.ChannelName, new Point(y_axis_label_x, plot_rect.Top + plot_rect.Height / 2), 11, Colors.Black);
        }
        draw_centered_text_in_band(
            context,
            element.XAxis.ChannelName,
            new Rect(plot_rect.Left, bounds.Bottom - bottom_axis_label_space, plot_rect.Width, bottom_axis_label_space),
            11,
            Colors.Black);
    }

    private void draw_grid(DrawingContext context, PagePlotElement element, Rect plot_rect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 0.8);
        foreach (double value in major_axis_ticks(element.XAxis))
        {
            double x = data_to_screen(new Point(value, element.YAxis.Minimum), element, plot_rect).X;
            context.DrawLine(pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }
        if (element.IsHistogram)
            return;
        foreach (double value in major_axis_ticks(element.YAxis))
        {
            double y = data_to_screen(new Point(element.XAxis.Minimum, value), element, plot_rect).Y;
            context.DrawLine(pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
        }
    }

    private void draw_gates(DrawingContext context, PagePlotElement element, Rect plot_rect)
    {
        foreach (var gate in resolve_plot_gates(element))
            draw_gate(context, element, gate, plot_rect);
    }

    private void draw_gate(DrawingContext context, PagePlotElement element, GateDefinition gate, Rect plot_rect)
    {
        if (gate.Vertices.Count == 0)
            return;
        var pen = new Pen(Brushes.Black, 1.4);
        if (gate.Kind == GateKind.Rectangle && gate.Vertices.Count >= 2)
        {
            context.DrawRectangle(null, pen, make_rect(data_to_screen(gate.Vertices[0], element, plot_rect), data_to_screen(gate.Vertices[1], element, plot_rect)));
        }
        else if (gate.Kind is GateKind.Threshold or GateKind.Range)
        {
            foreach (var vertex in gate.Vertices)
            {
                double x = data_to_screen(vertex, element, plot_rect).X;
                context.DrawLine(pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
            }
        }
        else if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
        {
            var point = data_to_screen(gate.Vertices[0], element, plot_rect);
            context.DrawLine(pen, new Point(point.X, plot_rect.Top), new Point(point.X, plot_rect.Bottom));
            context.DrawLine(pen, new Point(plot_rect.Left, point.Y), new Point(plot_rect.Right, point.Y));
        }
        else
        {
            for (int index = 0; index < gate.Vertices.Count; index++)
                context.DrawLine(pen, data_to_screen(gate.Vertices[index], element, plot_rect), data_to_screen(gate.Vertices[(index + 1) % gate.Vertices.Count], element, plot_rect));
        }

        if (element.ShowGateAnnotations)
            draw_gate_annotation(context, element, gate, plot_rect);
    }

    private void draw_gate_annotation(DrawingContext context, PagePlotElement element, GateDefinition gate, Rect plot_rect)
    {
        if (gate.Vertices.Count == 0)
            return;

        foreach (var region in annotation_regions(gate))
            draw_gate_region_annotation(context, element, gate, region, plot_rect);
    }

    private void draw_gate_region_annotation(DrawingContext context, PagePlotElement element, GateDefinition gate, PopulationRegion region, Rect plot_rect)
    {
        int event_count = 0;
        double parent_frequency = 0;
        int sample_count = 0;
        foreach (var sample in resolve_samples(element))
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

        string name = gate.HasLinkedPopulations ? $"{gate.Name} {population_region_name(region)}" : gate.Name;
        string label = $"{name}\n{event_count:N0} ({parent_frequency:0.#}%)";
        var text = create_formatted_text(label, 10, Colors.Black);
        var origin = gate_annotation_origin(element, gate, region, plot_rect, text.Width + 6, text.Height + 4);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), new Rect(origin.X - 3, origin.Y - 2, text.Width + 6, text.Height + 4));
        context.DrawText(text, origin);
    }

    private static Point gate_annotation_origin(PagePlotElement element, GateDefinition gate, PopulationRegion region, Rect plot_rect, double width, double height)
    {
        const double margin = 4;
        var anchor = data_to_screen(gate.Vertices[0], element, plot_rect);
        double x = anchor.X + 6;
        double y = anchor.Y - 36;

        if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
        {
            x = region is PopulationRegion.TopRight or PopulationRegion.BottomRight
                ? plot_rect.Right - width - margin + 3
                : plot_rect.Left + margin + 3;
            y = region is PopulationRegion.BottomRight or PopulationRegion.BottomLeft
                ? plot_rect.Bottom - height - margin + 2
                : plot_rect.Top + margin + 2;
        }
        else if (gate.Kind == GateKind.Range)
        {
            x = region switch
            {
                PopulationRegion.BelowRange => plot_rect.Left + margin + 3,
                PopulationRegion.AboveRange => plot_rect.Right - width - margin + 3,
                _ => plot_rect.Left + (plot_rect.Width - width) / 2 + 3
            };
            y = plot_rect.Top + margin + 2;
        }
        else if (gate.Kind == GateKind.Threshold)
        {
            x = region == PopulationRegion.More
                ? plot_rect.Right - width - margin + 3
                : plot_rect.Left + margin + 3;
            y = plot_rect.Top + margin + 2;
        }

        return new Point(
            clamp_to_range(x, plot_rect.Left + margin, plot_rect.Right - width + 3),
            clamp_to_range(y, plot_rect.Top + margin, plot_rect.Bottom - height + 2));
    }

    private static IReadOnlyList<PopulationRegion> annotation_regions(GateDefinition gate) =>
        gate.HasLinkedPopulations ? gate.PopulationRegions : [PopulationRegion.Primary];

    private static string population_region_name(PopulationRegion region) =>
        region switch
        {
            PopulationRegion.TopRight => "top right",
            PopulationRegion.TopLeft => "top left",
            PopulationRegion.BottomRight => "bottom right",
            PopulationRegion.BottomLeft => "bottom left",
            PopulationRegion.More => "more",
            PopulationRegion.Less => "less",
            PopulationRegion.InRange => "in range",
            PopulationRegion.BelowRange => "below",
            PopulationRegion.AboveRange => "above",
            _ => "population"
        };

    private static double clamp_to_range(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private static IEnumerable<FlowSample> resolve_samples(PagePlotElement element)
    {
        if (element.Sample is not null)
        {
            yield return element.Sample;
            yield break;
        }
        if (element.Group is not null)
            foreach (var sample in element.Group.Samples)
                yield return sample;
    }

    private static int[] resolve_event_indices(PagePlotElement element, FlowSample sample)
    {
        if (element.Population is not null && ReferenceEquals(element.Sample, sample))
            return element.Population.GetPlotEventIndices();
        if (element.Gate is not null)
        {
            var population = find_population(sample.Populations, element.Gate, element.Population?.Region);
            if (population is not null)
                return population.GetPlotEventIndices();
        }
        return sample.GetPlotEventIndices();
    }

    private static IEnumerable<GateDefinition> resolve_plot_gates(PagePlotElement element)
    {
        if (element.Gate is null)
            yield break;

        foreach (var child in element.Gate.Children)
        {
            if (element.Population is null || child.ParentPopulationRegion == element.Population.Region)
                yield return child;
        }
    }

    private void drag_over(object? sender, DragEventArgs e)
    {
        e.DragEffects = DraggedProjectNode is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void drop_node(object? sender, DragEventArgs e)
    {
        if (DraggedProjectNode is not { } node)
            return;
        var point = e.GetPosition(this);
        var request = new PageDropRequest(node, point);
        if (AddElementCommand?.CanExecute(request) == true)
            AddElementCommand.Execute(request);
        DraggedProjectNode = null;
        e.Handled = true;
    }

    private void snap_position(PagePlotElement element)
    {
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        element.X = Math.Max(0, element.X);
        element.Y = Math.Max(0, element.Y);
        var guides = PageElements.Where(item => !ReferenceEquals(item, element)).ToArray();
        snap_axis(guides.SelectMany(item => new[] { item.X, item.X + item.Size / 2, item.X + item.Size }), element.X, value => { element.X += value - element.X; active_vertical_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.Y, item.Y + item.Size / 2, item.Y + item.Size }), element.Y, value => { element.Y += value - element.Y; active_horizontal_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.X, item.X + item.Size / 2, item.X + item.Size }), element.X + element.Size / 2, value => { element.X += value - (element.X + element.Size / 2); active_vertical_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.Y, item.Y + item.Size / 2, item.Y + item.Size }), element.Y + element.Size / 2, value => { element.Y += value - (element.Y + element.Size / 2); active_horizontal_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.X, item.X + item.Size / 2, item.X + item.Size }), element.X + element.Size, value => { element.X += value - (element.X + element.Size); active_vertical_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.Y, item.Y + item.Size / 2, item.Y + item.Size }), element.Y + element.Size, value => { element.Y += value - (element.Y + element.Size); active_horizontal_snap_guide = value; });
    }

    private double snap_size(PagePlotElement element, double requested)
    {
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        double size = Math.Max(120, requested);
        foreach (var other in PageElements.Where(item => !ReferenceEquals(item, element)))
        {
            if (Math.Abs(element.X + size - other.X) <= 6)
            {
                size = other.X - element.X;
                active_vertical_snap_guide = other.X;
            }
            if (Math.Abs(element.Y + size - other.Y) <= 6)
            {
                size = other.Y - element.Y;
                active_horizontal_snap_guide = other.Y;
            }
            if (Math.Abs(element.X + size - (other.X + other.Size)) <= 6)
            {
                size = other.X + other.Size - element.X;
                active_vertical_snap_guide = other.X + other.Size;
            }
            if (Math.Abs(element.Y + size - (other.Y + other.Size)) <= 6)
            {
                size = other.Y + other.Size - element.Y;
                active_horizontal_snap_guide = other.Y + other.Size;
            }
        }
        return size;
    }

    private static void snap_axis(IEnumerable<double> guides, double value, Action<double> apply)
    {
        foreach (double guide in guides)
        {
            if (Math.Abs(value - guide) <= 6)
            {
                apply(guide);
                return;
            }
        }
    }

    private Size workspace_size()
    {
        return new Size(
            Math.Max(Math.Max(ViewportSize.Width, minimum_workspace_width), content_extent.Width),
            Math.Max(Math.Max(ViewportSize.Height, minimum_workspace_height), content_extent.Height));
    }

    private static Size export_size_for(IReadOnlyList<PagePlotElement> elements)
    {
        if (elements.Count == 0)
            return new Size(workspace_margin, workspace_margin);

        double width = elements.Max(element => element.X + element.Size + workspace_margin);
        double height = elements.Max(element => element.Y + element.Size + workspace_margin);
        return new Size(Math.Ceiling(width), Math.Ceiling(height));
    }

    private static Rect plot_rect_for(Rect bounds, bool show_tick_labels) =>
        new(
            bounds.Left + left_axis_label_space + (show_tick_labels ? left_tick_label_space : 0),
            bounds.Top + title_space,
            Math.Max(40, bounds.Width - left_axis_label_space - (show_tick_labels ? left_tick_label_space : 0) - right_spine_space),
            Math.Max(40, bounds.Height - title_space - bottom_axis_label_space - (show_tick_labels ? bottom_tick_label_space : 0)));

    private static Rect resize_handle_rect(PagePlotElement element) =>
        new(element.X + element.Size - 11, element.Y + element.Size - 11, 11, 11);

    private PagePlotElement[] PageElements =>
        Elements is IEnumerable enumerable ? enumerable.OfType<PagePlotElement>().ToArray() : [];

    private void resubscribe_elements()
    {
        if (subscribed_elements is not null)
            subscribed_elements.CollectionChanged -= elements_collection_changed;
        unsubscribe_page_elements();
        subscribed_elements = Elements as INotifyCollectionChanged;
        if (subscribed_elements is not null)
            subscribed_elements.CollectionChanged += elements_collection_changed;
        subscribe_page_elements();
        update_content_extent();
        InvalidateVisual();
    }

    private void elements_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        unsubscribe_page_elements();
        subscribe_page_elements();
        density_grid_cache.Clear();
        contour_geometry_cache.Clear();
        plot_bitmap_cache.Clear();
        element_bitmap_cache.Clear();
        update_content_extent();
        InvalidateVisual();
    }

    private void subscribe_page_elements()
    {
        subscribed_page_elements = PageElements;
        foreach (var element in subscribed_page_elements)
        {
            element.PropertyChanged += page_element_property_changed;
            element.XAxis.PropertyChanged += page_element_property_changed;
            element.YAxis.PropertyChanged += page_element_property_changed;
            element.DotColor.PropertyChanged += page_element_property_changed;
        }
    }

    private void unsubscribe_page_elements()
    {
        foreach (var element in subscribed_page_elements)
        {
            element.PropertyChanged -= page_element_property_changed;
            element.XAxis.PropertyChanged -= page_element_property_changed;
            element.YAxis.PropertyChanged -= page_element_property_changed;
            element.DotColor.PropertyChanged -= page_element_property_changed;
        }
        subscribed_page_elements = [];
    }

    private void page_element_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is PagePlotElement element)
        {
            if (page_property_affects_density_grid(e.PropertyName))
            {
                density_grid_cache.Remove(element.Id);
                contour_geometry_cache.Remove(element.Id);
                plot_bitmap_cache.Remove(element.Id);
            }
            else if (page_property_affects_plot_bitmap(e.PropertyName))
            {
                contour_geometry_cache.Remove(element.Id);
                plot_bitmap_cache.Remove(element.Id);
            }

            if (captured_element is null && e.PropertyName is nameof(PagePlotElement.X) or nameof(PagePlotElement.Y) or nameof(PagePlotElement.Size))
                update_content_extent();
            if (e.PropertyName is not (nameof(PagePlotElement.X) or nameof(PagePlotElement.Y)))
                element_bitmap_cache.Remove(element.Id);
        }
        else if (sender is AxisSettings axis)
        {
            foreach (var owner in subscribed_page_elements.Where(item => ReferenceEquals(item.XAxis, axis) || ReferenceEquals(item.YAxis, axis)))
            {
                density_grid_cache.Remove(owner.Id);
                contour_geometry_cache.Remove(owner.Id);
                plot_bitmap_cache.Remove(owner.Id);
                element_bitmap_cache.Remove(owner.Id);
            }
        }
        else if (sender is DotColorSettings dot_color)
        {
            foreach (var owner in subscribed_page_elements.Where(item => ReferenceEquals(item.DotColor, dot_color)))
            {
                density_grid_cache.Remove(owner.Id);
                contour_geometry_cache.Remove(owner.Id);
                plot_bitmap_cache.Remove(owner.Id);
                element_bitmap_cache.Remove(owner.Id);
            }
        }

        InvalidateVisual();
    }

    private static bool page_property_affects_density_grid(string? property_name) =>
        property_name is null
        or nameof(PagePlotElement.PlotMode)
        or nameof(PagePlotElement.DotColor)
        or nameof(PagePlotElement.Sample)
        or nameof(PagePlotElement.Gate)
        or nameof(PagePlotElement.Population);

    private static bool page_property_affects_plot_bitmap(string? property_name) =>
        property_name is nameof(PagePlotElement.PlotMode)
        or nameof(PagePlotElement.ShowOutlierPoints)
        or nameof(PagePlotElement.DrawLargeDots)
        or nameof(PagePlotElement.UsePseudocolor)
        or nameof(PagePlotElement.ContourLevelCount)
        or nameof(PagePlotElement.DensitySmoothing);

    private void update_content_extent()
    {
        var elements = PageElements;
        content_extent = elements.Length == 0
            ? default
            : new Size(
                Math.Ceiling(elements.Max(element => element.X + element.Size + workspace_margin)),
                Math.Ceiling(elements.Max(element => element.Y + element.Size + workspace_margin)));
        InvalidateMeasure();
    }

    private static int to_bin(double value, AxisSettings axis, double transformed_minimum, double transformed_span)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return -1;
        double normalized = (axis.Scale.Transform(value) - transformed_minimum) / transformed_span;
        if (double.IsNaN(normalized) || double.IsInfinity(normalized))
            return -1;
        if (normalized < 0 || normalized > 1)
            return -1;
        return Math.Clamp((int)(normalized * (raster_size - 1)), 0, raster_size - 1);
    }

    private static Point data_to_screen(Point data, PagePlotElement element, Rect plot_rect)
    {
        double x = normalize(data.X, element.XAxis);
        double y = normalize(data.Y, element.YAxis);
        return new Point(plot_rect.Left + x * plot_rect.Width, plot_rect.Bottom - y * plot_rect.Height);
    }

    private static double normalize(double value, AxisSettings axis)
    {
        double minimum = axis.Scale.Transform(axis.Minimum);
        double maximum = axis.Scale.Transform(axis.Maximum);
        if (maximum <= minimum)
            return 0;
        return Math.Clamp((axis.Scale.Transform(value) - minimum) / (maximum - minimum), 0, 1);
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

    private static double[,] normalized_density_grid(int[,] density, int max_density)
    {
        var normalized = new double[raster_size, raster_size];
        double denominator = Math.Log(1 + max_density);
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
            if (density[x, y] > 0)
                normalized[x, y] = Math.Log(1 + density[x, y]) / denominator;
        return normalized;
    }

    private static bool should_color_dots(PagePlotElement element) =>
        element.DotColor.HasChannel && element.PlotMode == PlotMode.Dotplot;

    private static Color?[,] create_dot_colors(PagePlotElement element, double[,]? color_sums, int[,]? color_counts)
    {
        if (color_sums is null || color_counts is null)
            return new Color?[raster_size, raster_size];

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
        {
            if (color_counts[x, y] <= 0)
                continue;
            double value = color_sums[x, y] / color_counts[x, y];
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        if (double.IsInfinity(minimum) || maximum <= minimum)
            return new Color?[raster_size, raster_size];

        var colors = new Color?[raster_size, raster_size];
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
        {
            if (color_counts[x, y] <= 0)
                continue;
            double value = color_sums[x, y] / color_counts[x, y];
            double normalized = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
            colors[x, y] = palette_color(normalized, element.DotColor.Palette);
        }

        return colors;
    }

    private static double transform_dot_color_value(double value, bool use_log_scale) =>
        use_log_scale ? Math.Log10(1 + Math.Max(0, value)) : value;

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
        var horizontal = new double[raster_size, raster_size];
        var result = new double[raster_size, raster_size];
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
        {
            double sum = 0;
            double weight = 0;
            for (int offset = -2; offset <= 2; offset++)
            {
                int sample_x = x + offset;
                if (sample_x < 0 || sample_x >= raster_size)
                    continue;
                sum += source[sample_x, y] * kernel[offset + 2];
                weight += kernel[offset + 2];
            }
            horizontal[x, y] = weight == 0 ? 0 : sum / weight;
        }
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
        {
            double sum = 0;
            double weight = 0;
            for (int offset = -2; offset <= 2; offset++)
            {
                int sample_y = y + offset;
                if (sample_y < 0 || sample_y >= raster_size)
                    continue;
                sum += horizontal[x, sample_y] * kernel[offset + 2];
                weight += kernel[offset + 2];
            }
            result[x, y] = weight == 0 ? 0 : sum / weight;
        }
        return result;
    }

    private static void add_dotplot_pixels(byte[] pixels, int[,] density, Color?[,]? colors, int total_count, bool large_dots)
    {
        double threshold = large_dots ? 0 : total_count * 0.00001;
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
            if (density[x, y] > threshold)
            {
                var color = colors?[x, y] ?? Colors.Black;
                if (large_dots)
                    set_pixel_rect(pixels, x, raster_size - 1 - y, 2, color);
                else
                    set_pixel(pixels, x, raster_size - 1 - y, color);
            }
    }

    private static void add_filled_contour_pixels(byte[] pixels, double[,] normalized_density, double[] levels)
    {
        for (int x = 0; x < raster_size; x++)
        for (int y = 0; y < raster_size; y++)
        {
            double value = normalized_density[x, y];
            int level_index = Array.FindLastIndex(levels, level => value >= level);
            if (level_index < 0)
                continue;

            double normalized_level = (level_index + 1.0) / levels.Length;
            byte shade = Convert.ToByte(250 - normalized_level * 210);
            set_pixel(pixels, x, raster_size - 1 - y, Color.FromRgb(shade, shade, shade));
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

    private static double[] contour_levels(int count)
    {
        count = Math.Clamp(count, 2, 80);
        var levels = new double[count];
        for (int index = 0; index < count; index++)
            levels[index] = 0.08 + 0.88 * (index + 1) / (count + 1);
        return levels;
    }

    private static Color plot_color(double value, PagePlotElement element)
    {
        value = Math.Clamp(value, 0, 1);
        if (element.PlotMode == PlotMode.Zebra)
        {
            Color[] cycle =
            [
                Color.FromRgb(220, 220, 220),
                Color.FromRgb(150, 150, 150),
                Color.FromRgb(85, 85, 85),
                Color.FromRgb(35, 35, 35),
                Colors.Black
            ];
            int bands = Math.Clamp(element.ContourLevelCount * 5, 10, 400);
            int band = Math.Clamp((int)Math.Floor(value * bands), 0, bands - 1);
            return cycle[band % cycle.Length];
        }

        if (!element.UsePseudocolor)
        {
            byte shade = Convert.ToByte(255 - value * 220);
            return Color.FromRgb(shade, shade, shade);
        }

        if (value < 0.25)
            return Color.FromRgb(21, 35, Convert.ToByte(110 + value * 360));
        if (value < 0.5)
            return Color.FromRgb(0, Convert.ToByte(80 + value * 260), 220);
        if (value < 0.75)
            return Color.FromRgb(Convert.ToByte((value - 0.5) * 720), 230, 95);
        return Color.FromRgb(255, Convert.ToByte(220 - (value - 0.75) * 260), 40);
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

    private static WriteableBitmap create_bitmap(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(raster_size, raster_size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        int row_bytes = raster_size * 4;
        if (frame.RowBytes == row_bytes)
        {
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            return bitmap;
        }
        for (int y = 0; y < raster_size; y++)
            Marshal.Copy(pixels, y * row_bytes, IntPtr.Add(frame.Address, y * frame.RowBytes), row_bytes);
        return bitmap;
    }

    private static void set_pixel(byte[] pixels, int x, int y, Color color)
    {
        if (x < 0 || x >= raster_size || y < 0 || y >= raster_size)
            return;

        int offset = (y * raster_size + x) * 4;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
        pixels[offset + 3] = color.A;
    }

    private static void set_pixel_rect(byte[] pixels, int center_x, int center_y, int radius, Color color)
    {
        for (int y = center_y - radius; y <= center_y + radius; y++)
        for (int x = center_x - radius; x <= center_x + radius; x++)
            set_pixel(pixels, x, y, color);
    }

    private static IEnumerable<double> major_axis_ticks(AxisSettings axis)
    {
        double[] values = axis.ScaleKind == CoordinateScaleKind.Logicle ? [0, 1000, 10000, 100000] : [0, 50000, 100000, 150000, 200000];
        return values.Where(value => value >= axis.Minimum && value <= axis.Maximum);
    }

    private static IEnumerable<double> minor_axis_ticks(AxisSettings axis)
    {
        if (axis.Maximum <= axis.Minimum)
            yield break;

        if (axis.ScaleKind == CoordinateScaleKind.Logicle)
        {
            for (int decade = 3; decade <= 4; decade++)
            {
                double base_value = Math.Pow(10, decade);
                for (int multiplier = 2; multiplier <= 9; multiplier++)
                {
                    double value = multiplier * base_value;
                    if (value >= axis.Minimum && value <= axis.Maximum)
                        yield return value;
                }
            }
            yield break;
        }

        for (double value = 10000; value < 200000; value += 10000)
        {
            if (value % 50000 == 0)
                continue;
            if (value >= axis.Minimum && value <= axis.Maximum)
                yield return value;
        }
    }

    private void draw_snap_guides(DrawingContext context, Rect bounds)
    {
        if (active_vertical_snap_guide is null && active_horizontal_snap_guide is null)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 0.8, DashStyle.Dash);
        if (active_vertical_snap_guide is { } x)
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        if (active_horizontal_snap_guide is { } y)
            context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
    }

    private static string format_axis_value(double value) =>
        Math.Abs(value) >= 1000 ? $"{value / 1000:0.#}k" : value.ToString("0.#", CultureInfo.InvariantCulture);

    private static Rect make_rect(Point first, Point second) =>
        new(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

    private void draw_text(DrawingContext context, string text, Point origin, double size, Color color, bool bolded = false) =>
        context.DrawText(create_formatted_text(text, size, color, bolded), origin);

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private void draw_centered_text_in_band(DrawingContext context, string text, Rect band, double size, Color color, bool bolded = false)
    {
        var formatted = create_formatted_text(text, size, color, bolded);
        context.DrawText(
            formatted,
            new Point(
                band.Left + (band.Width - formatted.Width) / 2,
                band.Top + (band.Height - formatted.Height) / 2));
    }

    private void draw_right_aligned_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width, origin.Y));
    }

    private void draw_vertical_centered_text(DrawingContext context, string text, Point center, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        var transform = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(transform))
            context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private FormattedText create_formatted_text(string text, double size, Color color, bool bolded = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, bolded ? FontWeight.Bold : FontWeight.Normal), size, new SolidColorBrush(color));

    private static string escape_xml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void append_svg_gates(StringBuilder svg, PagePlotElement element)
    {
        Rect plot = plot_rect_for(new Rect(0, 0, element.Size, element.Size), element.ShowTickLabels);
        foreach (var gate in resolve_plot_gates(element))
        {
            if (gate.Vertices.Count == 0)
                continue;
            if (gate.Kind == GateKind.Rectangle && gate.Vertices.Count >= 2)
            {
                var rect = make_rect(data_to_screen(gate.Vertices[0], element, plot), data_to_screen(gate.Vertices[1], element, plot));
                svg.AppendLine($"""<rect x="{rect.X.ToString(CultureInfo.InvariantCulture)}" y="{rect.Y.ToString(CultureInfo.InvariantCulture)}" width="{rect.Width.ToString(CultureInfo.InvariantCulture)}" height="{rect.Height.ToString(CultureInfo.InvariantCulture)}" fill="none" stroke="black" stroke-width="1.4"/>""");
            }
            else if (gate.Kind is GateKind.Threshold or GateKind.Range)
            {
                foreach (var vertex in gate.Vertices)
                {
                    double x = data_to_screen(vertex, element, plot).X;
                    svg.AppendLine($"""<line x1="{x.ToString(CultureInfo.InvariantCulture)}" y1="{plot.Top.ToString(CultureInfo.InvariantCulture)}" x2="{x.ToString(CultureInfo.InvariantCulture)}" y2="{plot.Bottom.ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1.4"/>""");
                }
            }
            else if (gate.Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant)
            {
                var point = data_to_screen(gate.Vertices[0], element, plot);
                svg.AppendLine($"""<line x1="{point.X.ToString(CultureInfo.InvariantCulture)}" y1="{plot.Top.ToString(CultureInfo.InvariantCulture)}" x2="{point.X.ToString(CultureInfo.InvariantCulture)}" y2="{plot.Bottom.ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1.4"/>""");
                svg.AppendLine($"""<line x1="{plot.Left.ToString(CultureInfo.InvariantCulture)}" y1="{point.Y.ToString(CultureInfo.InvariantCulture)}" x2="{plot.Right.ToString(CultureInfo.InvariantCulture)}" y2="{point.Y.ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1.4"/>""");
            }
            else
            {
                var points = string.Join(" ", gate.Vertices.Select(vertex =>
                {
                    var point = data_to_screen(vertex, element, plot);
                    return $"{point.X.ToString(CultureInfo.InvariantCulture)},{point.Y.ToString(CultureInfo.InvariantCulture)}";
                }));
                svg.AppendLine($"""<polygon points="{points}" fill="none" stroke="black" stroke-width="1.4"/>""");
            }

            if (element.ShowGateAnnotations)
                append_svg_gate_annotation(svg, element, gate, plot);
        }
    }

    private static void append_svg_axes(StringBuilder svg, PagePlotElement element)
    {
        Rect plot = plot_rect_for(new Rect(0, 0, element.Size, element.Size), element.ShowTickLabels);
        svg.AppendLine($"""<rect x="{plot.Left.ToString(CultureInfo.InvariantCulture)}" y="{plot.Top.ToString(CultureInfo.InvariantCulture)}" width="{plot.Width.ToString(CultureInfo.InvariantCulture)}" height="{plot.Height.ToString(CultureInfo.InvariantCulture)}" fill="none" stroke="black" stroke-width="1"/>""");
        foreach (double value in minor_axis_ticks(element.XAxis))
        {
            double x = data_to_screen(new Point(value, element.YAxis.Minimum), element, plot).X;
            svg.AppendLine($"""<line x1="{x.ToString(CultureInfo.InvariantCulture)}" y1="{plot.Bottom.ToString(CultureInfo.InvariantCulture)}" x2="{x.ToString(CultureInfo.InvariantCulture)}" y2="{(plot.Bottom + 3).ToString(CultureInfo.InvariantCulture)}" stroke="#5a5a5a" stroke-width="0.8"/>""");
            svg.AppendLine($"""<line x1="{x.ToString(CultureInfo.InvariantCulture)}" y1="{plot.Top.ToString(CultureInfo.InvariantCulture)}" x2="{x.ToString(CultureInfo.InvariantCulture)}" y2="{(plot.Top - 3).ToString(CultureInfo.InvariantCulture)}" stroke="#5a5a5a" stroke-width="0.8"/>""");
        }
        foreach (double value in major_axis_ticks(element.XAxis))
        {
            double x = data_to_screen(new Point(value, element.YAxis.Minimum), element, plot).X;
            svg.AppendLine($"""<line x1="{x.ToString(CultureInfo.InvariantCulture)}" y1="{plot.Bottom.ToString(CultureInfo.InvariantCulture)}" x2="{x.ToString(CultureInfo.InvariantCulture)}" y2="{(plot.Bottom + 5).ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1"/>""");
            svg.AppendLine($"""<line x1="{x.ToString(CultureInfo.InvariantCulture)}" y1="{plot.Top.ToString(CultureInfo.InvariantCulture)}" x2="{x.ToString(CultureInfo.InvariantCulture)}" y2="{(plot.Top - 5).ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1"/>""");
            if (element.ShowTickLabels)
                svg.AppendLine($"""<text x="{x.ToString(CultureInfo.InvariantCulture)}" y="{(plot.Bottom + 16).ToString(CultureInfo.InvariantCulture)}" text-anchor="middle" font-family="Arial" font-size="9" fill="black">{escape_xml(format_axis_value(value))}</text>""");
        }
        if (!element.IsHistogram)
        {
            foreach (double value in minor_axis_ticks(element.YAxis))
            {
                double y = data_to_screen(new Point(element.XAxis.Minimum, value), element, plot).Y;
                svg.AppendLine($"""<line x1="{(plot.Left - 3).ToString(CultureInfo.InvariantCulture)}" y1="{y.ToString(CultureInfo.InvariantCulture)}" x2="{plot.Left.ToString(CultureInfo.InvariantCulture)}" y2="{y.ToString(CultureInfo.InvariantCulture)}" stroke="#5a5a5a" stroke-width="0.8"/>""");
                svg.AppendLine($"""<line x1="{plot.Right.ToString(CultureInfo.InvariantCulture)}" y1="{y.ToString(CultureInfo.InvariantCulture)}" x2="{(plot.Right + 3).ToString(CultureInfo.InvariantCulture)}" y2="{y.ToString(CultureInfo.InvariantCulture)}" stroke="#5a5a5a" stroke-width="0.8"/>""");
            }
            foreach (double value in major_axis_ticks(element.YAxis))
            {
                double y = data_to_screen(new Point(element.XAxis.Minimum, value), element, plot).Y;
                svg.AppendLine($"""<line x1="{(plot.Left - 5).ToString(CultureInfo.InvariantCulture)}" y1="{y.ToString(CultureInfo.InvariantCulture)}" x2="{plot.Left.ToString(CultureInfo.InvariantCulture)}" y2="{y.ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1"/>""");
                svg.AppendLine($"""<line x1="{plot.Right.ToString(CultureInfo.InvariantCulture)}" y1="{y.ToString(CultureInfo.InvariantCulture)}" x2="{(plot.Right + 5).ToString(CultureInfo.InvariantCulture)}" y2="{y.ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1"/>""");
                if (element.ShowTickLabels)
                    svg.AppendLine($"""<text x="{(plot.Left - 7).ToString(CultureInfo.InvariantCulture)}" y="{(y + 3).ToString(CultureInfo.InvariantCulture)}" text-anchor="end" font-family="Arial" font-size="9" fill="black">{escape_xml(format_axis_value(value))}</text>""");
            }
        }
        double x_axis_label_y = element.Size - bottom_axis_label_space / 2 + 4;
        double y_axis_label_x = left_axis_label_space / 2;
        svg.AppendLine($"""<text x="{(plot.Left + plot.Width / 2).ToString(CultureInfo.InvariantCulture)}" y="{x_axis_label_y.ToString(CultureInfo.InvariantCulture)}" text-anchor="middle" font-family="Arial" font-size="11" fill="black">{escape_xml(element.XAxis.ChannelName)}</text>""");
        if (!element.IsHistogram)
            svg.AppendLine($"""<text x="{y_axis_label_x.ToString(CultureInfo.InvariantCulture)}" y="{(plot.Top + plot.Height / 2).ToString(CultureInfo.InvariantCulture)}" transform="rotate(-90 {y_axis_label_x.ToString(CultureInfo.InvariantCulture)} {(plot.Top + plot.Height / 2).ToString(CultureInfo.InvariantCulture)})" text-anchor="middle" font-family="Arial" font-size="11" fill="black">{escape_xml(element.YAxis.ChannelName)}</text>""");
    }

    private static void append_svg_gate_annotation(StringBuilder svg, PagePlotElement element, GateDefinition gate, Rect plot)
    {
        if (gate.Vertices.Count == 0)
            return;

        foreach (var region in annotation_regions(gate))
            append_svg_gate_region_annotation(svg, element, gate, region, plot);
    }

    private static void append_svg_gate_region_annotation(StringBuilder svg, PagePlotElement element, GateDefinition gate, PopulationRegion region, Rect plot)
    {
        int event_count = 0;
        double parent_frequency = 0;
        int sample_count = 0;
        foreach (var sample in resolve_samples(element))
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

        string name = gate.HasLinkedPopulations ? $"{gate.Name} {population_region_name(region)}" : gate.Name;
        string count_line = $"{event_count:N0} ({parent_frequency:0.#}%)";
        double text_width = Math.Max(name.Length, count_line.Length) * 5.8;
        var origin = gate_annotation_origin(element, gate, region, plot, text_width + 6, 26);

        svg.AppendLine($"""<rect x="{(origin.X - 3).ToString(CultureInfo.InvariantCulture)}" y="{(origin.Y - 10).ToString(CultureInfo.InvariantCulture)}" width="{(text_width + 6).ToString(CultureInfo.InvariantCulture)}" height="26" fill="white" fill-opacity="0.86"/>""");
        svg.AppendLine($"""<text x="{origin.X.ToString(CultureInfo.InvariantCulture)}" y="{origin.Y.ToString(CultureInfo.InvariantCulture)}" font-family="Arial" font-size="10" fill="black">{escape_xml(name)}</text>""");
        svg.AppendLine($"""<text x="{origin.X.ToString(CultureInfo.InvariantCulture)}" y="{(origin.Y + 12).ToString(CultureInfo.InvariantCulture)}" font-family="Arial" font-size="10" fill="black">{escape_xml(count_line)}</text>""");
    }

    private void append_svg_plot_image(StringBuilder svg, PagePlotElement element)
    {
        var bitmap = element.IsHistogram ? create_histogram_bitmap(element) : create_density_bitmap(element);
        if (bitmap is null)
            return;

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        string encoded = Convert.ToBase64String(stream.ToArray());
        Rect plot = plot_rect_for(new Rect(0, 0, element.Size, element.Size), element.ShowTickLabels);
        svg.AppendLine($"""<image x="{plot.X.ToString(CultureInfo.InvariantCulture)}" y="{plot.Y.ToString(CultureInfo.InvariantCulture)}" width="{plot.Width.ToString(CultureInfo.InvariantCulture)}" height="{plot.Height.ToString(CultureInfo.InvariantCulture)}" href="data:image/png;base64,{encoded}" preserveAspectRatio="none"/>""");
    }

    private sealed class ExportPageVisual : Control
    {
        private readonly IReadOnlyList<PagePlotElement> elements;
        private readonly bool transparent_background;
        private readonly Size export_size;

        public ExportPageVisual(IReadOnlyList<PagePlotElement> elements, bool transparent_background, Size export_size)
        {
            this.elements = elements;
            this.transparent_background = transparent_background;
            this.export_size = export_size;
        }

        public override void Render(DrawingContext context)
        {
            if (!transparent_background)
                context.FillRectangle(Brushes.White, new Rect(export_size));
            var helper = new PageEditorView { Elements = elements };
            foreach (var element in elements)
                helper.draw_page_element(context, element, selected: false);
        }
    }

    private sealed class ElementContentVisual : Control
    {
        private readonly PageEditorView owner;
        private readonly PagePlotElement element;
        private readonly Size size;

        public ElementContentVisual(PageEditorView owner, PagePlotElement element, Size size)
        {
            this.owner = owner;
            this.element = element;
            this.size = size;
        }

        public override void Render(DrawingContext context) =>
            owner.draw_page_element_content(context, element, new Rect(size));
    }

    private sealed record DensityGrid(int[,] Density, Color?[,] Colors, int MaxDensity, int TotalCount);

    private sealed record ContourGeometry(IReadOnlyList<ContourSegment> Segments);

    private readonly record struct ContourSegment(Point Start, Point End);
}
