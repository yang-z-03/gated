using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Data;
using Avalonia.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = view_model;
        view_model.RequestTextInputAsync = show_text_input_dialog;
        view_model.RequestScriptSaveChoiceAsync = show_script_save_dialog;
        view_model.RequestChoiceInputAsync = show_choice_input_dialog;
        view_model.RequestBooleanGateInputAsync = show_boolean_gate_input_dialog;
        view_model.RequestCompensationEditorAsync = show_compensation_editor_dialog;
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
        page_editor_scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                update_page_editor_viewport_size();
        };
        AddHandler(KeyDownEvent, window_key_down, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, drag_over);
        DragDrop.AddDropHandler(this, drop_files);

        this.PropertyChanged += (s, e) => {
            if (e.Property == Window.WindowStateProperty)
            {
                var state = this.WindowState;
                if (state == WindowState.Maximized)
                    this.window.Margin = new Thickness(8);
                else this.window.Margin = new Thickness(0);
            }
        };
    }

    private void view_model_property_changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
        page_editor.ViewportSize = page_editor_scroll.Bounds.Size;
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
            execute_shortcut_command(view_model.CollapseProjectTreeCommand, e);
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
            or ProjectNodeKind.Population;

    private async void open_fcs_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await open_fcs_files_async();

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

    private async Task save_workspace_async()
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
                return;

            path = file.Path.LocalPath;
        }

        try
        {
            await run_with_progress_dialog("Saving workspace ...", path, () => Task.Run(() => new WorkspaceBinarySerializer().Save(view_model.Workspace, path)));
            view_model.StatusText = $"Saved workspace: {System.IO.Path.GetFileName(path)}";
            current_workspace_path = path;
        }
        catch (Exception exception)
        {
            view_model.StatusText = $"Failed to save workspace: {exception.Message}";
        }
    }

    private async void about_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
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
            view_model.ApplyStatisticScript(script);
        }
        catch (Exception exception)
        {
            await show_message_dialog("Failed to add Python statistic", exception.Message);
        }
    }

    private async void check_for_updates_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await check_for_updates(is_manual_check: true, suppress_connection_errors: false);
    }

    public async Task CheckForUpdatesAtStartupAsync()
    {
        await check_for_updates(is_manual_check: false, suppress_connection_errors: true);
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
            string manifest_path = await manager.DownloadUpdateAsync(update, progress);
            dialog.SetProgress("Preparing updater ...", "Gated will close and restart after extraction.", null);
            manager.LaunchUpdater(manifest_path);

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gated", "update-suppression.txt");

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

    private async void export_page_png_click(object? sender, RoutedEventArgs e) =>
        await export_page_bitmap("Export page as PNG", "page.png", "png", transparent_background: true);

    private async void export_page_jpg_click(object? sender, RoutedEventArgs e) =>
        await export_page_bitmap("Export page as JPEG", "page.jpg", "jpg", transparent_background: false);

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

    private async Task export_page_bitmap(string title, string suggested_name, string extension, bool transparent_background)
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
            page_editor.SaveBitmap(file.Path.LocalPath, transparent_background);
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
        if (view_model.IsPythonScriptEditorMode && view_model.IsPythonScriptDirty)
        {
            await view_model.ClosePythonScriptEditorAsync();
            if (view_model.IsPythonScriptEditorMode)
                return;
        }

        var lifetime = Application.Current?.ApplicationLifetime;
        if (lifetime is ClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else this.Close();
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
        var values = (float[,])compensation.Values.Clone();
        var channels = compensation.ChannelNames.ToArray();
        var group = view_model.SelectedGroup;
        var population_choices = build_compensation_preview_population_choices(group).ToArray();
        var name_box = new TextBox
        {
            Text = compensation.Name,
            MinWidth = 280,
            Margin = new Thickness(0, 4, 0, 4)
        };
        var population_box = new ComboBox
        {
            ItemsSource = population_choices,
            SelectedIndex = population_choices.Length > 0 ? 0 : -1,
            Classes = { "Small" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var preview = new CompensationPreviewView
        {
            Margin = new Thickness(0, 8, 0, 16)
        };

        double text_width = Math.Max(80, channels.DefaultIfEmpty("").Max(channel => channel.Length) * 8 + 24);
        double matrix_table_width = text_width + channels.Length * 85 + 28;
        double preview_table_width = 82 + Math.Max(0, channels.Length - 1) * 96 + 28;
        double preview_table_height = 28 + Math.Max(0, channels.Length - 1) * 96;
        double preview_viewport_height = preview_table_height + 16 <= 500 ? preview_table_height + 16 : 500;
        double dialog_width = Math.Clamp(Math.Max(matrix_table_width, preview_table_width) + 48, 520, 1300);

        var table = new Grid
        {
            RowSpacing = 5,
            ColumnSpacing = 5,
            Margin = new Thickness(0, 16, 0, 16)
        };
        table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int column = 0; column < channels.Length; column++)
            table.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(80)));
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int row = 0; row < channels.Length; row++)
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int column = 0; column < channels.Length; column++)
        {
            var header = new TextBlock
            {
                Text = channels[column],
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetColumn(header, column + 1);
            table.Children.Add(header);
        }

        var inputs = new Dictionary<(int Row, int Column), TextBox>();
        for (int row = 0; row < channels.Length; row++)
        {
            var row_header = new TextBlock
            {
                Text = channels[row],
                Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 4, 0)
            };
            Grid.SetRow(row_header, row + 1);
            table.Children.Add(row_header);

            for (int column = 0; column < channels.Length; column++)
            {
                var input = new TextBox
                {
                    Text = row == column ? "100" : format_compensation_percent(values[row, column]),
                    MinWidth = 80,
                    IsEnabled = row != column,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right
                };
                Grid.SetRow(input, row + 1);
                Grid.SetColumn(input, column + 1);
                table.Children.Add(input);
                inputs[(row, column)] = input;
            }
        }

        var error_text = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        void update_preview()
        {
            if (!try_read_compensation_inputs(inputs, channels, values, error_text, show_errors: false, out var preview_values))
                return;

            preview.Configure(group, channels, preview_values, population_box.SelectedItem as CompensationPreviewPopulationChoice);
        }

        foreach (var input in inputs.Values)
            input.GetObservable(TextBox.TextProperty).Subscribe(_ => update_preview());
        
        population_box.SelectionChanged += (_, _) => update_preview();
        update_preview();
        var matrix_scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 500,
            Content = table
        };
        var preview_scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Height = preview_viewport_height,
            Content = preview
        };
        var preview_expander = new Expander
        {
            Header = "Preview",
            IsExpanded = true,
            Margin = new Thickness(0, 12, 0, 16),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Population",
                                Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)),
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                            },
                            population_box
                        }
                    },
                    preview_scroll
                }
            }
        };

        var dialog = new Window
        {
            Title = "Compensation table editor",
            Width = dialog_width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Content = new Grid
            {
                Margin = new Thickness(16),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    new TextBlock { Text = "Name", Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)) },
                    name_box,
                    matrix_scroll,
                    preview_expander,
                    error_text,
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

        var grid = (Grid)dialog.Content;
        Grid.SetRow(name_box, 1);
        Grid.SetRow((Control)grid.Children[2], 2);
        Grid.SetRow((Control)grid.Children[3], 3);
        Grid.SetRow(error_text, 4);
        Grid.SetRow((Control)grid.Children[5], 5);

        var buttons = ((StackPanel)grid.Children[5]).Children;
        apply_small_button_classes(grid);
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(false);
        ((Button)buttons[1]).Click += (_, _) =>
        {
            if (!try_read_compensation_inputs(inputs, channels, values, error_text, show_errors: true, out var parsed_values))
                return;

            compensation.Name = string.IsNullOrWhiteSpace(name_box.Text) ? compensation.Name : name_box.Text.Trim();
            compensation.ReplaceValues(parsed_values);
            dialog.Close(true);
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private static bool try_read_compensation_inputs(
        IReadOnlyDictionary<(int Row, int Column), TextBox> inputs,
        IReadOnlyList<string> channels,
        float[,] fallback_values,
        TextBlock error_text,
        bool show_errors,
        out float[,] parsed_values)
    {
        parsed_values = (float[,])fallback_values.Clone();
        for (int row = 0; row < channels.Count; row++)
        for (int column = 0; column < channels.Count; column++)
        {
            if (row == column)
            {
                parsed_values[row, column] = 1.0f;
                continue;
            }

            string? text = inputs[(row, column)].Text;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                parsed_values[row, column] = parsed / 100.0f;
                continue;
            }

            if (show_errors)
            {
                error_text.Text = $"Invalid number at {channels[row]} / {channels[column]}.";
                error_text.IsVisible = true;
            }
            return false;
        }

        error_text.IsVisible = false;
        return true;
    }

    private static IEnumerable<CompensationPreviewPopulationChoice> build_compensation_preview_population_choices(FlowGroup? group)
    {
        yield return new CompensationPreviewPopulationChoice("All events", null, PopulationRegion.Primary);
        if (group is null)
            yield break;

        foreach (var gate in all_preview_gates(group.Gates))
        foreach (var region in gate.PopulationRegions)
            yield return new CompensationPreviewPopulationChoice(
                gate.PopulationRegions.Count == 1 ? gate.Name : $"{gate.Name}: {population_region_name(region)}",
                gate.Id,
                region);
    }

    private static IEnumerable<GateDefinition> all_preview_gates(IEnumerable<GateDefinition> gates)
    {
        foreach (var gate in gates)
        {
            yield return gate;
            foreach (var child in all_preview_gates(gate.Children))
                yield return child;
        }
    }

    private static string population_region_name(PopulationRegion region) =>
        region switch
        {
            PopulationRegion.TopRight => "Top right",
            PopulationRegion.TopLeft => "Top left",
            PopulationRegion.BottomRight => "Bottom right",
            PopulationRegion.BottomLeft => "Bottom left",
            PopulationRegion.More => "More",
            PopulationRegion.Less => "Less",
            PopulationRegion.InRange => "In range",
            PopulationRegion.BelowRange => "Below range",
            PopulationRegion.AboveRange => "Above range",
            _ => "Population"
        };

    private static string format_compensation_percent(float value)
    {
        float percent = value * 100.0f;
        if (Math.Abs(percent) < 0.0000001f)
            return "0";
        if (Math.Abs(percent - 1.0f) < 0.0000001f)
            return "1";

        return percent.ToString("F2", CultureInfo.InvariantCulture);
    }
}
