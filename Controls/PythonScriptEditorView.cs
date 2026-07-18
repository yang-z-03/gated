using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using gated.Python;
using gated.ViewModels;
using TextMateSharp.Grammars;
using Avalonia.Svg.Skia;
using Avalonia.Platform;

namespace gated.Controls;

public sealed class PythonScriptEditorView : UserControl
{
    private const double TooltipApproachTolerance = 60;
    private readonly TextEditor editor;
    private readonly TextBlock output;
    private readonly ComboBox log_task_combo;
    private readonly StackPanel log_content;
    private readonly ScrollViewer log_scroll;
    private readonly ToggleButton info_toggle;
    private readonly ToggleButton warning_toggle;
    private readonly ToggleButton error_toggle;
    private readonly ToggleButton fatal_toggle;
    private readonly Button run_button;
    private readonly Button save_button;
    private readonly Button close_button;
    private readonly Canvas overlay_layer;
    private readonly Border completion_popup;
    private readonly ListBox completion_list;
    private readonly Border completion_detail;
    private readonly Border hover_popup;
    private readonly TextMate.Installation textmate;
    private MainWindowViewModel? view_model;
    private bool syncing_text;
    private int completion_request_id;
    private int completion_start_offset;
    private string completion_request_key = "";
    private bool suppress_next_completion_trigger;
    private CancellationTokenSource? hover_delay;
    private int hover_offset = -1;
    private int hover_line = -1;
    private Point hover_point;

    public PythonScriptEditorView()
    {
        editor = new TextEditor
        {
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            BorderBrush = Brushes.Transparent,
            Margin = new Thickness(16, 8, 8, 8),
            FontSize = 13,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        editor.Bind(TextBox.FontFamilyProperty, new DynamicResourceExtension("SemiFontFamilyFixed"));
        editor.TextArea.SelectionCornerRadius = 0;
        editor.LineNumbersMargin = new Thickness(12, 0, 12, 0);

        var registry_options = new RegistryOptions(ThemeName.DarkPlus);
        textmate = editor.InstallTextMate(registry_options);
        textmate.SetGrammar(registry_options.GetScopeByLanguageId("python"));

        output = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text4")),
            MinHeight = 20
        };
        log_task_combo = new ComboBox
        {
            MinWidth = 180,
            Width = 240
        };
        log_task_combo.Classes.Add("Small");
        log_content = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(8)
        };
        log_scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(4),
            Content = log_content
        };
        info_toggle = log_level_toggle("Info logs", "avares://gated/Resources/info.svg");
        warning_toggle = log_level_toggle("Warning logs", "avares://gated/Resources/warning.svg");
        error_toggle = log_level_toggle("Error logs", "avares://gated/Resources/error.svg");
        fatal_toggle = log_level_toggle("Fatal logs", "avares://gated/Resources/fail.svg");

        Image run_icon = new Avalonia.Controls.Image
        {
            Source = new SvgImage() { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/script.svg"))) },
            Width = 16, Height = 16
        };

        Image save_icon = new Avalonia.Controls.Image
        {
            Source = new SvgImage() { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/save.svg"))) },
            Width = 16, Height = 16
        };

        Image close_icon = new Avalonia.Controls.Image
        {
            Source = new SvgImage() { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri("avares://gated/Resources/delete.svg"))) },
            Width = 16, Height = 16
        };

        run_button = new Button { Content = run_icon, Padding = new Thickness(6, 2, 6, 2) };
        run_button.Classes.Add("Small");
        save_button = new Button { Content = save_icon, Padding = new Thickness(6, 2, 6, 2) };
        save_button.Classes.Add("Small");
        close_button = new Button { Content = close_icon, Padding = new Thickness(6, 2, 6, 2) };
        close_button.Classes.Add("Small");

        completion_list = new ListBox
        {
            MaxHeight = 280,
            MinWidth = 250,
            Padding = new Thickness(4),
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")),
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
            ItemTemplate = completion_item_template()
        };
        completion_list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(MarginProperty, new Thickness(0, 1)),
                new Setter(PaddingProperty, new Thickness(4, 2)),
                new Setter(BackgroundProperty, Brushes.Transparent),
                new Setter(CornerRadiusProperty, new CornerRadius(4))
            }
        });
        completion_list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover"))
        {
            Setters =
            {
                new Setter(BackgroundProperty, new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background5")))
            }
        });
        completion_list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters =
            {
                new Setter(BackgroundProperty, new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme2")))
            }
        });
        completion_detail = new Border
        {
            MinWidth = 280,
            MaxWidth = 420,
            MaxHeight = 280,
            Padding = new Thickness(8, 6, 6, 6),
            BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
            BorderThickness = new Thickness(1, 0, 0, 0),
            IsVisible = false
        };
        completion_popup = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")),
            BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border3")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 8,
                Children =
                {
                    completion_list,
                    completion_detail
                }
            }
        };
        Grid.SetColumn(completion_detail, 1);

        hover_popup = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")),
            BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border3")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            MaxWidth = 500,
            MaxHeight = 400
        };
        overlay_layer = new Canvas
        {
            ClipToBounds = true,
            IsHitTestVisible = false,
            Children =
            {
                hover_popup,
                completion_popup
            }
        };

        Content = build_layout();

        editor.TextChanged += editor_text_changed;
        editor.AddHandler(KeyDownEvent, editor_key_down, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.TextArea.AddHandler(KeyDownEvent, editor_key_down, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.KeyUp += editor_key_up;
        editor.TextArea.TextEntered += editor_text_entered;
        editor.TextArea.TextEntering += editor_text_entering;
        editor.TextArea.TextView.PointerMoved += editor_pointer_moved;
        editor.TextArea.TextView.PointerExited += editor_pointer_exited;
        editor.TextArea.TextView.AddHandler(PointerPressedEvent, editor_pointer_pressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        overlay_layer.PointerMoved += overlay_pointer_moved;
        overlay_layer.PointerExited += overlay_pointer_exited;
        completion_list.Tapped += (_, _) => run_completion_ui_action(insert_selected_completion);
        completion_list.SelectionChanged += (_, _) => run_completion_ui_action(update_completion_detail);
        run_button.Click += (_, _) => execute_run_command();
        save_button.Click += (_, _) => view_model?.SavePythonScript();
        close_button.Click += async (_, _) =>
        {
            if (view_model is not null)
                await view_model.ClosePythonScriptEditorAsync();
        };
        log_task_combo.SelectionChanged += log_task_combo_selection_changed;
        info_toggle.IsCheckedChanged += (_, _) => update_log_filter(toggle => view_model!.ShowPythonInfoLogs = toggle, info_toggle);
        warning_toggle.IsCheckedChanged += (_, _) => update_log_filter(toggle => view_model!.ShowPythonWarningLogs = toggle, warning_toggle);
        error_toggle.IsCheckedChanged += (_, _) => update_log_filter(toggle => view_model!.ShowPythonErrorLogs = toggle, error_toggle);
        fatal_toggle.IsCheckedChanged += (_, _) => update_log_filter(toggle => view_model!.ShowPythonFatalLogs = toggle, fatal_toggle);
        DataContextChanged += (_, _) => bind_view_model(DataContext as MainWindowViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            if (view_model is not null)
                sync_from_view_model(view_model);
            editor.Focus();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            close_completion_popup();
            close_hover_popup();
        };
    }

    private static ToggleButton log_level_toggle(string tooltip, string icon_uri)
    {
        var icon = new Avalonia.Controls.Image
        {
            Source = new SvgImage { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri(icon_uri))) },
            Width = 14,
            Height = 14
        };
        var toggle = new ToggleButton
        {
            Content = icon,
            IsChecked = true,
            Padding = new Thickness(0),
            Width = 28,
            Height = 24,
            MinWidth = 0
        };
        toggle.Classes.Add("Small");
        toggle.Classes.Add("LogLevelToggle");
        ToolTip.SetTip(toggle, tooltip);
        return toggle;
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        close_completion_popup();
        close_hover_popup();
        textmate.Dispose();
    }

    private Control build_layout()
    {
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto)
            },
            Margin = new Thickness(5, 5),
        };
        var name_panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                log_task_combo
            }
        };
        header.Children.Add(name_panel);
        var command_panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 5,
            Children = { info_toggle, warning_toggle, error_toggle, fatal_toggle, run_button, save_button, close_button }
        };
        Grid.SetColumn(command_panel, 2);
        header.Children.Add(command_panel);

        var editor_host = new Grid
        {
            ClipToBounds = true,
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = editor
                },
                overlay_layer
            }
        };

        var log_panel = new Border
        {
            BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")),
            ClipToBounds = true,
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(new GridLength(1, GridUnitType.Star))
                },
                Children =
                {
                    new Border
                    {
                        BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = header
                    },
                    log_scroll
                }
            }
        };
        Grid.SetRow((Control)((Grid)log_panel.Child).Children[1], 1);

        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions =
            {
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
                new RowDefinition(new GridLength(10)),
                new RowDefinition(new GridLength(240))
            },
            Children =
            {
                editor_host,
                new GridSplitter
                {
                    Height = 10,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    ResizeDirection = GridResizeDirection.Rows
                },
                log_panel
            }
        };
        Grid.SetRow((Control)grid.Children[0], 0);
        Grid.SetRow((Control)grid.Children[1], 1);
        Grid.SetRow((Control)grid.Children[2], 2);
        return grid;
    }

    private static IDataTemplate completion_item_template() =>
        new FuncDataTemplate<PythonCompletionData>((item, _) =>
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                    new ColumnDefinition(GridLength.Auto)
                },
                MinWidth = 250,
                ColumnSpacing = 18
            };

            grid.Children.Add(new TextBlock
            {
                Text = item?.Text ?? "",
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 0)
            });

            var type = new TextBlock
            {
                Text = item?.Type ?? "",
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text4")),
                FontSize = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetColumn(type, 1);
            grid.Children.Add(type);
            return grid;
        });

    private void bind_view_model(MainWindowViewModel? next)
    {
        if (ReferenceEquals(view_model, next))
            return;
        if (view_model is not null)
            view_model.PropertyChanged -= view_model_property_changed;

        view_model = next;
        if (view_model is not null)
        {
            view_model.PropertyChanged += view_model_property_changed;
            sync_from_view_model(view_model);
        }
    }

    private void view_model_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (view_model is null)
            return;

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedPythonLogTask)
            or nameof(MainWindowViewModel.PythonLogTasks)
            or nameof(MainWindowViewModel.ShowPythonInfoLogs)
            or nameof(MainWindowViewModel.ShowPythonWarningLogs)
            or nameof(MainWindowViewModel.ShowPythonErrorLogs)
            or nameof(MainWindowViewModel.ShowPythonFatalLogs))
        {
            sync_log_controls(view_model);
            update_log_view(view_model);
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.PythonScriptText)
            or nameof(MainWindowViewModel.PythonScriptOutput)
            or nameof(MainWindowViewModel.IsPythonScriptRunning)
            or nameof(MainWindowViewModel.PythonScriptName)
            or nameof(MainWindowViewModel.PythonScriptFileName)
            or nameof(MainWindowViewModel.IsPythonScriptDirty)
            or nameof(MainWindowViewModel.CanSavePythonScript)
            or nameof(MainWindowViewModel.SelectedPythonLogTask))
            sync_from_view_model(view_model);
    }

    private void sync_from_view_model(MainWindowViewModel model)
    {
        if (editor.Text != model.PythonScriptText)
        {
            syncing_text = true;
            editor.Text = model.PythonScriptText;
            editor.CaretOffset = editor.Text?.Length ?? 0;
            syncing_text = false;
        }

        sync_log_controls(model);
        update_log_view(model);
        output.Text = model.PythonScriptOutput;
        output.Foreground = model.PythonScriptOutput.StartsWith("Completed", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(gated.Shared.ThemeResources.AppColor("SuccessText"))
            : model.PythonScriptOutput.StartsWith("Running", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text4"))
                : Brushes.IndianRed;
        run_button.IsEnabled = !model.IsPythonScriptRunning;
        save_button.IsEnabled = model.CanSavePythonScript;
    }

    private void sync_log_controls(MainWindowViewModel model)
    {
        syncing_text = true;
        try
        {
            log_task_combo.ItemsSource = model.PythonLogTasks;
            if (!ReferenceEquals(log_task_combo.SelectedItem, model.SelectedPythonLogTask))
                log_task_combo.SelectedItem = model.SelectedPythonLogTask;
            info_toggle.IsChecked = model.ShowPythonInfoLogs;
            warning_toggle.IsChecked = model.ShowPythonWarningLogs;
            error_toggle.IsChecked = model.ShowPythonErrorLogs;
            fatal_toggle.IsChecked = model.ShowPythonFatalLogs;
        }
        finally
        {
            syncing_text = false;
        }
    }

    private void update_log_view(MainWindowViewModel model)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => update_log_view(model));
            return;
        }

        log_content.Children.Clear();
        var task = model.SelectedPythonLogTask;
        if (task is not null)
        {
            foreach (var run in task.Runs)
            {
                log_content.Children.Add(run_separator(run));
                foreach (var message in run.Messages.Where(message => log_level_visible(model, message.Level)))
                    log_content.Children.Add(log_message_view(message));
            }
        }
        log_content.InvalidateVisual();
        log_scroll.InvalidateVisual();
        log_scroll.ScrollToEnd();
    }

    private static bool log_level_visible(MainWindowViewModel model, PythonLogLevel level) =>
        level switch
        {
            PythonLogLevel.Info => model.ShowPythonInfoLogs,
            PythonLogLevel.Warning => model.ShowPythonWarningLogs,
            PythonLogLevel.Error => model.ShowPythonErrorLogs,
            PythonLogLevel.Fatal => model.ShowPythonFatalLogs,
            _ => true
        };

    private static Control run_separator(PythonLogRun run)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star))
            },
            ColumnSpacing = 10,
            Margin = new Thickness(0, 6, 0, 2)
        };
        grid.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var title = new TextBlock
        {
            Text = $"Run {run.Index}  {run.StartedAt:HH:mm:ss}",
            FontSize = 11,
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text4"))
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var right = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private static Control log_message_view(PythonLogEntry message)
    {
        var color = message.Level switch
        {
            PythonLogLevel.Warning => gated.Shared.ThemeResources.AppColor("WarningText"),
            PythonLogLevel.Error => gated.Shared.ThemeResources.AppColor("DangerText"),
            PythonLogLevel.Fatal => gated.Shared.ThemeResources.AppColor("DangerTextStrong"),
            _ => gated.Shared.ThemeResources.AppColor("Text3")
        };
        var text = new TextBlock
        {
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            FontSize = 12,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap
        };
        text.Text = message.Text;
        return text;
    }

    private void editor_text_changed(object? sender, EventArgs e)
    {
        if (syncing_text || view_model is null)
            return;
        view_model.PythonScriptText = editor.Text ?? "";
    }

    private void log_task_combo_selection_changed(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing_text || view_model is null)
            return;
        view_model.SelectedPythonLogTask = log_task_combo.SelectedItem as PythonLogTask;
        update_log_view(view_model);
    }

    private void update_log_filter(Action<bool> setter, ToggleButton toggle)
    {
        if (syncing_text || view_model is null)
            return;
        setter(toggle.IsChecked == true);
        update_log_view(view_model);
    }

    private void editor_key_down(object? sender, KeyEventArgs e)
    {
        if (has_real_completion_items())
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                move_completion_selection(1);
                suppress_next_completion_trigger = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                move_completion_selection(-1);
                suppress_next_completion_trigger = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (selected_completion_would_not_change_text())
                {
                    close_completion_popup();
                    suppress_next_completion_trigger = true;
                    return;
                }
                e.Handled = true;
                insert_selected_completion();
                suppress_next_completion_trigger = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                close_completion_popup();
                return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            execute_run_command();
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Space)
        {
            e.Handled = true;
            start_completion(editor.TextArea);
        }
    }

    private void editor_key_up(object? sender, KeyEventArgs e)
    {
        try
        {
            if (suppress_next_completion_trigger)
            {
                suppress_next_completion_trigger = false;
                return;
            }

            if (e.KeyModifiers != KeyModifiers.None)
                return;

            int offset = editor.TextArea.Caret.Offset;
            string text = editor.TextArea.Document.Text;
            if (offset <= 0 || offset > text.Length)
                return;
            if (is_inside_python_string(text, offset))
                return;

            char previous = text[offset - 1];
            if (previous == '.' || char.IsLetter(previous) || previous == '_')
                start_completion(editor.TextArea);
        }
        catch (Exception)
        {
            close_completion_after_error();
        }
    }

    private void execute_run_command()
    {
        if (view_model?.RunPythonScriptCommand.CanExecute(null) == true)
            view_model.RunPythonScriptCommand.Execute(null);
    }

    private void editor_text_entering(object? sender, TextInputEventArgs e)
    {
        try
        {
            if (!has_real_completion_items() || e.Text is not { Length: > 0 } text)
                return;

            if (text == ".")
            {
                if (selected_completion_would_not_change_text())
                {
                    close_completion_popup();
                    return;
                }
                insert_selected_completion();
            }
            else if (!char.IsLetterOrDigit(text[0]) && text[0] != '_')
            {
                close_completion_popup();
                suppress_next_completion_trigger = true;
            }
        }
        catch (Exception)
        {
            close_completion_after_error();
        }
    }

    private void editor_text_entered(object? sender, TextInputEventArgs e)
    {
        try
        {
            if (sender is not TextArea text_area)
                return;

            if (is_inside_python_string(text_area.Document.Text, text_area.Caret.Offset))
                return;

            if (e.Text == "." || (e.Text is { Length: 1 } text && (char.IsLetter(text[0]) || text[0] == '_')))
                start_completion(text_area);
        }
        catch (Exception)
        {
            close_completion_after_error();
        }
    }

    private void start_completion(TextArea text_area)
    {
        _ = show_completion_async(text_area);
    }

    private async Task show_completion_async(TextArea text_area)
    {
        try
        {
            string code = text_area.Document.Text;
            int offset = text_area.Caret.Offset;
            if (is_inside_python_string(code, offset))
            {
                close_completion_popup();
                return;
            }

            string request_key = $"{offset}:{code.GetHashCode(StringComparison.Ordinal)}";
            if (completion_popup.IsVisible && request_key == completion_request_key)
                return;

            completion_request_key = request_key;
            int request_id = ++completion_request_id;
            completion_start_offset = completion_start(text_area.Document, offset);
            var location = document_location(text_area.Document, offset);

            close_hover_popup();

            var items = await Task.Run(() => completion_items(code, location.Line, location.Column).ToArray());
            if (request_id != completion_request_id)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (request_id != completion_request_id)
                    return;
                if (items.Length == 0)
                {
                    close_completion_popup();
                    return;
                }

                reset_completion_list();
                completion_list.ItemsSource = items;
                completion_list.SelectedIndex = 0;
                update_completion_detail();
                position_completion_popup(offset);
                completion_popup.IsVisible = true;
                completion_list.InvalidateMeasure();
                completion_list.InvalidateArrange();
                completion_list.ScrollIntoView(items[0]);
            });
        }
        catch (Exception)
        {
            await close_completion_after_error_async();
        }
    }

    private static IEnumerable<PythonCompletionData> completion_items(string code, int line, int column)
    {
        return PythonExtensionRuntime.CompletePython(code, line, column)
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !item.Name.StartsWith('_') && item.Type is not "path" and not "file")
            .Select(item => new PythonCompletionData(item))
            .OrderBy(item => item.Text, StringComparer.OrdinalIgnoreCase);
    }

    private static int completion_start(TextDocument document, int offset)
    {
        offset = Math.Clamp(offset, 0, document.TextLength);
        int start = offset;
        string text = document.Text;
        while (start > 0)
        {
            char value = text[start - 1];
            if (!char.IsLetterOrDigit(value) && value != '_')
                break;
            start--;
        }
        return start;
    }

    private void move_completion_selection(int delta)
    {
        run_completion_ui_action(() =>
        {
            int count = completion_list.ItemCount;
            if (count == 0)
                return;
            int current = completion_list.SelectedIndex < 0 ? 0 : completion_list.SelectedIndex;
            int next = (current + delta) % count;
            if (next < 0)
                next += count;
            completion_list.SelectedIndex = next;
            update_completion_detail();
            if (completion_list.SelectedItem is not null)
                completion_list.ScrollIntoView(completion_list.SelectedItem);
        });
    }

    private void insert_selected_completion()
    {
        run_completion_ui_action(() =>
        {
            if (!completion_popup.IsVisible || completion_list.SelectedItem is not PythonCompletionData item)
                return;

            int offset = editor.TextArea.Caret.Offset;
            int length = Math.Max(0, offset - completion_start_offset);
            editor.Document.Replace(completion_start_offset, length, item.InsertionText);
            editor.CaretOffset = completion_start_offset + item.InsertionText.Length;
            close_completion_popup();
        });
    }

    private bool selected_completion_would_not_change_text()
    {
        try
        {
            if (!completion_popup.IsVisible || completion_list.SelectedItem is not PythonCompletionData item)
                return false;
            int offset = editor.TextArea.Caret.Offset;
            int length = Math.Max(0, offset - completion_start_offset);
            if (completion_start_offset < 0 || completion_start_offset + length > editor.Document.TextLength)
                return false;
            string current = editor.Document.Text.Substring(completion_start_offset, length);
            return current == item.InsertionText;
        }
        catch (Exception)
        {
            close_completion_after_error();
            return false;
        }
    }

    private bool completion_has_empty_prefix()
    {
        try
        {
            int offset = editor.TextArea.Caret.Offset;
            return completion_start_offset >= 0 && offset <= editor.Document.TextLength && offset <= completion_start_offset;
        }
        catch (Exception)
        {
            close_completion_after_error();
            return false;
        }
    }

    private void close_completion_popup(bool invalidate_request = true)
    {
        if (invalidate_request)
            completion_request_id++;
        completion_request_key = "";
        completion_popup.IsVisible = false;
        reset_completion_list();
        completion_detail.Child = null;
        completion_detail.IsVisible = false;
        if (!hover_popup.IsVisible)
            overlay_layer.IsHitTestVisible = false;
    }

    private void reset_completion_list()
    {
        completion_list.SelectedIndex = -1;
        completion_list.ItemsSource = null;
        completion_list.InvalidateMeasure();
        completion_list.InvalidateArrange();
    }

    private bool has_real_completion_items() =>
        completion_popup.IsVisible && completion_list.ItemsSource is not null && completion_list.ItemCount > 0;

    private void run_completion_ui_action(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            close_completion_after_error();
        }
    }

    private async Task close_completion_after_error_async()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                close_completion_popup();
            else
                await Dispatcher.UIThread.InvokeAsync(() => close_completion_popup());
        }
        catch (Exception)
        {
        }
    }

    private void close_completion_after_error()
    {
        try
        {
            close_completion_popup();
        }
        catch (Exception)
        {
        }
    }

    private void position_completion_popup(int offset)
    {
        var point = caret_point(offset);
        place_overlay_near_point(completion_popup, point, prefer_below: true);
    }

    private void update_completion_detail()
    {
        run_completion_ui_action(() =>
        {
            if (completion_list.SelectedItem is not PythonCompletionData item)
            {
                completion_detail.Child = null;
                completion_detail.IsVisible = false;
                return;
            }

            completion_detail.Child = build_completion_detail_view(item, completion_detail);
            completion_detail.IsVisible = true;
        });
    }

    private Point caret_point(int offset)
    {
        var location = document_location(editor.Document, offset);
        double line_height = Math.Max(16, editor.FontSize * 1.45);
        double character_width = Math.Max(7, editor.FontSize * 0.62);
        double line_number_margin = editor.ShowLineNumbers ? 52 : 12;
        var scroll = editor.TextArea.TextView.ScrollOffset;
        double x = line_number_margin + location.Column * character_width - scroll.X;
        double y = location.Line * line_height - scroll.Y + 4;
        return new Point(x, y);
    }

    private void place_overlay_near_point(Control control, Point anchor, bool prefer_below)
    {
        const double margin = 8;
        const double gap = 8;

        double overlay_width = Math.Max(0, overlay_layer.Bounds.Width);
        double overlay_height = Math.Max(0, overlay_layer.Bounds.Height);
        if (overlay_width <= margin * 2 || overlay_height <= margin * 2)
            return;

        double max_width = Math.Max(120, overlay_width - margin * 2);
        if (!double.IsInfinity(control.MaxWidth) && control.MaxWidth > 0)
            max_width = Math.Min(max_width, control.MaxWidth);
        double space_below = Math.Max(0, overlay_height - anchor.Y - gap - margin);
        double space_above = Math.Max(0, anchor.Y - gap - margin);
        bool place_below = prefer_below
            ? space_below >= 120 || space_below >= space_above
            : !(space_above >= 120 || space_above >= space_below);
        double max_height = Math.Max(80, place_below ? space_below : space_above);
        if (!double.IsInfinity(control.MaxHeight) && control.MaxHeight > 0)
            max_height = Math.Min(max_height, control.MaxHeight);

        control.MaxWidth = max_width;
        control.MaxHeight = max_height;
        control.Measure(new Size(max_width, max_height));

        Size desired = control.DesiredSize;
        double popup_width = Math.Min(max_width, Math.Max(control.Bounds.Width, desired.Width));
        double popup_height = Math.Min(max_height, Math.Max(control.Bounds.Height, desired.Height));
        if (popup_width <= 0)
            popup_width = Math.Min(max_width, 320);
        if (popup_height <= 0)
            popup_height = Math.Min(max_height, 180);

        double left_limit = margin;
        double right_limit = Math.Max(margin, overlay_width - popup_width - margin);
        double x = Math.Clamp(anchor.X, left_limit, right_limit);
        double y = place_below
            ? anchor.Y + gap
            : anchor.Y - gap - popup_height;
        double bottom_limit = Math.Max(margin, overlay_height - popup_height - margin);
        y = Math.Clamp(y, margin, bottom_limit);

        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
    }

    private void close_tooltip_windows()
    {
        close_completion_popup();
        close_hover_popup();
    }

    private void editor_pointer_pressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (!completion_popup.IsVisible && !hover_popup.IsVisible)
                return;

            close_tooltip_windows();
            e.Handled = true;
        }
        catch (Exception)
        {
            close_completion_after_error();
            close_hover_after_error();
        }
    }

    private void editor_pointer_exited(object? sender, PointerEventArgs e)
    {
        try
        {
            if (hover_popup.IsVisible && is_point_inside_overlay_child(e.GetPosition(overlay_layer), hover_popup, tolerance: TooltipApproachTolerance))
                return;
            hover_delay?.Cancel();
            hover_delay = null;
            hover_offset = -1;
            close_hover_popup();
        }
        catch (Exception)
        {
            close_hover_after_error();
        }
    }

    private void editor_pointer_moved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (sender is not AvaloniaEdit.Rendering.TextView text_view)
                return;

            var position = text_view.GetPositionFloor(e.GetPosition(text_view) + text_view.ScrollOffset);
            if (!position.HasValue)
                return;

            var overlay_point = e.GetPosition(overlay_layer);
            if (is_point_inside_visible_overlay_child(overlay_point, tolerance: TooltipApproachTolerance))
            {
                overlay_layer.IsHitTestVisible = true;
                return;
            }

            overlay_layer.IsHitTestVisible = false;

            int line = position.Value.Location.Line;
            if (hover_popup.IsVisible && hover_line >= 0 && line != hover_line)
            {
                hover_delay?.Cancel();
                hover_offset = -1;
                hover_line = -1;
                close_hover_popup();
            }

            hover_point = e.GetPosition(overlay_layer);
            int offset = text_view.Document.GetOffset(position.Value.Location);
            if (offset == hover_offset)
                return;
            close_hover_popup();
            hover_offset = offset;
            hover_line = line;
            hover_delay?.Cancel();
            hover_delay = new CancellationTokenSource();
            _ = show_hover_after_delay_async(text_view, offset, hover_delay.Token);
        }
        catch (Exception)
        {
            close_hover_after_error();
        }
    }

    private void overlay_pointer_moved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(overlay_layer);
        if (is_point_inside_visible_overlay_child(point, tolerance: TooltipApproachTolerance))
        {
            overlay_layer.IsHitTestVisible = true;
            return;
        }

        overlay_layer.IsHitTestVisible = false;
    }

    private void overlay_pointer_exited(object? sender, PointerEventArgs e)
    {
        overlay_layer.IsHitTestVisible = false;
    }

    private async Task show_hover_after_delay_async(AvaloniaEdit.Rendering.TextView text_view, int offset, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);

            var location = document_location(text_view.Document, offset);
            int line = location.Line;
            string code = text_view.Document.Text;
            var hover = await Task.Run(() => PythonExtensionRuntime.GetPythonHoverInfo(code, location.Line, location.Column), token);
            if (token.IsCancellationRequested || hover_offset != offset || hover_line != line || completion_popup.IsVisible || hover is null || hover.Type is "keyword")
            {
                await Dispatcher.UIThread.InvokeAsync(close_hover_popup);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || hover_offset != offset || hover_line != line)
                    return;
                hover_popup.Child = build_hover_title_view(hover, hover_popup);
                place_overlay_near_point(hover_popup, new Point(hover_point.X + 14, hover_point.Y + 8), prefer_below: true);
                hover_popup.IsVisible = true;
            });

            await Task.Yield();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || hover_offset != offset || hover_line != line || completion_popup.IsVisible)
                    return;
                hover_popup.Child = build_documentation_view(hover, hover_popup);
                place_overlay_near_point(hover_popup, new Point(hover_point.X + 14, hover_point.Y + 8), prefer_below: true);
                hover_popup.IsVisible = true;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await close_hover_after_error_async();
        }
    }

    private void close_hover_popup()
    {
        hover_popup.IsVisible = false;
        hover_popup.Child = null;
        hover_offset = -1;
        hover_line = -1;
        if (!completion_popup.IsVisible)
            overlay_layer.IsHitTestVisible = false;
    }

    private async Task close_hover_after_error_async()
    {
        try
        {
            hover_delay?.Cancel();
            hover_delay = null;
            if (Dispatcher.UIThread.CheckAccess())
                close_hover_popup();
            else
                await Dispatcher.UIThread.InvokeAsync(close_hover_popup);
        }
        catch (Exception)
        {
        }
    }

    private void close_hover_after_error()
    {
        try
        {
            hover_delay?.Cancel();
            hover_delay = null;
            close_hover_popup();
        }
        catch (Exception)
        {
        }
    }

    private bool is_point_inside_visible_overlay_child(Point point, double tolerance = 0) =>
        is_point_inside_overlay_child(point, completion_popup, tolerance) || is_point_inside_overlay_child(point, hover_popup, tolerance);

    private static bool is_point_inside_overlay_child(Point point, Control child, double tolerance = 0)
    {
        if (!child.IsVisible)
            return false;
        double left = Canvas.GetLeft(child);
        double top = Canvas.GetTop(child);
        if (double.IsNaN(left))
            left = 0;
        if (double.IsNaN(top))
            top = 0;
        double width = Math.Max(child.Bounds.Width, child.DesiredSize.Width);
        double height = Math.Max(child.Bounds.Height, child.DesiredSize.Height);
        return new Rect(
            left - tolerance,
            top - tolerance,
            width + tolerance * 2,
            height + tolerance * 2).Contains(point);
    }

    private static Typeface current_typeface(Control control) =>
        new(
            TextElement.GetFontFamily(control),
            TextElement.GetFontStyle(control),
            TextElement.GetFontWeight(control),
            TextElement.GetFontStretch(control));

    private static Typeface current_typeface_bolded(Control control) =>
        new(
            TextElement.GetFontFamily(control),
            TextElement.GetFontStyle(control),
            FontWeight.Bold,
            TextElement.GetFontStretch(control));

    private static Control build_documentation_view(PythonHoverItem item, Control parent)
    {
        var panel = new StackPanel { Spacing = 8 };
        string title = string.IsNullOrWhiteSpace(item.Title)
            ? item.Type
            : item.Title;
        if (!string.IsNullOrWhiteSpace(title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                LineHeight = 18,
                FontFamily = string.IsNullOrWhiteSpace(item.Signature) ?
                    current_typeface(parent).FontFamily :
                    FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var block in parse_documentation(item))
            panel.Children.Add(block);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
    }

    private static Control build_completion_detail_view(PythonCompletionData item, Control parent)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = item.DisplayTitle,
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        });

        string documentation = string.IsNullOrWhiteSpace(item.Documentation)
            ? item.Detail
            : item.Documentation;
        foreach (var block in parse_docstring(documentation))
            panel.Children.Add(block);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
    }

    private static Control build_hover_title_view(PythonHoverItem item, Control parent) => new TextBlock
    {
        Text = string.IsNullOrWhiteSpace(item.Title) ? item.Type : item.Title,
        FontFamily = string.IsNullOrWhiteSpace(item.Signature)
            ? current_typeface(parent).FontFamily
            : FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
        TextWrapping = TextWrapping.Wrap
    };

    private static IEnumerable<Control> parse_documentation(PythonHoverItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Documentation))
            return parse_docstring(item.Documentation);

        if (!string.IsNullOrWhiteSpace(item.DocumentationBlocksJson))
        {
            IEnumerable<Control>? parsed = parse_structured_documentation(item.DocumentationBlocksJson);
            if (parsed is not null)
                return parsed;
        }

        return parse_docstring(item.Documentation);
    }

    private static IEnumerable<Control>? parse_structured_documentation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
                return null;

            var controls = new List<Control>();
            foreach (var block in document.RootElement.EnumerateArray())
            {
                string kind = json_text(block, "kind");
                switch (kind)
                {
                    case "section":
                        string title = json_text(block, "title");
                        if (!string.IsNullOrWhiteSpace(title))
                            controls.Add(heading_block(title));
                        break;
                    case "paragraph":
                        string paragraph = json_text(block, "text");
                        if (!string.IsNullOrWhiteSpace(paragraph))
                            controls.AddRange(parse_docstring(paragraph));
                        break;
                    case "code":
                        string code = json_text(block, "text");
                        if (!string.IsNullOrWhiteSpace(code))
                            controls.Add(code_block(code));
                        break;
                    case "list":
                        if (block.TryGetProperty("items", out var items) && items.ValueKind is JsonValueKind.Array)
                            controls.Add(list_block(items));
                        break;
                    case "raw":
                        string raw = json_text(block, "text");
                        if (!string.IsNullOrWhiteSpace(raw))
                            controls.AddRange(parse_docstring(raw));
                        break;
                }
            }

            return controls;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Control> parse_docstring(string documentation)
    {
        var lines = normalized_doc_lines(documentation);
        var paragraph = new List<string>();
        var code = new List<string>();
        var list = new List<(string Label, string Text)>();
        var definition_table = new List<(string Term, string Description)>();

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (is_numpy_heading(lines, index))
            {
                foreach (var block in flush_code(code))
                    yield return block;
                foreach (var block in flush_paragraph(paragraph))
                    yield return block;
                foreach (var block in flush_list(list))
                    yield return block;
                foreach (var block in flush_definition_table(definition_table))
                    yield return block;
                yield return heading_block(line.Trim());
                index++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                foreach (var block in flush_code(code))
                    yield return block;
                foreach (var block in flush_paragraph(paragraph))
                    yield return block;
                foreach (var block in flush_list(list))
                    yield return block;
                foreach (var block in flush_definition_table(definition_table))
                    yield return block;
                continue;
            }

            if (code.Count > 0)
            {
                code.Add(line.TrimEnd());
                continue;
            }

            if (try_parse_definition_item(lines, index, out var term, out var description, out int consumed))
            {
                foreach (var block in flush_code(code))
                    yield return block;
                foreach (var block in flush_paragraph(paragraph))
                    yield return block;
                foreach (var block in flush_list(list))
                    yield return block;
                definition_table.Add((term, description));
                index += consumed - 1;
            }
            else if (try_parse_list_item(line, out var label, out var text))
            {
                foreach (var block in flush_code(code))
                    yield return block;
                foreach (var block in flush_paragraph(paragraph))
                    yield return block;
                foreach (var block in flush_definition_table(definition_table))
                    yield return block;
                list.Add((label, text));
            }
            else if (looks_like_code(line))
            {
                foreach (var block in flush_paragraph(paragraph))
                    yield return block;
                foreach (var block in flush_list(list))
                    yield return block;
                foreach (var block in flush_definition_table(definition_table))
                    yield return block;
                code.Add(line.TrimEnd());
            }
            else
            {
                foreach (var block in flush_code(code))
                    yield return block;
                foreach (var block in flush_list(list))
                    yield return block;
                foreach (var block in flush_definition_table(definition_table))
                    yield return block;
                paragraph.Add(line.Trim());
            }
        }

        foreach (var block in flush_code(code))
            yield return block;
        foreach (var block in flush_paragraph(paragraph))
            yield return block;
        foreach (var block in flush_list(list))
            yield return block;
        foreach (var block in flush_definition_table(definition_table))
            yield return block;
    }

    private static bool is_numpy_heading(string[] lines, int index)
    {
        if (index + 1 >= lines.Length)
            return false;
        string title = lines[index].Trim();
        string underline = lines[index + 1].Trim();
        return title.Length > 0
            && underline.Length >= Math.Min(3, title.Length)
            && underline.All(character => character == '-' || character == '=');
    }

    private static bool looks_like_code(string line)
    {
        string trimmed = line.TrimStart();
        return line.StartsWith("    ", StringComparison.Ordinal)
            || line.StartsWith("\t", StringComparison.Ordinal)
            || trimmed.StartsWith(">>>", StringComparison.Ordinal)
            || trimmed.StartsWith("...", StringComparison.Ordinal)
            || trimmed.Contains(" : ", StringComparison.Ordinal);
    }

    private static bool try_parse_definition_item(string[] lines, int index, out string term, out string description, out int consumed)
    {
        term = "";
        description = "";
        consumed = 0;
        if (index + 1 >= lines.Length || is_indented(lines[index]) || string.IsNullOrWhiteSpace(lines[index]))
            return false;

        string candidate = lines[index].Trim();
        if (candidate.StartsWith(">>>", StringComparison.Ordinal)
            || candidate.StartsWith("...", StringComparison.Ordinal)
            || candidate.StartsWith("- ", StringComparison.Ordinal)
            || candidate.StartsWith("* ", StringComparison.Ordinal))
            return false;

        bool colon_definition = candidate.Contains(" : ", StringComparison.Ordinal);
        if (!colon_definition && try_split_inline_definition(candidate, out term, out description))
        {
            consumed = 1;
            return true;
        }

        var description_lines = new List<string>();
        int next = index + 1;
        bool saw_description = false;
        while (next < lines.Length)
        {
            string line = lines[next];
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!saw_description || next + 1 >= lines.Length || !is_indented(lines[next + 1]))
                    break;
                description_lines.Add("");
                next++;
                continue;
            }

            if (!is_indented(line))
                break;
            if (line.TrimStart().StartsWith(".. ", StringComparison.Ordinal))
            {
                next++;
                continue;
            }
            description_lines.Add(line.Trim());
            saw_description = true;
            next++;
        }

        if (!colon_definition && description_lines.Count == 0)
            return false;

        if (colon_definition && description_lines.Count == 0 && try_split_inline_definition(candidate, out term, out description))
        {
            consumed = 1;
            return true;
        }

        term = candidate;
        description = join_definition_description(description_lines);
        consumed = next - index;
        return true;
    }

    private static bool try_split_inline_definition(string text, out string term, out string description)
    {
        term = "";
        description = "";
        int separator = text.IndexOf(" : ", StringComparison.Ordinal);
        if (separator <= 0)
            return false;
        term = text[..separator].Trim();
        description = text[(separator + 3)..].Trim();
        return term.Length > 0 && description.Length > 0;
    }

    private static string join_definition_description(IEnumerable<string> lines)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(string.Join(" ", current));
                    current.Clear();
                }
                continue;
            }
            current.Add(line);
        }

        if (current.Count > 0)
            paragraphs.Add(string.Join(" ", current));
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    private static string[] normalized_doc_lines(string documentation)
    {
        string[] raw = (documentation ?? "").Replace("\r\n", "\n").Split('\n');
        int first = 0;
        while (first < raw.Length && string.IsNullOrWhiteSpace(raw[first]))
            first++;
        int last = raw.Length - 1;
        while (last >= first && string.IsNullOrWhiteSpace(raw[last]))
            last--;
        if (first > last)
            return [];

        int indent = raw[first..(last + 1)]
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(leading_whitespace_width)
            .DefaultIfEmpty(0)
            .Min();

        return raw[first..(last + 1)]
            .Select(line => remove_indent(line, indent).TrimEnd())
            .ToArray();
    }

    private static int leading_whitespace_width(string line)
    {
        int width = 0;
        foreach (char character in line)
        {
            if (character == ' ')
                width++;
            else if (character == '\t')
                width += 4;
            else
                break;
        }
        return width;
    }

    private static string remove_indent(string line, int indent)
    {
        int width = 0;
        int index = 0;
        while (index < line.Length && width < indent)
        {
            if (line[index] == ' ')
                width++;
            else if (line[index] == '\t')
                width += 4;
            else
                break;
            index++;
        }
        return line[index..];
    }

    private static bool is_indented(string line) =>
        line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal);

    private static bool try_parse_list_item(string line, out string label, out string text)
    {
        label = "";
        text = "";
        string trimmed = line.Trim();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            text = trimmed[2..].Trim();
            return text.Length > 0;
        }

        int dot = trimmed.IndexOf('.');
        if (dot > 0 && dot < 4 && trimmed[..dot].All(char.IsDigit) && dot + 1 < trimmed.Length && char.IsWhiteSpace(trimmed[dot + 1]))
        {
            text = trimmed[(dot + 2)..].Trim();
            return text.Length > 0;
        }

        return false;
    }

    private static IEnumerable<Control> flush_paragraph(List<string> lines)
    {
        if (lines.Count == 0)
            yield break;
        string text = string.Join(" ", lines);
        lines.Clear();
        yield return paragraph_block(text);
    }

    private static IEnumerable<Control> flush_code(List<string> lines)
    {
        if (lines.Count == 0)
            yield break;
        string text = string.Join(Environment.NewLine, lines);
        lines.Clear();
        yield return code_block(text);
    }

    private static IEnumerable<Control> flush_list(List<(string Label, string Text)> items)
    {
        if (items.Count == 0)
            yield break;
        var snapshot = items.ToArray();
        items.Clear();
        yield return list_block(snapshot);
    }

    private static IEnumerable<Control> flush_definition_table(List<(string Term, string Description)> items)
    {
        if (items.Count == 0)
            yield break;
        var snapshot = items.ToArray();
        items.Clear();
        yield return definition_table_block(snapshot);
    }

    private static Control paragraph_block(string text) => new TextBlock
    {
        Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3")),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19
    }.WithReStructuredTextInlines(text);

    private static Control code_block(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            LineHeight = 17,
            FontSize = 12,
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
            TextWrapping = TextWrapping.NoWrap
        };
        return new Border
        {
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background2")),
            BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border2")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6),
            Child = block
        };
    }

    private static Control list_block(JsonElement items)
    {
        var values = items.EnumerateArray()
            .Select(item => (json_text(item, "label"), json_text(item, "text")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Item1) || !string.IsNullOrWhiteSpace(item.Item2))
            .ToArray();
        return list_block(values);
    }

    private static Control list_block(IEnumerable<(string Label, string Text)> items)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var (label, text) in items)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(new GridLength(1, GridUnitType.Star))
                },
                ColumnSpacing = 8
            };
            row.Children.Add(new TextBlock
            {
                Text = "-",
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme6")),
                Margin = new Thickness(0, 0, 0, 0)
            });
            var content = new TextBlock
            {
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            };
            if (!string.IsNullOrWhiteSpace(label))
            {
                content.Inlines?.Add(new Run(label) { FontWeight = FontWeight.SemiBold });
                if (!string.IsNullOrWhiteSpace(text))
                    content.Inlines?.Add(new Run(" - "));
            }
            content.WithReStructuredTextInlines(text);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control definition_table_block(IEnumerable<(string Term, string Description)> items)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 2, 0, 2)
        };

        foreach (var (term, description) in items)
        {
            var item_panel = new StackPanel { Spacing = 4 };
            item_panel.Children.Add(new TextBlock
            {
                Text = term,
                FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
                FontSize = 12,
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
                TextWrapping = TextWrapping.Wrap
            });

            var description_panel = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(16, 0, 0, 0)
            };
            foreach (var block in parse_docstring(description))
                description_panel.Children.Add(block);
            item_panel.Children.Add(description_panel);
            panel.Children.Add(item_panel);
        }

        return panel;
    }

    private static Control heading_block(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme6")),
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static string json_text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return "";
        return property.ValueKind is JsonValueKind.String ? property.GetString() ?? "" : property.ToString();
    }

    private static (int Line, int Column) document_location(TextDocument document, int offset)
    {
        offset = Math.Clamp(offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        return (line.LineNumber, offset - line.Offset);
    }

    private static bool is_inside_python_string(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        bool in_single = false;
        bool in_double = false;
        bool triple = false;
        char quote = '\0';
        bool escaped = false;

        for (int index = 0; index < offset; index++)
        {
            char current = text[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (in_single || in_double)
            {
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == quote)
                {
                    if (triple)
                    {
                        if (index + 2 < offset && text[index + 1] == quote && text[index + 2] == quote)
                        {
                            index += 2;
                            in_single = false;
                            in_double = false;
                            triple = false;
                        }
                    }
                    else
                    {
                        in_single = false;
                        in_double = false;
                    }
                }

                continue;
            }

            if (current == '#')
            {
                while (index < offset && text[index] != '\n')
                    index++;
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                triple = index + 2 < offset && text[index + 1] == current && text[index + 2] == current;
                if (triple)
                    index += 2;
                in_single = current == '\'';
                in_double = current == '"';
            }
        }

        return in_single || in_double;
    }

    private sealed class PythonCompletionData
    {
        public PythonCompletionData(PythonCompletionItem item)
        {
            Text = item.Name;
            InsertionText = string.IsNullOrWhiteSpace(item.Complete) ? item.Name : item.Name;
            Type = item.Type;
            Detail = item.Description;
            Signature = item.Signature;
            Documentation = item.Documentation;
            Description = string.IsNullOrWhiteSpace(item.Documentation)
                ? $"{item.Type} {item.Description}".Trim()
                : $"{item.Type} {item.Description}\n\n{item.Documentation}".Trim();
            Priority = item.Type is "keyword" ? 3 : 1;
        }

        public string Text { get; }
        public string InsertionText { get; }
        public string Type { get; }
        public string Detail { get; }
        public string Signature { get; }
        public string Documentation { get; }
        public string DisplayTitle => string.IsNullOrWhiteSpace(Signature)
            ? string.IsNullOrWhiteSpace(Type) ? Text : $"{Text}  {Type}"
            : Signature;
        public object Content => Text;
        public object Description { get; }
        public double Priority { get; }

        public override string ToString() => string.IsNullOrWhiteSpace(Type) ? Text : $"{Text}    {Type}";
    }
}

internal static class PythonScriptEditorGridExtensions
{
    public static Grid WithRows(this Grid grid)
    {
        for (int index = 0; index < grid.Children.Count; index++)
            Grid.SetRow((Control)grid.Children[index], index);
        return grid;
    }

    public static TextBlock WithReStructuredTextInlines(this TextBlock block, string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            int next = next_inline_marker(text, index);
            if (next < 0)
            {
                block.Inlines?.Add(new Run(text[index..]));
                break;
            }
            if (next > index)
                block.Inlines?.Add(new Run(text[index..next]));

            if (try_add_inline_code(block, text, next, out int consumed)
                || try_add_rest_role(block, text, next, out consumed)
                || try_add_rest_link(block, text, next, out consumed)
                || try_add_link(block, text, next, out consumed)
                || try_add_strong(block, text, next, out consumed)
                || try_add_emphasis(block, text, next, out consumed))
            {
                index = next + consumed;
                continue;
            }

            block.Inlines?.Add(new Run(text[next].ToString()));
            index = next + 1;
        }

        return block;
    }

    private static int next_inline_marker(string text, int start)
    {
        int best = -1;
        foreach (char marker in new[] { '`', '*', '[', ':', '<' })
        {
            int found = text.IndexOf(marker, start);
            if (found >= 0 && (best < 0 || found < best))
                best = found;
        }
        return best;
    }

    private static bool try_add_inline_code(TextBlock block, string text, int start, out int consumed)
    {
        consumed = 0;
        if (text[start] != '`')
            return false;
        bool doubled = start + 1 < text.Length && text[start + 1] == '`';
        int content_start = doubled ? start + 2 : start + 1;
        string delimiter = doubled ? "``" : "`";
        int end = text.IndexOf(delimiter, content_start, StringComparison.Ordinal);
        if (end <= content_start)
            return false;
        block.Inlines?.Add(new Run(text[content_start..end])
        {
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2"))
        });
        consumed = end - start + delimiter.Length;
        return true;
    }

    private static bool try_add_rest_role(TextBlock block, string text, int start, out int consumed)
    {
        consumed = 0;
        if (text[start] != ':')
            return false;
        int role_end = text.IndexOf(":`", start + 1, StringComparison.Ordinal);
        if (role_end <= start + 1)
            return false;
        int content_start = role_end + 2;
        int content_end = text.IndexOf('`', content_start);
        if (content_end <= content_start)
            return false;
        block.Inlines?.Add(new Run(text[content_start..content_end])
        {
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2"))
        });
        consumed = content_end - start + 1;
        return true;
    }

    private static bool try_add_rest_link(TextBlock block, string text, int start, out int consumed)
    {
        consumed = 0;
        if (text[start] != '<')
            return false;
        int close = text.IndexOf(">_", start, StringComparison.Ordinal);
        if (close <= start + 1)
            return false;

        block.Inlines?.Add(new Run(text[(start + 1)..close])
        {
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme6")),
            TextDecorations = TextDecorations.Underline
        });
        consumed = close - start + 2;
        return true;
    }

    private static bool try_add_strong(TextBlock block, string text, int start, out int consumed)
    {
        consumed = 0;
        if (!text.AsSpan(start).StartsWith("**", StringComparison.Ordinal))
            return false;
        int end = text.IndexOf("**", start + 2, StringComparison.Ordinal);
        if (end <= start + 2)
            return false;
        block.Inlines?.Add(new Run(text[(start + 2)..end]) { FontWeight = FontWeight.SemiBold });
        consumed = end - start + 2;
        return true;
    }

    private static bool try_add_emphasis(TextBlock block, string text, int start, out int consumed)
    {
        consumed = 0;
        if (text[start] != '*' || text.AsSpan(start).StartsWith("**", StringComparison.Ordinal))
            return false;
        int end = text.IndexOf('*', start + 1);
        if (end <= start + 1)
            return false;
        block.Inlines?.Add(new Run(text[(start + 1)..end]) { FontStyle = FontStyle.Italic });
        consumed = end - start + 1;
        return true;
    }

    private static bool try_add_link(TextBlock block, string text, int start, out int consumed)
    {
        consumed = 0;
        if (text[start] != '[')
            return false;
        int text_end = text.IndexOf(']', start + 1);
        if (text_end <= start + 1 || text_end + 1 >= text.Length || text[text_end + 1] != '(')
            return false;
        int url_end = text.IndexOf(')', text_end + 2);
        if (url_end <= text_end + 2)
            return false;
        block.Inlines?.Add(new Run(text[(start + 1)..text_end])
        {
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme6")),
            TextDecorations = TextDecorations.Underline
        });
        consumed = url_end - start + 1;
        return true;
    }
}
