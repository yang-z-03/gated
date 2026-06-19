using System;
using System.Collections;
using System.Collections.Generic;
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
using Avalonia.Threading;
using gated.Models;
using gated.ViewModels;

namespace gated.Controls;

public sealed class PlatformPopulationFitTableView : Control
{
    public static readonly StyledProperty<Platform?> PlatformProperty =
        AvaloniaProperty.Register<PlatformPopulationFitTableView, Platform?>(nameof(Platform));
    public static readonly StyledProperty<ICommand?> SelectionChangedCommandProperty =
        AvaloniaProperty.Register<PlatformPopulationFitTableView, ICommand?>(nameof(SelectionChangedCommand));
    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<PlatformPopulationFitTableView, ICommand?>(nameof(DropCommand));
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<PlatformPopulationFitTableView, bool>(nameof(IsReadOnly));

    private const double header_height = 30;
    private const double row_height = 28;
    private const double checkbox_size = 14;
    private Platform? subscribed_platform;
    private INotifyCollectionChanged? subscribed_populations;
    private INotifyCollectionChanged? subscribed_tables;
    private IntegrationJobPopulationSelection[] subscribed_rows = [];

    static PlatformPopulationFitTableView()
    {
        AffectsRender<PlatformPopulationFitTableView>(PlatformProperty, IsReadOnlyProperty);
    }

    public PlatformPopulationFitTableView()
    {
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, drag_over);
        DragDrop.AddDropHandler(this, drop_node);
    }

    public Platform? Platform
    {
        get => GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    public ICommand? SelectionChangedCommand
    {
        get => GetValue(SelectionChangedCommandProperty);
        set => SetValue(SelectionChangedCommandProperty, value);
    }

    public ICommand? DropCommand
    {
        get => GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlatformProperty)
            resubscribe();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Max(double.IsInfinity(availableSize.Width) ? 620 : availableSize.Width, 370 + fit_columns().Length * 112 + 24);
        return new Size(width, header_height + System.Math.Max(3, visible_rows().Length) * row_height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsReadOnly)
            return;
        var point = e.GetPosition(this);
        int index = (int)System.Math.Floor((point.Y - header_height) / row_height);
        var current = visible_rows();
        if (index < 0 || index >= current.Length)
            return;
        if (point.X > 34)
            return;

        current[index].IsSelected = !current[index].IsSelected;
        if (SelectionChangedCommand?.CanExecute(null) == true)
            SelectionChangedCommand.Execute(null);
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), Bounds);
        var rows = visible_rows();
        var parameter_columns = fit_columns();
        draw_header(context, parameter_columns);
        if (rows.Length == 0)
        {
            draw_text(context, "Drop population nodes here", new Point(14, 43), 13, Color.FromRgb(230, 232, 238), true);
            draw_text(context, "Each checked population is modeled independently per sample.", new Point(14, 67), 11, Color.FromRgb(164, 168, 178), false);
            return;
        }

        var values = fit_values();
        for (int index = 0; index < rows.Length; index++)
            draw_row(context, rows[index], index, parameter_columns, values);
    }

    private IntegrationJobPopulationSelection[] visible_rows() =>
        Platform?.Populations
            .Where(row => row.IsPopulation && row.IsPlatformDropped)
            .OrderBy(row => row.SampleName, StringComparer.Ordinal)
            .ThenBy(row => row.PopulationName, StringComparer.Ordinal)
            .ToArray() ?? [];

    private string[] fit_columns()
    {
        var table = Platform?.ResultTables.FirstOrDefault();
        if (table is null || table.Columns.Length <= 2)
            return [];
        return table.Columns.Skip(2).ToArray();
    }

    private Dictionary<(string Sample, string Population), Dictionary<string, string>> fit_values()
    {
        var result = new Dictionary<(string, string), Dictionary<string, string>>();
        var table = Platform?.ResultTables.FirstOrDefault();
        if (table is null || table.Columns.Length < 2)
            return result;
        foreach (var row in table.Rows)
        {
            if (row.Length < 2)
                continue;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 2; index < System.Math.Min(row.Length, table.Columns.Length); index++)
                values[table.Columns[index]] = row[index];
            result[(row[0], row[1])] = values;
        }
        return result;
    }

    private void draw_header(DrawingContext context, IReadOnlyList<string> parameter_columns)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 24)), new Rect(0, 0, Bounds.Width, header_height));
        draw_text(context, "Sample", new Point(62, 6), 13, Color.FromRgb(164, 168, 178), true);
        draw_text(context, "Population", new Point(210, 6), 13, Color.FromRgb(164, 168, 178), true);
        double x = 370;
        foreach (string column in parameter_columns)
        {
            draw_text(context, column, new Point(x, 6), 13, Color.FromRgb(164, 168, 178), true);
            x += 112;
        }
    }

    private void draw_row(
        DrawingContext context,
        IntegrationJobPopulationSelection row,
        int index,
        IReadOnlyList<string> parameter_columns,
        IReadOnlyDictionary<(string Sample, string Population), Dictionary<string, string>> values)
    {
        double top = header_height + index * row_height;
        var background = index % 2 == 0 ? Color.FromRgb(34, 34, 34) : Color.FromRgb(29, 29, 29);
        context.FillRectangle(new SolidColorBrush(background), new Rect(0, top, Bounds.Width, row_height));
        draw_checkbox(context, new Rect(14, top + 6, checkbox_size, checkbox_size), row.IsSelected);
        draw_icon(context, new Rect(40, top + 6, 15, 15), "avares://gated/Resources/subset.svg");
        draw_swatch(context, new Rect(60, top + 8, 16, 10), PlatformPalette.ColorForIndex(source_index(row)));
        draw_text(context, row.SampleName, new Point(84, top + 5), 13, Color.FromRgb(236, 238, 244), false);
        draw_text(context, row.PopulationName, new Point(210, top + 5), 13, Color.FromRgb(214, 218, 226), false);

        values.TryGetValue((row.SampleName, row.PopulationName), out var row_values);
        double x = 370;
        foreach (string column in parameter_columns)
        {
            string value = row_values is not null && row_values.TryGetValue(column, out string? text) ? text : "";
            draw_text(context, value, new Point(x, top + 5), 13, Color.FromRgb(214, 218, 226), false);
            x += 112;
        }
    }

    private void drag_over(object? sender, DragEventArgs e)
    {
        var node = PageEditorView.DraggedProjectNode;
        e.DragEffects = node?.Kind is ProjectNodeKind.Population or ProjectNodeKind.Gate or ProjectNodeKind.Sample
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void drop_node(object? sender, DragEventArgs e)
    {
        if (PageEditorView.DraggedProjectNode is not { } node)
            return;
        if (DropCommand?.CanExecute(node) == true)
            DropCommand.Execute(node);
        PageEditorView.DraggedProjectNode = null;
        e.Handled = true;
    }

    private static void draw_icon(DrawingContext context, Rect rect, string uri)
    {
        var icon = new SvgImage { Source = SvgSource.LoadFromStream(AssetLoader.Open(new System.Uri(uri))) };
        context.DrawImage(icon, rect);
    }

    private static void draw_checkbox(DrawingContext context, Rect rect, bool is_checked)
    {
        IBrush fill = is_checked ? new SolidColorBrush(Color.FromRgb(76, 132, 255)) : Brushes.Transparent;
        context.DrawRectangle(fill, new Pen(new SolidColorBrush(Color.FromRgb(120, 126, 138)), 1), rect, 4);
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

    private static void draw_swatch(DrawingContext context, Rect rect, Color color)
    {
        context.DrawRectangle(new SolidColorBrush(color), null, rect, 2);
    }

    private int source_index(IntegrationJobPopulationSelection row)
    {
        if (Platform is null)
            return 0;
        for (int index = 0; index < Platform.RowMap.Sources.Count; index++)
        {
            var source = Platform.RowMap.Sources[index];
            if (source.GroupId == row.GroupId &&
                source.SampleId == row.SampleId &&
                source.GateId == row.GateId &&
                source.Region == row.Region)
                return index;
        }

        return visible_rows().TakeWhile(item => !ReferenceEquals(item, row)).Count();
    }

    private void draw_text(DrawingContext context, string text, Point point, double size, Color color, bool bold) =>
        context.DrawText(new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
            size,
            new SolidColorBrush(color)), point);

    private void resubscribe()
    {
        unsubscribe();
        subscribed_platform = Platform;
        if (subscribed_platform is null)
            return;
        subscribed_platform.PropertyChanged += platform_changed;
        subscribed_populations = subscribed_platform.Populations;
        subscribed_tables = subscribed_platform.ResultTables;
        subscribed_populations.CollectionChanged += collection_changed;
        subscribed_tables.CollectionChanged += collection_changed;
        subscribed_rows = subscribed_platform.Populations.ToArray();
        foreach (var row in subscribed_rows)
            row.PropertyChanged += row_changed;
        invalidate_on_ui_thread();
    }

    private void unsubscribe()
    {
        if (subscribed_platform is not null)
            subscribed_platform.PropertyChanged -= platform_changed;
        if (subscribed_populations is not null)
            subscribed_populations.CollectionChanged -= collection_changed;
        if (subscribed_tables is not null)
            subscribed_tables.CollectionChanged -= collection_changed;
        foreach (var row in subscribed_rows)
            row.PropertyChanged -= row_changed;
        subscribed_platform = null;
        subscribed_populations = null;
        subscribed_tables = null;
        subscribed_rows = [];
    }

    private void platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        invalidate_on_ui_thread();
    }

    private void collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            resubscribe();
        else
            Dispatcher.UIThread.Post(resubscribe);
    }

    private void row_changed(object? sender, PropertyChangedEventArgs e)
    {
        invalidate_on_ui_thread();
    }

    private void invalidate_on_ui_thread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
            InvalidateMeasure();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            InvalidateVisual();
            InvalidateMeasure();
        });
    }
}
