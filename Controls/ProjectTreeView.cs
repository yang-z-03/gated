using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using Avalonia.Rendering;
using gated.ViewModels;
using Avalonia.Svg.Skia;
using Avalonia.Platform;
using gated.Models;

namespace gated.Controls;

public sealed class ProjectTreeView : Control, ICustomHitTest
{
    public static readonly StyledProperty<INotifyCollectionChanged?> NodesProperty =
        AvaloniaProperty.Register<ProjectTreeView, INotifyCollectionChanged?>(nameof(Nodes));

    public static readonly StyledProperty<ICommand?> SelectNodeCommandProperty =
        AvaloniaProperty.Register<ProjectTreeView, ICommand?>(nameof(SelectNodeCommand));

    public static readonly StyledProperty<ICommand?> ToggleNodeCommandProperty =
        AvaloniaProperty.Register<ProjectTreeView, ICommand?>(nameof(ToggleNodeCommand));

    private const double top_padding = 2;
    private const double row_height = 25;
    private const double indent_width = 25;
    private const double chevron_width = 21;
    private const double icon_width = 16;
    private const double count_width = 100;
    private const double font_size = 13;
    private INotifyCollectionChanged? subscribed_nodes;
    private ProjectNode[] subscribed_project_nodes = [];
    private ProjectNode? pressed_node;
    private Point pressed_point;
    private bool drag_started;
    private bool pressed_chevron;

    public event EventHandler<ProjectNodeContextRequestedEventArgs>? NodeContextRequested;

    static ProjectTreeView()
    {
        AffectsRender<ProjectTreeView>(
            NodesProperty,
            SelectNodeCommandProperty,
            ToggleNodeCommandProperty);
    }

    public INotifyCollectionChanged? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public ICommand? SelectNodeCommand
    {
        get => GetValue(SelectNodeCommandProperty);
        set => SetValue(SelectNodeCommandProperty, value);
    }

    public ICommand? ToggleNodeCommand
    {
        get => GetValue(ToggleNodeCommandProperty);
        set => SetValue(ToggleNodeCommandProperty, value);
    }

    public ProjectNode? GetNodeAt(Point point) => node_at(point.Y);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NodesProperty)
            resubscribe_nodes();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var node = node_at(point.Y);
        if (node is null)
            return;

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (is_context_menu_node(node))
            {
                select_node(node);
                NodeContextRequested?.Invoke(this, new ProjectNodeContextRequestedEventArgs(node, point));
                e.Handled = true;
            }
            return;
        }

        pressed_node = node;
        pressed_point = point;
        drag_started = false;

        double chevron_left = 8 + node.Depth * indent_width;
        pressed_chevron = node.HasChildren && point.X >= chevron_left && point.X <= chevron_left + chevron_width + 4;
        if (pressed_chevron)
        {
            if (ToggleNodeCommand?.CanExecute(node) == true)
                ToggleNodeCommand.Execute(node);
        }
        else if (!is_draggable_node(node))
        {
            select_node(node);
        }

        e.Handled = true;
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (pressed_node is null || drag_started || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - pressed_point.X) < 6 && Math.Abs(point.Y - pressed_point.Y) < 6)
            return;

        if (!is_draggable_node(pressed_node))
            return;

        drag_started = true;
        PageEditorView.DraggedProjectNode = pressed_node;
        await DragDrop.DoDragDropAsync(e, new DataTransfer(), DragDropEffects.Copy);
        PageEditorView.DraggedProjectNode = null;
        pressed_node = null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressed_node is not null && !drag_started && !pressed_chevron)
            select_node(pressed_node);
        pressed_node = null;
        drag_started = false;
        pressed_chevron = false;
    }

    private static bool is_draggable_node(ProjectNode node) =>
        node.Kind is ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.Population
            or ProjectNodeKind.Sample
            or ProjectNodeKind.Group
            or ProjectNodeKind.GateFolder
            or ProjectNodeKind.StatisticDefinition
            or ProjectNodeKind.Platform;

    private static bool is_context_menu_node(ProjectNode node) =>
        node.Kind is ProjectNodeKind.Workspace
            or ProjectNodeKind.Metadata
            or ProjectNodeKind.LayoutFolder
            or ProjectNodeKind.Layout
            or ProjectNodeKind.IntegrationJobFolder
            or ProjectNodeKind.Platform
            or ProjectNodeKind.Group
            or ProjectNodeKind.Sample
            or ProjectNodeKind.Population
            or ProjectNodeKind.GateFolder
            or ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.StatisticDefinition
            or ProjectNodeKind.StatisticValue
            or ProjectNodeKind.CompensationFolder
            or ProjectNodeKind.Compensation
            or ProjectNodeKind.Embedding;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        double height = top_padding + Math.Max(1, ProjectNodes.Length) * row_height;
        return new Size(width, height);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (subscribed_nodes is not null)
            subscribed_nodes.CollectionChanged -= nodes_collection_changed;
        subscribed_nodes = null;
        unsubscribe_project_nodes();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(Brushes.Transparent, bounds);

        var nodes = ProjectNodes;
        if (nodes.Length == 0)
        {
            draw_text(context, "No workspace nodes", new Point(10, 6), font_size, Color.FromRgb(130, 136, 148));
            return;
        }

        for (int index = 0; index < nodes.Length; index++)
            draw_node(context, nodes[index], index, bounds.Width);
    }

    private ProjectNode[] ProjectNodes =>
        Nodes is System.Collections.IEnumerable enumerable
            ? enumerable.OfType<ProjectNode>().ToArray()
            : Array.Empty<ProjectNode>();

    private ProjectNode? node_at(double y)
    {
        int index = (int)Math.Floor((y - top_padding) / row_height);
        var nodes = ProjectNodes;
        if (index < 0 || index >= nodes.Length)
            return null;

        return nodes[index];
    }

    private void select_node(ProjectNode node)
    {
        if (SelectNodeCommand?.CanExecute(node) == true)
            SelectNodeCommand.Execute(node);
    }

    private void draw_node(DrawingContext context, ProjectNode node, int index, double width)
    {
        double top = top_padding + index * row_height;
        var row_rect = new Rect(4, top + 1, Math.Max(0, width - 8), row_height - 2);
        Color row_background = Color.FromRgb(30, 30, 30);
        if (node.IsSelected)
        {
            row_background = Color.FromRgb(52, 58, 70);
            context.DrawRectangle(
                new Pen(new SolidColorBrush(Color.FromRgb(29, 117, 219)), 1.5),
                new Rect(row_rect.Left - 1, row_rect.Top - 1, row_rect.Width + 2, row_rect.Height + 2), 6);

            context.FillRectangle(
                new SolidColorBrush(Color.FromRgb(52, 58, 70)),
                // new SolidColorBrush(Color.FromRgb(29, 117, 219)),
                new Rect(row_rect.Left + 1, row_rect.Top + 1, row_rect.Width - 2, row_rect.Height - 2), 4);
        }

        double x = 6 + node.Depth * indent_width;
        if (node.HasChildren)
            draw_chevron(context, new Rect(x, top + 4, 16, 16), node.IsExpanded);

        x += chevron_width;
        draw_icon(context, new Rect(x, top + 4, 16, 16), node);
        x += icon_width + 3;

        double count_left = Math.Max(x + 72, width - count_width + 8);
        double text_left = x + 4;
        var text_rect = new Rect(text_left, top + 3, Math.Max(0, count_left - text_left - 10), row_height - 5);
        draw_fading_text(context, node.Name, text_rect, font_size, Color.FromRgb(218, 221, 228), row_background);
        if (!string.IsNullOrWhiteSpace(node.CountText))
            draw_text(context, node.CountText, new Point(count_left, top + 4), font_size, Color.FromRgb(164, 168, 178));
    }

    private static void draw_chevron(DrawingContext context, Rect rect, bool expanded)
    {
        SvgImage chev_down = new SvgImage();
        chev_down.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/chevron-down.svg")));

        SvgImage chev_right = new SvgImage();
        chev_right.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/chevron-right.svg")));

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(180, 184, 191)), 1.5);
        if (expanded)
        {
            context.DrawImage(chev_down, rect);
            return;
        }

        context.DrawImage(chev_right, rect);
    }

    private static void draw_icon(DrawingContext context, Rect rect, ProjectNode node)
    {
        SvgImage icon = new SvgImage();
        switch (node.Kind)
        {
            case ProjectNodeKind.Workspace:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/workspace.svg")));
                break;

            case ProjectNodeKind.Metadata:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/table-edit.svg")));
                break;

            case ProjectNodeKind.Group:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/group.svg")));
                break;

            case ProjectNodeKind.LayoutFolder:
            case ProjectNodeKind.Layout:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/table-edit.svg")));
                break;

            case ProjectNodeKind.IntegrationJobFolder:
            case ProjectNodeKind.Platform:
            case ProjectNodeKind.Embedding:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/embedding.svg")));
                break;

            case ProjectNodeKind.GateFolder:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/gates.svg")));
                break;

            case ProjectNodeKind.Gate:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/gate.svg")));
                break;

            case ProjectNodeKind.StatisticDefinition:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/stats.svg")));
                break;

            case ProjectNodeKind.CompensationFolder:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/matrix.svg")));
                break;

            case ProjectNodeKind.Compensation:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri(node.IsAppliedCompensation
                    ? "avares://gated/Resources/ok.svg"
                    : "avares://gated/Resources/matrix.svg")));
                break;

            case ProjectNodeKind.Sample:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/tube.svg")));
                break;

            case ProjectNodeKind.Population:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/subset.svg")));
                break;

            case ProjectNodeKind.StatisticValue:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/statistics.svg")));
                break;
            
            default:
                icon.Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/unk.svg")));
                break;
        };

        context.DrawImage(icon, rect);
    }

    private void draw_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_formatted_text(text, size, color);
        context.DrawText(formatted, origin);
    }

    private void draw_fading_text(DrawingContext context, string text, Rect bounds, double size, Color color, Color row_background)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using (context.PushClip(bounds))
            context.DrawText(create_formatted_text(text, size, color), bounds.Position);

        var end_rect = new Rect(Math.Max(bounds.Left, bounds.Right - 28), bounds.Top, Math.Min(28, bounds.Width), bounds.Height);
        var fade_color = row_background.A == 0 ? Color.FromRgb(25, 25, 25) : row_background;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, fade_color.R, fade_color.G, fade_color.B), 0),
                new GradientStop(fade_color, 1)
            }
        };
        context.FillRectangle(brush, end_rect);
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

    private void resubscribe_nodes()
    {
        if (subscribed_nodes is not null)
            subscribed_nodes.CollectionChanged -= nodes_collection_changed;
        unsubscribe_project_nodes();

        subscribed_nodes = Nodes;
        if (subscribed_nodes is not null)
            subscribed_nodes.CollectionChanged += nodes_collection_changed;
        subscribe_project_nodes();

        InvalidateVisual();
        InvalidateMeasure();
    }

    private void nodes_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        unsubscribe_project_nodes();
        subscribe_project_nodes();
        InvalidateVisual();
        InvalidateMeasure();
    }

    private void subscribe_project_nodes()
    {
        subscribed_project_nodes = ProjectNodes;
        foreach (var node in subscribed_project_nodes)
            node.PropertyChanged += project_node_property_changed;
    }

    private void unsubscribe_project_nodes()
    {
        foreach (var node in subscribed_project_nodes)
            node.PropertyChanged -= project_node_property_changed;
        subscribed_project_nodes = [];
    }

    private void project_node_property_changed(object? sender, PropertyChangedEventArgs e) =>
        invalidate_tree_layout();

    private void invalidate_tree_layout()
    {
        InvalidateVisual();
        InvalidateMeasure();
    }
}

public sealed class ProjectNodeContextRequestedEventArgs : EventArgs
{
    public ProjectNodeContextRequestedEventArgs(ProjectNode node, Point point)
    {
        Node = node;
        Point = point;
    }

    public ProjectNode Node { get; }
    public Point Point { get; }
}
