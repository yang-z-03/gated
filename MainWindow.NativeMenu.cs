using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace gated;

public partial class MainWindow
{
    partial void configure_native_menu()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var native_menu = new NativeMenu();
        native_menu.NeedsUpdate += (_, _) => rebuild_native_menu(native_menu);
        rebuild_native_menu(native_menu);
        NativeMenu.SetMenu(this, native_menu);
        main_menu.IsVisible = false;
    }

    private void rebuild_native_menu(NativeMenu native_menu)
    {
        native_menu.Items.Clear();
        foreach (var item in main_menu.Items)
        {
            if (to_native_menu_item(item) is { } native_item)
                native_menu.Items.Add(native_item);
        }
    }

    private static NativeMenuItemBase? to_native_menu_item(object? item)
    {
        if (item is Separator)
            return new NativeMenuItemSeparator();
        if (item is not MenuItem menu_item)
            return null;

        var native_item = new NativeMenuItem
        {
            Header = header_text(menu_item.Header),
            Gesture = menu_item.InputGesture,
            ToggleType = to_native_toggle_type(menu_item.ToggleType),
            IsChecked = menu_item.IsChecked,
            IsEnabled = menu_item.IsEnabled,
            IsVisible = menu_item.IsVisible,
            Command = menu_item.Command,
            CommandParameter = menu_item.CommandParameter
        };

        native_item.Click += (_, _) =>
        {
            if (menu_item.ToggleType != MenuItemToggleType.None)
                menu_item.IsChecked = native_item.IsChecked;

            if (menu_item.Command is { } command)
            {
                var parameter = menu_item.CommandParameter;
                if (command.CanExecute(parameter))
                    command.Execute(parameter);
                return;
            }

            menu_item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        };

        foreach (var child in menu_item.Items)
        {
            if (to_native_menu_item(child) is { } native_child)
            {
                native_item.Menu ??= new NativeMenu();
                native_item.Menu.Items.Add(native_child);
            }
        }

        return native_item;
    }

    private static string header_text(object? header) =>
        header?.ToString()?.Replace("_", string.Empty) ?? string.Empty;

    private static NativeMenuItemToggleType to_native_toggle_type(MenuItemToggleType toggle_type) =>
        Enum.TryParse<NativeMenuItemToggleType>(toggle_type.ToString(), out var native_toggle_type)
            ? native_toggle_type
            : NativeMenuItemToggleType.None;
}
