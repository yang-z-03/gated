using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace gated.Controls;

public static class PlatformPopulationDropBehavior
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, ICommand?>("Command", typeof(PlatformPopulationDropBehavior));

    static PlatformPopulationDropBehavior()
    {
        CommandProperty.Changed.AddClassHandler<DataGrid>(command_changed);
    }

    public static void SetCommand(AvaloniaObject target, ICommand? value) => target.SetValue(CommandProperty, value);
    public static ICommand? GetCommand(AvaloniaObject target) => target.GetValue(CommandProperty);

    private static void command_changed(DataGrid grid, AvaloniaPropertyChangedEventArgs change)
    {
        bool enabled = change.NewValue is ICommand;
        DragDrop.SetAllowDrop(grid, enabled);
        grid.RemoveHandler(DragDrop.DragOverEvent, drag_over);
        grid.RemoveHandler(DragDrop.DropEvent, drop);
        if (!enabled)
            return;
        grid.AddHandler(DragDrop.DragOverEvent, drag_over);
        grid.AddHandler(DragDrop.DropEvent, drop);
    }

    private static void drag_over(object? sender, DragEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;
        var node = PageEditorView.ResolveDraggedProjectNode(e.DataTransfer);
        var command = GetCommand(grid);
        e.DragEffects = node is not null && command?.CanExecute(node) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void drop(object? sender, DragEventArgs e)
    {
        if (sender is not DataGrid grid || PageEditorView.ResolveDraggedProjectNode(e.DataTransfer) is not { } node)
            return;
        var command = GetCommand(grid);
        if (command?.CanExecute(node) == true)
            command.Execute(node);
        PageEditorView.DraggedProjectNode = null;
        e.Handled = true;
    }
}
