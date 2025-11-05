using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Gated.Models;
using Gated.Preprocessing;
using ScottPlot.Avalonia;
using ScottPlot;
using ScottPlot.Interactivity;

namespace Gated;

public partial class MainWindow : Window
{
    private AvaPlot plotter;
    private FontStyle axis_title_font;
    private FontStyle axis_tick_font;

    private Grouping? current_grouping = null;
    private Tube? current_tube = null;
    
    public MainWindow()
    {
        InitializeComponent();

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
        this.plotter.Plot.DataBackground.Color = Color.FromHex("#ffffff");
        // change axis and grid colors
        this.plotter.Plot.Axes.Color(Color.FromHex("#d0d0d0"));
        this.plotter.Plot.Grid.MajorLineColor = Color.FromHex("#d0d0d0");
        this.plotter.Plot.Font.Set("Inter", ScottPlot.FontWeight.Normal, FontSlant.Upright, FontSpacing.Normal);

        this.DataContext = this.ViewModel;
        this.workspaceTree.Source = this.ViewModel.WorkspaceView;
        this.ViewModel.WorkspaceView.RowSelection!.SelectedIndex = 0;
        this.ViewModel.WorkspaceView.RowSelection.SelectionChanged += workspace_tree_select;

        this.featureTree.Source = this.ViewModel.FeatureView;
    }

    private void lock_plot()
    {
        this.plotter.UserInputProcessor.IsEnabled = true;
        this.plotter.UserInputProcessor.UserActionResponses.Clear();
    }

    private void reset_user_input()
    {
        this.plotter.UserInputProcessor.IsEnabled = true;
        this.plotter.UserInputProcessor.Reset();
    }

    private void draw_polygon_gate()
    {
        this.lock_plot();
        this.plotter.UserInputProcessor.UserActionResponses.Add(
            new Gated.Actions.Polygon(ScottPlot.Interactivity.StandardMouseButtons.Left));
    }

    public MainWindowViewModel ViewModel { get; set; } = new();

    private async void mnu_open_click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
        {
            Title = "Open FCS or Workspace",
            AllowMultiple = true
        });

        if (files.Count >= 1)
        {
            foreach (var file in files)
            {
                try
                {
                    Grouping default_grouping = this.ViewModel.Workspace.Groupings[4];
                    var tube = new Tube(file.Path.AbsolutePath.Replace("%20", " "), default_grouping);
                    bool success = default_grouping.AddSample(tube);

                    if (!success && this.ViewModel.Workspace.Groupings.Count > 5)
                    {
                        for (int index = 5; index < this.ViewModel.Workspace.Groupings.Count; index++)
                        {
                            success = this.ViewModel.Workspace.Groupings[index].AddSample(tube);
                            if (success)
                            {
                                tube.ParentGroup = this.ViewModel.Workspace.Groupings[index];
                                break;
                            }
                        }
                    }

                    if (!success)
                    {
                        var g = new Grouping(
                            "Grouping " + (this.ViewModel.Workspace.Groupings.Count - 4).ToString(), 
                            new List<Tube>() {tube}, null, true);
                        this.ViewModel.Workspace.Groupings.Add(g);
                    }
                    
                } catch {}
            }
        }
    }

    private void workspace_tree_select(object? sender, TreeSelectionModelSelectionChangedEventArgs e)
    {
        // force single select
        var index = e.SelectedIndexes.First();
        var item = e.SelectedItems.First();

        if (item is Tube tube)
        {
            this.ViewModel.Dimensions.Clear();
            foreach(var dim in tube.Channels)
                this.ViewModel.Dimensions.Add(dim.Value);

            this.display_scatter(
                tube,
                tube.GetChannelByName("FSC-A"),
                tube.GetChannelByName("SSC-A"));
        }
    }

    private void display_scatter(
        Tube tube, Channel? x, Channel? y, 
        int maxDisplay = 10000, int densityEstimate = 3000,
        bool shouldRefreshAxis = true)
    {
        if (x == null || y == null) return;
        this.plotter.Plot.Clear();
        var dict = tube.GetValues(maxDisplay, x!, y!);
        var xs = dict[x!]!;
        var ys = dict[y!]!;

        var dictDens = tube.GetValues(densityEstimate, x!, y!);
        var densx = dictDens[x!]!;
        var densy = dictDens[y!]!;
        
        // axis limits

        if (shouldRefreshAxis)
        {
            this.plotter.Plot.Axes.SetLimitsX(0, dict[x!]!.Max());
            this.plotter.Plot.Axes.SetLimitsY(0, dict[y!]!.Max());
        }

        // hide axis edge line
        this.plotter.Plot.Axes.Right.FrameLineStyle.Width = 0;
        this.plotter.Plot.Axes.Top.FrameLineStyle.Width = 0;
        // scientific notation
        this.plotter.Plot.Axes.SetupMultiplierNotation(this.plotter.Plot.Axes.Left);
        this.plotter.Plot.Axes.SetupMultiplierNotation(this.plotter.Plot.Axes.Bottom);
        
        GaussianKDE kde = new GaussianKDE(densx, densy);
        double[] density = new double[xs.Length];
        for (int j = 0; j < xs.Length; j++)
            density[j] = kde.Estimate(xs[j], ys[j]);

        this.plotter.Plot.XLabel($"{x!.Label} ({x!.Name})");
        this.plotter.Plot.YLabel($"{y!.Label} ({y!.Name})");

        double minDensity = density.Min();
        double maxDensity = density.Max();
        double spanDensity = maxDensity - minDensity;
        var colormap = new ScottPlot.Colormaps.Turbo();
        for (int j = 0; j < xs.Length; j++)
        {
            double fraction = (density[j] - minDensity) / spanDensity;
            var marker = this.plotter.Plot.Add.Marker(xs[j], ys[j]);
            marker.Color = colormap.GetColor(fraction).WithAlpha(.8);
            marker.Size = 2;
        }
        
        this.plotter.Refresh();
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
                    new TemplateColumn<INode>(
                        "Node", "nodeDataTemplate",null,
                        width: new GridLength(300, GridUnitType.Pixel)),
                    x => x.Children, null, x => x.IsExpanded),
                new TextColumn<INode, string>("# Cells", x => x.GetNCells(), width: new GridLength(100, GridUnitType.Pixel))
            }
        };

        this.Dimensions = new();
        this.FeatureView = new FlatTreeDataGridSource<Dimension>(this.Dimensions)
        {
            Columns = {
                new TemplateColumn<Dimension>(
                    "Name", "dimensionDataTemplate",null,
                    width: new GridLength(150, GridUnitType.Pixel)),
                new TextColumn<Dimension, string>("Label", x => x.Label, 
                    width: new GridLength(100, GridUnitType.Pixel),
                    new TextColumnOptions<Dimension>(){BeginEditGestures = BeginEditGestures.Tap})
            }
        };
    }
    
    public HierarchicalTreeDataGridSource<INode> WorkspaceView;
    public FlatTreeDataGridSource<Dimension> FeatureView;
    public Workspace Workspace { get; set; }
    public ObservableCollection<Dimension> Dimensions { get; set; }
}