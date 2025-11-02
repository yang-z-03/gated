
using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Data;
using Gated.Models;
using ScottPlot.Avalonia;
using ScottPlot;

namespace Gated;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        double[] dataX = { 1, 2, 3, 4, 5 };
        double[] dataY = { 1, 4, 9, 16, 25 };
        
        // set the color palette used when coloring new items added to the plot
        this.avaPlot.Plot.Add.Palette = new ScottPlot.Palettes.Penumbra();
        this.avaPlot.Plot.Add.Scatter(dataX, dataY);
        // change figure colors
        this.avaPlot.Plot.FigureBackground.Color = Color.FromHex("#303030");
        this.avaPlot.Plot.DataBackground.Color = Color.FromHex("#1f1f1f");
        // change axis and grid colors
        this.avaPlot.Plot.Axes.Color(Color.FromHex("#d7d7d7"));
        this.avaPlot.Plot.Grid.MajorLineColor = Color.FromHex("#404040");
        this.avaPlot.Refresh();

        this.DataContext = this.ViewModel;
        this.workspaceTree.Source = this.ViewModel.WorkspaceView;
    }

    public MainWindowViewModel ViewModel { get; set; } = new();
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
                    new TextColumn<INode, string>("Node", x => x.Name),
                    x => x.Children),
                new TextColumn<INode, string>("Type", x => x.GetType().Name)
            }
        };

    }
    
    public HierarchicalTreeDataGridSource<INode> WorkspaceView;
    public Workspace Workspace { get; set; }
}