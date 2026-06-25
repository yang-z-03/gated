using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Data;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using gated.Models;
using gated.ViewModels;
using gated.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using gated.Services;

namespace gated;

internal enum UpdateDialogChoice
{
    Cancel,
    Update,
    SuppressForOneWeek
}

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel view_model = new();
    private readonly Dictionary<MenuItem, ChannelMenuState> channel_menu_states = new();
    private string? current_workspace_path;
    private bool channel_menu_update_pending;
    private bool close_confirmed;
    private bool close_prompt_active;
    private bool plot_properties_panel_visible = true;
    private double analysis_properties_panel_width = 336;
    private double layout_properties_panel_width = 336;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = view_model;
        view_model.RequestTextInputAsync = show_text_input_dialog;
        view_model.RequestScriptSaveChoiceAsync = show_script_save_dialog;
        view_model.RequestChoiceInputAsync = show_choice_input_dialog;
        view_model.RequestMultipleChoiceInputAsync = show_multiple_choice_input_dialog;
        view_model.RequestBooleanGateInputAsync = show_boolean_gate_input_dialog;
        view_model.RequestCompensationEditorAsync = show_compensation_editor_dialog;
        gated.Python.PythonExtensionRuntime.InputRequested = show_python_input_dialog_blocking;
        view_model.PropertyChanged += view_model_property_changed;
        view_model.AxisChoices.CollectionChanged += channel_choices_collection_changed;
        view_model.ColorChoices.CollectionChanged += channel_choices_collection_changed;
        view_model.SelectedPageAxisChoices.CollectionChanged += channel_choices_collection_changed;
        view_model.SelectedPageColorChoices.CollectionChanged += channel_choices_collection_changed;
        view_model.RecentFilePaths.CollectionChanged += recent_file_paths_collection_changed;
        view_model.MacroScripts.CollectionChanged += script_repository_collection_changed;
        view_model.StatisticScripts.CollectionChanged += script_repository_collection_changed;
        update_statistics_columns();
        update_metadata_columns(workspace_metadata_grid, view_model.WorkspaceMetadataTable);
        update_channel_menus();
        update_recent_items_menu();
        update_script_repository_menus();
        update_page_editor_viewport_size();
        project_tree.NodeContextRequested += project_tree_node_context_requested;
        page_editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                update_page_editor_viewport_size();
        };
        Closing += window_closing;
        AddHandler(KeyDownEvent, window_key_down, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, drag_over);
        DragDrop.AddDropHandler(this, drop_files);

        this.PropertyChanged += (s, e) => {
            if (e.Property == Window.WindowStateProperty)
            {
                update_window_margin_for_state();
            }
        };
        WindowPlacementStore.Restore(this);
        restore_panel_layout(WindowPlacementStore.Load());
        configure_platform_window_chrome();
        update_window_margin_for_state();
        update_main_mode_switch();
    }

    private async void analysis_mode_tab_pressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        await view_model.ShowAnalysisModeAsync();
    }

    private async void layout_mode_tab_pressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        await view_model.ShowLayoutModeAsync();
    }

    private void code_mode_tab_pressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        view_model.OpenPythonScriptEditor();
    }

    private void update_main_mode_switch()
    {
        mainModeSwitchThumb.Margin = view_model.ViewState == MainWindowViewState.Code
            ? new Thickness(147, 3, 3, 3)
            : view_model.ViewState == MainWindowViewState.Layout
                ? new Thickness(75, 3, 0, 3)
                : new Thickness(3, 3, 0, 3);

        var active = new SolidColorBrush(Color.FromRgb(32, 35, 44));
        var inactive = new SolidColorBrush(Color.FromRgb(185, 189, 202));
        mainModeAnalysisText.Foreground = view_model.ViewState is MainWindowViewState.Analysis
            or MainWindowViewState.Metadata
            or MainWindowViewState.Platform ? active : inactive;
        mainModeLayoutText.Foreground = view_model.ViewState == MainWindowViewState.Layout ? active : inactive;
        mainModeCodeText.Foreground = view_model.ViewState == MainWindowViewState.Code ? active : inactive;
    }

    private void update_window_margin_for_state()
    {
        if (!OperatingSystem.IsWindows())
        {
            window.Margin = new Thickness(0);
            return;
        }

        if (WindowState == WindowState.Maximized)
            window.Margin = new Thickness(8);
        else window.Margin = new Thickness(0);
    }

    private void configure_platform_window_chrome()
    {
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.Default;
        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = 0;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = false;
            titleBar.IsVisible = false;
            return;
        }
    }

    private void restore_panel_layout(WindowPlacementStore.WindowPlacement? placement)
    {
        if (placement?.ProjectTreeWidth is { } project_tree_width)
            main_workspace_grid.ColumnDefinitions[0].Width = new GridLength(project_tree_width);
        if (placement?.StatisticsPanelHeight is { } statistics_panel_height)
            analysis_mode_grid.RowDefinitions[2].Height = new GridLength(statistics_panel_height);
    }

    private double current_project_tree_width() =>
        main_workspace_grid.ColumnDefinitions[0].ActualWidth;

    private double current_statistics_panel_height() =>
        analysis_mode_grid.RowDefinitions[2].ActualHeight;

    private void toggle_plot_properties_menu_item_click(object? sender, RoutedEventArgs e) =>
        set_plot_properties_panel_visible(sender is not MenuItem item || item.IsChecked != false);

    private void set_plot_properties_panel_visible(bool is_visible)
    {
        if (plot_properties_panel_visible == is_visible)
            return;

        plot_properties_panel_visible = is_visible;
        if (!is_visible)
        {
            analysis_properties_panel_width = Math.Max(336, analysis_plot_grid.ColumnDefinitions[1].ActualWidth);
            layout_properties_panel_width = Math.Max(336, layout_mode_grid.ColumnDefinitions[1].ActualWidth);
        }

        set_properties_panel_column(analysis_plot_grid, analysis_properties_panel, 1, is_visible, analysis_properties_panel_width);
        set_properties_panel_column(layout_mode_grid, layout_properties_panel, 1, is_visible, layout_properties_panel_width);
        toggle_plot_properties_menu_item.IsChecked = is_visible;
        toggle_layout_properties_menu_item.IsChecked = is_visible;
        update_page_editor_viewport_size();
    }

    private static void set_properties_panel_column(Grid grid, Control panel, int column_index, bool is_visible, double visible_width)
    {
        panel.IsVisible = is_visible;
        grid.ColumnDefinitions[column_index].Width = is_visible
            ? new GridLength(Math.Max(240, visible_width))
            : new GridLength(0);
    }

    private void view_model_property_changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ViewState))
            update_main_mode_switch();
        if (e.PropertyName == nameof(MainWindowViewModel.ViewState) && view_model.IsPageEditorMode)
            page_editor.RefreshRenderCachesSequentially(view_model.PageElements);
        if (e.PropertyName == nameof(MainWindowViewModel.StatisticTable))
            update_statistics_columns();
        if (e.PropertyName == nameof(MainWindowViewModel.WorkspaceMetadataTable))
            update_metadata_columns(workspace_metadata_grid, view_model.WorkspaceMetadataTable);
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedGroup))
            update_script_repository_menus();
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedXAxisChoice)
            or nameof(MainWindowViewModel.SelectedYAxisChoice)
            or nameof(MainWindowViewModel.SelectedDotColorChoice)
            or nameof(MainWindowViewModel.SelectedPageXAxisChoice)
            or nameof(MainWindowViewModel.SelectedPageYAxisChoice)
            or nameof(MainWindowViewModel.SelectedPageDotColorChoice))
            request_update_channel_menus();
    }

    private void channel_choices_collection_changed(object? sender, NotifyCollectionChangedEventArgs e) =>
        request_update_channel_menus();

    private void recent_file_paths_collection_changed(object? sender, NotifyCollectionChangedEventArgs e) =>
        update_recent_items_menu();

    private void script_repository_collection_changed(object? sender, NotifyCollectionChangedEventArgs e) =>
        update_script_repository_menus();

    private void request_update_channel_menus()
    {
        if (channel_menu_update_pending)
            return;

        channel_menu_update_pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            channel_menu_update_pending = false;
            update_channel_menus();
        });
    }

    private void update_page_editor_viewport_size()
    {
        page_editor.ViewportSize = page_editor.Bounds.Size;
    }

    private void update_recent_items_menu()
    {
        recent_items_menu.Items.Clear();
        if (view_model.RecentFilePaths.Count == 0)
        {
            recent_items_menu.Items.Add(new MenuItem { Header = "(No recent items)", IsEnabled = false });
        }
        else
        {
            foreach (string path in view_model.RecentFilePaths)
            {
                var item = new MenuItem
                {
                    Header = path,
                    Tag = path
                };
                item.Click += recent_file_menu_item_click;
                recent_items_menu.Items.Add(item);
            }
        }

        recent_items_menu.Items.Add(new Separator());
        var clear_item = new MenuItem
        {
            Header = "Clear file history",
            IsEnabled = view_model.RecentFilePaths.Count > 0
        };
        clear_item.Click += clear_recent_files_menu_item_click;
        recent_items_menu.Items.Add(clear_item);
    }

    private void update_script_repository_menus()
    {
        populate_script_menu(run_macros_menu, view_model.MacroScripts, run_macro_menu_item_click);
        populate_script_menu(edit_macros_menu, view_model.MacroScripts, edit_script_menu_item_click);
        populate_script_menu(python_statistics_menu, view_model.StatisticScripts, apply_statistic_script_menu_item_click);
        populate_script_menu(edit_python_statistics_menu, view_model.StatisticScripts, edit_script_menu_item_click);
    }

    private static void populate_script_menu(
        MenuItem menu,
        IEnumerable<gated.Services.PythonScriptDefinition> scripts,
        EventHandler<RoutedEventArgs> click)
    {
        menu.Items.Clear();
        var script_list = scripts.OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (script_list.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Empty", IsEnabled = false });
            return;
        }

        foreach (var script in script_list)
        {
            var item = new MenuItem
            {
                Header = script.Name,
                Tag = script
            };
            item.Click += click;
            menu.Items.Add(item);
        }
    }

    private async void recent_file_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string path)
            return;

        if (!File.Exists(path))
        {
            await show_message_dialog("Recent item unavailable", $"The file could not be found:{Environment.NewLine}{path}");
            return;
        }

        if (path.EndsWith(".gated", StringComparison.OrdinalIgnoreCase))
        {
            await load_workspace_with_progress(path);
            return;
        }

        await import_fcs_files([path], "Failed to open recent item", target_group: selected_import_target_group());
    }

    private void clear_recent_files_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ClearRecentFilePaths();

    private void update_statistics_columns()
    {
        statistics.Columns.Clear();
        statistics.ItemsSource = view_model.StatisticTableView;
        foreach (System.Data.DataColumn column in view_model.StatisticTable.Columns)
        {
            var binding = new Binding($"Row.ItemArray[{column.Ordinal}]");
            if (column.DataType == typeof(bool))
            {
                statistics.Columns.Add(new DataGridCheckBoxColumn
                {
                    Header = column.ColumnName,
                    Binding = binding
                });
            }
            else
            {
                statistics.Columns.Add(new DataGridTextColumn
                {
                    Header = column.ColumnName,
                    Binding = binding
                });
            }
        }
    }

    private static void update_metadata_columns(DataGrid grid, System.Data.DataTable table)
    {
        grid.Columns.Clear();
        grid.ItemsSource = table.DefaultView;
        foreach (System.Data.DataColumn column in table.Columns)
        {
            if (column.ColumnName.StartsWith("__", StringComparison.Ordinal))
                continue;
            var binding = new Binding(column.ColumnName)
            {
                Mode = column.ColumnName is "Group" or "Sample" ? BindingMode.OneWay : BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.ColumnName,
                Binding = binding,
                Width = column.ColumnName is "Group" or "Sample" ? new DataGridLength(130) : new DataGridLength(120),
                IsReadOnly = column.ColumnName is "Group" or "Sample"
            });
        }
    }

    private static void apply_small_button_classes(Control root)
    {
        if (root is Button button && !button.Classes.Contains("Small"))
            button.Classes.Add("Small");
        if (root is Panel panel)
            foreach (var child in panel.Children)
                apply_small_button_classes(child);
        if (root is ContentControl { Content: Control content })
            apply_small_button_classes(content);
    }

    private async void window_key_down(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O)
        {
            e.Handled = true;
            await open_fcs_files_async();
            return;
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.O)
        {
            e.Handled = true;
            await open_workspace_async();
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S)
        {
            e.Handled = true;
            await save_workspace_async();
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.G)
        {
            execute_shortcut_command(view_model.CreateGroupCommand, e);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.L)
        {
            execute_shortcut_command(view_model.CreateLayoutCommand, e);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.E)
        {
            execute_shortcut_command(view_model.ExpandProjectTreeCommand, e);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.R)
        {
            e.Handled = true;
            set_plot_properties_panel_visible(!plot_properties_panel_visible);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.P)
        {
            if (view_model.EditCompensationCommand.CanExecute(null))
            {
                e.Handled = true;
                view_model.EditCompensationCommand.Execute(null);
            }
            return;
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.P)
        {
            e.Handled = true;
            await view_model.CreateMacroAsync();
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Delete)
        {
            if (view_model.IsPageEditorMode)
            {
                e.Handled = true;
                if (view_model.DeletePageElementCommand.CanExecute(null))
                    view_model.DeletePageElementCommand.Execute(null);
                return;
            }

            if (can_delete_with_shortcut() && view_model.DeleteSelectedCommand.CanExecute(null))
            {
                e.Handled = true;
                view_model.DeleteSelectedCommand.Execute(null);
            }
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F2)
        {
            if (view_model.RenameSelectedNodeCommand.CanExecute(null))
            {
                e.Handled = true;
                view_model.RenameSelectedNodeCommand.Execute(null);
            }
        }
    }

    private static void execute_shortcut_command(System.Windows.Input.ICommand command, KeyEventArgs e)
    {
        if (!command.CanExecute(null))
            return;

        e.Handled = true;
        command.Execute(null);
    }

    private bool can_delete_with_shortcut() =>
        view_model.SelectedNode?.Kind is ProjectNodeKind.Group
            or ProjectNodeKind.Sample
            or ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.Population
            or ProjectNodeKind.Embedding
            or ProjectNodeKind.StatisticDefinition
            or ProjectNodeKind.StatisticValue
            or ProjectNodeKind.Layout
            or ProjectNodeKind.Compensation
            or ProjectNodeKind.Platform;

    private async void open_fcs_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await open_fcs_files_async();

    private async void concatenate_samples_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await concatenate_samples_async();

    private async void split_sample_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await split_sample_async();

    private async Task open_fcs_files_async()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open FCS samples",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("FCS files") { Patterns = ["*.fcs", "*.FCS"] },
                FilePickerFileTypes.All
            ]
        });

        await import_fcs_files(
            files.Select(file => file.Path.LocalPath),
            "Failed to open FCS files",
            target_group: selected_import_target_group());
    }

    private FlowGroup? selected_import_target_group()
    {
        var node = view_model.SelectedNode;
        return node?.Kind == ProjectNodeKind.Workspace ? null : node?.Group;
    }

    private async void open_workspace_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await open_workspace_async();

    private async Task open_workspace_async()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Gated workspace",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Gated workspaces") { Patterns = ["*.gated"] },
                FilePickerFileTypes.All
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        string path = file.Path.LocalPath;
        await load_workspace_with_progress(path);
    }

    private async void save_workspace_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await save_workspace_async();

    private async void close_workspace_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await close_workspace_async();

    private async Task close_workspace_async()
    {
        if (view_model.IsPythonScriptEditorMode && view_model.IsPythonScriptDirty)
        {
            await view_model.ClosePythonScriptEditorAsync();
            if (view_model.IsPythonScriptEditorMode)
                return;
        }

        var choice = await show_workspace_exit_dialog();
        if (choice == ScriptSaveChoice.Cancel)
            return;
        if (choice == ScriptSaveChoice.Save && !await save_workspace_async())
            return;

        view_model.CloseWorkspace();
        current_workspace_path = null;
    }

    private async Task<bool> save_workspace_async()
    {
        string? path = current_workspace_path;
        if (string.IsNullOrWhiteSpace(path))
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Gated workspace",
                SuggestedFileName = "workspace.gated",
                DefaultExtension = "gated",
                FileTypeChoices =
                [
                    new FilePickerFileType("Gated workspaces") { Patterns = ["*.gated"] },
                    FilePickerFileTypes.All
                ]
            });

            if (file is null)
                return false;

            path = file.Path.LocalPath;
        }

        try
        {
            await run_with_progress_dialog("Saving workspace ...", path, () => Task.Run(() => new WorkspaceBinarySerializer().Save(view_model.Workspace, path)));
            view_model.StatusText = $"Saved workspace: {System.IO.Path.GetFileName(path)}";
            current_workspace_path = path;
            return true;
        }
        catch (Exception exception)
        {
            view_model.StatusText = $"Failed to save workspace: {exception.Message}";
            return false;
        }
    }

    private async void about_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    private async void preferences_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool saved = await new PreferencesWindow().ShowDialog<bool>(this);
        if (saved)
        {
            view_model.RefreshConfigurationAssumptions();
            view_model.StatusText = "Preferences updated";
        }
    }

    private async void manage_extension_packages_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await new PythonPackageManagerWindow().ShowDialog(this);
    }

    private async void create_macro_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await view_model.CreateMacroAsync();

    private async void create_statistic_script_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await view_model.CreateStatisticScriptAsync();

    private async void run_macro_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not gated.Services.PythonScriptDefinition script)
            return;
        try
        {
            await view_model.RunMacroAsync(script);
        }
        catch (Exception exception)
        {
            await show_message_dialog("Macro failed", exception.Message);
        }
    }

    private async void edit_script_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is gated.Services.PythonScriptDefinition script)
            await view_model.OpenPythonScriptEditorAsync(script);
    }

    private async void apply_statistic_script_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not gated.Services.PythonScriptDefinition script)
            return;
        try
        {
            var requirements = gated.Python.PythonExtensionRuntime.GetStatisticRequirements(script.Source);
            string? parameters_json = await show_statistic_requirements_dialog(script.Name, requirements, "Add");
            if (parameters_json is null)
                return;

            view_model.ApplyStatisticScript(script, parameters_json);
        }
        catch (Exception exception)
        {
            await show_message_dialog("Failed to add Python statistic", exception.Message);
        }
    }

    private async Task<string?> show_statistic_requirements_dialog(
        string statistic_name,
        IReadOnlyList<gated.Python.PythonStatisticRequirement> requirements,
        string confirm_text = "OK")
    {
        if (requirements.Count == 0)
            return "[]";

        const double field_width = 260;
        var fields = new List<StatisticRequirementField>();
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };

        foreach (var requirement in requirements)
        {
            var field_panel = new StackPanel { Spacing = 6 };
            if (!is_checkbox_requirement(requirement))
            {
                field_panel.Children.Add(new TextBlock
                {
                    Text = requirement.Name,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var field = create_statistic_requirement_field(requirement, field_width);
            field_panel.Children.Add(field.Control);
            fields.Add(field);
            panel.Children.Add(field_panel);
        }

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 84 };
        var ok = new Button { Content = confirm_text, MinWidth = 84 };
        cancel.Classes.Add("Small");
        ok.Classes.Add("Small");
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = statistic_name,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Child = panel
            }
        };

        cancel.Click += (_, _) => dialog.Close(null);
        ok.Click += (_, _) =>
        {
            var values = fields.Select(field => field.Value()).ToArray();
            dialog.Close(JsonSerializer.Serialize(values));
        };

        return await dialog.ShowDialog<string?>(this);
    }

    private string? show_python_input_dialog_blocking(
        string title,
        IReadOnlyList<gated.Python.PythonStatisticRequirement> requirements)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return show_statistic_requirements_dialog(title, requirements, "OK").GetAwaiter().GetResult();

        var completion = new TaskCompletionSource<string?>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await show_statistic_requirements_dialog(title, requirements, "OK"));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private StatisticRequirementField create_statistic_requirement_field(gated.Python.PythonStatisticRequirement requirement, double field_width)
    {
        var channels = view_model.SelectedGroup?.Channels
            .Select(channel => new StatisticChannelChoice(channel.Name, channel.Label))
            .ToArray() ?? [];
        switch (requirement.Type.Trim().ToLowerInvariant())
        {
            case "channel" when requirement.Multiple:
            {
                var defaults = json_string_array(requirement.Default).ToHashSet(StringComparer.Ordinal);
                var checks = channels.Select(channel => new CheckBox
                {
                    Content = channel_content(channel),
                    Tag = channel.Name,
                    IsChecked = defaults.Count == 0 ? false : defaults.Contains(channel.Name),
                    Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                }).ToArray();
                var stack = new StackPanel { Spacing = 6, Width = field_width - 16 };
                foreach (var check in checks)
                    stack.Children.Add(check);
                var scroller = new ScrollViewer
                {
                    Width = field_width - 16,
                    MaxHeight = 150,
                    Margin = new Thickness(8, 8),
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = stack
                };
                var border = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromRgb(22, 22, 24)),
                    Child = scroller
                };
                return new StatisticRequirementField(border, () => checks
                    .Where(check => check.IsChecked == true)
                    .Select(check => check.Tag?.ToString() ?? "")
                    .Where(value => value.Length > 0)
                    .ToArray());
            }
            case "channel":
            {
                var combo = new ComboBox { ItemsSource = channels, Width = field_width };
                combo.Classes.Add("Small");
                var template = channel_choice_template();
                combo.ItemTemplate = template;
                combo.SelectionBoxItemTemplate = template;
                string default_channel = json_string(requirement.Default);
                combo.SelectedItem = channels.FirstOrDefault(channel => channel.Name == default_channel);
                if (combo.SelectedItem is null && channels.Length > 0)
                    combo.SelectedIndex = 0;
                return new StatisticRequirementField(combo, () => (combo.SelectedItem as StatisticChannelChoice)?.Name ?? "");
            }
            case "integer":
            {
                var input = new NumericUpDown
                {
                    Minimum = requirement.Min.HasValue ? (decimal)requirement.Min.Value : int.MinValue,
                    Maximum = requirement.Max.HasValue ? (decimal)requirement.Max.Value : int.MaxValue,
                    Increment = 1,
                    Value = json_decimal(requirement.Default) ?? 0,
                    Width = field_width
                };
                input.Classes.Add("Small");
                return new StatisticRequirementField(input, () => Convert.ToInt32(input.Value ?? 0));
            }
            case "float":
            {
                var input = new NumericUpDown
                {
                    Minimum = requirement.Min.HasValue ? (decimal)requirement.Min.Value : decimal.MinValue,
                    Maximum = requirement.Max.HasValue ? (decimal)requirement.Max.Value : decimal.MaxValue,
                    Increment = 0.1m,
                    Value = json_decimal(requirement.Default) ?? 0,
                    Width = field_width
                };
                input.Classes.Add("Small");
                return new StatisticRequirementField(input, () => Convert.ToDouble(input.Value ?? 0));
            }
            case "enum":
            {
                var combo = new ComboBox { ItemsSource = requirement.Values, Width = field_width };
                combo.Classes.Add("Small");
                combo.SelectedItem = json_string(requirement.Default);
                if (combo.SelectedItem is null && requirement.Values.Length > 0)
                    combo.SelectedIndex = 0;
                return new StatisticRequirementField(combo, () => combo.SelectedItem?.ToString() ?? "");
            }
            case "option":
            {
                var check = new CheckBox
                {
                    Content = requirement.Name,
                    IsChecked = json_bool(requirement.Default),
                    Foreground = Brushes.White,
                    Width = field_width
                };
                return new StatisticRequirementField(check, () => check.IsChecked == true);
            }
            default:
            {
                var text = new TextBox { Text = json_string(requirement.Default), Width = field_width };
                text.Classes.Add("Small");
                return new StatisticRequirementField(text, () => text.Text ?? "");
            }
        }
    }

    private static bool is_checkbox_requirement(gated.Python.PythonStatisticRequirement requirement) =>
        string.Equals(requirement.Type, "option", StringComparison.OrdinalIgnoreCase);

    private static IDataTemplate channel_choice_template() =>
        new FuncDataTemplate<StatisticChannelChoice>((choice, _) => channel_content(choice), supportsRecycling: false);

    private static Control channel_content(StatisticChannelChoice? choice)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8
        };
        if (choice is null)
            return panel;
        if (choice.HasLabel)
        {
            panel.Children.Add(new TextBlock
            {
                Text = choice.Label,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 240, 245)),
                FontWeight = FontWeight.SemiBold
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = choice.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178))
        });
        return panel;
    }

    private static string json_string(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    private static string[] json_string_array(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(json_string).Where(value => value.Length > 0).ToArray();
        string value = json_string(element);
        return value.Length == 0 ? [] : [value];
    }

    private static decimal? json_decimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value) ? value : null;

    private static bool json_bool(JsonElement element) =>
        element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False && element.GetBoolean();

    private sealed record StatisticRequirementField(Control Control, Func<object?> Value);

    private sealed class StatisticChannelChoice
    {
        public StatisticChannelChoice(string name, string label)
        {
            Name = name;
            Label = label;
        }

        public string Name { get; }
        public string Label { get; }
        public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
        public override string ToString() => HasLabel ? Label : Name;
    }

    private async void check_for_updates_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await check_for_updates(is_manual_check: true, suppress_connection_errors: false);
    }

    public async Task CheckForUpdatesAtStartupAsync()
    {
        await check_for_updates(is_manual_check: false, suppress_connection_errors: true);
    }

    public async Task<bool> BootstrapPythonIfMissingAsync()
    {
        if (File.Exists(gated.Shared.PlatformSupport.EmbeddedPythonLibraryPath(AppContext.BaseDirectory)))
            return false;

        var manager = new UpdateManager();
        var dialog = new ProgressDialog("Preparing Python runtime ...", "Installing embedded Python and required packages.");
        var progress = new Progress<UpdateProgress>(status => dialog.SetProgress(status.Title, status.Detail, status.Fraction));
        var dialog_task = dialog.ShowDialog(this);

        try
        {
            await Task.Yield();
            await manager.EnsureUpdaterCurrentAsync(progress);
            dialog.SetProgress("Starting updater ...", "Gated will close while Python is installed.", null);
            manager.LaunchPythonBootstrapUpdater();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
        catch (Exception exception)
        {
            dialog.Close();
            await dialog_task;
            await show_message_dialog("Python runtime is missing", $"Unable to start the Python bootstrap updater: {exception.Message}");
            return false;
        }

        dialog.Close();
        await dialog_task;
        return true;
    }

    private async Task check_for_updates(bool is_manual_check, bool suppress_connection_errors)
    {
        try
        {
            var manager = new UpdateManager();
            var status = await manager.GetUpdateStatusAsync();
            if (status.Update is null)
            {
                if (is_manual_check)
                    await show_message_dialog("No updates found", build_update_status_text(status));
                return;
            }

            if (!is_manual_check && is_update_suppressed(status.Update.Latest.Version))
                return;

            string changelog;
            try
            {
                changelog = await manager.DownloadChangelogAsync(status.Update.Latest);
            }
            catch (Exception exception)
            {
                changelog = $"Unable to load changelog: {exception.Message}";
            }

            var choice = await show_update_available_dialog(status, changelog);
            if (choice == UpdateDialogChoice.Update)
                await install_update(manager, status.Update);
            else if (choice == UpdateDialogChoice.SuppressForOneWeek)
                suppress_update_for_one_week(status.Update.Latest.Version);
        }
        catch (Exception exception)
        {
            if (!suppress_connection_errors)
                await show_message_dialog("Update check failed", exception.Message);
        }
    }

    private async Task install_update(UpdateManager manager, UpdateInfo update)
    {
        var dialog = new ProgressDialog("Preparing update ...", $"Gated {update.Latest.Version}");
        var progress = new Progress<UpdateProgress>(status => dialog.SetProgress(status.Title, status.Detail, status.Fraction));
        var dialog_task = dialog.ShowDialog(this);

        try
        {
            await Task.Yield();
            await manager.EnsureUpdaterCurrentAsync(progress);
            dialog.SetProgress("Starting updater ...", "Gated will close while the updater downloads and installs packages.", null);
            manager.LaunchUpdater(update);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
        catch (Exception exception)
        {
            dialog.Close();
            await dialog_task;
            await show_message_dialog("Update failed", exception.Message);
            return;
        }

        dialog.Close();
        await dialog_task;
    }

    private async Task<UpdateDialogChoice> show_update_available_dialog(UpdateCheckResult status, string changelog)
    {
        var update = status.Update ?? throw new ArgumentException("Update status does not contain an update.", nameof(status));
        var changelog_text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(changelog) ? "No changelog is available for this version." : changelog,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(218, 221, 228)),
        };
        changelog_text.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("SemiFontFamilyFixed"));

        var changelog_viewer = new ScrollViewer
        {
            Content = changelog_text,
            MinHeight = 180,
            MaxHeight = 200,
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var dialog = new Window
        {
            Title = "Update available",
            Width = 640,
            MinWidth = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Update available",
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = $"Gated {update.Latest.Version} is available.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(218, 221, 228))
                    },
                    new TextBlock
                    {
                        Text = build_update_status_text(status),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        LineHeight = 18,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
                    },
                    new TextBlock
                    {
                        Text = "Changelog",
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White,
                        Margin = new Thickness(0, 12, 0, 4)
                    },
                    changelog_viewer,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children =
                        {
                            new Button { Content = "Suppress for 1 week", MinWidth = 140 },
                            new Button { Content = "Cancel", MinWidth = 80 },
                            new Button { Content = "Update", MinWidth = 96, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[5]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(UpdateDialogChoice.SuppressForOneWeek);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(UpdateDialogChoice.Cancel);
        ((Button)buttons[2]).Click += (_, _) => dialog.Close(UpdateDialogChoice.Update);
        return await dialog.ShowDialog<UpdateDialogChoice>(this);
    }

    private static string build_update_status_text(UpdateCheckResult status)
    {
        string latest_remote = status.LatestRemote?.Version.ToString() ?? "not available";
        string compatible = status.LatestCompatible?.Version.ToString() ?? "none";
        return (
            $"Current version: {status.Current}\n" +
            $"Most updated remote version: {latest_remote}\n" +
            $"Latest compatible version: {compatible}\n" +
            $"Current OS: {status.System.DisplayName}"
        );
    }

    private static bool is_update_suppressed(AppVersion version)
    {
        try
        {
            string path = update_suppression_path();
            if (!File.Exists(path))
                return false;

            string[] parts = File.ReadAllText(path).Split('|');
            if (parts.Length != 2 ||
                !AppVersion.TryParse(parts[0], out var suppressed_version) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                return false;

            return suppressed_version.CompareTo(version) == 0 && DateTimeOffset.UtcNow < new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch
        {
            return false;
        }
    }

    private static void suppress_update_for_one_week(AppVersion version)
    {
        try
        {
            string path = update_suppression_path();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var until = DateTimeOffset.UtcNow.AddDays(7);
            File.WriteAllText(path, $"{version}|{until.UtcTicks}");
        }
        catch
        {
        }
    }

    private static string update_suppression_path() =>
        Path.Combine(gated.Shared.PlatformSupport.PersistenceDirectory, "update-suppression.txt");

    private async Task show_message_dialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            MinWidth = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18,
                        Foreground = new SolidColorBrush(Color.FromRgb(218, 221, 228))
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "OK", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[2]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    public async Task OpenCommandLineFilesAsync(IEnumerable<string> paths)
    {
        var file_paths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Where(File.Exists)
            .ToArray();
        if (file_paths.Length == 0)
            return;

        var workspace_path = file_paths.FirstOrDefault(path => path.EndsWith(".gated", StringComparison.OrdinalIgnoreCase));
        if (workspace_path is not null)
            await load_workspace_with_progress(workspace_path);

        var fcs_paths = file_paths
            .Where(path => path.EndsWith(".fcs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fcs_paths.Length > 0)
            await import_fcs_files(fcs_paths, "Failed to open command line FCS files", target_group: null);
    }

    private async Task load_workspace_with_progress(string path)
    {
        if (!await view_model.TryLeavePythonScriptEditorAsync())
            return;

        try
        {
            await run_with_progress_dialog("Loading workspace ...", path, async () =>
            {
                var workspace = await Task.Run(() => new WorkspaceBinarySerializer().Load(path));
                view_model.LoadWorkspace(workspace, path);
            });
            current_workspace_path = path;
            view_model.AddRecentFilePaths([path]);
        }
        catch (Exception exception)
        {
            view_model.StatusText = $"Failed to load workspace: {exception.Message}";
            if (exception is NotSupportedException)
                await show_message_dialog("Workspace version incompatible", exception.Message);
        }
    }

    private async Task run_with_progress_dialog(string title, string subtitle, Func<Task> operation)
    {
        var dialog = new ProgressDialog(title, subtitle);
        var stopwatch = Stopwatch.StartNew();
        var dialog_task = dialog.ShowDialog(this);
        try
        {
            await Task.Yield();
            await operation();
        }
        finally
        {
            int remaining = 500 - (int)stopwatch.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(remaining);
            dialog.Close();
            await dialog_task;
        }
    }

    private void density_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SelectedPlotMode = PlotMode.Density;

    private void dotplot_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SelectedPlotMode = PlotMode.Dotplot;

    private void contour_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SelectedPlotMode = PlotMode.Contour;

    private void zebra_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SelectedPlotMode = PlotMode.Zebra;

    private void histogram_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SelectedPlotMode = PlotMode.Histogram;

    private void view_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.View;

    private void polygon_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Polygon;

    private void rectangle_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Rectangle;

    private void quadrant_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Quadrant;

    private void curly_quadrant_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.CurlyQuadrant;

    private void offset_quadrant_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.OffsetQuadrant;

    private void threshold_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Threshold;

    private void range_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Range;

    private void update_channel_menus()
    {
        populate_channel_menu(editor_x_channel_menu, view_model.AxisChoices, view_model.SelectedXAxisChoice, editor_x_channel_menu_item_click);
        populate_channel_menu(editor_y_channel_menu, view_model.AxisChoices, view_model.SelectedYAxisChoice, editor_y_channel_menu_item_click);
        populate_channel_menu(editor_color_channel_menu, view_model.ColorChoices, view_model.SelectedDotColorChoice, editor_color_channel_menu_item_click);
        populate_channel_menu(layout_x_channel_menu, view_model.SelectedPageAxisChoices, view_model.SelectedPageXAxisChoice, layout_x_channel_menu_item_click);
        populate_channel_menu(layout_y_channel_menu, view_model.SelectedPageAxisChoices, view_model.SelectedPageYAxisChoice, layout_y_channel_menu_item_click);
        populate_channel_menu(layout_color_channel_menu, view_model.SelectedPageColorChoices, view_model.SelectedPageDotColorChoice, layout_color_channel_menu_item_click);
    }

    private void editor_x_channel_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source as MenuItem)?.Tag is AxisChoice choice)
            view_model.SelectedXAxisChoice = choice;
    }

    private void editor_y_channel_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source as MenuItem)?.Tag is AxisChoice choice)
            view_model.SelectedYAxisChoice = choice;
    }

    private void editor_color_channel_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source as MenuItem)?.Tag is AxisChoice choice)
            view_model.SelectedDotColorChoice = choice;
    }

    private void layout_x_channel_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source as MenuItem)?.Tag is AxisChoice choice)
            view_model.SelectedPageXAxisChoice = choice;
    }

    private void layout_y_channel_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source as MenuItem)?.Tag is AxisChoice choice)
            view_model.SelectedPageYAxisChoice = choice;
    }

    private void layout_color_channel_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((e.Source as MenuItem)?.Tag is AxisChoice choice)
            view_model.SelectedPageDotColorChoice = choice;
    }

    private void viridis_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.DotColor.Palette = PlotColorPalette.Viridis;

    private void plasma_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.DotColor.Palette = PlotColorPalette.Plasma;

    private void turbo_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.DotColor.Palette = PlotColorPalette.Turbo;

    private void gray_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.DotColor.Palette = PlotColorPalette.Gray;

    private void layout_viridis_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        set_selected_page_palette(PlotColorPalette.Viridis);

    private void layout_plasma_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        set_selected_page_palette(PlotColorPalette.Plasma);

    private void layout_turbo_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        set_selected_page_palette(PlotColorPalette.Turbo);

    private void layout_gray_color_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        set_selected_page_palette(PlotColorPalette.Gray);

    private void set_selected_page_palette(PlotColorPalette palette)
    {
        if (view_model.SelectedPageElement is not null)
            view_model.SelectedPageElement.DotColor.Palette = palette;
    }

    private void project_tree_node_context_requested(object? sender, ProjectNodeContextRequestedEventArgs e)
    {
        var menu = build_project_tree_context_menu(e.Node);
        if (menu.Items.Count == 0)
            return;

        project_tree.ContextMenu = menu;
        menu.Open(project_tree);
    }

    private ContextMenu build_project_tree_context_menu(ProjectNode node)
    {
        var menu = new ContextMenu
        {
            DataContext = view_model,
            Placement = PlacementMode.Pointer
        };

        switch (node.Kind)
        {
            case ProjectNodeKind.Workspace:
                add_menu_items(menu,
                    command_menu_item("Rename workspace ...", view_model.RenameWorkspaceCommand),
                    command_menu_item("Create group ...", view_model.CreateGroupCommand),
                    click_menu_item("Append FCS samples ...", open_fcs_menu_item_click),
                    new Separator(),
                    command_menu_item("Expand all", view_model.ExpandProjectTreeCommand),
                    command_menu_item("Collapse all", view_model.CollapseProjectTreeCommand));
                break;

            case ProjectNodeKind.Metadata:
                add_menu_items(menu,
                    command_menu_item("Add string column ...", view_model.AddStringMetadataColumnCommand),
                    command_menu_item("Add floating point column ...", view_model.AddFloatMetadataColumnCommand),
                    command_menu_item("Add integer column ...", view_model.AddIntegerMetadataColumnCommand));
                break;

            case ProjectNodeKind.LayoutFolder:
                add_menu_items(menu,
                    command_menu_item("Create layout ...", view_model.CreateLayoutCommand));
                break;

            case ProjectNodeKind.Layout:
                add_menu_items(menu,
                    command_menu_item("Rename layout ...", view_model.RenameLayoutCommand),
                    command_menu_item("Delete layout", view_model.DeleteSelectedCommand),
                    new Separator(),
                    command_menu_item("Force refresh layout", view_model.RefreshSelectedLayoutCommand),
                    click_menu_item("Export as PNG ...", export_page_png_click),
                    click_menu_item("Export as JPG ...", export_page_jpg_click),
                    click_menu_item("Export as SVG ...", export_page_svg_click));
                break;

            case ProjectNodeKind.IntegrationJobFolder:
                add_menu_items(menu,
                    command_menu_item("Create integration", view_model.CreateIntegrationJobCommand, "Integration"),
                    command_menu_item("Create cell cycle model", view_model.CreateIntegrationJobCommand, "CellCycle"),
                    command_menu_item("Create proliferation model", view_model.CreateIntegrationJobCommand, "Proliferation"),
                    command_menu_item("Create intensity comparison", view_model.CreateIntegrationJobCommand, "IntensityComparison"));
                break;

            case ProjectNodeKind.Platform:
                add_menu_items(menu,
                    command_menu_item("Rename platform ...", view_model.RenameIntegrationJobCommand),
                    command_menu_item("Delete platform", view_model.DeleteSelectedCommand));
                break;

            case ProjectNodeKind.Group:
                add_menu_items(menu,
                    command_menu_item("Rename grouping ...", view_model.RenameGroupCommand),
                    command_menu_item("Delete grouping", view_model.DeleteSelectedCommand),
                    click_menu_item("Append FCS to grouping ...", open_fcs_menu_item_click),
                    new Separator(),
                    click_menu_item("Concatenate samples ...", concatenate_samples_menu_item_click),
                    new Separator(),
                    build_plotting_context_menu("Default plotting options"),
                    new Separator(),
                    command_menu_item("Expand all", view_model.ExpandProjectTreeCommand),
                    command_menu_item("Collapse all", view_model.CollapseProjectTreeCommand));
                break;

            case ProjectNodeKind.GateFolder:
                add_menu_items(menu,
                    build_create_gate_context_menu(),
                    build_plotting_context_menu("Default plotting options"),
                    new Separator(),
                    command_menu_item("Recalculate gating scheme", view_model.RecalculateSelectedGroupCommand),
                    new Separator(),
                    command_menu_item("Expand all", view_model.ExpandProjectTreeCommand),
                    command_menu_item("Collapse all", view_model.CollapseProjectTreeCommand));
                break;

            case ProjectNodeKind.Gate:
            {
                bool hide_population = node.Gate?.PopulationRegions.Count(region => region == PopulationRegion.Primary) == 1 &&
                    node.Gate.PopulationRegions.Count == 1;
                string population_name = node.Gate?.PopulationName(PopulationRegion.Primary) ?? "population";
                add_menu_items(menu,
                    build_create_gate_context_menu(),
                    build_statistics_context_menu(hide_population ? "Create statistics ..." : $"Create statistics ({population_name}) ..."),
                    build_plotting_context_menu(hide_population ? "Default plotting options" : $"Default plotting options ({population_name})"),
                    new Separator(),
                    command_menu_item("Recalculate gating scheme", view_model.RecalculateSelectedGateCommand),
                    command_menu_item("Recalculate statistics", view_model.RecalculateSelectedGroupCommand),
                    new Separator(),
                    command_menu_item("Rename gating strategy ...", view_model.RenameSelectedNodeCommand),
                    command_menu_item("Delete strategy", view_model.DeleteSelectedCommand),
                    new Separator(),
                    command_menu_item("Expand all", view_model.ExpandProjectTreeCommand),
                    command_menu_item("Collapse all", view_model.CollapseProjectTreeCommand));
                break;
            }

            case ProjectNodeKind.GatePopulationSlot:
            case ProjectNodeKind.Population:
                add_menu_items(menu,
                    build_create_gate_context_menu(),
                    build_statistics_context_menu("Create statistics ..."),
                    build_plotting_context_menu("Default plotting options"),
                    new Separator(),
                    command_menu_item("Copy hierarchy view options to group", view_model.CopyHierarchyViewOptionsToGroupCommand),
                    new Separator(),
                    command_menu_item("Recalculate gating scheme", view_model.RecalculateSelectedGateCommand),
                    command_menu_item("Recalculate statistics", view_model.RecalculateSelectedGroupCommand),
                    new Separator(),
                    command_menu_item("Rename population ...", view_model.RenameSelectedNodeCommand),
                    new Separator(),
                    command_menu_item("Expand all", view_model.ExpandProjectTreeCommand),
                    command_menu_item("Collapse all", view_model.CollapseProjectTreeCommand));
                break;

            case ProjectNodeKind.StatisticDefinition:
            case ProjectNodeKind.StatisticValue:
                add_menu_items(menu,
                    command_menu_item("Recalculate", view_model.RecalculateSelectedStatisticCommand),
                    new Separator(),
                    command_menu_item("Rename statistics ...", view_model.RenameSelectedNodeCommand),
                    command_menu_item("Delete statistics", view_model.DeleteSelectedCommand));
                break;

            case ProjectNodeKind.CompensationFolder:
                add_menu_items(menu,
                    command_menu_item("Create compensation ...", view_model.CreateCompensationCommand),
                    command_menu_item("Re-apply compensation", view_model.ReapplyCompensationCommand));
                break;

            case ProjectNodeKind.Compensation:
                add_menu_items(menu,
                    command_menu_item("Apply compensation", view_model.ApplyCompensationCommand),
                    new Separator(),
                    command_menu_item("Rename compensation ...", view_model.RenameSelectedNodeCommand),
                    command_menu_item("Delete compensation", view_model.DeleteSelectedCommand));
                break;

            case ProjectNodeKind.Sample:
                add_menu_items(menu,
                    build_create_gate_context_menu(),
                    build_plotting_context_menu("Default plotting options"),
                    new Separator(),
                    command_menu_item("Copy hierarchy view options to group", view_model.CopyHierarchyViewOptionsToGroupCommand),
                    new Separator(),
                    command_menu_item("Recalculate gating scheme", view_model.RecalculateSelectedGroupCommand),
                    click_menu_item("Split sample ...", split_sample_menu_item_click),
                    new Separator(),
                    command_menu_item("Expand all", view_model.ExpandProjectTreeCommand),
                    command_menu_item("Collapse all", view_model.CollapseProjectTreeCommand));
                break;

            case ProjectNodeKind.Embedding:
                add_menu_items(menu,
                    command_menu_item("Rename embedding ...", view_model.RenameSelectedNodeCommand),
                    command_menu_item("Delete embedding", view_model.DeleteSelectedCommand));
                break;
        }

        return menu;
    }

    private MenuItem build_plotting_context_menu(string header = "Plotting")
    {
        var menu = parent_menu_item(header,
            radio_click_menu_item("Density", view_model.IsDensityPlotMode, density_menu_item_click),
            radio_click_menu_item("Dotplot", view_model.IsDotplotPlotMode, dotplot_menu_item_click),
            radio_click_menu_item("Contour", view_model.IsContourPlotMode, contour_menu_item_click),
            radio_click_menu_item("Zebra", view_model.IsZebraPlotMode, zebra_menu_item_click),
            radio_click_menu_item("Histogram", view_model.IsHistogramPlotMode, histogram_menu_item_click),
            new Separator(),
            bound_check_menu_item("Show outlier points", nameof(MainWindowViewModel.ShowOutlierPoints)),
            bound_check_menu_item("Draw large dots", nameof(MainWindowViewModel.DrawLargeDots)),
            bound_check_menu_item("Show gridlines", nameof(MainWindowViewModel.ShowGridlines)),
            bound_check_menu_item("Show gate annotations", nameof(MainWindowViewModel.ShowGateAnnotations)),
            bound_check_menu_item("Show gate annotation names", nameof(MainWindowViewModel.ShowGateAnnotationNames)),
            new Separator(),
            channel_context_menu("X selected channel", view_model.AxisChoices, view_model.SelectedXAxisChoice, editor_x_channel_menu_item_click),
            channel_context_menu("Y selected channel", view_model.AxisChoices, view_model.SelectedYAxisChoice, editor_y_channel_menu_item_click),
            channel_context_menu("Dot color", view_model.ColorChoices, view_model.SelectedDotColorChoice, editor_color_channel_menu_item_click),
            bound_check_menu_item("Coloring in log scale", "DotColor.UseLogScale"),
            parent_menu_item("Dot color palette",
                click_menu_item("Viridis", viridis_color_menu_item_click),
                click_menu_item("Plasma", plasma_color_menu_item_click),
                click_menu_item("Turbo", turbo_color_menu_item_click),
                click_menu_item("Gray", gray_color_menu_item_click)),
            parent_menu_item("X axis scale",
                bound_radio_menu_item("Linear", nameof(MainWindowViewModel.IsEditorXAxisLinearScale)),
                bound_radio_menu_item("Logicle", nameof(MainWindowViewModel.IsEditorXAxisLogicleScale))),
            parent_menu_item("Y axis scale",
                bound_radio_menu_item("Linear", nameof(MainWindowViewModel.IsEditorYAxisLinearScale)),
                bound_radio_menu_item("Logicle", nameof(MainWindowViewModel.IsEditorYAxisLogicleScale))));
        menu.IsEnabled = view_model.IsDefaultAnalysisMode;
        return menu;
    }

    private MenuItem build_create_gate_context_menu()
    {
        var menu = parent_menu_item("Create gate",
            radio_click_menu_item("Polygon gate", view_model.IsPolygonTool, polygon_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Rectangle gate", view_model.IsRectangleTool, rectangle_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Quad gate", view_model.IsQuadrantTool, quadrant_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Curly quad gate", view_model.IsCurlyQuadrantTool, curly_quadrant_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Offset quad gate", view_model.IsOffsetQuadrantTool, offset_quadrant_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Threshold gate", view_model.IsThresholdTool, threshold_tool_menu_item_click, view_model.CanCreateOneDimensionalGate),
            radio_click_menu_item("Range gate", view_model.IsRangeTool, range_tool_menu_item_click, view_model.CanCreateOneDimensionalGate));
        menu.IsEnabled = view_model.IsDefaultAnalysisMode;
        return menu;
    }

    private MenuItem build_gating_context_menu(bool include_gate_management)
    {
        var menu = parent_menu_item("Gating",
            radio_click_menu_item("View", view_model.IsViewTool, view_tool_menu_item_click),
            radio_click_menu_item("Polygon gate", view_model.IsPolygonTool, polygon_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Rectangle gate", view_model.IsRectangleTool, rectangle_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Quad gate", view_model.IsQuadrantTool, quadrant_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Curly quad gate", view_model.IsCurlyQuadrantTool, curly_quadrant_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Offset quad gate", view_model.IsOffsetQuadrantTool, offset_quadrant_tool_menu_item_click, view_model.CanCreateTwoDimensionalGate),
            radio_click_menu_item("Threshold gate", view_model.IsThresholdTool, threshold_tool_menu_item_click, view_model.CanCreateOneDimensionalGate),
            radio_click_menu_item("Range gate", view_model.IsRangeTool, range_tool_menu_item_click, view_model.CanCreateOneDimensionalGate),
            new Separator(),
            command_menu_item("Merge", view_model.AddMergeGateCommand),
            command_menu_item("Exclude", view_model.AddExcludeGateCommand),
            command_menu_item("Overlap", view_model.AddOverlapGateCommand));

        if (include_gate_management)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(command_menu_item("Rename gate", view_model.RenameGateCommand));
            menu.Items.Add(command_menu_item("Delete selected gate", view_model.DeleteSelectedCommand));
        }

        menu.IsEnabled = view_model.IsDefaultAnalysisMode;
        return menu;
    }

    private MenuItem build_statistics_context_menu(string header = "Statistics")
    {
        return parent_menu_item(header,
            command_menu_item("Sum ...", view_model.AddCountStatisticCommand),
            command_menu_item("Mean ...", view_model.AddMeanStatisticCommand),
            command_menu_item("Median ...", view_model.AddMedianStatisticCommand),
            command_menu_item("Geometric mean ...", view_model.AddGeometricMeanStatisticCommand),
            command_menu_item("Coefficient of variation (CV) ...", view_model.AddCoefficientOfVariationStatisticCommand),
            command_menu_item("Standard deviation (SD) ...", view_model.AddStandardDeviationStatisticCommand),
            command_menu_item("Frequency of parent population ...", view_model.AddFrequencyOfParentStatisticCommand),
            command_menu_item("Frequency of all ...", view_model.AddFrequencyOfAllStatisticCommand),
            script_context_menu("Python statistics", view_model.StatisticScripts, apply_statistic_script_menu_item_click));
    }

    private MenuItem build_create_platform_context_menu() =>
        parent_menu_item("Create platform",
            command_menu_item("Integration", view_model.CreateIntegrationJobCommand, "Integration"),
            command_menu_item("Cell cycle", view_model.CreateIntegrationJobCommand, "CellCycle"),
            command_menu_item("Proliferation", view_model.CreateIntegrationJobCommand, "Proliferation"),
            command_menu_item("Intensity comparison", view_model.CreateIntegrationJobCommand, "IntensityComparison"));

    private MenuItem channel_context_menu(string header, IEnumerable<AxisChoice> choices, AxisChoice? selected, EventHandler<RoutedEventArgs> click)
    {
        var menu = new MenuItem { Header = header };
        string selected_name = selected?.Name ?? "";
        foreach (var choice in choices)
        {
            var item = new MenuItem
            {
                Header = create_channel_menu_header(choice),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = selected_name == choice.Name,
                Tag = choice
            };
            item.Click += click;
            menu.Items.Add(item);
        }

        return menu;
    }

    private MenuItem script_context_menu(
        string header,
        IEnumerable<gated.Services.PythonScriptDefinition> scripts,
        EventHandler<RoutedEventArgs> click)
    {
        var menu = new MenuItem { Header = header };
        var script_list = scripts.OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (script_list.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Empty", IsEnabled = false });
            return menu;
        }

        foreach (var script in script_list)
        {
            var item = new MenuItem
            {
                Header = script.Name,
                Tag = script
            };
            item.Click += click;
            menu.Items.Add(item);
        }

        return menu;
    }

    private MenuItem parent_menu_item(string header, params object[] items)
    {
        var menu = new MenuItem
        {
            Header = header,
            DataContext = view_model
        };
        foreach (var item in items)
            menu.Items.Add(item);
        return menu;
    }

    private MenuItem command_menu_item(string header, ICommand? command, object? parameter = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandParameter = parameter
        };
        if (command is null || !command.CanExecute(parameter))
            item.IsEnabled = false;
        return item;
    }

    private static MenuItem click_menu_item(string header, EventHandler<RoutedEventArgs> click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    private static MenuItem radio_click_menu_item(string header, bool is_checked, EventHandler<RoutedEventArgs> click, bool is_enabled = true)
    {
        var item = click_menu_item(header, click);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = is_checked;
        item.IsEnabled = is_enabled;
        return item;
    }

    private MenuItem bound_check_menu_item(string header, string property_path)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            DataContext = view_model
        };
        item.Bind(MenuItem.IsCheckedProperty, new Binding(property_path) { Mode = BindingMode.TwoWay });
        return item;
    }

    private MenuItem bound_radio_menu_item(string header, string property_path)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            DataContext = view_model
        };
        item.Bind(MenuItem.IsCheckedProperty, new Binding(property_path) { Mode = BindingMode.TwoWay });
        return item;
    }

    private static void add_menu_items(ContextMenu menu, params object[] items)
    {
        foreach (var item in items)
            menu.Items.Add(item);
    }

    private void populate_channel_menu(MenuItem menu, IEnumerable<AxisChoice> choices, AxisChoice? selected, EventHandler<RoutedEventArgs> click)
    {
        var choice_list = choices as IReadOnlyList<AxisChoice> ?? choices.ToArray();
        string selected_name = selected?.Name ?? "";
        if (channel_menu_states.TryGetValue(menu, out var state) &&
            state.Matches(choice_list, click))
        {
            update_channel_menu_selection(menu, selected_name);
            return;
        }

        menu.Items.Clear();
        foreach (var choice in choice_list)
        {
            var item = new MenuItem
            {
                Header = create_channel_menu_header(choice),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = selected_name == choice.Name,
                Tag = choice
            };
            item.Click += click;
            menu.Items.Add(item);
        }

        channel_menu_states[menu] = new ChannelMenuState(choice_list, click);
    }

    private static void update_channel_menu_selection(MenuItem menu, string selected_name)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is AxisChoice choice)
                item.IsChecked = selected_name == choice.Name;
        }
    }

    private static Control create_channel_menu_header(AxisChoice choice)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8
        };

        if (choice.HasLabel)
        {
            panel.Children.Add(new TextBlock
            {
                Text = choice.Label,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = choice.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178))
        });

        return panel;
    }

    private sealed class ChannelMenuState
    {
        private readonly EventHandler<RoutedEventArgs> click;
        private readonly (string Name, string Label)[] choices;

        public ChannelMenuState(IReadOnlyList<AxisChoice> choices, EventHandler<RoutedEventArgs> click)
        {
            this.click = click;
            this.choices = choices.Select(choice => (choice.Name, choice.Label)).ToArray();
        }

        public bool Matches(IReadOnlyList<AxisChoice> current_choices, EventHandler<RoutedEventArgs> current_click)
        {
            if (!ReferenceEquals(click, current_click) || choices.Length != current_choices.Count)
                return false;

            for (int index = 0; index < choices.Length; index++)
            {
                var current = current_choices[index];
                if (choices[index].Name != current.Name || choices[index].Label != current.Label)
                    return false;
            }

            return true;
        }
    }

    private void swap_axes_button_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SwapAxes();

    private void drag_over(object? sender, DragEventArgs e)
    {
        if (PageEditorView.DraggedProjectNode is not null)
            return;

        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && get_drop_target_node(e) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void drop_files(object? sender, DragEventArgs e)
    {
        if (PageEditorView.DraggedProjectNode is not null)
            return;

        e.Handled = true;
        var target_node = get_drop_target_node(e);
        if (target_node is null)
        {
            await show_message_dialog("Drop failed", "FCS files can only be dropped onto the workspace or a grouping node.");
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0)
        {
            await show_message_dialog("Drop failed", "The dropped data did not contain files.");
            return;
        }

        await import_fcs_files(
            files.Select(file => file.Path.LocalPath),
            "Failed to import dropped FCS files",
            target_node.Kind == ProjectNodeKind.Group ? target_node.Group : null);
    }

    private ProjectNode? get_drop_target_node(DragEventArgs e)
    {
        var point = e.GetPosition(project_tree);
        if (point.X < 0 || point.Y < 0 || point.X > project_tree.Bounds.Width || point.Y > project_tree.Bounds.Height)
            return null;

        var node = project_tree.GetNodeAt(point);
        return node?.Kind is ProjectNodeKind.Workspace or ProjectNodeKind.Group ? node : null;
    }

    private async Task import_fcs_files(IEnumerable<string?> paths, string failure_title, FlowGroup? target_group)
    {
        var file_paths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Where(path => path.EndsWith(".fcs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (file_paths.Length == 0)
        {
            await show_message_dialog(failure_title, "No .fcs files were provided.");
            return;
        }

        var missing_paths = file_paths.Where(path => !File.Exists(path)).ToArray();
        if (missing_paths.Length > 0)
        {
            await show_message_dialog(
                failure_title,
                $"The following file could not be found:{Environment.NewLine}{missing_paths[0]}");
            return;
        }

        try
        {
            await run_with_progress_dialog("Loading FCS files ...", fcs_import_subtitle(file_paths), async () =>
            {
                var samples = await Task.Run(() =>
                {
                    var reader = new FcsReader();
                    return file_paths.Select(file_path => reader.Read(file_path)).ToArray();
                });

                var groups = target_group is null
                    ? view_model.AddSamples(samples)
                    : view_model.AddSamplesToGroup(samples, target_group);
                await Task.Run(() => view_model.RecalculateImportedGroups(groups));
                view_model.FinishSampleImport(samples.Length);
            });
            view_model.AddRecentFilePaths(file_paths);
        }
        catch (Exception exception)
        {
            view_model.StatusText = $"{failure_title}: {exception.Message}";
            await show_message_dialog(failure_title, exception.Message);
        }
    }

    private static string fcs_import_subtitle(IReadOnlyList<string> file_paths) =>
        file_paths.Count == 1
            ? file_paths[0]
            : $"{file_paths.Count} FCS samples";

    private async Task concatenate_samples_async()
    {
        var group = view_model.SelectedGroup;
        if (group is null || group.Samples.Count == 0)
            return;

        var result = await new ConcatenateSamplesWindow(group).ShowDialog<ConcatenateSamplesResult?>(this);
        if (result is null)
            return;

        var sample = build_concatenated_sample(result);
        view_model.AddArtificialSample(group, sample, $"Concatenated {result.Sources.Count} selection(s)");
    }

    private static FlowSample build_concatenated_sample(ConcatenateSamplesResult result)
    {
        var first_sample = result.Sources[0].Sample;
        int column_count = first_sample.ChannelCount;
        var selected_rows = result.Sources
            .Select(source => (source.Sample, Indices: source.EventIndices ?? Enumerable.Range(0, source.Sample.EventCount).ToArray()))
            .ToArray();
        int row_count = selected_rows.Sum(source => source.Indices.Length);
        var raw_events = new float[row_count, column_count];
        var time_columns = first_sample.Channels
            .Where(channel => Configuration.IsTimeChannel(channel.Name))
            .Select(channel => channel.Index)
            .Where(index => index >= 0 && index < column_count)
            .ToArray();
        var joined_time_next = time_columns.ToDictionary(index => index, _ => 0f);

        int target_row = 0;
        foreach (var source in selected_rows)
        {
            var time_offsets = new Dictionary<int, float>();
            foreach (int time_column in time_columns)
            {
                float first_time = source.Indices.Length == 0 ? 0 : source.Sample.RawEvents[source.Indices[0], time_column];
                time_offsets[time_column] = result.JoinTime ? joined_time_next[time_column] - first_time : 0f;
            }

            foreach (int source_row in source.Indices)
            {
                for (int column = 0; column < column_count; column++)
                {
                    float value = source.Sample.RawEvents[source_row, column];
                    if (result.JoinTime && time_offsets.TryGetValue(column, out float offset))
                        value += offset;
                    raw_events[target_row, column] = value;
                }
                target_row++;
            }

            if (!result.JoinTime)
                continue;

            foreach (int time_column in time_columns)
            {
                if (source.Indices.Length == 0)
                    continue;
                float adjusted_maximum = source.Indices
                    .Select(index => source.Sample.RawEvents[index, time_column] + time_offsets[time_column])
                    .Max();
                joined_time_next[time_column] = adjusted_maximum + estimate_time_step(source.Sample, source.Indices, time_column);
            }
        }

        return new FlowSample(result.Name, first_sample.Channels, raw_events);
    }

    private static float estimate_time_step(FlowSample sample, IReadOnlyList<int> indices, int time_column)
    {
        float step = 1f;
        for (int index = 1; index < indices.Count; index++)
        {
            float delta = sample.RawEvents[indices[index], time_column] - sample.RawEvents[indices[index - 1], time_column];
            if (delta > 0)
                step = Math.Min(step <= 0 ? delta : step, delta);
        }
        return step <= 0 ? 1f : step;
    }

    private async Task split_sample_async()
    {
        var group = view_model.SelectedGroup;
        var sample = view_model.SelectedSample;
        if (group is null || sample is null)
            return;

        var time_channel = sample.Channels.FirstOrDefault(channel => Configuration.IsTimeChannel(channel.Name));
        if (time_channel is null)
        {
            await show_message_dialog("Split sample", "The selected sample does not contain a time channel.");
            return;
        }

        var ssc_channel = sample.Channels.FirstOrDefault(channel => Configuration.IsSscChannel(channel.Name))?.Name;
        var result = await new SplitSampleWindow(sample, time_channel.Name, ssc_channel).ShowDialog<SplitSampleResult?>(this);
        if (result is null)
            return;

        var fragments = build_split_samples(sample, time_channel.Index, result);
        if (fragments.Count == 0)
        {
            await show_message_dialog("Split sample", "No events fell inside the selected time ranges.");
            return;
        }

        view_model.AddArtificialSamples(group, fragments, $"Split sample: {sample.Name}");
    }

    private static IReadOnlyList<FlowSample> build_split_samples(FlowSample sample, int time_column, SplitSampleResult result)
    {
        var fragments = new List<FlowSample>();
        for (int fragment_index = 0; fragment_index < result.Fragments.Count; fragment_index++)
        {
            var fragment = result.Fragments[fragment_index];
            bool is_last = fragment_index == result.Fragments.Count - 1;
            var indices = Enumerable.Range(0, sample.EventCount)
                .Where(row =>
                {
                    float time = sample.RawEvents[row, time_column];
                    return time >= fragment.Start && (is_last ? time <= fragment.End : time < fragment.End);
                })
                .ToArray();
            if (indices.Length == 0)
                continue;

            var raw_events = new float[indices.Length, sample.ChannelCount];
            for (int row = 0; row < indices.Length; row++)
            for (int column = 0; column < sample.ChannelCount; column++)
                raw_events[row, column] = sample.RawEvents[indices[row], column];
            fragments.Add(new FlowSample(fragment.Name, sample.Channels, raw_events));
        }

        return fragments;
    }

    private async void export_page_png_click(object? sender, RoutedEventArgs e) =>
        await export_page_bitmap("Export page as PNG", "page.png", "png", allow_transparent_background: true);

    private async void export_page_jpg_click(object? sender, RoutedEventArgs e) =>
        await export_page_bitmap("Export page as JPEG", "page.jpg", "jpg", allow_transparent_background: false);

    private async void export_page_svg_click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export page as SVG",
            SuggestedFileName = "page.svg",
            DefaultExtension = "svg",
            FileTypeChoices =
            [
                new FilePickerFileType("SVG files") { Patterns = ["*.svg"] },
                FilePickerFileTypes.All
            ]
        });

        if (file is null)
            return;

        try
        {
            page_editor.SaveSvg(file.Path.LocalPath);
            view_model.StatusText = $"Exported page: {System.IO.Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception exception)
        {
            await show_message_dialog("Export failed", exception.Message);
        }
    }

    private async Task export_page_bitmap(string title, string suggested_name, string extension, bool allow_transparent_background)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested_name,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType($"{extension.ToUpperInvariant()} files") { Patterns = [$"*.{extension}"] },
                FilePickerFileTypes.All
            ]
        });

        if (file is null)
            return;

        try
        {
            page_editor.SaveBitmap(
                file.Path.LocalPath,
                allow_transparent_background && view_model.ExportBitmapTransparentBackground,
                view_model.ExportBitmapDpi,
                view_model.ExportBitmapApplyRasterizationResolution);
            view_model.StatusText = $"Exported page: {System.IO.Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception exception)
        {
            await show_message_dialog("Export failed", exception.Message);
        }
    }
    
    private void title_bar_pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
    
    private void window_minimize(object? sender, RoutedEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized)
        {
            this.WindowState = WindowState.Normal;
            this.window.Margin = new Thickness(0);
            return;
        }
        this.WindowState = WindowState.Minimized;
    }

    private void window_maximize(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private async void window_close(object? sender, RoutedEventArgs e)
    {
        await request_application_close_async();
    }

    private async void window_closing(object? sender, WindowClosingEventArgs e)
    {
        if (close_confirmed)
            return;

        e.Cancel = true;
        await request_application_close_async();
    }

    private async Task request_application_close_async()
    {
        if (close_prompt_active)
            return;

        if (view_model.IsPythonScriptEditorMode && view_model.IsPythonScriptDirty)
        {
            await view_model.ClosePythonScriptEditorAsync();
            if (view_model.IsPythonScriptEditorMode)
                return;
        }

        close_prompt_active = true;
        var choice = await show_workspace_exit_dialog();
        close_prompt_active = false;

        if (choice == ScriptSaveChoice.Cancel)
            return;
        if (choice == ScriptSaveChoice.Save && !await save_workspace_async())
            return;

        WindowPlacementStore.Save(this, current_project_tree_width(), current_statistics_panel_height());
        close_confirmed = true;
        var lifetime = Application.Current?.ApplicationLifetime;
        if (lifetime is ClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else this.Close();
    }

    private async Task<ScriptSaveChoice> show_workspace_exit_dialog()
    {
        var dialog = new Window
        {
            Title = "Save workspace",
            Width = 460,
            MinWidth = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Save workspace before exit?",
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = "Choose Save to write the current workspace, Discard to exit without saving, or Cancel to keep working.",
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18,
                        Foreground = new SolidColorBrush(Color.FromRgb(218, 221, 228))
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 80, IsCancel = true },
                            new Button { Content = "Discard", MinWidth = 80 },
                            new Button { Content = "Save", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[2]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(ScriptSaveChoice.Cancel);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(ScriptSaveChoice.Discard);
        ((Button)buttons[2]).Click += (_, _) => dialog.Close(ScriptSaveChoice.Save);
        return await dialog.ShowDialog<ScriptSaveChoice>(this);
    }

    private async Task<ScriptSaveChoice> show_script_save_dialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            MinWidth = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18,
                        Foreground = new SolidColorBrush(Color.FromRgb(218, 221, 228))
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 80, IsCancel = true },
                            new Button { Content = "Discard", MinWidth = 80 },
                            new Button { Content = "Save", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[2]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(ScriptSaveChoice.Cancel);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(ScriptSaveChoice.Discard);
        ((Button)buttons[2]).Click += (_, _) => dialog.Close(ScriptSaveChoice.Save);
        return await dialog.ShowDialog<ScriptSaveChoice>(this);
    }

    private async Task<string?> show_text_input_dialog(string title, string default_value)
    {
        var input = new TextBox
        {
            Text = default_value,
            MinWidth = 320
        };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            MinWidth = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 80, IsCancel = true },
                            new Button { Content = "OK", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[2]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(null);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(input.Text);
        input.AttachedToVisualTree += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<string?> show_choice_input_dialog(string title, System.Collections.Generic.IReadOnlyList<AxisChoice> choices)
    {
        var input = new ComboBox
        {
            ItemsSource = choices,
            Classes = { "Small" },
            SelectedIndex = choices.Count > 0 ? 0 : -1,
            MinWidth = 320,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<AxisChoice>((choice, _) =>
            {
                var panel = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8
                };
                if (choice is null)
                    return panel;

                if (choice.HasLabel)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = choice.Label,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    });
                }

                panel.Children.Add(new TextBlock
                {
                    Text = choice.Name,
                    Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178))
                });
                return panel;
            })
        };
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            MinWidth = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 80, IsCancel = true },
                            new Button { Content = "OK", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[2]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(null);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close((input.SelectedItem as AxisChoice)?.Name);
        input.AttachedToVisualTree += (_, _) => input.Focus();

        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<IReadOnlyList<string>?> show_multiple_choice_input_dialog(string title, System.Collections.Generic.IReadOnlyList<AxisChoice> choices)
    {
        var checks = choices.Select(choice => new CheckBox
        {
            Content = channel_content(new StatisticChannelChoice(choice.Name, choice.Label)),
            Tag = choice.Name,
            IsChecked = true,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        }).ToArray();
        var stack = new StackPanel { Spacing = 6 };
        foreach (var check in checks)
            stack.Children.Add(check);

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            MinWidth = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title },
                    new Border
                    {
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                        CornerRadius = new CornerRadius(6),
                        Background = new SolidColorBrush(Color.FromRgb(22, 22, 24)),
                        Child = new ScrollViewer
                        {
                            MaxHeight = 240,
                            Margin = new Thickness(8),
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            Content = stack
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 80, IsCancel = true },
                            new Button { Content = "OK", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[2]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(null);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(checks
            .Where(check => check.IsChecked == true)
            .Select(check => check.Tag?.ToString() ?? "")
            .Where(value => value.Length > 0)
            .ToArray());
        dialog.AttachedToVisualTree += (_, _) => checks.FirstOrDefault()?.Focus();

        return await dialog.ShowDialog<IReadOnlyList<string>?>(this);
    }

    private async Task<BooleanGateSelection?> show_boolean_gate_input_dialog(string title, System.Collections.Generic.IReadOnlyList<BooleanPopulationChoice> choices)
    {
        ComboBox make_combo() => new()
        {
            ItemsSource = choices,
            Classes = { "Small" },
            SelectedIndex = choices.Count > 0 ? 0 : -1,
            MinWidth = 320,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<BooleanPopulationChoice>((choice, _) => new TextBlock
            {
                Text = choice?.DisplayName ?? "",
                Foreground = Brushes.White
            })
        };

        var first_input = make_combo();
        var second_input = make_combo();
        if (choices.Count > 1)
            second_input.SelectedIndex = 1;

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            MinWidth = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = "First population", Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)) },
                    first_input,
                    new TextBlock { Text = "Second population", Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)) },
                    second_input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 80, IsCancel = true },
                            new Button { Content = "OK", MinWidth = 80, IsDefault = true }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[5]).Children;
        apply_small_button_classes((Control)dialog.Content);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(null);
        ((Button)buttons[1]).Click += (_, _) =>
        {
            if (first_input.SelectedItem is BooleanPopulationChoice first &&
                second_input.SelectedItem is BooleanPopulationChoice second)
                dialog.Close(new BooleanGateSelection(first, second));
            else dialog.Close(null);
        };
        first_input.AttachedToVisualTree += (_, _) => first_input.Focus();

        return await dialog.ShowDialog<BooleanGateSelection?>(this);
    }

    private async Task<bool> show_compensation_editor_dialog(CompensationMatrix compensation)
    {
        return await new CompensationEditorWindow(compensation).ShowDialog<bool>(this);
    }
}
