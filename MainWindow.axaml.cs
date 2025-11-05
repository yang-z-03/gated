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
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Gated.Configurations;
using Gated.Models;
using Gated.Preprocessing;
using ScottPlot.Avalonia;
using ScottPlot;
using ScottPlot.Interactivity;
using Color = ScottPlot.Color;
using Colors = ScottPlot.Colors;
using FontStyle = ScottPlot.FontStyle;
using Population = Gated.Models.Population;

namespace Gated;

public partial class MainWindow : Window
{
    private AvaPlot plotter;
    private FontStyle axis_title_font;
    private FontStyle axis_tick_font;

    private Grouping? current_grouping = null;
    private Tube? current_tube = null;
    private Population? current_population = null;
    private GatingStrategy? current_gate = null;
    private ScatterConfig? current_scatter = null;
    
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
        this.plotter.Plot.FigureBackground.Color = Color.FromHex("#2e3238");
        this.plotter.Plot.DataBackground.Color = Color.FromHex("#ffffff");
        // change axis and grid colors
        this.plotter.Plot.Axes.Color(Color.FromHex("#d0d0d0"));
        this.plotter.Plot.Grid.MajorLineColor = Color.FromHex("#f0f0f0");
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

    private Actions.Polygon draw_polygon_gate(PolygonalGate? gate = null)
    {
        this.lock_plot();
        Actions.Polygon cont;
        if (gate == null)
            cont = new Gated.Actions.Polygon(
                    button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                    editLocked: true
                );
        else 
            cont = new Gated.Actions.Polygon(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                gate: gate, control: this.plotter
            );
        
        this.plotter.UserInputProcessor.UserActionResponses.Add(cont);
        return cont;
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

            this.current_tube = tube;
            this.current_population = tube;
            this.placeHold.IsVisible = false;
            this.plotter.IsVisible = true;
            this.current_grouping = tube.ParentGroup;
            this.current_gate = this.current_population.AssociatedGate;

            this.current_scatter = tube.Display(
                this.plotter,
                tube.GetDefaultX(),
                tube.GetDefaultY(),
                null
            );
            
            // exit from previous tool to panning
            this.reset_user_input();
            this.mnuToolPan.IsChecked = true;
        }
        else if (item is Subset subset)
        {
            this.ViewModel.Dimensions.Clear();
            foreach(var dim in subset.Channels)
                this.ViewModel.Dimensions.Add(dim.Value);

            this.current_tube = subset.ParentTube;
            this.current_population = subset;
            this.placeHold.IsVisible = false;
            this.plotter.IsVisible = true;
            this.current_grouping = subset.ParentGroup;
            this.current_gate = this.current_population.AssociatedGate;

            this.current_scatter = subset.Display(
                this.plotter,
                subset.GetDefaultX(),
                subset.GetDefaultY(),
                null
            );
            
            // exit from previous tool to panning
            this.reset_user_input();
            this.mnuToolPan.IsChecked = true;
        }
        
    }

    private void mnu_add_polygon_gate(object? sender, RoutedEventArgs e)
    {
        if (this.current_scatter == null) return;
        if (this.current_tube == null) return;
        if (this.current_population == null) return;
        if (this.current_grouping == null) return;
        
        var action = draw_polygon_gate(null);
        action.GateDefined += (s, e) =>
        {
            Actions.Polygon? p = s as Actions.Polygon;
            this.current_grouping.AddGate(this.current_gate, new PolygonalGate(this.current_scatter, p!));
        };
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