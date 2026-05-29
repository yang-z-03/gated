using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Gated.Configurations;
using Gated.Models;
using Gated.Preprocessing;
using ScottPlot;
using ScottPlot.Avalonia;
using Color = ScottPlot.Color;
using Population = Gated.Models.Population;

namespace Gated;

public partial class MainWindow : Window
{
    private readonly AvaPlot plotter;

    private Grouping? current_grouping;
    private Tube? current_tube;
    private Population? current_population;
    private GatingStrategy? current_gate;
    private ScatterConfig? current_scatter;

    private bool should_combo_respond = true;
    private bool x_logicle = false;
    private bool y_logicle = false;

    private List<INode> equivalent_nodes = new();
    private int current_index = -1;
    
    public MainWindow()
    {
        InitializeComponent();

        this.plotter = this.avaPlot;
        
        // change figure colors
        this.plotter.Plot.FigureBackground.Color = Color.FromHex("#282828");
        this.plotter.Plot.DataBackground.Color = Color.FromHex("#1e1e1e");
        // change axis and grid colors
        this.plotter.Plot.Axes.Color(Color.FromHex("#707070"));
        this.plotter.Plot.Grid.MajorLineColor = Color.FromHex("#303030");
        this.plotter.Plot.Font.Set("Roboto");
        this.lock_plot();
        this.plotter.Plot.Axes.SetLimitsX(0, 10);
        this.plotter.Plot.Axes.SetLimitsY(0, 10);
        
        // hide axis edge line
        this.plotter.Plot.Axes.Right.FrameLineStyle.Width = 0;
        this.plotter.Plot.Axes.Top.FrameLineStyle.Width = 0;
        this.plotter.Plot.Axes.Left.SetTicks([1, 3, 5, 7, 9], ["1k", "10k", "100k", "1M", "10M"]);
        this.plotter.Plot.Axes.Bottom.SetTicks([1, 3, 5, 7, 9], ["1k", "10k", "100k", "1M", "10M"]);
        
        this.DataContext = this.ViewModel;
        this.workspaceTree.Source = this.ViewModel.WorkspaceView;
        this.ViewModel.WorkspaceView.RowSelection!.SelectedIndex = 0;
        this.ViewModel.WorkspaceView.RowSelection.SelectionChanged += workspace_tree_select;

        this.featureTree.Source = this.ViewModel.FeatureView;
        
        this.cmbX.SelectionChanged += combo_axis_changed;
        this.cmbY.SelectionChanged += combo_axis_changed;

        this.btnTransformX.Click += (s, e) => { this.btnTransformX.ContextFlyout!.ShowAt(this.btnTransformX); };
        this.btnTransformY.Click += (s, e) => { this.btnTransformY.ContextFlyout!.ShowAt(this.btnTransformY); };

        this.cmbToolPan.IsCheckedChanged += (s, e) =>
        {
            if (this.cmbToolPan.IsChecked ?? false)
                switch_tool(Tool.Pan);
        };
        
        this.cmbToolPolygon.IsCheckedChanged += (s, e) =>
        {
            if (this.cmbToolPolygon.IsChecked ?? false)
                switch_tool(Tool.Polygon);
        };
        
                this.cmbToolQuad.IsCheckedChanged += (s, e) =>
        {
            if (this.cmbToolQuad.IsChecked ?? false)
                switch_tool(Tool.Quad);
        };

        this.cmbToolBinary.IsCheckedChanged += (s, e) =>
        {
            if (this.cmbToolBinary.IsChecked ?? false)
                switch_tool(Tool.Binary);
        };

        this.cmbToolRange.IsCheckedChanged += (s, e) =>
        {
            if (this.cmbToolRange.IsChecked ?? false)
                switch_tool(Tool.Range);
        };

        this.cmbToolCurlyQuad.IsCheckedChanged += (s, e) =>
        {
            if (this.cmbToolCurlyQuad.IsChecked ?? false)
                switch_tool(Tool.CurlyQuad);
        };

        this.btnPrev.Click += (s, e) =>
        {
            if (this.equivalent_nodes.Count == 0) return;
            if (this.current_population == null) return;
            if (this.current_index < 1) return;
            if (this.current_index >= this.equivalent_nodes.Count) return;

            this.current_index--;
            this.select_node(this.equivalent_nodes[this.current_index]);
            this.update_equivalent();
        };
        
        this.btnNext.Click += (s, e) =>
        {
            if (this.equivalent_nodes.Count == 0) return;
            if (this.current_population == null) return;
            if (this.current_index < 0) return;
            if (this.current_index >= this.equivalent_nodes.Count - 1) return;

            this.current_index++;
            this.select_node(this.equivalent_nodes[this.current_index]);
            this.update_equivalent();
        };
    }

    private void toogle_x_linear(object? sender, RoutedEventArgs e)
    {
        this.x_logicle = false;
        transformation_changed();
    }
    
    private void toogle_x_logicle(object? sender, RoutedEventArgs e)
    {
        this.x_logicle = true;
        transformation_changed();
    }
    
    private void toogle_y_linear(object? sender, RoutedEventArgs e)
    {
        this.y_logicle = false;
        transformation_changed();
    }
    
    private void toogle_y_logicle(object? sender, RoutedEventArgs e)
    {
        this.y_logicle = true;
        transformation_changed();
    }

    private void transformation_changed()
    {
        if (should_combo_respond)
        {
            if (this.current_population != null)
            {
                if (x_logicle)
                    this.current_scatter!.XTransform = new LogicleTransform(t: this.current_scatter!.X.Maximum);
                else this.current_scatter!.XTransform = new LinearTransform();
                
                if (y_logicle)
                    this.current_scatter!.YTransform = new LogicleTransform(t: this.current_scatter!.Y.Maximum);
                else this.current_scatter!.YTransform = new LinearTransform();
                // range updates automatically from channel max in Display()
                
                this.current_scatter = this.current_population!.Display(
                    this.plotter,
                    (this.cmbX.SelectedItem as Dimension)!,
                    (this.cmbY.SelectedItem as Dimension)!,
                    this.current_scatter
                );

                this.current_population.ParentGroup!.ScatterConfigs[
                        (this.cmbX.SelectedItem as Dimension)!][(this.cmbY.SelectedItem as Dimension)!] =
                    this.current_scatter;

                // exit from previous tool to panning
                this.switch_tool(Tool.Pan);
            }
        }
    }

    private enum Tool
    {
        Pan,
        Polygon,
        Quad,
        Binary,
        Range,
        CurlyQuad
    }

    private void switch_tool(Tool t)
    {
        switch (t)
        {
            case Tool.Pan:
                this.mnuToolPan.IsChecked = true;
                this.cmbToolPan.IsChecked = true;
                this.lock_plot();
                break;
            
            case Tool.Quad:
                this.mnuToolQuad.IsChecked = true;
                this.cmbToolQuad.IsChecked = true;
                
                if (this.current_scatter == null) return;
                if (this.current_tube == null) return;
                if (this.current_population == null) return;
                if (this.current_grouping == null) return;
        
                var action_q = draw_cross_gate(null);
                action_q.GateDefined += (s, e) =>
                {
                    Actions.Quad? p = s as Actions.Quad;
                    QuadGate gate = new QuadGate(
                        this.current_scatter, 
                        this.current_scatter!.XTransform.InverseTransform((float)p!.vertice.GetValueOrDefault().X), 
                        this.current_scatter!.YTransform.InverseTransform((float)p!.vertice.GetValueOrDefault().Y), 
                        this.current_grouping);
                    p.defined_gate = gate;
                    this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
                    this.refresh_tree();
                };
        
                action_q.GateUpdated += (s, e) =>
                {
                    Actions.Quad? p = s as Actions.Quad;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.HorizontalCutoff = this.current_scatter!.XTransform.InverseTransform((float)p.vertice.GetValueOrDefault().X);
                        p!.defined_gate.VerticalCutoff = this.current_scatter!.YTransform.InverseTransform((float)p.vertice.GetValueOrDefault().Y);
                        p!.defined_gate!.Update();
                    }
                    
                    this.refresh_tree();
                };
                break;
            
            case Tool.Polygon:
                this.mnuToolPolygon.IsChecked = true;
                this.cmbToolPolygon.IsChecked = true;
                
                if (this.current_scatter == null) return;
                if (this.current_tube == null) return;
                if (this.current_population == null) return;
                if (this.current_grouping == null) return;
        
                var action_p = draw_polygon_gate(null);
                action_p.GateDefined += (s, e) =>
                {
                    Actions.Polygon? p = s as Actions.Polygon;
                    PolygonalGate gate = new PolygonalGate(this.current_scatter, p!, this.current_grouping);
                    p!.defined_gate = gate;
                    this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
                    this.refresh_tree();
                };
        
                action_p.GateUpdated += (s, e) =>
                {
                    Actions.Polygon? p = s as Actions.Polygon;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.Polygon = p!.vertices.Select(v => new Coordinates(
                        this.current_scatter!.XTransform.InverseTransform(v.X),
                        this.current_scatter!.YTransform.InverseTransform(v.Y)
                    )).ToList();
                        p!.defined_gate!.Update();
                    }
                    
                    this.refresh_tree();
                };

                break;
            
            case Tool.Binary:
                this.mnuToolBinary.IsChecked = true;
                this.cmbToolBinary.IsChecked = true;

                if (this.current_scatter == null) return;
                if (this.current_population == null) return;
                if (this.current_grouping == null) return;

                var action_b = draw_binary_gate(null);
                action_b.GateDefined += (s, e) =>
                {
                    Actions.BinaryGateLine? p = s as Actions.BinaryGateLine;
                    float origThreshold = this.current_scatter!.XTransform.InverseTransform(p!.threshold);
                    BinaryGate gate = new BinaryGate(
                        this.current_scatter!.X, this.current_scatter!.XTransform,
                        origThreshold, this.current_grouping);
                    p.defined_gate = gate;
                    this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
                    this.refresh_tree();
                };
                action_b.GateUpdated += (s, e) =>
                {
                    Actions.BinaryGateLine? p = s as Actions.BinaryGateLine;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.Threshold = this.current_scatter!.XTransform.InverseTransform(p!.threshold);
                        p!.defined_gate!.Update();
                    }
                    this.refresh_tree();
                };
                break;

            case Tool.Range:
                this.mnuToolRange.IsChecked = true;
                this.cmbToolRange.IsChecked = true;

                if (this.current_scatter == null) return;
                if (this.current_population == null) return;
                if (this.current_grouping == null) return;

                var action_r = draw_range_gate(null);
                action_r.GateDefined += (s, e) =>
                {
                    Actions.RangeLines? p = s as Actions.RangeLines;
                    float origLower = this.current_scatter!.XTransform.InverseTransform(p!.lower);
                    float origUpper = this.current_scatter!.XTransform.InverseTransform(p!.upper);
                    RangeGate gate = new RangeGate(
                        this.current_scatter!.X, this.current_scatter!.XTransform,
                        origLower, origUpper, this.current_grouping);
                    p.defined_gate = gate;
                    this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
                    this.refresh_tree();
                };
                action_r.GateUpdated += (s, e) =>
                {
                    Actions.RangeLines? p = s as Actions.RangeLines;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.Lower = this.current_scatter!.XTransform.InverseTransform(p!.lower);
                        p!.defined_gate.Upper = this.current_scatter!.XTransform.InverseTransform(p!.upper);
                        p!.defined_gate!.Update();
                    }
                    this.refresh_tree();
                };
                break;

            case Tool.CurlyQuad:
                this.mnuToolCurlyQuad.IsChecked = true;
                this.cmbToolCurlyQuad.IsChecked = true;

                if (this.current_scatter == null) return;
                if (this.current_population == null) return;
                if (this.current_grouping == null) return;

                var action_cq = draw_curly_quad_gate(null);
                action_cq.GateDefined += (s, e) =>
                {
                    Actions.CurlyQuad? p = s as Actions.CurlyQuad;
                    if (p!.crosshair == null) return;
                    float origH = this.current_scatter!.XTransform.InverseTransform((float)p.crosshair.Value.X);
                    float origV = this.current_scatter!.YTransform.InverseTransform((float)p.crosshair.Value.Y);
                    CurlyQuadGate gate = new CurlyQuadGate(
                        this.current_scatter, origH, origV,
                        p.topAnchorX, p.rightAnchorY,
                        this.current_grouping);
                    p.defined_gate = gate;
                    this.current_grouping.AddGate(this.current_gate, gate, this.current_population.AssociatedGateIndex);
                    this.refresh_tree();
                };
                action_cq.GateUpdated += (s, e) =>
                {
                    Actions.CurlyQuad? p = s as Actions.CurlyQuad;
                    if (p!.defined_gate != null && p!.crosshair != null)
                    {
                        p!.defined_gate.HorizontalCutoff = this.current_scatter!.XTransform.InverseTransform((float)p.crosshair.Value.X);
                        p!.defined_gate.VerticalCutoff = this.current_scatter!.YTransform.InverseTransform((float)p.crosshair.Value.Y);
                        p!.defined_gate.HorizontalCurliness = p.topAnchorX;
                        p!.defined_gate.VerticalCurliness = p.rightAnchorY;
                        p!.defined_gate!.Update();
                    }
                    this.refresh_tree();
                };
                break;
        }
    }

    private ScatterConfig try_get_config(Grouping group, Dimension x, Dimension y)
    {
        if (group.ScatterConfigs!.ContainsKey(x))
                if (group.ScatterConfigs[x]!.ContainsKey(y))
                    return group!.ScatterConfigs[x][y];
        return new ScatterConfig(x, y);
    }

    private void combo_axis_changed(object? sender, SelectionChangedEventArgs e)
    {
        if (should_combo_respond)
        {
            if (this.current_population != null)
            {
                var config = try_get_config(
                    this.current_population.ParentGroup!,
                    (this.cmbX.SelectedItem as Dimension)!,
                    (this.cmbY.SelectedItem as Dimension)!
                );

                this.x_logicle = config.XTransform is LogicleTransform;
                this.y_logicle = config.YTransform is LogicleTransform;
                
                this.current_scatter = this.current_population!.Display(
                    this.plotter,
                    (this.cmbX.SelectedItem as Dimension)!,
                    (this.cmbY.SelectedItem as Dimension)!,
                    config
                );

                this.current_population.ParentGroup!.ScatterConfigs[
                        (this.cmbX.SelectedItem as Dimension)!][(this.cmbY.SelectedItem as Dimension)!] =
                    this.current_scatter;

                this.transformation_changed();
                // exit from previous tool to panning
                this.switch_tool(Tool.Pan);
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
    
    private Actions.BinaryGateLine draw_binary_gate(BinaryGate? gate = null)
    {
        this.lock_plot();
        Actions.BinaryGateLine act;
        if (gate == null)
            act = new Actions.BinaryGateLine(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                editLocked: true
            );
        else
            act = new Actions.BinaryGateLine(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                gate: gate, control: this.plotter
            );
        this.plotter.UserInputProcessor.UserActionResponses.Add(act);
        return act;
    }

    private Actions.RangeLines draw_range_gate(RangeGate? gate = null)
    {
        this.lock_plot();
        Actions.RangeLines act;
        if (gate == null)
            act = new Actions.RangeLines(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                editLocked: true
            );
        else
            act = new Actions.RangeLines(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                gate: gate, control: this.plotter
            );
        this.plotter.UserInputProcessor.UserActionResponses.Add(act);
        return act;
    }

    private Actions.CurlyQuad draw_curly_quad_gate(CurlyQuadGate? gate = null)
    {
        this.lock_plot();
        Actions.CurlyQuad act;
        if (gate == null)
            act = new Actions.CurlyQuad(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                editLocked: true
            );
        else
            act = new Actions.CurlyQuad(
                button: ScottPlot.Interactivity.StandardMouseButtons.Left,
                gate: gate, control: this.plotter
            );
        this.plotter.UserInputProcessor.UserActionResponses.Add(act);
        return act;
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
        var top_level = TopLevel.GetTopLevel(this);

        var files = await top_level!.StorageProvider.OpenFilePickerAsync(
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
        var item = e.SelectedItems.First();
        this.select_node(item);

        // set equivalent node
        if (this.current_grouping != null)
        {
            if (this.current_population == this.current_tube)
            {
                this.equivalent_nodes.Clear();
                foreach(var sample in this.current_grouping.Samples)
                    this.equivalent_nodes.Add(sample);
            }
            else if (this.current_gate != null)
            {
                this.equivalent_nodes.Clear();
                foreach(var sample in this.current_gate.Populations.Keys)
                    this.equivalent_nodes.Add(sample);
            }
            
            this.update_equivalent();
        }
    }

    private void update_equivalent()
    {
        if (this.current_population == null) return;
        if (!this.equivalent_nodes.Contains(this.current_population)) return;
        this.lblRounder.Content =
            $"{this.equivalent_nodes.IndexOf(this.current_population) + 1} / {this.equivalent_nodes.Count}";
        this.lblSample.Content = this.current_population.ParentTube!.Name;
        this.current_index = this.equivalent_nodes.IndexOf(this.current_population);
    }

    private void select_node(object? item)
    {
        should_combo_respond = false;
        
        Dimension? prev_x = this.cmbX.SelectedItem as Dimension;
        Dimension? prev_y = this.cmbY.SelectedItem as Dimension;
        
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
                this.cmbX.Items.Contains(prev_x) ? prev_x! : tube.GetDefaultX(),
                this.cmbX.Items.Contains(prev_y) ? prev_y! : tube.GetDefaultY(),
                null
            );
            
            // exit from previous tool to panning
            this.cmbX.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.X);
            this.cmbY.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.Y);
            this.cmbX.IsEnabled = true;
            this.cmbY.IsEnabled = true;
            this.btnTransformX.IsEnabled = true;
            this.btnTransformY.IsEnabled = true;
            this.switch_tool(Tool.Pan);
            
            this.lblY.Content = "All data points";
            this.lblX.Content = "All data points";
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
                this.cmbX.Items.Contains(prev_x) ? prev_x! : subset.GetDefaultX(),
                this.cmbX.Items.Contains(prev_y) ? prev_y! : subset.GetDefaultY(),
                null
            );
            
            // exit from previous tool to panning
            this.cmbX.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.X);
            this.cmbY.SelectedIndex = this.cmbX.Items.IndexOf(this.current_scatter!.Y);
            this.cmbX.IsEnabled = true;
            this.cmbY.IsEnabled = true;
            this.btnTransformX.IsEnabled = true;
            this.btnTransformY.IsEnabled = true;
            this.switch_tool(Tool.Pan);
            this.lblY.Content = subset.Name;
            this.lblX.Content = subset.Name;
        }
        else if (item is GatingStrategy gate)
        {
            this.ViewModel.Dimensions.Clear();
            this.current_tube = null;
            this.current_population = null;
            this.plotter.IsVisible = true;
            this.current_grouping = gate.ParentGroup;
            this.current_gate = gate;
            this.cmbX.Items.Clear();
            this.cmbY.Items.Clear();
            this.lblY.Content = gate.ParentGroup!.Name + " (Concatenated)";
            this.lblX.Content = gate.ParentGroup!.Name + " (Concatenated)";

            if (item is PolygonalGate polyg)
            {
                this.ViewModel.Dimensions.Add(polyg.X);
                this.ViewModel.Dimensions.Add(polyg.Y);
                this.cmbX.Items.Add(polyg.X);
                this.cmbX.SelectedIndex = 0;
                this.cmbY.Items.Add(polyg.Y);
                this.cmbY.SelectedIndex = 0;
                
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
                        p!.defined_gate.Polygon = p!.vertices.Select(v => new Coordinates(
                        this.current_scatter!.XTransform.InverseTransform(v.X),
                        this.current_scatter!.YTransform.InverseTransform(v.Y)
                    )).ToList();
                        p!.defined_gate!.Update();
                    }
                };
            }
            else if(item is QuadGate q)
            {
                this.ViewModel.Dimensions.Add(q.X);
                this.ViewModel.Dimensions.Add(q.Y);
                q.Display(this.plotter);
                this.cmbX.Items.Add(q.X);
                this.cmbX.SelectedIndex = 0;
                this.cmbY.Items.Add(q.Y);
                this.cmbY.SelectedIndex = 0;
                var action = draw_cross_gate(q);
                
                action.GateDefined += (s, e) =>
                {
                    Actions.Quad? p = s as Actions.Quad;
                    QuadGate gate = new QuadGate(
                        this.current_scatter!,
                        this.current_scatter!.XTransform.InverseTransform((float)p!.vertice.GetValueOrDefault().X),
                        this.current_scatter!.YTransform.InverseTransform((float)p!.vertice.GetValueOrDefault().Y),
                        this.current_grouping!);
                    p!.defined_gate = gate;
                    this.current_grouping!.AddGate(this.current_gate, gate);
                };

                action.GateUpdated += (s, e) =>
                {
                    Actions.Quad? p = s as Actions.Quad;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.HorizontalCutoff = this.current_scatter!.XTransform.InverseTransform((float)p.vertice.GetValueOrDefault().X);
                        p!.defined_gate.VerticalCutoff = this.current_scatter!.YTransform.InverseTransform((float)p.vertice.GetValueOrDefault().Y);
                        p!.defined_gate!.Update();
                    }
                };
            }
            else if (item is BinaryGate bg)
            {
                this.ViewModel.Dimensions.Add(bg.X);
                this.ViewModel.Dimensions.Add(bg.X);
                bg.Display(this.plotter);
                this.cmbX.Items.Add(bg.X);
                this.cmbX.SelectedIndex = 0;
                this.cmbY.Items.Add(bg.X);
                this.cmbY.SelectedIndex = 0;
                var action = draw_binary_gate(bg);

                action.GateDefined += (s, e) =>
                {
                    Actions.BinaryGateLine? p = s as Actions.BinaryGateLine;
                    float origThreshold = bg.XTransform.InverseTransform(p!.threshold);
                    BinaryGate gate = new BinaryGate(bg.X, bg.XTransform, origThreshold, this.current_grouping!);
                    p!.defined_gate = gate;
                    this.current_grouping!.AddGate(this.current_gate, gate);
                };

                action.GateUpdated += (s, e) =>
                {
                    Actions.BinaryGateLine? p = s as Actions.BinaryGateLine;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.Threshold = bg.XTransform.InverseTransform(p!.threshold);
                        p!.defined_gate!.Update();
                    }
                };
            }
            else if (item is RangeGate rg)
            {
                this.ViewModel.Dimensions.Add(rg.X);
                this.ViewModel.Dimensions.Add(rg.X);
                rg.Display(this.plotter);
                this.cmbX.Items.Add(rg.X);
                this.cmbX.SelectedIndex = 0;
                this.cmbY.Items.Add(rg.X);
                this.cmbY.SelectedIndex = 0;
                var action = draw_range_gate(rg);

                action.GateDefined += (s, e) =>
                {
                    Actions.RangeLines? p = s as Actions.RangeLines;
                    float origLower = rg.XTransform.InverseTransform(p!.lower);
                    float origUpper = rg.XTransform.InverseTransform(p!.upper);
                    RangeGate gate = new RangeGate(rg.X, rg.XTransform, origLower, origUpper, this.current_grouping!);
                    p!.defined_gate = gate;
                    this.current_grouping!.AddGate(this.current_gate, gate);
                };

                action.GateUpdated += (s, e) =>
                {
                    Actions.RangeLines? p = s as Actions.RangeLines;
                    if (p!.defined_gate != null)
                    {
                        p!.defined_gate.Lower = rg.XTransform.InverseTransform(p!.lower);
                        p!.defined_gate.Upper = rg.XTransform.InverseTransform(p!.upper);
                        p!.defined_gate!.Update();
                    }
                };
            }
            else if (item is CurlyQuadGate cqg)
            {
                this.ViewModel.Dimensions.Add(cqg.X);
                this.ViewModel.Dimensions.Add(cqg.Y);
                cqg.Display(this.plotter);
                this.cmbX.Items.Add(cqg.X);
                this.cmbX.SelectedIndex = 0;
                this.cmbY.Items.Add(cqg.Y);
                this.cmbY.SelectedIndex = 0;
                var action = draw_curly_quad_gate(cqg);

                action.GateDefined += (s, e) =>
                {
                    Actions.CurlyQuad? p = s as Actions.CurlyQuad;
                    if (p!.crosshair == null) return;
                    float origH = cqg.XTransform.InverseTransform((float)p.crosshair.Value.X);
                    float origV = cqg.YTransform.InverseTransform((float)p.crosshair.Value.Y);
                    CurlyQuadGate gate = new CurlyQuadGate(
                        this.current_scatter!, origH, origV,
                        p.topAnchorX, p.rightAnchorY,
                        this.current_grouping!);
                    p!.defined_gate = gate;
                    this.current_grouping!.AddGate(this.current_gate, gate);
                };

                action.GateUpdated += (s, e) =>
                {
                    Actions.CurlyQuad? p = s as Actions.CurlyQuad;
                    if (p!.defined_gate != null && p!.crosshair != null)
                    {
                        p!.defined_gate.HorizontalCutoff = cqg.XTransform.InverseTransform((float)p.crosshair.Value.X);
                        p!.defined_gate.VerticalCutoff = cqg.YTransform.InverseTransform((float)p.crosshair.Value.Y);
                        p!.defined_gate.HorizontalCurliness = p.topAnchorX;
                        p!.defined_gate.VerticalCurliness = p.rightAnchorY;
                        p!.defined_gate!.Update();
                    }
                };
            }
            
            this.cmbX.IsEnabled = false;
            this.cmbY.IsEnabled = false;
            this.btnTransformX.IsEnabled = false;
            this.btnTransformY.IsEnabled = false;
            this.plotter.Refresh();
        }
        
        should_combo_respond = true;
    }

    private void mnu_add_polygon_gate(object? sender, RoutedEventArgs e)
    {
        this.switch_tool(Tool.Polygon);
    }
    
    private void mnu_add_quad_gate(object? sender, RoutedEventArgs e)
    {
        this.switch_tool(Tool.Quad);
    }

    private void mnu_add_binary_gate(object? sender, RoutedEventArgs e)
    {
        this.switch_tool(Tool.Binary);
    }

    private void mnu_add_range_gate(object? sender, RoutedEventArgs e)
    {
        this.switch_tool(Tool.Range);
    }

    private void mnu_add_curly_quad_gate(object? sender, RoutedEventArgs e)
    {
        this.switch_tool(Tool.CurlyQuad);
    }

    private void mnu_create_group(object? sender, RoutedEventArgs e)
    {
        int nextNum = this.ViewModel.Workspace.Groupings.Count - 4;
        string name = "Grouping " + (nextNum + 1).ToString();
        var g = new Grouping(name, new List<Tube>(), null);
        this.ViewModel.Workspace.Groupings.Add(g);
        this.refresh_tree();
    }

    private void mnu_move_tube_to_group(object? sender, RoutedEventArgs e)
    {
        if (this.current_tube == null) return;
        var tube = this.current_tube;
        var srcGroup = tube.ParentGroup;
        if (srcGroup == null) return;

        foreach (var targetGroup in this.ViewModel.Workspace.Groupings)
        {
            if (targetGroup == srcGroup) continue;
            srcGroup.Samples.Remove(tube);
            if (targetGroup.AddSample(tube))
            {
                tube.ParentGroup = targetGroup;
                this.current_grouping = targetGroup;
                this.refresh_tree();
                return;
            }
            else
            {
                srcGroup.AddSample(tube);
            }
        }
    }

    private void refresh_tree()
    {
        if (current_grouping == null) return;
        this.current_grouping!.IsExpanded = false;
        this.current_grouping!.IsExpanded = true;
    }

    private void mnu_about(object? sender, RoutedEventArgs e)
    {
        (new About()).ShowDialog(this);
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
                new TextColumn<Dimension, string>("Max", x => x.DisplayMaximum,
                    width: new GridLength(100, GridUnitType.Pixel)),
                new TextColumn<Dimension, string>("Label", x => x.Label, 
                    width: new GridLength(100, GridUnitType.Pixel))
            }
        };
    }
    
    public HierarchicalTreeDataGridSource<INode> WorkspaceView;
    public FlatTreeDataGridSource<Dimension> FeatureView;
    public Workspace Workspace { get; set; }
    public ObservableCollection<Dimension> Dimensions { get; set; }
}