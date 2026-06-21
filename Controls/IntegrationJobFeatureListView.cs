using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using gated.Models;

namespace gated.Controls;

public sealed class IntegrationJobFeatureListView : Control
{
    public static readonly StyledProperty<INotifyCollectionChanged?> ItemsProperty =
        AvaloniaProperty.Register<IntegrationJobFeatureListView, INotifyCollectionChanged?>(nameof(Items));
    public static readonly StyledProperty<ICommand?> SelectionChangedCommandProperty =
        AvaloniaProperty.Register<IntegrationJobFeatureListView, ICommand?>(nameof(SelectionChangedCommand));
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<IntegrationJobFeatureListView, bool>(nameof(IsReadOnly));

    private const double header_height = 30;
    private const double row_height = 25;
    private const double indent_width = 25;
    private const double chevron_width = 21;
    private const double icon_width = 16;
    private INotifyCollectionChanged? subscribed_items;
    private IntegrationJobFeatureSelection[] subscribed_rows = [];

    static IntegrationJobFeatureListView()
    {
        AffectsRender<IntegrationJobFeatureListView>(ItemsProperty, SelectionChangedCommandProperty, IsReadOnlyProperty);
    }

    public INotifyCollectionChanged? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? SelectionChangedCommand
    {
        get => GetValue(SelectionChangedCommandProperty);
        set => SetValue(SelectionChangedCommandProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
            resubscribe();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 460 : availableSize.Width;
        return new Size(width, header_height + System.Math.Max(1, visible_rows().Length) * row_height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        int index = (int)System.Math.Floor((point.Y - header_height) / row_height);
        var current = visible_rows();
        if (index < 0 || index >= current.Length)
            return;

        var row = current[index];
        if (IsReadOnly)
            return;
        double x = 8 + row.Depth * indent_width;
        if (row.HasChildren && point.X >= x && point.X <= x + chevron_width)
            row.IsExpanded = !row.IsExpanded;
        else if (row.IsEnabled)
        {
            bool next = !row.IsSelected;
            row.IsSelected = next;
            if (!next)
                clear_descendants(row.RowKey);
            apply_hierarchy_states();
            if (SelectionChangedCommand?.CanExecute(null) == true)
                SelectionChangedCommand.Execute(null);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, Bounds);
        draw_header(context);
        var current = visible_rows();
        for (int index = 0; index < current.Length; index++)
            draw_row(context, current[index], index);
    }

    private void draw_row(DrawingContext context, IntegrationJobFeatureSelection row, int index)
    {
        double top = header_height + index * row_height;
        var row_rect = new Rect(4, top + 1, System.Math.Max(0, Bounds.Width - 8), row_height - 2);
        var background = row.IsEnabled ? Color.FromRgb(30, 30, 30) : Color.FromRgb(24, 24, 24);
        context.FillRectangle(new SolidColorBrush(background), row_rect, 4);

        double x = 6 + row.Depth * indent_width;
        if (row.HasChildren)
            draw_chevron(context, new Rect(x, top + 4, 16, 16), row.IsExpanded);
        x += chevron_width;
        draw_icon(context, new Rect(x, top + 4, 16, 16), row.IsChannel ? "avares://gated/Resources/channel.svg" : "avares://gated/Resources/grouping.svg");
        x += icon_width + 7;
        draw_checkbox(context, new Rect(x, top + 5, 14, 14), row.IsSelected, row.IsIndeterminate, row.IsEnabled);
        x += 23;

        if (!row.IsChannel)
        {
            draw_text(context, row.DisplayName, new Point(x, top + 4), 13, row.IsEnabled ? Color.FromRgb(218, 221, 228) : Color.FromRgb(115, 120, 130), false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(row.Label))
        {
            var label = create_text(row.Label, 13, row.IsEnabled ? Color.FromRgb(238, 240, 245) : Color.FromRgb(135, 140, 150), true);
            context.DrawText(label, new Point(x, top + 4));
            x += label.Width + 8;
        }
        draw_text(context, row.ChannelName, new Point(x, top + 4), 13, row.IsEnabled ? Color.FromRgb(164, 168, 178) : Color.FromRgb(105, 110, 120), false);
    }

    private void draw_header(DrawingContext context) =>
        draw_text(context, "Feature channels", new Point(10, 6), 13, Color.FromRgb(164, 168, 178), false);

    private static void draw_icon(DrawingContext context, Rect rect, string uri)
    {
        var icon = new SvgImage { Source = SvgSource.LoadFromStream(AssetLoader.Open(new System.Uri(uri))) };
        context.DrawImage(icon, rect);
    }

    private static void draw_chevron(DrawingContext context, Rect rect, bool expanded)
    {
        var uri = expanded ? "avares://gated/Resources/chevron-down.svg" : "avares://gated/Resources/chevron-right.svg";
        draw_icon(context, rect, uri);
    }

    private static void draw_checkbox(DrawingContext context, Rect rect, bool is_checked, bool is_indeterminate, bool is_enabled)
    {
        IBrush fill = is_checked || is_indeterminate
            ? new SolidColorBrush(is_enabled ? Color.FromRgb(76, 132, 255) : Color.FromRgb(72, 82, 104))
            : Brushes.Transparent;
        context.DrawRectangle(fill, new Pen(new SolidColorBrush(is_enabled ? Color.FromRgb(120, 126, 138) : Color.FromRgb(74, 78, 88)), 1), rect, 4);
        if (is_indeterminate)
        {
            context.DrawLine(new Pen(Brushes.White, 1.7), new Point(rect.Left + 3, rect.Top + 7), new Point(rect.Right - 3, rect.Top + 7));
            return;
        }
        if (!is_checked)
            return;
        var check = new StreamGeometry();
        using (var geometry = check.Open())
        {
            geometry.BeginFigure(new Point(rect.Left + 3, rect.Top + 7), false);
            geometry.LineTo(new Point(rect.Left + 6, rect.Top + 10));
            geometry.LineTo(new Point(rect.Left + 11, rect.Top + 4));
        }
        context.DrawGeometry(null, new Pen(Brushes.White, 1.7), check);
    }

    private void draw_text(DrawingContext context, string text, Point point, double size, Color color, bool bold) =>
        context.DrawText(create_text(text, size, color, bold), point);

    private FormattedText create_text(string text, double size, Color color, bool bold) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                TextElement.GetFontFamily(this),
                FontStyle.Normal,
                bold ? FontWeight.Bold : FontWeight.Normal,
                FontStretch.Normal),
            size,
            new SolidColorBrush(color));

    private IntegrationJobFeatureSelection[] rows() =>
        Items is System.Collections.IEnumerable enumerable
            ? enumerable.OfType<IntegrationJobFeatureSelection>().ToArray()
            : [];

    private IntegrationJobFeatureSelection[] visible_rows()
    {
        var all = rows();
        return all.Where((row, index) =>
        {
            var current = row;
            for (int prior = index - 1; prior >= 0; prior--)
            {
                if (all[prior].Depth >= current.Depth)
                    continue;
                if (!all[prior].IsExpanded)
                    return false;
                current = all[prior];
            }
            return true;
        }).ToArray();
    }

    private void apply_hierarchy_states()
    {
        var all = rows();
        var children = all.GroupBy(row => row.ParentKey)
            .Where(group => group.Key.HasValue)
            .ToDictionary(group => group.Key!.Value, group => group.ToArray());

        foreach (var row in all)
        {
            row.IsEnabled = true;
            row.IsIndeterminate = false;
        }

        foreach (var root in all.Where(row => row.ParentKey is null))
            apply_descendant_states(root, inherited_selected: false);

        bool apply_descendant_states(IntegrationJobFeatureSelection row, bool inherited_selected)
        {
            bool selected = row.IsSelected;
            if (inherited_selected)
            {
                row.IsEnabled = false;
                row.IsSelected = true;
                selected = true;
            }

            bool descendant_selected = false;
            if (children.TryGetValue(row.RowKey, out var child_rows))
            {
                foreach (var child in child_rows)
                    descendant_selected |= apply_descendant_states(child, inherited_selected || selected);
                if (!selected && descendant_selected)
                    row.IsIndeterminate = true;
            }

            return selected || descendant_selected;
        }
    }

    private void clear_descendants(System.Guid row_key)
    {
        foreach (var child in rows().Where(row => row.ParentKey == row_key))
        {
            child.IsSelected = false;
            child.IsIndeterminate = false;
            clear_descendants(child.RowKey);
        }
    }

    private void resubscribe()
    {
        if (subscribed_items is not null)
            subscribed_items.CollectionChanged -= items_changed;
        unsubscribe_rows();
        subscribed_items = Items;
        if (subscribed_items is not null)
            subscribed_items.CollectionChanged += items_changed;
        subscribe_rows();
        InvalidateVisual();
        InvalidateMeasure();
    }

    private void items_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        unsubscribe_rows();
        subscribe_rows();
        InvalidateVisual();
        InvalidateMeasure();
    }

    private void subscribe_rows()
    {
        subscribed_rows = rows();
        foreach (var row in subscribed_rows)
            row.PropertyChanged += row_changed;
    }

    private void unsubscribe_rows()
    {
        foreach (var row in subscribed_rows)
            row.PropertyChanged -= row_changed;
        subscribed_rows = [];
    }

    private void row_changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
}
