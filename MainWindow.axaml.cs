using System.Linq;
using Avalonia.Data;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using gated.Models;
using gated.ViewModels;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace gated;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel view_model = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = view_model;
        view_model.PropertyChanged += view_model_property_changed;
        update_statistics_columns();
        AddHandler(DragDrop.DragOverEvent, drag_over);
        AddHandler(DragDrop.DropEvent, drop_files);

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

        view_model.AddFiles(files.Select(file => file.Path.LocalPath));
    }

    private void density_menu_item_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SelectedPlotMode = PlotMode.Density;

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

    private void swap_axes_button_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        view_model.SwapAxes();

    private void drag_over(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void drop_files(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null)
            return;

        view_model.AddFiles(files
            .Select(file => file.Path.LocalPath)
            .Where(path => path.EndsWith(".fcs", System.StringComparison.OrdinalIgnoreCase)));
        e.Handled = true;
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
}
