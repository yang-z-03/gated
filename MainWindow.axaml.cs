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
    private string? current_workspace_path;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = view_model;
        view_model.RequestTextInputAsync = show_text_input_dialog;
        view_model.RequestChoiceInputAsync = show_choice_input_dialog;
        view_model.RequestCompensationEditorAsync = show_compensation_editor_dialog;
        view_model.PropertyChanged += view_model_property_changed;
        view_model.AxisChoices.CollectionChanged += channel_choices_collection_changed;
        view_model.SelectedPageAxisChoices.CollectionChanged += channel_choices_collection_changed;
        update_statistics_columns();
        update_channel_menus();
        update_page_editor_viewport_size();
        page_editor_scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                update_page_editor_viewport_size();
        };
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
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedXAxisChoice)
            or nameof(MainWindowViewModel.SelectedYAxisChoice)
            or nameof(MainWindowViewModel.SelectedPageXAxisChoice)
            or nameof(MainWindowViewModel.SelectedPageYAxisChoice))
            update_channel_menus();
    }

    private void channel_choices_collection_changed(object? sender, NotifyCollectionChangedEventArgs e) =>
        update_channel_menus();

    private void update_page_editor_viewport_size()
    {
        page_editor.ViewportSize = page_editor_scroll.Bounds.Size;
    }

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

    private async void open_fcs_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
            target_group: null);
    }

    private async void open_workspace_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

    private async void save_workspace_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(UpdateDialogChoice.SuppressForOneWeek);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(UpdateDialogChoice.Cancel);
        ((Button)buttons[2]).Click += (_, _) => dialog.Close(UpdateDialogChoice.Update);
        return await dialog.ShowDialog<UpdateDialogChoice>(this);
    }

    private static string build_update_status_text(UpdateCheckResult status)
    {
        string latest_remote = status.LatestRemote?.Version.ToString() ?? "not available";
        string compatible = status.LatestCompatible?.Version.ToString() ?? "none";
        return $"Current version: {status.Current}\nMost updated remote version: {latest_remote}\nLatest compatible version: {compatible}\nCurrent OS: {status.System.DisplayName}";
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

    private void threshold_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Threshold;

    private void range_tool_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.ActiveTool = GatingTool.Range;

    private void update_channel_menus()
    {
        populate_channel_menu(editor_x_channel_menu, view_model.AxisChoices, view_model.SelectedXAxisChoice, editor_x_channel_menu_item_click);
        populate_channel_menu(editor_y_channel_menu, view_model.AxisChoices, view_model.SelectedYAxisChoice, editor_y_channel_menu_item_click);
        populate_channel_menu(layout_x_channel_menu, view_model.SelectedPageAxisChoices, view_model.SelectedPageXAxisChoice, layout_x_channel_menu_item_click);
        populate_channel_menu(layout_y_channel_menu, view_model.SelectedPageAxisChoices, view_model.SelectedPageYAxisChoice, layout_y_channel_menu_item_click);
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

    private static void populate_channel_menu(MenuItem menu, IEnumerable<AxisChoice> choices, AxisChoice? selected, EventHandler<RoutedEventArgs> click)
    {
        menu.Items.Clear();
        foreach (var choice in choices)
        {
            var item = new MenuItem
            {
                Header = create_channel_menu_header(choice),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = selected?.Name == choice.Name,
                Tag = choice
            };
            item.Click += click;
            menu.Items.Add(item);
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
            if (target_group is null)
                view_model.AddFiles(file_paths);
            else
                view_model.AddFilesToGroup(file_paths, target_group);
        }
        catch (Exception exception)
        {
            view_model.StatusText = $"{failure_title}: {exception.Message}";
            await show_message_dialog(failure_title, exception.Message);
        }
    }

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

    private void window_close(object? sender, RoutedEventArgs e)
    {
        var lifetime = Application.Current?.ApplicationLifetime;
        if (lifetime is ClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else this.Close();
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
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(null);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close((input.SelectedItem as AxisChoice)?.Name);
        input.AttachedToVisualTree += (_, _) => input.Focus();

        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> show_compensation_editor_dialog(CompensationMatrix compensation)
    {
        var values = (float[,])compensation.Values.Clone();
        var channels = compensation.ChannelNames.ToArray();
        var name_box = new TextBox
        {
            Text = compensation.Name,
            MinWidth = 280,
            Margin = new Thickness(0, 4, 0, 4)
        };

        var table = new Grid
        {
            RowSpacing = 5,
            ColumnSpacing = 5,
            Margin = new Thickness(0, 16, 0, 0)
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

        var dialog = new Window
        {
            Title = "Compensation table editor",
            Width = Math.Clamp(180 + channels.Length * 100, 520, 1100),
            Height = Math.Clamp(240 + channels.Length * 38, 360, 760),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Content = new Grid
            {
                Margin = new Thickness(16),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(new GridLength(1, GridUnitType.Star)),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    new TextBlock { Text = "Name", Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)) },
                    name_box,
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        Content = table
                    },
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
        Grid.SetRow(error_text, 3);
        Grid.SetRow((Control)grid.Children[4], 4);

        var buttons = ((StackPanel)grid.Children[4]).Children;
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(false);
        ((Button)buttons[1]).Click += (_, _) =>
        {
            for (int row = 0; row < channels.Length; row++)
            for (int column = 0; column < channels.Length; column++)
            {
                if (row == column)
                {
                    values[row, column] = 1.0f;
                    continue;
                }

                string? text = inputs[(row, column)].Text;
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    values[row, column] = parsed / 100.0f;
                    continue;
                }

                error_text.Text = $"Invalid number at {channels[row]} / {channels[column]}.";
                error_text.IsVisible = true;
                return;
            }

            compensation.Name = string.IsNullOrWhiteSpace(name_box.Text) ? compensation.Name : name_box.Text.Trim();
            compensation.ReplaceValues(values);
            dialog.Close(true);
        };

        return await dialog.ShowDialog<bool>(this);
    }

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
