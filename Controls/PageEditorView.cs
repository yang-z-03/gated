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
using System.Threading;
using System.Threading.Tasks;
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
using gated.ViewModels;

namespace gated.Controls;

public sealed class PageElementContextRequestedEventArgs(PagePlotElement element, Point point) : EventArgs
{
    public PagePlotElement Element { get; } = element;
    public Point Point { get; } = point;
}

public sealed class PageEditorView : Control
{
    public const string ProjectNodeDataFormat = "gated/project-node";
    public const string ProjectNodePayloadPrefix = "gated/node:";
    public static ProjectNode? DraggedProjectNode { get; set; }
    public static Func<string, ProjectNode?>? ResolveProjectNodeByKey { get; set; }

    public static readonly StyledProperty<IEnumerable?> ElementsProperty =
        AvaloniaProperty.Register<PageEditorView, IEnumerable?>(nameof(Elements));

    public static readonly StyledProperty<PagePlotElement?> SelectedElementProperty =
        AvaloniaProperty.Register<PageEditorView, PagePlotElement?>(nameof(SelectedElement));

    public static readonly StyledProperty<ICommand?> AddElementCommandProperty =
        AvaloniaProperty.Register<PageEditorView, ICommand?>(nameof(AddElementCommand));

    public static readonly StyledProperty<Size> ViewportSizeProperty =
        AvaloniaProperty.Register<PageEditorView, Size>(nameof(ViewportSize));

    public static readonly StyledProperty<int> RefreshRevisionProperty =
        AvaloniaProperty.Register<PageEditorView, int>(nameof(RefreshRevision));

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
    private const double large_dot_radius = 1.75;
    private const double large_dot_opacity = 0.6;
    private const double scrollbar_margin = 8;
    private const double scrollbar_thickness = 10;
    private const double scrollbar_minimum_thumb = 36;
    private const double a4_width = 793.7;
    private const double a4_height = 1122.5;
    private PagePlotElement? captured_element;
    private Point drag_start_page;
    private Point drag_start_viewport;
    private Vector drag_start_scroll_offset;
    private double drag_start_x;
    private double drag_start_y;
    private double drag_start_size;
    private double drag_start_width;
    private double drag_start_height;
    private bool resizing;
    private ScrollDragKind scroll_drag_kind = ScrollDragKind.None;
    private ResizeCorner resize_corner = ResizeCorner.None;
    private Vector scroll_offset;
    private double render_scale = 1;
    private bool use_visual_root_render_scale = true;
    private bool apply_rasterization_resolution;
    private INotifyCollectionChanged? subscribed_elements;
    private PagePlotElement[] subscribed_page_elements = [];
    private Size content_extent;
    private double? active_vertical_snap_guide;
    private double? active_horizontal_snap_guide;
    private readonly Dictionary<Guid, (string Key, DensityGrid? Grid)> density_grid_cache = new();
    private readonly Dictionary<Guid, (string Key, ContourGeometry? Geometry)> contour_geometry_cache = new();
    private readonly Dictionary<Guid, (string Key, WriteableBitmap? Bitmap)> plot_bitmap_cache = new();
    private readonly Dictionary<Guid, (string Key, RenderTargetBitmap Bitmap)> element_bitmap_cache = new();
    private readonly HashSet<Guid> refreshing_element_ids = new();
    private CancellationTokenSource? render_cache_refresh_cancellation;

    public event EventHandler<PageElementContextRequestedEventArgs>? ElementContextRequested;

    static PageEditorView()
    {
        AffectsRender<PageEditorView>(ElementsProperty, SelectedElementProperty, RefreshRevisionProperty);
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

    public int RefreshRevision
    {
        get => GetValue(RefreshRevisionProperty);
        set => SetValue(RefreshRevisionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        update_render_scale_from_visual_root();
        var bounds = Bounds;
        context.FillRectangle(Brushes.White, bounds);

        var clip = new Rect(bounds.Size);
        using (context.PushClip(clip))
        using (context.PushTransform(Matrix.CreateTranslation(-scroll_offset.X, -scroll_offset.Y)))
        {
            foreach (var element in PageElements)
                draw_page_element(context, element);
            draw_snap_guides(context, new Rect(scroll_offset.X, scroll_offset.Y, bounds.Width, bounds.Height));
            if (SelectedElement is not null)
                draw_selection_border(context, SelectedElement);
            draw_a4_grid(context, workspace_size());
            if (SelectedElement is not null)
                draw_resize_grips(context, SelectedElement);
        }
        draw_manual_scrollbars(context, bounds);
    }

    public void RefreshRenderCachesSequentially(IEnumerable? elements)
    {
        var work_items = (elements ?? Array.Empty<PagePlotElement>())
            .OfType<PagePlotElement>()
            .Where(element => element.ElementKind == PageElementKind.FlowPlot)
            .Select(element => new LayoutCacheWorkItem(
                element,
                density_grid_key(element),
                contour_geometry_key(element),
                element.PlotMode == PlotMode.Contour))
            .ToArray();
        if (work_items.Length == 0)
            return;

        render_cache_refresh_cancellation?.Cancel();
        render_cache_refresh_cancellation?.Dispose();
        render_cache_refresh_cancellation = new CancellationTokenSource();
        var token = render_cache_refresh_cancellation.Token;
        refreshing_element_ids.Clear();
        foreach (var item in work_items)
            refreshing_element_ids.Add(item.Element.Id);

        _ = refresh_render_caches_sequentially_async(work_items, token);
    }

    public void RefreshElementRenderCache(PagePlotElement element)
    {
        remove_element_caches(element.Id);
        InvalidateVisual();
    }

    private async Task refresh_render_caches_sequentially_async(IReadOnlyList<LayoutCacheWorkItem> work_items, CancellationToken token)
    {
        foreach (var item in work_items)
        {
            LayoutCacheResult result;
            try
            {
                result = await Task.Run(() => create_layout_cache_result(item), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            density_grid_cache[result.ElementId] = (result.DensityKey, result.Grid);
            if (result.NeedsContour)
                contour_geometry_cache[result.ElementId] = (result.ContourKey, result.Contour);
            else
                contour_geometry_cache.Remove(result.ElementId);
            plot_bitmap_cache.Remove(result.ElementId);
            element_bitmap_cache.Remove(result.ElementId);
            refreshing_element_ids.Remove(result.ElementId);
            InvalidateVisual();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private LayoutCacheResult create_layout_cache_result(LayoutCacheWorkItem item)
    {
        var grid = create_density_grid(item.Element);
        ContourGeometry? contour = null;
        if (item.NeedsContour && grid is not null)
        {
            var normalized = smooth_density(normalized_density_grid(grid.Density, grid.MaxDensity), item.Element.DensitySmoothing);
            contour = new ContourGeometry(create_contour_segments(normalized, contour_levels(item.Element.ContourLevelCount)), normalized.GetLength(0));
        }

        return new LayoutCacheResult(item.Element.Id, item.DensityKey, grid, item.ContourKey, contour, item.NeedsContour);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ElementsProperty)
            resubscribe_elements();
        if (change.Property == ViewportSizeProperty)
            update_workspace_size();
        if (change.Property == RefreshRevisionProperty)
            clear_render_caches();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        update_render_scale_from_visual_root();
        clear_render_caches();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var viewport_point = e.GetPosition(this);
        if (try_begin_scroll_drag(viewport_point, e))
            return;

        var point = to_page_point(viewport_point);
        var element = PageElements.LastOrDefault(item => item.Bounds.Contains(point));
        SelectedElement = element;
        if (element is null)
        {
            InvalidateVisual();
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ElementContextRequested?.Invoke(this, new PageElementContextRequestedEventArgs(element, viewport_point));
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        captured_element = element;
        drag_start_page = point;
        drag_start_viewport = viewport_point;
        drag_start_x = element.X;
        drag_start_y = element.Y;
        drag_start_size = element.Size;
        drag_start_width = element.Width;
        drag_start_height = element.Height;
        resize_corner = resize_corner_at(element, point);
        resizing = resize_corner != ResizeCorner.None;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (scroll_drag_kind != ScrollDragKind.None)
        {
            update_scroll_drag(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (captured_element is null)
            return;

        var point = to_page_point(e.GetPosition(this));
        if (resizing)
        {
            if (is_free_aspect_element(captured_element))
            {
                var requested = requested_rect_size(captured_element, point);
                var snapped = snap_rect_size(captured_element, requested);
                apply_resized_rect(captured_element, snapped);
                captured_element.Width = snapped.Width;
                captured_element.Height = snapped.Height;
            }
            else
            {
                double requested = requested_square_size(point);
                captured_element.Size = snap_size(captured_element, requested);
                apply_resized_square(captured_element);
            }
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
        resize_corner = ResizeCorner.None;
        scroll_drag_kind = ScrollDragKind.None;
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        e.Pointer.Capture(null);
        update_content_extent();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = -e.Delta.Y * 72;
        var requested = (e.KeyModifiers & KeyModifiers.Shift) != 0
            ? new Vector(scroll_offset.X + delta, scroll_offset.Y)
            : new Vector(scroll_offset.X, scroll_offset.Y + delta);
        set_scroll_offset(requested);
        e.Handled = true;
    }

    public void SaveBitmap(string file_path, bool transparent_background, double dpi, bool apply_rasterization_resolution)
    {
        var export_size = export_size_for(PageElements);
        dpi = double.IsFinite(dpi) ? Math.Clamp(dpi, 72, 1200) : 300;
        double scale = dpi / 96.0;
        var bitmap = new RenderTargetBitmap(
            new PixelSize(
                Math.Max(1, (int)Math.Ceiling(export_size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(export_size.Height * scale))),
            new Vector(dpi, dpi));
        var visual = new ExportPageVisual(PageElements, transparent_background, export_size, this, scale, apply_rasterization_resolution);
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
            svg.AppendLine($"""<rect width="{element.Width.ToString(CultureInfo.InvariantCulture)}" height="{element.Height.ToString(CultureInfo.InvariantCulture)}" fill="white"/>""");
            if (element.ElementKind == PageElementKind.FlowPlot)
            {
                append_svg_plot_image(svg, element);
                if (element.PlotMode == PlotMode.Contour)
                    append_svg_contours(svg, element);
                append_svg_axes(svg, element);
                svg.AppendLine($"""<text x="{(element.Width / 2).ToString(CultureInfo.InvariantCulture)}" y="{(title_space / 2 + 5).ToString(CultureInfo.InvariantCulture)}" text-anchor="middle" font-family="Arial" font-size="13" font-weight="700" fill="black">{escape_xml(element.Title)}</text>""");
                if (element.ShowGates)
                    append_svg_gates(svg, element);
            }
            else
            {
                append_svg_element_bitmap(svg, element);
            }
            svg.AppendLine("</g>");
        }
        svg.AppendLine("</svg>");
        System.IO.File.WriteAllText(file_path, svg.ToString());
    }

    private void draw_page_element(DrawingContext context, PagePlotElement element)
    {
        var bounds = element.Bounds;
        var bitmap = get_element_bitmap(element);
        context.DrawImage(bitmap, sourceRect: new(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), destRect: bounds);
    }

    private static void draw_selection_border(DrawingContext context, PagePlotElement element)
    {
        var bounds = element.Bounds;
        var selection_pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1.5);
        context.DrawRectangle(null, selection_pen, bounds);
    }

    private static void draw_resize_grips(DrawingContext context, PagePlotElement element)
    {
        var grip_brush = new SolidColorBrush(Color.FromRgb(0, 120, 215));
        foreach (var handle in resize_handle_rects(element))
            context.FillRectangle(grip_brush, handle);
    }

    private void draw_page_element_content(DrawingContext context, PagePlotElement element, Rect bounds)
    {
        context.FillRectangle(Brushes.White, bounds);
        if (element is PlatformPlotElement platform_plot)
        {
            draw_platform_plot(context, platform_plot, bounds);
            return;
        }
        if (element is PlatformStatisticTableElement platform_table)
        {
            draw_platform_statistic_table(context, platform_table, bounds);
            return;
        }
        if (element is StatisticTableElement statistic_table)
        {
            draw_statistic_table(context, statistic_table, bounds);
            return;
        }

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

    private void draw_platform_plot(DrawingContext context, PlatformPlotElement element, Rect bounds)
    {
        draw_centered_text_in_band(context, element.Title, new Rect(bounds.Left, bounds.Top, bounds.Width, title_space), 13, Colors.Black, bolded: true);
        var series = PlatformResultPlotView.CreateDisplaySeries(element.Platform);
        if (!string.IsNullOrWhiteSpace(element.PlotKey))
            series = series.Where(item => item.Key == element.PlotKey).ToArray();
        bool has_data = series.Length > 0 && series.Any(item => item.X.Length > 0 && item.Y.Length > 0);
        if (!has_data)
            series = platform_dummy_series(element.Platform);

        var plot_rect = plot_rect_for(bounds, element.ShowTickLabels);

        var points = series
            .SelectMany(item => item.X.Zip(item.Y).Where(pair => double.IsFinite(pair.First) && double.IsFinite(pair.Second)))
            .ToArray();
        if (points.Length == 0)
            return;
        double min_x = points.Min(item => item.First);
        double max_x = points.Max(item => item.First);
        double min_y = Math.Min(0, points.Min(item => item.Second));
        double max_y = points.Max(item => item.Second);
        if (max_x <= min_x) max_x = min_x + 1;
        if (max_y <= min_y) max_y = min_y + 1;
        max_y *= 1.08;

        draw_platform_axes_and_grid(context, element, bounds, plot_rect, min_x, max_x, min_y, max_y, series);
        if (!has_data)
        {
            draw_centered_text_in_band(context, "No platform plot data", plot_rect, 12, Colors.Gray, bolded: false);
            return;
        }

        foreach (var plot in series)
        {
            int count = Math.Min(plot.X.Length, plot.Y.Length);
            var pen = platform_plot_pen(plot.Key);
            Point? previous = null;
            for (int index = 0; index < count; index++)
            {
                if (!double.IsFinite(plot.X[index]) || !double.IsFinite(plot.Y[index]))
                    continue;
                double x = plot_rect.Left + (plot.X[index] - min_x) / (max_x - min_x) * plot_rect.Width;
                double y = plot_rect.Bottom - (Math.Max(0, plot.Y[index]) - min_y) / (max_y - min_y) * plot_rect.Height;
                var point = new Point(x, y);
                if (previous is not null)
                    context.DrawLine(pen, previous.Value, point);
                previous = point;
            }
        }
    }

    private void draw_platform_statistic_table(DrawingContext context, PlatformStatisticTableElement element, Rect bounds)
    {
        var (columns, rows, colors) = platform_statistic_table_data(element.Platform);
        draw_table(context, bounds, element.Title, columns, rows, colors);
    }

    private void draw_statistic_table(DrawingContext context, StatisticTableElement element, Rect bounds)
    {
        var columns = new[] { "Sample" }.Concat(element.Columns.Select(column => column.Title)).ToArray();
        var group = element.Group ?? element.Columns.Select(column => column.Group).FirstOrDefault(item => item is not null);
        var rows = group is null
            ? Array.Empty<string[]>()
            : group.Samples.Select(sample => statistic_table_row(sample, element.Columns)).ToArray();
        draw_table(context, bounds, element.Title, columns, rows, null);
    }

    private static string[] statistic_table_row(FlowSample sample, IReadOnlyList<StatisticTableColumn> columns)
    {
        var row = new List<string> { sample.Name };
        foreach (var column in columns)
        {
            if (column.Statistic is null)
            {
                row.Add("");
                continue;
            }

            if (column.Gate is not null)
            {
                var population = find_population(sample.Populations, column.Gate);
                var statistic = population?.Statistics.FirstOrDefault(item => statistic_matches_definition(item, column.Statistic));
                row.Add(statistic?.DisplayValue ?? "");
            }
            else
            {
                var all = sample.GetPlotEventIndices();
                row.Add(StatisticsCalculator.Calculate(sample, column.Statistic, all, all.Length, all.Length).DisplayValue);
            }
        }
        return row.ToArray();
    }

    private static bool statistic_matches_definition(StatisticResult result, StatisticDefinition definition) =>
        result.Kind == definition.Kind &&
        result.ChannelName == definition.ChannelName &&
        (definition.Kind != StatisticKind.Python ||
         (result.PythonSource == definition.PythonSource &&
          result.PythonCallableName == definition.PythonCallableName &&
          result.PythonParametersJson == definition.PythonParametersJson));

    private void draw_table(DrawingContext context, Rect bounds, string title, IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, IReadOnlyList<Color?>? row_colors)
    {
        draw_centered_text_in_band(context, title, new Rect(bounds.Left, bounds.Top, bounds.Width, title_space), 13, Colors.Black, bolded: true);
        var table = new Rect(bounds.Left + 10, bounds.Top + title_space, Math.Max(1, bounds.Width - 20), Math.Max(1, bounds.Height - title_space - 10));
        int column_count = Math.Max(1, columns.Count);
        double row_height = 20;
        var column_widths = table_column_widths(columns, rows, table.Width);
        var border_pen = new Pen(new SolidColorBrush(Color.FromRgb(186, 186, 186)), 0.8);
        var header_brush = new SolidColorBrush(Color.FromRgb(236, 236, 236));
        var alternate_brush = new SolidColorBrush(Color.FromRgb(248, 248, 248));
        var table_width = column_widths.Sum();
        context.DrawRectangle(null, border_pen, new Rect(table.Left, table.Top, table_width, Math.Min(table.Height, row_height * (rows.Count + 1))));
        double left = table.Left;
        for (int column = 0; column < column_count; column++)
        {
            var cell = new Rect(left, table.Top, column_widths[column], row_height);
            context.FillRectangle(header_brush, cell);
            context.DrawRectangle(null, border_pen, cell);
            draw_text(context, columns[column], new Point(cell.Left + 5, cell.Top + 3), 11, Colors.Black, bolded: true);
            left += column_widths[column];
        }

        int visible_rows = Math.Min(rows.Count, Math.Max(0, (int)((table.Height - row_height) / row_height)));
        for (int row = 0; row < visible_rows; row++)
        {
            left = table.Left;
            if (row % 2 == 1)
                context.FillRectangle(alternate_brush, new Rect(table.Left, table.Top + (row + 1) * row_height, table_width, row_height));
            for (int column = 0; column < column_count; column++)
            {
                var cell = new Rect(left, table.Top + (row + 1) * row_height, column_widths[column], row_height);
                context.DrawRectangle(null, border_pen, cell);
                string value = column < rows[row].Length ? rows[row][column] : "";
                if (column == 0 && row_colors is not null && row < row_colors.Count && row_colors[row] is { } color)
                    context.DrawRectangle(new SolidColorBrush(color), null, new Rect(cell.Left + 6, cell.Top + 5, 12, 10), 2);
                draw_text(context, value, new Point(cell.Left + (column == 0 && row_colors is not null ? 24 : 5), cell.Top + 3), 11, Colors.Black);
                left += column_widths[column];
            }
        }
    }

    private void draw_platform_axes_and_grid(
        DrawingContext context,
        PlatformPlotElement element,
        Rect bounds,
        Rect plot_rect,
        double min_x,
        double max_x,
        double min_y,
        double max_y,
        IReadOnlyList<PlatformPlotSeries> series)
    {
        var spine_pen = new Pen(Brushes.Black, 1);
        var major_pen = new Pen(Brushes.Black, 1);
        var minor_pen = new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90)), 0.8);
        var major_grid_pen = new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 0.8);
        var minor_grid_pen = new Pen(new SolidColorBrush(Color.FromRgb(238, 238, 238)), 0.6);

        foreach (double tick in platform_y_ticks(min_y, max_y, major: false))
        {
            double y = plot_rect.Bottom - (tick - min_y) / (max_y - min_y) * plot_rect.Height;
            if (element.ShowGridlines)
                context.DrawLine(minor_grid_pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
            context.DrawLine(minor_pen, new Point(plot_rect.Left - 3, y), new Point(plot_rect.Left, y));
            context.DrawLine(minor_pen, new Point(plot_rect.Right, y), new Point(plot_rect.Right + 3, y));
        }
        foreach (double tick in platform_y_ticks(min_y, max_y, major: true))
        {
            double y = plot_rect.Bottom - (tick - min_y) / (max_y - min_y) * plot_rect.Height;
            if (element.ShowGridlines)
                context.DrawLine(major_grid_pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
            context.DrawLine(major_pen, new Point(plot_rect.Left - 5, y), new Point(plot_rect.Left, y));
            context.DrawLine(major_pen, new Point(plot_rect.Right, y), new Point(plot_rect.Right + 5, y));
            if (element.ShowTickLabels)
                draw_right_aligned_text(context, Configuration.FormatAxisValue(tick), new Point(plot_rect.Left - 7, y - 6), 9, Colors.Black);
        }

        foreach (var tick in platform_x_ticks(element.Platform, min_x, max_x, major: false))
        {
            double x = plot_rect.Left + (tick.Position - min_x) / (max_x - min_x) * plot_rect.Width;
            if (element.ShowGridlines)
                context.DrawLine(minor_grid_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
            context.DrawLine(minor_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 3));
            context.DrawLine(minor_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Top - 3));
        }
        foreach (var tick in platform_x_ticks(element.Platform, min_x, max_x, major: true))
        {
            double x = plot_rect.Left + (tick.Position - min_x) / (max_x - min_x) * plot_rect.Width;
            if (element.ShowGridlines)
                context.DrawLine(major_grid_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
            context.DrawLine(major_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 5));
            context.DrawLine(major_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Top - 5));
            if (element.ShowTickLabels)
                draw_centered_text(context, tick.Label, new Point(x, plot_rect.Bottom + 7), 9, Colors.Black);
        }

        context.DrawRectangle(null, spine_pen, plot_rect);
        string x_label = series.FirstOrDefault()?.XLabel ?? (element.Platform?.Kind == PlatformKind.Kinetics ? "Time" : "Intensity");
        string y_label = element.Platform?.Kind == PlatformKind.Kinetics
            ? series.FirstOrDefault()?.YLabel ?? element.Platform.SelectedFeatureNames.FirstOrDefault() ?? "Signal"
            : "Frequency";
        draw_centered_text_in_band(context, x_label, new Rect(plot_rect.Left, bounds.Bottom - bottom_axis_label_space, plot_rect.Width, bottom_axis_label_space), 11, Colors.Black);
        draw_vertical_centered_text(context, y_label, new Point(bounds.Left + left_axis_label_space / 2, plot_rect.Top + plot_rect.Height / 2), 11, Colors.Black);
    }

    private static PlatformPlotSeries[] platform_dummy_series(Platform? platform)
    {
        double minimum;
        double maximum;
        if (platform?.Kind == PlatformKind.Kinetics)
        {
            minimum = 0;
            maximum = 1;
        }
        else if (platform?.Axis.Transform == PlatformTransformationKind.Logicle)
        {
            var transform = new LogicleTransform(platform.Axis.Logicle);
            minimum = transform.Transform(platform.Axis.Minimum);
            maximum = transform.Transform(platform.Axis.Maximum);
        }
        else
        {
            minimum = platform?.Axis.Minimum ?? 0;
            maximum = platform?.Axis.Maximum ?? 1;
        }
        if (maximum <= minimum)
            maximum = minimum + 1;

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

    private static IEnumerable<double> platform_y_ticks(double minimum, double maximum, bool major)
    {
        var axis = new AxisSettings { Minimum = minimum, Maximum = maximum, ScaleKind = CoordinateScaleKind.Linear };
        return major ? Configuration.MajorAxisTicks(axis) : Configuration.MinorAxisTicks(axis);
    }

    private static IEnumerable<(double Position, string Label)> platform_x_ticks(Platform? platform, double minimum, double maximum, bool major)
    {
        if (platform?.Axis.Transform == PlatformTransformationKind.Logicle && platform.Kind != PlatformKind.Kinetics)
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

    private static Pen platform_plot_pen(string key)
    {
        bool is_component = key.Contains("component", StringComparison.OrdinalIgnoreCase) || key.Contains("generation", StringComparison.OrdinalIgnoreCase);
        bool is_sum = key.StartsWith("fit_", StringComparison.OrdinalIgnoreCase) || key.Contains("_fit_", StringComparison.OrdinalIgnoreCase);
        Color color = PlatformPalette.ColorForSeriesKey(key);
        if (is_sum)
            color = Color.FromRgb((byte)Math.Max(0, color.R - 42), (byte)Math.Max(0, color.G - 42), (byte)Math.Max(0, color.B - 42));
        var pen = new Pen(new SolidColorBrush(color), is_component ? 1.2 : is_sum ? 2.0 : 1.2);
        if (is_component)
            pen.DashStyle = DashStyle.Dot;
        return pen;
    }

    private (string[] Columns, string[][] Rows, Color?[] Colors) platform_statistic_table_data(Platform? platform)
    {
        var table = platform?.ResultTables.FirstOrDefault();
        if (platform is null || table is null)
        {
            var rows = platform?.PlatformStatistics.Select(item => new[] { item.Name, item.Value }).ToArray() ?? [];
            return (["Statistic", "Value"], rows, rows.Select(_ => (Color?)null).ToArray());
        }

        var rows_with_colors = table.Rows
            .Select(row => (Row: row, Color: platform_table_row_color(platform, row)))
            .ToArray();
        return (table.Columns, rows_with_colors.Select(item => item.Row).ToArray(), rows_with_colors.Select(item => item.Color).ToArray());
    }

    private static Color? platform_table_row_color(Platform platform, string[] row)
    {
        if (row.Length < 2)
            return null;
        for (int index = 0; index < platform.Populations.Count; index++)
        {
            var population = platform.Populations[index];
            if (!population.IsPopulation || !population.IsPlatformDropped)
                continue;
            if (!string.Equals(population.SampleName, row[0], StringComparison.Ordinal) ||
                !string.Equals(population.PopulationName, row[1], StringComparison.Ordinal))
                continue;
            int source_index = platform_source_index(platform, population);
            return PlatformPalette.ColorForIndex(source_index >= 0 ? source_index : index);
        }
        return null;
    }

    private static int platform_source_index(Platform platform, IntegrationJobPopulationSelection row)
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

    private static int platform_smoothing_half_window(Platform platform) =>
        platform switch
        {
            UnivariatePlatform univariate => univariate.Smoothing.HalfWindow,
            BivariatePlatform bivariate => bivariate.Smoothing.HalfWindow,
            _ => 0
        };

    private static bool platform_smoothing_enabled(Platform platform) =>
        platform switch
        {
            UnivariatePlatform univariate => univariate.Smoothing.Enabled,
            BivariatePlatform bivariate => bivariate.Smoothing.Enabled,
            _ => false
        };

    private static bool platform_draw_model_sum(Platform platform) =>
        platform switch
        {
            CellCyclePlatform cell_cycle => cell_cycle.DrawModelSum,
            ProliferationPlatform proliferation => proliferation.DrawModelSum,
            _ => true
        };

    private static bool platform_draw_components(Platform platform) =>
        platform switch
        {
            CellCyclePlatform cell_cycle => cell_cycle.DrawComponents,
            ProliferationPlatform proliferation => proliferation.DrawComponents,
            _ => true
        };

    private double[] table_column_widths(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, double minimum_total_width)
    {
        var widths = new double[Math.Max(1, columns.Count)];
        for (int column = 0; column < widths.Length; column++)
        {
            double width = column < columns.Count ? text_width(columns[column], 11, bolded: true) + 18 : 50;
            foreach (var row in rows)
                if (column < row.Length)
                    width = Math.Max(width, text_width(row[column], 11, bolded: false) + (column == 0 ? 34 : 18));
            widths[column] = Math.Clamp(width, 46, 190);
        }
        double total = widths.Sum();
        if (total < minimum_total_width && widths.Length > 0)
        {
            double extra = (minimum_total_width - total) / widths.Length;
            for (int index = 0; index < widths.Length; index++)
                widths[index] += extra;
        }
        return widths;
    }

    private double text_width(string text, double size, bool bolded)
    {
        var formatted = create_formatted_text(text, size, Colors.Black, bolded);
        return formatted.Width;
    }

    private RenderTargetBitmap get_element_bitmap(PagePlotElement element)
    {
        update_render_scale_from_visual_root();
        string key = element_bitmap_key(element);
        if (element_bitmap_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Bitmap;
        if (refreshing_element_ids.Contains(element.Id) &&
            element_bitmap_cache.TryGetValue(element.Id, out cached))
            return cached.Bitmap;

        int width = Math.Max(1, (int)Math.Ceiling(element.Width * render_scale));
        int height = Math.Max(1, (int)Math.Ceiling(element.Height * render_scale));
        double dpi = 96 * render_scale;
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(dpi, dpi));
        var visual = new ElementContentVisual(this, element, new Size(element.Width, element.Height), render_scale);
        var logical_size = new Size(element.Width, element.Height);
        visual.Measure(logical_size);
        visual.Arrange(new Rect(logical_size));
        bitmap.Render(visual);
        element_bitmap_cache[element.Id] = (key, bitmap);
        return bitmap;
    }

    private void update_render_scale_from_visual_root()
    {
        if (!use_visual_root_render_scale)
            return;
        if (apply_rasterization_resolution)
            return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        scale = Math.Round(scale, 3);
        if (Math.Abs(render_scale - scale) < 0.001)
            return;

        render_scale = scale;
        element_bitmap_cache.Clear();
    }

    private string element_bitmap_key(PagePlotElement element) =>
        string.Join("|",
            plot_bitmap_key(element),
            element.Size,
            element.Width,
            element.Height,
            element.Title,
            platform_element_key(element),
            element.ShowTickLabels,
            element.ShowGates,
            element.ShowGateAnnotations,
            element.ShowGateAnnotationNames,
            axis_title(element, element.XAxis),
            axis_title(element, element.YAxis),
            element.XAxis.ChannelName,
            element.YAxis.ChannelName,
            element.DotColor.ChannelName,
            element.DotColor.Palette,
            element.DotColor.UseLogScale,
            element.DotColor.RangeMinimum,
            element.DotColor.RangeMaximum,
            render_scale.ToString("G6", CultureInfo.InvariantCulture));

    private static string platform_element_key(PagePlotElement element) =>
        element switch
        {
            PlatformPlotElement platform_plot when platform_plot.Platform is { } platform => string.Join("|",
                platform.Status,
                platform.Axis.Transform,
                platform.Axis.Minimum,
                platform.Axis.Maximum,
                platform.Axis.Logicle.T,
                platform.Axis.Logicle.W,
                platform.Axis.Logicle.M,
                platform.Axis.Logicle.A,
                platform_smoothing_half_window(platform),
                platform_smoothing_enabled(platform),
                string.Join(",", platform.SelectedFeatureNames),
                platform_draw_model_sum(platform),
                platform_draw_components(platform),
                platform.RowMap.Count,
                platform.PlotSeries.Count,
                string.Join(";", platform.PlotSeries.Select(plot => $"{plot.Key}:{plot.X.Length}:{plot.Y.Length}:{plot.X.FirstOrDefault():G6}:{plot.Y.FirstOrDefault():G6}:{plot.X.LastOrDefault():G6}:{plot.Y.LastOrDefault():G6}")),
                platform.FitCurves.Count,
                string.Join(";", platform.FitCurves.Select(curve => $"{curve.Key}:{curve.Kind}:{curve.SourceId}:{curve.Normalizer:G6}:{string.Join(",", curve.Parameters.Select(value => value.ToString("G6", CultureInfo.InvariantCulture)))}")),
                string.Join(",", platform.Populations.Where(row => row.IsPopulation && row.IsPlatformDropped).Select(row => row.IsSelected))),
            PlatformStatisticTableElement platform_table when platform_table.Platform is { } platform => string.Join("|",
                platform.ResultTables.Count,
                string.Join(";", platform.ResultTables.Select(table => $"{table.Key}:{string.Join(",", table.Columns)}:{string.Join("/", table.Rows.Select(row => string.Join(",", row)))}")),
                platform.PlatformStatistics.Count,
                string.Join(";", platform.PlatformStatistics.Select(statistic => $"{statistic.Name}:{statistic.Value}"))),
            _ => ""
        };

    private WriteableBitmap? create_density_bitmap(PagePlotElement element)
    {
        var grid = get_density_grid(element);
        if (grid is null)
            return null;

        int size = grid.Density.GetLength(0);
        var pixels = new byte[size * size * 4];
        if (element.PlotMode == PlotMode.Dotplot)
        {
            add_dotplot_pixels(pixels, grid.Density, grid.Colors, grid.TotalCount, element.DrawLargeDots, size);
            return create_bitmap(pixels, size);
        }

        var normalized = normalized_density_grid(grid.Density, grid.MaxDensity);
        if (element.PlotMode == PlotMode.Contour)
            return create_contour_bitmap(element, grid.Density, grid.TotalCount, normalized);

        if (element.PlotMode == PlotMode.Zebra)
            normalized = smooth_density(normalized, element.DensitySmoothing);
        if (element.PlotMode == PlotMode.Zebra && element.ShowOutlierPoints)
            add_dotplot_pixels(pixels, grid.Density, grid.Colors, grid.TotalCount, element.DrawLargeDots, size);

        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            double value = normalized[x, y];
            if (value <= 0)
                continue;

            set_pixel(pixels, x, size - 1 - y, plot_color(value, element), size);
        }

        return create_bitmap(pixels, size);
    }

    private WriteableBitmap create_contour_bitmap(PagePlotElement element, int[,] density, int total_count, double[,] normalized)
    {
        int size = density.GetLength(0);
        var pixels = new byte[size * size * 4];
        if (element.ShowOutlierPoints)
            add_dotplot_pixels(pixels, density, null, total_count, element.DrawLargeDots, size);

        var smoothed = smooth_density(normalized, element.DensitySmoothing);
        double[] levels = contour_levels(element.ContourLevelCount);
        add_filled_contour_pixels(pixels, smoothed, levels, size);
        return create_bitmap(pixels, size);
    }

    private WriteableBitmap? get_plot_bitmap(PagePlotElement element)
    {
        string key = plot_bitmap_key(element);
        if (plot_bitmap_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Bitmap;
        if (refreshing_element_ids.Contains(element.Id) &&
            plot_bitmap_cache.TryGetValue(element.Id, out cached))
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
            element.DensityPalette,
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
        if (refreshing_element_ids.Contains(element.Id) &&
            density_grid_cache.TryGetValue(element.Id, out cached))
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

        var pen = new Pen(Brushes.Black, 1.1);
        foreach (var segment in geometry.Segments)
            context.DrawLine(pen, density_to_screen(segment.Start, plot_rect, geometry.Size), density_to_screen(segment.End, plot_rect, geometry.Size));
    }

    private ContourGeometry? get_contour_geometry(PagePlotElement element)
    {
        string key = contour_geometry_key(element);
        if (contour_geometry_cache.TryGetValue(element.Id, out var cached) && cached.Key == key)
            return cached.Geometry;
        if (refreshing_element_ids.Contains(element.Id) &&
            contour_geometry_cache.TryGetValue(element.Id, out cached))
            return cached.Geometry;

        var grid = get_density_grid(element);
        if (grid is null)
        {
            contour_geometry_cache[element.Id] = (key, null);
            return null;
        }

        var normalized = smooth_density(normalized_density_grid(grid.Density, grid.MaxDensity), element.DensitySmoothing);
        var segments = create_contour_segments(normalized, contour_levels(element.ContourLevelCount));
        var geometry = new ContourGeometry(segments, normalized.GetLength(0));
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
        int size = density.GetLength(0);
        for (int x = 0; x < size - 1; x++)
        for (int y = 0; y < size - 1; y++)
        {
            var points = contour_cell_points(density[x, y], density[x + 1, y], density[x + 1, y + 1], density[x, y + 1], x, y, level);
            for (int index = 0; index + 1 < points.Count; index += 2)
                segments.Add(new ContourSegment(points[index], points[index + 1]));
        }
    }

    private static Point density_to_screen(Point point, Rect plot_rect, int size) =>
        new(
            plot_rect.Left + point.X / (size - 1) * plot_rect.Width,
            plot_rect.Bottom - point.Y / (size - 1) * plot_rect.Height);

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
            element.UsesPopulation,
            element.PopulationRegion);

    private WriteableBitmap? create_histogram_bitmap(PagePlotElement element)
    {
        var samples = resolve_samples(element).ToArray();
        if (samples.Length == 0)
            return null;

        int size = current_raster_size();
        int[] bins = new int[size];
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
                int bin = to_bin(x_values[index], element.XAxis, x_minimum, x_span, size);
                if (bin < 0)
                    continue;
                max_count = Math.Max(max_count, ++bins[bin]);
            }
        }

        if (max_count == 0)
            return null;

        var pixels = new byte[size * size * 4];
        for (int bin = 0; bin < size; bin++)
        {
            int height = Math.Clamp((int)Math.Round(bins[bin] / (double)max_count * size), 0, size);
            for (int y = size - height; y < size; y++)
                set_pixel(pixels, bin, y, Colors.Black, size);
        }
        return create_bitmap(pixels, size);
    }

    private DensityGrid? create_density_grid(PagePlotElement element)
    {
        var samples = resolve_samples(element).ToArray();
        if (samples.Length == 0)
            return null;

        int size = current_raster_size();
        var density = new int[size, size];
        var color_sums = should_color_dots(element) ? new double[size, size] : null;
        var color_counts = should_color_dots(element) ? new int[size, size] : null;
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
                int x_bin = to_bin(x_values[index], element.XAxis, x_minimum, x_span, size);
                int y_bin = to_bin(y_values[index], element.YAxis, y_minimum, y_span, size);
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
                    color_sums[x_bin, y_bin] += transform_dot_color_value(color_values[index], element.DotColor.UseLogScale, element.DotColor.ClampNegativeValuesToZero);
                    color_counts[x_bin, y_bin]++;
                }
            }
        }

        var colors = color_sums is null || color_counts is null
            ? new Color?[size, size]
            : create_dot_colors(element, color_sums, color_counts);
        return max_density == 0 ? null : new DensityGrid(density, colors, max_density, total_count);
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
            draw_vertical_centered_text(context, axis_title(element, element.YAxis), new Point(y_axis_label_x, plot_rect.Top + plot_rect.Height / 2), 11, Colors.Black);
        }
        draw_centered_text_in_band(
            context,
            axis_title(element, element.XAxis),
            new Rect(plot_rect.Left, bounds.Bottom - bottom_axis_label_space, plot_rect.Width, bottom_axis_label_space),
            11,
            Colors.Black);
    }

    private static string axis_title(PagePlotElement element, AxisSettings axis)
    {
        string name = axis.ChannelName ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var channel = element.Sample?.Channels.FirstOrDefault(item => item.Name == name) ??
                      element.Group?.Channels.FirstOrDefault(item => item.Name == name);
        string label = channel?.Label?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(label) || string.Equals(label, name, StringComparison.Ordinal)
            ? name
            : $"{label} ({name})";
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

        string name = gate.PopulationName(region);
        string statistics = $"{event_count:N0} ({parent_frequency:0.#}%)";
        string label = element.ShowGateAnnotationNames ? $"{name}\n{statistics}" : statistics;
        var text = create_formatted_text(label, 10, Colors.Black);
        var origin = gate_annotation_origin(element, gate, region, plot_rect, text.Width + 6, text.Height + 4);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), new Rect(origin.X - 3, origin.Y - 2, text.Width + 6, text.Height + 4));
        context.DrawText(text, origin);
    }

    private static Point gate_annotation_origin(PagePlotElement element, GateDefinition gate, PopulationRegion region, Rect plot_rect, double width, double height)
    {
        const double margin = 4;
        if (gate.Kind is GateKind.Polygon or GateKind.Rectangle)
        {
            var top_left = gate_screen_bounds_top_left(element, gate, plot_rect);
            return new Point(
                clamp_to_range(top_left.X, plot_rect.Left + margin, plot_rect.Right - width + 3),
                clamp_to_range(top_left.Y, plot_rect.Top + margin, plot_rect.Bottom - height + 2));
        }

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

    private static Point gate_screen_bounds_top_left(PagePlotElement element, GateDefinition gate, Rect plot_rect)
    {
        double left = double.PositiveInfinity;
        double top = double.PositiveInfinity;
        foreach (var vertex in gate.Vertices)
        {
            var point = data_to_screen(vertex, element, plot_rect);
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
        }

        return double.IsFinite(left) && double.IsFinite(top)
            ? new Point(left, top)
            : data_to_screen(gate.Vertices[0], element, plot_rect);
    }

    private static IReadOnlyList<PopulationRegion> annotation_regions(GateDefinition gate) =>
        gate.HasLinkedPopulations ? gate.PopulationRegions : [PopulationRegion.Primary];

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
            if (!element.UsesPopulation && element.Gate.HasLinkedPopulations)
                return resolve_parent_event_indices(sample, element.Gate);

            var population = find_population(sample.Populations, element.Gate, element.UsesPopulation ? element.PopulationRegion : null);
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

    private static IEnumerable<GateDefinition> resolve_plot_gates(PagePlotElement element)
    {
        if (element.Gate is null)
        {
            if (element.Group is null)
                yield break;

            foreach (var gate in element.Group.Gates)
                if (gate_matches_element_axes(gate, element))
                    yield return gate;
            yield break;
        }

        foreach (var child in element.Gate.Children)
        {
            if ((!element.UsesPopulation || child.ParentPopulationRegion == element.PopulationRegion) &&
                gate_matches_element_axes(child, element))
                yield return child;
        }
    }

    private static bool gate_matches_element_axes(GateDefinition gate, PagePlotElement element)
    {
        if (gate.XChannel != element.XAxis.ChannelName)
            return false;
        if (gate.IsOneDimensional || string.IsNullOrWhiteSpace(gate.YChannel))
            return element.IsHistogram;
        return !element.IsHistogram && gate.YChannel == element.YAxis.ChannelName;
    }

    private void drag_over(object? sender, DragEventArgs e)
    {
        e.DragEffects = ResolveDraggedProjectNode(e.DataTransfer) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void drop_node(object? sender, DragEventArgs e)
    {
        if (ResolveDraggedProjectNode(e.DataTransfer) is not { } node)
            return;
        var point = to_page_point(e.GetPosition(this));
        var request = new PageDropRequest(node, point);
        if (AddElementCommand?.CanExecute(request) == true)
            AddElementCommand.Execute(request);
        DraggedProjectNode = null;
        e.Handled = true;
    }

    public static string ProjectNodePayload(ProjectNode node) =>
        ProjectNodePayloadPrefix + node.Key;

    public static ProjectNode? ResolveDraggedProjectNode(IDataTransfer? data)
    {
        if (DraggedProjectNode is not null)
            return DraggedProjectNode;

        string? text = data?.TryGetText();
        if (string.IsNullOrWhiteSpace(text) ||
            !text.StartsWith(ProjectNodePayloadPrefix, StringComparison.Ordinal))
            return null;

        string key = text[ProjectNodePayloadPrefix.Length..];
        return string.IsNullOrWhiteSpace(key) ? null : ResolveProjectNodeByKey?.Invoke(key);
    }

    private void snap_position(PagePlotElement element)
    {
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        element.X = Math.Max(0, element.X);
        element.Y = Math.Max(0, element.Y);
        var guides = PageElements.Where(item => !ReferenceEquals(item, element)).ToArray();
        snap_axis(guides.SelectMany(item => new[] { item.X, item.X + item.Width / 2, item.X + item.Width }), element.X, value => { element.X += value - element.X; active_vertical_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.Y, item.Y + item.Height / 2, item.Y + item.Height }), element.Y, value => { element.Y += value - element.Y; active_horizontal_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.X, item.X + item.Width / 2, item.X + item.Width }), element.X + element.Width / 2, value => { element.X += value - (element.X + element.Width / 2); active_vertical_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.Y, item.Y + item.Height / 2, item.Y + item.Height }), element.Y + element.Height / 2, value => { element.Y += value - (element.Y + element.Height / 2); active_horizontal_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.X, item.X + item.Width / 2, item.X + item.Width }), element.X + element.Width, value => { element.X += value - (element.X + element.Width); active_vertical_snap_guide = value; });
        snap_axis(guides.SelectMany(item => new[] { item.Y, item.Y + item.Height / 2, item.Y + item.Height }), element.Y + element.Height, value => { element.Y += value - (element.Y + element.Height); active_horizontal_snap_guide = value; });
    }

    private double snap_size(PagePlotElement element, double requested)
    {
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        double size = Math.Max(Math.Max(element.MinimumWidth, element.MinimumHeight), requested);
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
            if (Math.Abs(element.X + size - (other.X + other.Width)) <= 6)
            {
                size = other.X + other.Width - element.X;
                active_vertical_snap_guide = other.X + other.Width;
            }
            if (Math.Abs(element.Y + size - (other.Y + other.Height)) <= 6)
            {
                size = other.Y + other.Height - element.Y;
                active_horizontal_snap_guide = other.Y + other.Height;
            }
        }
        return size;
    }

    private Size snap_rect_size(PagePlotElement element, Size requested)
    {
        active_vertical_snap_guide = null;
        active_horizontal_snap_guide = null;
        double width = Math.Max(element.MinimumWidth, requested.Width);
        double height = Math.Max(element.MinimumHeight, requested.Height);
        foreach (var other in PageElements.Where(item => !ReferenceEquals(item, element)))
        {
            if (Math.Abs(element.X + width - other.X) <= 6)
            {
                width = other.X - element.X;
                active_vertical_snap_guide = other.X;
            }
            if (Math.Abs(element.X + width - (other.X + other.Width)) <= 6)
            {
                width = other.X + other.Width - element.X;
                active_vertical_snap_guide = other.X + other.Width;
            }
            if (Math.Abs(element.Y + height - other.Y) <= 6)
            {
                height = other.Y - element.Y;
                active_horizontal_snap_guide = other.Y;
            }
            if (Math.Abs(element.Y + height - (other.Y + other.Height)) <= 6)
            {
                height = other.Y + other.Height - element.Y;
                active_horizontal_snap_guide = other.Y + other.Height;
            }
        }

        return new Size(Math.Max(element.MinimumWidth, width), Math.Max(element.MinimumHeight, height));
    }

    private Size requested_rect_size(PagePlotElement element, Point point)
    {
        double dx = point.X - drag_start_page.X;
        double dy = point.Y - drag_start_page.Y;
        double width = resize_corner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft
            ? drag_start_width - dx
            : drag_start_width + dx;
        double height = resize_corner is ResizeCorner.TopLeft or ResizeCorner.TopRight
            ? drag_start_height - dy
            : drag_start_height + dy;
        return new Size(Math.Max(element.MinimumWidth, width), Math.Max(element.MinimumHeight, height));
    }

    private double requested_square_size(Point point)
    {
        double dx = point.X - drag_start_page.X;
        double dy = point.Y - drag_start_page.Y;
        double width = resize_corner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft
            ? drag_start_size - dx
            : drag_start_size + dx;
        double height = resize_corner is ResizeCorner.TopLeft or ResizeCorner.TopRight
            ? drag_start_size - dy
            : drag_start_size + dy;
        return Math.Max(width, height);
    }

    private void apply_resized_rect(PagePlotElement element, Size size)
    {
        if (resize_corner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft)
            element.X = Math.Max(0, drag_start_x + drag_start_width - size.Width);
        if (resize_corner is ResizeCorner.TopLeft or ResizeCorner.TopRight)
            element.Y = Math.Max(0, drag_start_y + drag_start_height - size.Height);
    }

    private void apply_resized_square(PagePlotElement element)
    {
        if (resize_corner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft)
            element.X = Math.Max(0, drag_start_x + drag_start_size - element.Size);
        if (resize_corner is ResizeCorner.TopLeft or ResizeCorner.TopRight)
            element.Y = Math.Max(0, drag_start_y + drag_start_size - element.Size);
    }

    private static bool is_free_aspect_element(PagePlotElement element) =>
        element.ElementKind is PageElementKind.PlatformPlot or PageElementKind.StatisticTable or PageElementKind.PlatformStatisticTable;

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
        var viewport = viewport_size();
        return new Size(
            Math.Max(Math.Max(viewport.Width, minimum_workspace_width), content_extent.Width),
            Math.Max(Math.Max(viewport.Height, minimum_workspace_height), content_extent.Height));
    }

    private Size viewport_size()
    {
        var bounds = Bounds.Size;
        double width = bounds.Width > 0 ? bounds.Width : ViewportSize.Width;
        double height = bounds.Height > 0 ? bounds.Height : ViewportSize.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private static Size export_size_for(IReadOnlyList<PagePlotElement> elements)
    {
        if (elements.Count == 0)
            return new Size(workspace_margin, workspace_margin);

        double width = elements.Max(element => element.X + element.Width + workspace_margin);
        double height = elements.Max(element => element.Y + element.Height + workspace_margin);
        return new Size(Math.Ceiling(width), Math.Ceiling(height));
    }

    private static Rect plot_rect_for(Rect bounds, bool show_tick_labels) =>
        new(
            bounds.Left + left_axis_label_space + (show_tick_labels ? left_tick_label_space : 0),
            bounds.Top + title_space,
            Math.Max(40, bounds.Width - left_axis_label_space - (show_tick_labels ? left_tick_label_space : 0) - right_spine_space),
            Math.Max(40, bounds.Height - title_space - bottom_axis_label_space - (show_tick_labels ? bottom_tick_label_space : 0)));

    private static IReadOnlyList<Rect> resize_handle_rects(PagePlotElement element) =>
    [
        resize_handle_rect(element, ResizeCorner.TopLeft),
        resize_handle_rect(element, ResizeCorner.TopRight),
        resize_handle_rect(element, ResizeCorner.BottomLeft),
        resize_handle_rect(element, ResizeCorner.BottomRight)
    ];

    private static Rect resize_handle_rect(PagePlotElement element, ResizeCorner corner)
    {
        const double size = 11;
        double x = corner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft ? element.X : element.X + element.Width - size;
        double y = corner is ResizeCorner.TopLeft or ResizeCorner.TopRight ? element.Y : element.Y + element.Height - size;
        return new Rect(x, y, size, size);
    }

    private static ResizeCorner resize_corner_at(PagePlotElement element, Point point)
    {
        foreach (var corner in new[] { ResizeCorner.TopLeft, ResizeCorner.TopRight, ResizeCorner.BottomLeft, ResizeCorner.BottomRight })
            if (resize_handle_rect(element, corner).Contains(point))
                return corner;
        return ResizeCorner.None;
    }

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
        unsubscribe_collection_items(e.OldItems);
        subscribe_collection_items(e.NewItems);
        remove_collection_item_caches(e.OldItems);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            remove_stale_element_caches();
            unsubscribe_page_elements();
            subscribe_page_elements();
        }
        else
        {
            subscribed_page_elements = PageElements;
        }
        update_content_extent();
        InvalidateVisual();
    }

    private void remove_collection_item_caches(IList? items)
    {
        if (items is null)
            return;
        foreach (var element in items.OfType<PagePlotElement>())
            remove_element_caches(element.Id);
    }

    private void remove_stale_element_caches()
    {
        var active_ids = PageElements.Select(element => element.Id).ToHashSet();
        foreach (var id in density_grid_cache.Keys.Where(id => !active_ids.Contains(id)).ToArray())
            remove_element_caches(id);
        foreach (var id in element_bitmap_cache.Keys.Where(id => !active_ids.Contains(id)).ToArray())
            remove_element_caches(id);
    }

    private void remove_element_caches(Guid id)
    {
        refreshing_element_ids.Remove(id);
        density_grid_cache.Remove(id);
        contour_geometry_cache.Remove(id);
        plot_bitmap_cache.Remove(id);
        element_bitmap_cache.Remove(id);
    }

    private void clear_render_caches()
    {
        render_cache_refresh_cancellation?.Cancel();
        refreshing_element_ids.Clear();
        density_grid_cache.Clear();
        contour_geometry_cache.Clear();
        plot_bitmap_cache.Clear();
        element_bitmap_cache.Clear();
        InvalidateVisual();
    }

    private void subscribe_page_elements()
    {
        subscribed_page_elements = PageElements;
        subscribe_collection_items(subscribed_page_elements);
    }

    private void subscribe_collection_items(IEnumerable? items)
    {
        if (items is null)
            return;
        foreach (var element in items.OfType<PagePlotElement>())
        {
            element.PropertyChanged += page_element_property_changed;
            element.XAxis.PropertyChanged += page_element_property_changed;
            element.YAxis.PropertyChanged += page_element_property_changed;
            element.DotColor.PropertyChanged += page_element_property_changed;
        }
    }

    private void unsubscribe_page_elements()
    {
        unsubscribe_collection_items(subscribed_page_elements);
        subscribed_page_elements = [];
    }

    private void unsubscribe_collection_items(IEnumerable? items)
    {
        if (items is null)
            return;
        foreach (var element in items.OfType<PagePlotElement>())
        {
            element.PropertyChanged -= page_element_property_changed;
            element.XAxis.PropertyChanged -= page_element_property_changed;
            element.YAxis.PropertyChanged -= page_element_property_changed;
            element.DotColor.PropertyChanged -= page_element_property_changed;
        }
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

            if (captured_element is null && e.PropertyName is nameof(PagePlotElement.X) or nameof(PagePlotElement.Y) or nameof(PagePlotElement.Size) or nameof(PagePlotElement.Width) or nameof(PagePlotElement.Height))
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
        or nameof(PagePlotElement.ShowGateAnnotationNames)
        or nameof(PagePlotElement.ContourLevelCount)
        or nameof(PagePlotElement.DensitySmoothing)
        or nameof(PagePlotElement.DensityPalette);

    private Point to_page_point(Point viewport_point) =>
        new(viewport_point.X + scroll_offset.X, viewport_point.Y + scroll_offset.Y);

    private void set_scroll_offset(Vector requested)
    {
        var viewport = viewport_size();
        var extent = workspace_size();
        scroll_offset = new Vector(
            Math.Clamp(requested.X, 0, Math.Max(0, extent.Width - viewport.Width)),
            Math.Clamp(requested.Y, 0, Math.Max(0, extent.Height - viewport.Height)));
        InvalidateVisual();
    }

    private bool try_begin_scroll_drag(Point point, PointerPressedEventArgs e)
    {
        var bars = scrollbar_geometry();
        if (bars.VerticalThumb.Contains(point))
        {
            scroll_drag_kind = ScrollDragKind.Vertical;
        }
        else if (bars.HorizontalThumb.Contains(point))
        {
            scroll_drag_kind = ScrollDragKind.Horizontal;
        }
        else
        {
            return false;
        }

        drag_start_viewport = point;
        drag_start_scroll_offset = scroll_offset;
        e.Pointer.Capture(this);
        e.Handled = true;
        return true;
    }

    private void update_scroll_drag(Point point)
    {
        var bars = scrollbar_geometry();
        var viewport = viewport_size();
        var extent = workspace_size();
        if (scroll_drag_kind == ScrollDragKind.Vertical)
        {
            double track_span = Math.Max(1, bars.VerticalTrack.Height - bars.VerticalThumb.Height);
            double content_span = Math.Max(0, extent.Height - viewport.Height);
            double delta = (point.Y - drag_start_viewport.Y) / track_span * content_span;
            set_scroll_offset(new Vector(scroll_offset.X, drag_start_scroll_offset.Y + delta));
        }
        else if (scroll_drag_kind == ScrollDragKind.Horizontal)
        {
            double track_span = Math.Max(1, bars.HorizontalTrack.Width - bars.HorizontalThumb.Width);
            double content_span = Math.Max(0, extent.Width - viewport.Width);
            double delta = (point.X - drag_start_viewport.X) / track_span * content_span;
            set_scroll_offset(new Vector(drag_start_scroll_offset.X + delta, scroll_offset.Y));
        }
    }

    private ScrollbarGeometry scrollbar_geometry()
    {
        var viewport = viewport_size();
        var extent = workspace_size();
        bool show_horizontal = extent.Width > viewport.Width + 0.5;
        bool show_vertical = extent.Height > viewport.Height + 0.5;
        double right = viewport.Width - scrollbar_margin;
        double bottom = viewport.Height - scrollbar_margin;
        double horizontal_width = Math.Max(1, viewport.Width - scrollbar_margin * 2 - (show_vertical ? scrollbar_thickness + scrollbar_margin : 0));
        double vertical_height = Math.Max(1, viewport.Height - scrollbar_margin * 2 - (show_horizontal ? scrollbar_thickness + scrollbar_margin : 0));
        var horizontal_track = show_horizontal
            ? new Rect(scrollbar_margin, bottom - scrollbar_thickness, horizontal_width, scrollbar_thickness)
            : default;
        var vertical_track = show_vertical
            ? new Rect(right - scrollbar_thickness, scrollbar_margin, scrollbar_thickness, vertical_height)
            : default;

        var horizontal_thumb = default(Rect);
        if (show_horizontal && horizontal_track.Width > 100)
        {
            double thumb_width = Math.Clamp(horizontal_track.Width * viewport.Width / extent.Width, scrollbar_minimum_thumb, horizontal_track.Width);
            double travel = Math.Max(0, horizontal_track.Width - thumb_width);
            double left = horizontal_track.Left + (extent.Width <= viewport.Width ? 0 : scroll_offset.X / (extent.Width - viewport.Width) * travel);
            horizontal_thumb = new Rect(left, horizontal_track.Top, thumb_width, horizontal_track.Height);
        }

        var vertical_thumb = default(Rect);
        if (show_vertical && vertical_track.Height > 100)
        {
            double thumb_height = Math.Clamp(vertical_track.Height * viewport.Height / extent.Height, scrollbar_minimum_thumb, vertical_track.Height);
            double travel = Math.Max(0, vertical_track.Height - thumb_height);
            double top = vertical_track.Top + (extent.Height <= viewport.Height ? 0 : scroll_offset.Y / (extent.Height - viewport.Height) * travel);
            vertical_thumb = new Rect(vertical_track.Left, top, vertical_track.Width, thumb_height);
        }

        return new ScrollbarGeometry(horizontal_track, horizontal_thumb, vertical_track, vertical_thumb);
    }

    private void draw_manual_scrollbars(DrawingContext context, Rect bounds)
    {
        var bars = scrollbar_geometry();
        var track_brush = new SolidColorBrush(Color.FromArgb(72, 80, 80, 80));
        var thumb_brush = new SolidColorBrush(Color.FromArgb(150, 76, 84, 96));
        if (bars.HorizontalTrack.Width > 0 && bars.HorizontalTrack.Height > 0)
        {
            // context.DrawRectangle(track_brush, null, bars.HorizontalTrack, 4, 4);
            context.DrawRectangle(thumb_brush, null, bars.HorizontalThumb, 5, 5);
        }
        if (bars.VerticalTrack.Width > 0 && bars.VerticalTrack.Height > 0)
        {
            // context.DrawRectangle(track_brush, null, bars.VerticalTrack, 4, 4);
            context.DrawRectangle(thumb_brush, null, bars.VerticalThumb, 5, 5);
        }
    }

    private void draw_a4_grid(DrawingContext context, Size extent)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(130, 92, 128, 172)), 0.8, DashStyle.Dash);
        var text_brush = new SolidColorBrush(Color.FromArgb(130, 70, 91, 122));
        for (double x = a4_width; x < extent.Width; x += a4_width)
            context.DrawLine(pen, new Point(x, 0), new Point(x, extent.Height));
        for (double y = a4_height; y < extent.Height; y += a4_height)
            context.DrawLine(pen, new Point(0, y), new Point(extent.Width, y));
        var label = new FormattedText("A4", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, current_typeface(), 11, text_brush);
        for (double x = 0; x + a4_width <= extent.Width; x += a4_width)
        for (double y = 0; y + a4_height <= extent.Height; y += a4_height)
            context.DrawText(label, new Point(x + 10, y + 8));
    }

    private void update_content_extent()
    {
        var elements = PageElements;
        content_extent = elements.Length == 0
            ? default
            : new Size(
                Math.Ceiling(elements.Max(element => element.X + element.Width + workspace_margin)),
                Math.Ceiling(elements.Max(element => element.Y + element.Height + workspace_margin)));
        update_workspace_size();
    }

    private void update_workspace_size()
    {
        set_scroll_offset(scroll_offset);
        InvalidateVisual();
    }

    private int current_raster_size() =>
        apply_rasterization_resolution
            ? Math.Clamp((int)Math.Round(raster_size * render_scale), raster_size, raster_size * 8)
            : raster_size;

    private static int to_bin(double value, AxisSettings axis, double transformed_minimum, double transformed_span, int size)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return -1;
        double normalized = (axis.Scale.Transform(value) - transformed_minimum) / transformed_span;
        if (double.IsNaN(normalized) || double.IsInfinity(normalized))
            return -1;
        if (normalized < 0 || normalized > 1)
            return -1;
        return Math.Clamp((int)(normalized * (size - 1)), 0, size - 1);
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
        int size = density.GetLength(0);
        var normalized = new double[size, size];
        double denominator = Math.Log(1 + max_density);
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
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

        int size = color_sums.GetLength(0);
        double minimum = transform_dot_color_value(element.DotColor.RangeMinimum, element.DotColor.UseLogScale, element.DotColor.ClampNegativeValuesToZero);
        double maximum = transform_dot_color_value(element.DotColor.RangeMaximum, element.DotColor.UseLogScale, element.DotColor.ClampNegativeValuesToZero);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return new Color?[size, size];

        var colors = new Color?[size, size];
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            if (color_counts[x, y] <= 0)
                continue;
            double value = color_sums[x, y] / color_counts[x, y];
            double normalized = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
            colors[x, y] = PlotColorMaps.ColorAt(element.DotColor.Palette, normalized);
        }

        return colors;
    }

    private static double transform_dot_color_value(double value, bool use_log_scale, bool clamp_negative_values_to_zero = false)
    {
        if (clamp_negative_values_to_zero)
            value = Math.Max(0, value);
        return use_log_scale ? Math.Log10(1 + Math.Max(0, value)) : value;
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
        int size = source.GetLength(0);
        double[] kernel = [1, 4, 6, 4, 1];
        var horizontal = new double[size, size];
        var result = new double[size, size];
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            double sum = 0;
            double weight = 0;
            for (int offset = -2; offset <= 2; offset++)
            {
                int sample_x = x + offset;
                if (sample_x < 0 || sample_x >= size)
                    continue;
                sum += source[sample_x, y] * kernel[offset + 2];
                weight += kernel[offset + 2];
            }
            horizontal[x, y] = weight == 0 ? 0 : sum / weight;
        }
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            double sum = 0;
            double weight = 0;
            for (int offset = -2; offset <= 2; offset++)
            {
                int sample_y = y + offset;
                if (sample_y < 0 || sample_y >= size)
                    continue;
                sum += horizontal[x, sample_y] * kernel[offset + 2];
                weight += kernel[offset + 2];
            }
            result[x, y] = weight == 0 ? 0 : sum / weight;
        }
        return result;
    }

    private static void add_dotplot_pixels(byte[] pixels, int[,] density, Color?[,]? colors, int total_count, bool large_dots, int size)
    {
        double threshold = large_dots ? 0 : total_count * 0.00001;
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
            if (density[x, y] > threshold)
            {
                var color = colors?[x, y] ?? Colors.Black;
                if (large_dots)
                    set_large_dot(pixels, x, size - 1 - y, color, density[x, y], size);
                else
                    set_pixel(pixels, x, size - 1 - y, color, size);
            }
    }

    private static void add_filled_contour_pixels(byte[] pixels, double[,] normalized_density, double[] levels, int size)
    {
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            double value = normalized_density[x, y];
            int level_index = Array.FindLastIndex(levels, level => value >= level);
            if (level_index < 0)
                continue;

            double normalized_level = (level_index + 1.0) / levels.Length;
            byte shade = Convert.ToByte(250 - normalized_level * 210);
            set_pixel(pixels, x, size - 1 - y, Color.FromRgb(shade, shade, shade), size);
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

        return PlotColorMaps.ColorAt(element.DensityPalette, value);
    }

    private static byte to_byte(double value) =>
        Convert.ToByte(Math.Clamp((int)Math.Round(value), 0, 255));

    private static WriteableBitmap create_bitmap(byte[] pixels, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        int row_bytes = size * 4;
        if (frame.RowBytes == row_bytes)
        {
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            return bitmap;
        }
        for (int y = 0; y < size; y++)
            Marshal.Copy(pixels, y * row_bytes, IntPtr.Add(frame.Address, y * frame.RowBytes), row_bytes);
        return bitmap;
    }

    private static void set_pixel(byte[] pixels, int x, int y, Color color, int size)
    {
        if (x < 0 || x >= size || y < 0 || y >= size)
            return;

        int offset = (y * size + x) * 4;
        pixels[offset] = premultiply(color.B, color.A);
        pixels[offset + 1] = premultiply(color.G, color.A);
        pixels[offset + 2] = premultiply(color.R, color.A);
        pixels[offset + 3] = color.A;
    }

    private static void set_large_dot(byte[] pixels, int center_x, int center_y, Color color, int count, int size)
    {
        if (count <= 0)
            return;

        byte alpha = to_alpha(color.A * (1 - Math.Pow(1 - large_dot_opacity, count)));
        double scaled_radius = large_dot_radius * size / raster_size;
        int radius = (int)Math.Ceiling(scaled_radius);
        for (int y = center_y - radius; y <= center_y + radius; y++)
        for (int x = center_x - radius; x <= center_x + radius; x++)
        {
            double distance = Math.Sqrt((x - center_x) * (x - center_x) + (y - center_y) * (y - center_y));
            if (distance > scaled_radius)
                continue;

            blend_pixel(pixels, x, y, Color.FromArgb(alpha, color.R, color.G, color.B), size);
        }
    }

    private static void blend_pixel(byte[] pixels, int x, int y, Color color, int size)
    {
        if (x < 0 || x >= size || y < 0 || y >= size)
            return;

        int offset = (y * size + x) * 4;
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

    private static IEnumerable<double> major_axis_ticks(AxisSettings axis)
    {
        return Configuration.MajorAxisTicks(axis).Where(value => value >= axis.Minimum && value <= axis.Maximum);
    }

    private static IEnumerable<double> minor_axis_ticks(AxisSettings axis)
    {
        if (axis.Maximum <= axis.Minimum)
            yield break;

        foreach (double value in Configuration.MinorAxisTicks(axis))
            if (value >= axis.Minimum && value <= axis.Maximum)
                yield return value;
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
        Configuration.FormatAxisValue(value);

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
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bolded ? current_typeface_bolded() : current_typeface(), size, new SolidColorBrush(color));

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
        svg.AppendLine($"""<text x="{(plot.Left + plot.Width / 2).ToString(CultureInfo.InvariantCulture)}" y="{x_axis_label_y.ToString(CultureInfo.InvariantCulture)}" text-anchor="middle" font-family="Arial" font-size="11" fill="black">{escape_xml(axis_title(element, element.XAxis))}</text>""");
        if (!element.IsHistogram)
            svg.AppendLine($"""<text x="{y_axis_label_x.ToString(CultureInfo.InvariantCulture)}" y="{(plot.Top + plot.Height / 2).ToString(CultureInfo.InvariantCulture)}" transform="rotate(-90 {y_axis_label_x.ToString(CultureInfo.InvariantCulture)} {(plot.Top + plot.Height / 2).ToString(CultureInfo.InvariantCulture)})" text-anchor="middle" font-family="Arial" font-size="11" fill="black">{escape_xml(axis_title(element, element.YAxis))}</text>""");
    }

    private void append_svg_contours(StringBuilder svg, PagePlotElement element)
    {
        var geometry = get_contour_geometry(element);
        if (geometry is null)
            return;

        Rect plot = plot_rect_for(new Rect(0, 0, element.Size, element.Size), element.ShowTickLabels);
        foreach (var segment in geometry.Segments)
        {
            var start = density_to_screen(segment.Start, plot, geometry.Size);
            var end = density_to_screen(segment.End, plot, geometry.Size);
            svg.AppendLine($"""<line x1="{start.X.ToString(CultureInfo.InvariantCulture)}" y1="{start.Y.ToString(CultureInfo.InvariantCulture)}" x2="{end.X.ToString(CultureInfo.InvariantCulture)}" y2="{end.Y.ToString(CultureInfo.InvariantCulture)}" stroke="black" stroke-width="1.1" fill="none"/>""");
        }
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

        string name = gate.PopulationName(region);
        string count_line = $"{event_count:N0} ({parent_frequency:0.#}%)";
        double text_width = (element.ShowGateAnnotationNames ? Math.Max(name.Length, count_line.Length) : count_line.Length) * 5.8;
        double text_height = element.ShowGateAnnotationNames ? 26 : 14;
        var origin = gate_annotation_origin(element, gate, region, plot, text_width + 6, text_height);

        bool top_left_origin = gate.Kind is GateKind.Polygon or GateKind.Rectangle;
        double rect_y = top_left_origin ? origin.Y : origin.Y - 10;
        double text_y = top_left_origin ? origin.Y + 10 : origin.Y;
        svg.AppendLine($"""<rect x="{(origin.X - 3).ToString(CultureInfo.InvariantCulture)}" y="{rect_y.ToString(CultureInfo.InvariantCulture)}" width="{(text_width + 6).ToString(CultureInfo.InvariantCulture)}" height="{text_height.ToString(CultureInfo.InvariantCulture)}" fill="white" fill-opacity="0.86"/>""");
        if (element.ShowGateAnnotationNames)
        {
            svg.AppendLine($"""<text x="{origin.X.ToString(CultureInfo.InvariantCulture)}" y="{text_y.ToString(CultureInfo.InvariantCulture)}" font-family="Arial" font-size="10" fill="black">{escape_xml(name)}</text>""");
            svg.AppendLine($"""<text x="{origin.X.ToString(CultureInfo.InvariantCulture)}" y="{(text_y + 12).ToString(CultureInfo.InvariantCulture)}" font-family="Arial" font-size="10" fill="black">{escape_xml(count_line)}</text>""");
        }
        else
        {
            svg.AppendLine($"""<text x="{origin.X.ToString(CultureInfo.InvariantCulture)}" y="{text_y.ToString(CultureInfo.InvariantCulture)}" font-family="Arial" font-size="10" fill="black">{escape_xml(count_line)}</text>""");
        }
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

    private void append_svg_element_bitmap(StringBuilder svg, PagePlotElement element)
    {
        var bitmap = get_element_bitmap(element);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        string encoded = Convert.ToBase64String(stream.ToArray());
        svg.AppendLine($"""<image x="0" y="0" width="{element.Width.ToString(CultureInfo.InvariantCulture)}" height="{element.Height.ToString(CultureInfo.InvariantCulture)}" href="data:image/png;base64,{encoded}" preserveAspectRatio="none"/>""");
    }

    private sealed class ExportPageVisual : Control
    {
        private readonly IReadOnlyList<PagePlotElement> elements;
        private readonly bool transparent_background;
        private readonly Size export_size;
        private readonly PageEditorView source;
        private readonly double scale;
        private readonly bool apply_rasterization_resolution;

        public ExportPageVisual(
            IReadOnlyList<PagePlotElement> elements, 
            bool transparent_background, 
            Size export_size, 
            PageEditorView source, 
            double scale,
            bool apply_rasterization_resolution)
        {
            this.elements = elements;
            this.transparent_background = transparent_background;
            this.export_size = export_size;
            this.source = source;
            this.scale = scale;
            this.apply_rasterization_resolution = apply_rasterization_resolution;
            copy_text_properties(source, this);
        }

        public override void Render(DrawingContext context)
        {
            if (!transparent_background)
                context.FillRectangle(Brushes.White, new Rect(export_size));
            var renderer = new PageEditorView
            {
                Elements = elements,
                render_scale = scale,
                use_visual_root_render_scale = false,
                apply_rasterization_resolution = apply_rasterization_resolution
            };
            copy_text_properties(source, renderer);
            renderer.Measure(export_size);
            renderer.Arrange(new Rect(export_size));
            foreach (var element in elements)
                renderer.draw_page_element(context, element);
        }
    }

    private sealed class ElementContentVisual : Control
    {
        private readonly PageEditorView owner;
        private readonly PagePlotElement element;
        private readonly Size size;
        private readonly double scale;

        public ElementContentVisual(PageEditorView owner, PagePlotElement element, Size size, double scale)
        {
            this.owner = owner;
            this.element = element;
            this.size = size;
            this.scale = scale;
            copy_text_properties(owner, this);
        }

        public override void Render(DrawingContext context)
        {
            owner.draw_page_element_content(context, element, new Rect(size));
        }
    }

    private static void copy_text_properties(Control source, Control target)
    {
        target.SetValue(TextElement.FontFamilyProperty, TextElement.GetFontFamily(source));
        target.SetValue(TextElement.FontStyleProperty, TextElement.GetFontStyle(source));
        target.SetValue(TextElement.FontWeightProperty, TextElement.GetFontWeight(source));
        target.SetValue(TextElement.FontStretchProperty, TextElement.GetFontStretch(source));
    }

    private sealed record DensityGrid(int[,] Density, Color?[,] Colors, int MaxDensity, int TotalCount);

    private sealed record ContourGeometry(IReadOnlyList<ContourSegment> Segments, int Size);

    private readonly record struct ContourSegment(Point Start, Point End);

    private sealed record LayoutCacheWorkItem(PagePlotElement Element, string DensityKey, string ContourKey, bool NeedsContour);

    private sealed record LayoutCacheResult(Guid ElementId, string DensityKey, DensityGrid? Grid, string ContourKey, ContourGeometry? Contour, bool NeedsContour);

    private readonly record struct ScrollbarGeometry(Rect HorizontalTrack, Rect HorizontalThumb, Rect VerticalTrack, Rect VerticalThumb);

    private enum ScrollDragKind
    {
        None,
        Horizontal,
        Vertical
    }

    private enum ResizeCorner
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}
