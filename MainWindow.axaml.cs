using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Gated.Models;
using ScottPlot.Avalonia;
using ScottPlot;

namespace Gated;

public partial class MainWindow : Window
{
    private AvaPlot plotter;
    private FontStyle axis_title_font;
    private FontStyle axis_tick_font;
    
    public MainWindow()
    {
        InitializeComponent();
        
        double[] dataX = { 1, 2, 3, 4, 5 };
        double[] dataY = { 1, 4, 9, 16, 25 };

        this.plotter = this.avaPlot;
        
        this.axis_title_font = new ScottPlot.FontStyle()
        {
            Name = "Inter",
            Size = 13,
            Bold = true,
            Color = Colors.White
        };
        
        this.axis_tick_font = new ScottPlot.FontStyle()
        {
            Name = "Inter",
            Size = 12,
            Bold = false,
            Color = Colors.White
        };
        
        // change figure colors
        this.plotter.Plot.FigureBackground.Color = Color.FromHex("#1e1e1e");
        this.plotter.Plot.DataBackground.Color = Color.FromHex("#303030");
        // change axis and grid colors
        this.plotter.Plot.Axes.Color(Color.FromHex("#d7d7d7"));
        this.plotter.Plot.Grid.MajorLineColor = Color.FromHex("#404040");

        this.DataContext = this.ViewModel;
        this.workspaceTree.Source = this.ViewModel.WorkspaceView;
        this.ViewModel.WorkspaceView.ExpandAll();
        this.ViewModel.WorkspaceView.RowSelection!.SelectedIndex = 0;
        this.workspaceTree.SelectionChanging += workspace_tree_select;
    }

    public MainWindowViewModel ViewModel { get; set; } = new();

    private async void mnu_open_click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        // 启动异步操作以打开对话框。
        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
        {
            Title = "Open FCS or Workspace",
            AllowMultiple = false
        });

        if (files.Count >= 1)
        {
            this.ViewModel.Workspace.Children[4].Children.Add(
                new Tube(files[0].Path.AbsolutePath)
            );
        }
    }

    private void workspace_tree_select(object? sender, CancelEventArgs e)
    {
        TreeDataGrid? grid = sender as TreeDataGrid;
        if (grid != null)
        {
            // force single select
            var index = grid.RowSelection?.SelectedIndex;
            var item = grid.RowSelection?.SelectedItem;
            
            if (item is Tube tube)
            {
                display_scatter(tube, tube.GetChannelByName("FSC-A")!, tube.GetChannelByName("FSC-H")!);
            }
        }
    }

    private void display_scatter(Tube tube, Channel x, Channel y, int maxDisplay = 10000)
    {
        var dict = tube.GetValues(maxDisplay, x, y);
        var scatter = this.plotter.Plot.Add.Markers(dict[x]!, dict[y]!);
        this.plotter.Plot.Axes.SetLimitsX(0, dict[x]!.Max());
        this.plotter.Plot.Axes.SetLimitsY(0, dict[y]!.Max());
    }
}

public class MainWindowViewModel
{
    public MainWindowViewModel()
    {
        this.Workspace = new Workspace("Untitled Workspace");
        this.WorkspaceView = new HierarchicalTreeDataGridSource<INode>(this.Workspace)
        {
            Columns = {
                new HierarchicalExpanderColumn<INode>(
                    new TextColumn<INode, string>("Node", x => x.Name, 
                        width: new GridLength(300, GridUnitType.Pixel)),
                    x => x.Children),
                new TextColumn<INode, string>("Type", x => x.GetType().Name, width: new GridLength(100, GridUnitType.Pixel))
            }
        };

    }
    
    public HierarchicalTreeDataGridSource<INode> WorkspaceView;
    public Workspace Workspace { get; set; }
}