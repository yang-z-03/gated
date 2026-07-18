using System;
using System.Collections;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using gated.Models;
using gated.ViewModels;

namespace gated.Controls;

public sealed class PlatformPopulationDropView : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<PlatformPopulationDropView, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<PlatformPopulationDropView, ICommand?>(nameof(DropCommand));

    static PlatformPopulationDropView()
    {
        AffectsRender<PlatformPopulationDropView>(ItemsProperty);
    }

    public PlatformPopulationDropView()
    {
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, drag_over);
        DragDrop.AddDropHandler(this, drop_node);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? DropCommand
    {
        get => GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        var background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3"));
        var border = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border4")), 1);
        context.FillRectangle(background, bounds);
        context.DrawRectangle(null, border, bounds);

        var rows = Items is IEnumerable enumerable
            ? enumerable.OfType<IntegrationJobPopulationSelection>()
                .Where(item => item.IsSelected && item.IsPopulation)
                .Take(8)
                .Select(item => $"{item.SampleName} - {item.PopulationName}")
                .ToArray()
            : [];

        if (rows.Length == 0)
        {
            draw_text(context, "Drop population nodes here", new Point(16, 18), 14, gated.Shared.ThemeResources.AppColor("Text2"), bold: true);
            draw_text(context, "Modeling platforms use compensated event values from dropped populations.", new Point(16, 42), 11, gated.Shared.ThemeResources.AppColor("Text4"), bold: false);
            return;
        }

        draw_text(context, "Dropped populations", new Point(16, 12), 12, gated.Shared.ThemeResources.AppColor("Text4"), bold: true);
        for (int index = 0; index < rows.Length; index++)
            draw_text(context, rows[index], new Point(16, 36 + index * 22), 12, gated.Shared.ThemeResources.AppColor("Text2"), bold: false);
    }

    private void drag_over(object? sender, DragEventArgs e)
    {
        var node = PageEditorView.ResolveDraggedProjectNode(e.DataTransfer);
        e.DragEffects = node?.Kind is ProjectNodeKind.Population or ProjectNodeKind.Gate or ProjectNodeKind.Sample
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void drop_node(object? sender, DragEventArgs e)
    {
        if (PageEditorView.ResolveDraggedProjectNode(e.DataTransfer) is not { } node)
            return;

        if (DropCommand?.CanExecute(node) == true)
            DropCommand.Execute(node);
        PageEditorView.DraggedProjectNode = null;
        e.Handled = true;
    }

    private static void draw_text(DrawingContext context, string text, Point origin, double size, Color color, bool bold)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size,
            new SolidColorBrush(color));
        context.DrawText(formatted, origin);
    }
}
