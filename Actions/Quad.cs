using System;
using System.Collections.Generic;
using System.Linq;
using Gated.Models;
using ScottPlot;
using ScottPlot.Interactivity;

namespace Gated.Actions;


public class Quad(MouseButton button) : IUserActionResponse, IGatingAction
{
    public Quad(MouseButton button, bool editLocked) : this(button)
    {
        this.edit_locked = editLocked;
    }
    
    public Quad(IPlotControl control, QuadGate gate, MouseButton button) : this(button)
    {
        this.vertice = new Coordinates(gate.HorizontalCutoff, gate.VerticalCutoff);
        this.closed = true;
        this.pixel = control.Plot.GetPixel(this.vertice ?? new Coordinates());
        this.apply(control.Plot);
        this.edit_locked = true;
        this.defined_gate = gate;
    }
    
    public MouseButton MouseButton { get; } = button;
    public int Tolerance { get; } = 4;
    internal Coordinates? vertice = new();
    private Pixel? pixel = new();
    private List<ScottPlot.IPlottable> plottables = new();
    private bool closed = false;
    internal QuadGate? defined_gate = null;
    
    private bool edit_mode = false;
    private bool edit_locked = false;

    public event EventHandler? GateDefined;
    public event EventHandler? GateUpdated;
    
    public void ResetState(IPlotControl plotControl)
    {
        return;
    }

    private void reset_state(IPlotControl plotControl)
    {
        this.vertice = null;
        this.pixel = null;
        foreach(var pl in this.plottables)
            plotControl.Plot.Remove(pl);
        this.plottables.Clear();
        this.closed = false;
    }

    private bool hit_on_first(Pixel p, int tolerance = 4)
    {
        Pixel px = this.pixel ?? new Pixel();
        if ((p.X >= px.X - tolerance) && (p.X <= px.X + tolerance) &&
            (p.Y >= px.Y - tolerance) && (p.Y <= px.Y + tolerance))
            return true;

        return false;
    }

    public ResponseInfo Execute(IPlotControl plotControl, IUserAction userInput, KeyboardState keys)
    {
        // mouse down add a vertex
        if (userInput is IMouseButtonAction mouseDownAction
            && mouseDownAction.Button == MouseButton
            && mouseDownAction.IsPressed)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseDownAction.Pixel);
            if (plot == null) return ResponseInfo.NoActionRequired;
            
            Coordinates current = plot!.GetCoordinates(mouseDownAction.Pixel);
            
            // must be within plotting area!
            var limits = plot.Axes.GetLimits();
            if ((current.X >= limits.Left)
                && (current.X <= limits.Right)
                && (current.Y >= limits.Bottom)
                && (current.Y <= limits.Top))
            {
                // valid
            } else return ResponseInfo.NoActionRequired;
            
            if (closed)
            {
                // hit on the vertices of the previous polygons
                bool hit_test = hit_on_first(mouseDownAction.Pixel, this.Tolerance);
                if (hit_test)
                {
                    // move the vertix.
                    this.edit_mode = true;
                    return ResponseInfo.NoActionRequired;
                }
                else if(!this.edit_locked) { this.reset_state(plotControl); }
                else return ResponseInfo.NoActionRequired;
            }
            else
            {
                this.vertice = current;
                this.pixel = mouseDownAction.Pixel;
                this.apply(plot);
                this.GateDefined?.Invoke(this, EventArgs.Empty);
                this.edit_mode = true;
                this.closed = true;
                return ResponseInfo.Refresh;
            }
        }

        if (userInput is IMouseButtonAction mouseUpAction
            && mouseUpAction.Button == MouseButton
            && !mouseUpAction.IsPressed)
        {
            if(this.edit_mode && this.closed)
                this.GateUpdated?.Invoke(this, EventArgs.Empty);
            
            if (this.edit_mode)
            {
                this.edit_mode = false;
            }
            
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseAction mouseMoveAction
            && this.edit_mode)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseMoveAction.Pixel);
            if (plot == null) return ResponseInfo.NoActionRequired;
            Coordinates current = plot!.GetCoordinates(mouseMoveAction.Pixel);
            this.vertice = current;
            this.pixel = mouseMoveAction.Pixel;
            this.apply(plot);
            return ResponseInfo.Refresh;
        }

        return ResponseInfo.NoActionRequired;
    }

    private void apply(Plot plot, int highlight = -1)
    {
        foreach(var pl in this.plottables)
            plot.Remove(pl);

        if (this.pixel != null)
        {
            var p1 = plot.Add.HorizontalLine(vertice.GetValueOrDefault().Y, 2, Colors.Black, LinePattern.Solid);
            var p2 = plot.Add.VerticalLine(vertice.GetValueOrDefault().X, 2, Colors.Black, LinePattern.Solid);
            var marker = plot.Add.Marker(vertice ?? new(), MarkerShape.FilledSquare, 8, Colors.White);
            marker.MarkerFillColor = Colors.White;
            marker.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
            marker.MarkerStyle.OutlineWidth = 1;
            marker.MarkerStyle.OutlinePattern = LinePattern.Solid;
            this.plottables.Add(marker);
            this.plottables.Add(p1);
            this.plottables.Add(p2);
        }
    }
}