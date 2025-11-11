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
using Gated.Actions;
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

    private bool should_combo_respond = true;
    
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
        this.plotter.Plot.FigureBackground.Color = Color.FromHex("#282828");
        this.plotter.Plot.DataBackground.Color = Color.FromHex("#1e1e1e");
        // change axis and grid colors
        this.plotter.Plot.Axes.Color(Color.FromHex("#707070"));
        this.plotter.Plot.Grid.MajorLineColor = Color.FromHex("#303030");
        this.plotter.Plot.Font.Set("Inter", ScottPlot.FontWeight.Normal, FontSlant.Upright, FontSpacing.Normal);
        this.lock_plot();
        this.plotter.Plot.Axes.SetLimitsX(0, 10);
        this.plotter.Plot.Axes.SetLimitsY(0, 10);
        
        // hide axis edge line
        this.plotter.Plot.Axes.Right.FrameLineStyle.Width = 0;
        this.plotter.Plot.Axes.Top.FrameLineStyle.Width = 0;
        this.plotter.Plot.Axes.Left.SetTicks(new double[]{ 1, 3, 5, 7, 9}, new []{"1k", "10k", "100k", "1M", "10M"});
        this.plotter.Plot.Axes.Bottom.SetTicks(new double[]{ 1, 3, 5, 7, 9}, new []{"1k", "10k", "100k", "1M", "10M"});
        
        this.DataContext = this.ViewModel;
        this.workspaceTree.Source = this.ViewModel.WorkspaceView;
        this.ViewModel.WorkspaceView.RowSelection!.SelectedIndex = 0;
        this.ViewModel.WorkspaceView.RowSelection.SelectionChanged += workspace_tree_select;

        this.featureTree.Source = this.ViewModel.FeatureView;
        
        this.cmbX.SelectionChanged += combo_axis_changed;
        this.cmbY.SelectionChanged += combo_axis_changed;
    }

    private void combo_axis_changed(object? sender, SelectionChangedEventArgs e)
    {
        if (should_combo_respond)
        {
            if (this.current_population != null)
            {
                this.current_scatter = this.current_population!.Display(
                    this.plotter,
                    (this.cmbX.SelectedItem as Dimension)!,
                    (this.cmbY.SelectedItem as Dimension)!,
                    null
                );

                this.current_population.ParentGroup!.ScatterConfigs[
                        (this.cmbX.SelectedItem as Dimension)!][(this.cmbY.SelectedItem as Dimension)!] =
                    this.current_scatter;

                // exit from previous tool to panning
                this.lock_plot();
                this.mnuToolPan.IsChecked = true;
            }
        }
        
        this.plotter.Refresh();
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
    
    private Actions.Quad draw_cross_gate(QuadGate? gate = null)
    {
        this.lock_plot();
        Actions.Quad cont;
        if (gate == null)
            cont = new Gated.Actions.Quad(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                editLocked: true
            );
        else 
            cont = new Gated.Actions.Quad(
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
        should_combo_respond = false;
        // force single select
        var index = e.SelectedIndexes.First();
        var item = e.SelectedItems.First();

        if (item is Tube tube)
        {
            this.ViewModel.Dimensions.Clear();
            this.cmbX.Items.Clear();
            this.cmbY.Items.Clear();
            foreach (var dim in tube.Channels)
            {
                this.ViewModel.Dimensions.Add(dim.Value);
                this.cmbX.Items.Add(dim.Value);
                this.cmbY.Items.Add(dim.Value);
            }

            this.current_tube = tube;
            this.current_population = tube;
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
            this.cmbX.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.X);
            this.cmbY.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.Y);
            this.cmbX.IsEnabled = true;
            this.cmbY.IsEnabled = true;
            this.lock_plot();
            this.mnuToolPan.IsChecked = true;
        }
        else if (item is Subset subset)
        {
            this.ViewModel.Dimensions.Clear();
            this.cmbX.Items.Clear();
            this.cmbY.Items.Clear();
            foreach (var dim in subset.Channels)
            {
                this.cmbX.Items.Add(dim.Value);
                this.cmbY.Items.Add(dim.Value);
                this.ViewModel.Dimensions.Add(dim.Value);
            }

            this.current_tube = subset.ParentTube;
            this.current_population = subset;
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
            this.cmbX.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.X);
            this.cmbY.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.Y);
            this.cmbX.IsEnabled = true;
            this.cmbY.IsEnabled = true;
            this.lock_plot();
            this.mnuToolPan.IsChecked = true;
        }
        else if (item is GatingStrategy gate)
        {
            this.ViewModel.Dimensions.Clear();
            this.current_tube = null;
            this.current_population = null;
            this.plotter.IsVisible = true;
            this.current_grouping = gate.ParentGroup;
            this.current_gate = gate;

            if (item is PolygonalGate polyg)
            {
                this.ViewModel.Dimensions.Add(polyg.X);
                this.ViewModel.Dimensions.Add(polyg.Y);
                polyg.Display(this.plotter);
                var action = draw_polygon_gate(polyg);
                
                action.GateDefined += (s, e) =>
                {
                    Actions.Polygon? p = s as Actions.Polygon;
                    PolygonalGate gate = new PolygonalGate(this.current_scatter!, p!, this.current_grouping!);
                    p!.defined_gate = gate;
                    this.current_grouping!.AddGate(this.current_gate, gate);
                };
        
                action.GateUpdated += (s, e) =>
                {
                    Actions.Polygon? p = s as Actions.Polygon;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.Polygon = p!.vertices;
                        p!.defined_gate!.Update();
                    }
                };
            }
            else if(item is QuadGate q)
            {
                this.ViewModel.Dimensions.Add(q.X);
                this.ViewModel.Dimensions.Add(q.Y);
                q.Display(this.plotter);
                var action = draw_cross_gate(q);
                
                action.GateDefined += (s, e) =>
                {
                    Actions.Quad? p = s as Actions.Quad;
                    QuadGate gate = new QuadGate(
                        this.current_scatter!,
                        Convert.ToSingle(p!.vertice.GetValueOrDefault().X),
                        Convert.ToSingle(p!.vertice.GetValueOrDefault().Y),
                        this.current_grouping!);
                    p!.defined_gate = gate;
                    this.current_grouping!.AddGate(this.current_gate, gate);
                };

                action.GateUpdated += (s, e) =>
                {
                    Actions.Quad? p = s as Actions.Quad;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.HorizontalCutoff = Convert.ToSingle(p.vertice.GetValueOrDefault().X);
                        p!.defined_gate.VerticalCutoff = Convert.ToSingle(p.vertice.GetValueOrDefault().Y);
                        p!.defined_gate!.Update();
                    }
                };
            }

            this.plotter.Refresh();
        }

        should_combo_respond = true;
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
            PolygonalGate gate = new PolygonalGate(this.current_scatter, p!, this.current_grouping);
            p!.defined_gate = gate;
            this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
        };
        
        action.GateUpdated += (s, e) =>
        {
            Actions.Polygon? p = s as Actions.Polygon;
            if (p!.defined_gate != null)
            {
                p!.defined_gate.Polygon = p!.vertices;
                p!.defined_gate!.Update();
            }
        };
    }
    
    private void mnu_add_quad_gate(object? sender, RoutedEventArgs e)
    {
        if (this.current_scatter == null) return;
        if (this.current_tube == null) return;
        if (this.current_population == null) return;
        if (this.current_grouping == null) return;
        
        var action = draw_cross_gate(null);
        action.GateDefined += (s, e) =>
        {
            Actions.Quad? p = s as Actions.Quad;
            QuadGate gate = new QuadGate(
                this.current_scatter, 
                Convert.ToSingle(p!.vertice.GetValueOrDefault().X), 
                Convert.ToSingle(p!.vertice.GetValueOrDefault().Y), 
                this.current_grouping);
            p.defined_gate = gate;
            this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
        };
        
        action.GateUpdated += (s, e) =>
        {
            Actions.Quad? p = s as Actions.Quad;
            if (p!.defined_gate != null)
            {
                p!.defined_gate.HorizontalCutoff = Convert.ToSingle(p.vertice.GetValueOrDefault().X);
                p!.defined_gate.VerticalCutoff = Convert.ToSingle(p.vertice.GetValueOrDefault().Y);
                p!.defined_gate!.Update();
            }
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