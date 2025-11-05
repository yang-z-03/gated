using System;
using System.Collections.Generic;
using System.Linq;
using Gated.Models;
using ScottPlot;
using ScottPlot.Interactivity;

namespace Gated.Actions;

public interface IGatingAction
{
    public event EventHandler? GateDefined;
}

public class Polygon(MouseButton button) : IUserActionResponse, IGatingAction
{
    public Polygon(MouseButton button, bool editLocked) : this(button)
    {
        this.edit_locked = editLocked;
    }
    
    public Polygon(IPlotControl control, PolygonalGate gate, MouseButton button) : this(button)
    {
        this.vertices = gate.Polygon;
        this.closed = true;
        foreach (Coordinates v in vertices)
            this.pixels.Add(control.Plot.GetPixel(v));
        this.apply(control.Plot);
        this.edit_locked = true;
    }
    
    public MouseButton MouseButton { get; } = button;
    public int Tolerance { get; } = 4;
    internal List<Coordinates> vertices = new();
    private List<Pixel> pixels = new();
    private ScottPlot.Plottables.Polygon? polygon = null;
    private bool closed = false;
    
    private bool edit_mode = false;
    private int edit_index = -1;
    private bool edit_locked = false;

    public event EventHandler? GateDefined;
    
    public void ResetState(IPlotControl plotControl)
    {
        return;
    }

    private void reset_state(IPlotControl plotControl)
    {
        this.vertices.Clear();
        this.pixels.Clear();
        if (this.polygon != null)
            plotControl.Plot.Remove(this.polygon);
        this.polygon = null;
        this.closed = false;
    }

    private int hit_on_any(Pixel pixel, int tolerance = 4)
    {
        int index = 0;
        foreach (var px in this.pixels)
        {
            if ((pixel.X >= px.X - tolerance) && (pixel.X <= px.X + tolerance) &&
                (pixel.Y >= px.Y - tolerance) && (pixel.Y <= px.Y + tolerance))
                return index;
            index++;
        }

        return -1;
    }
    
    private bool hit_on_first(Pixel pixel, int tolerance = 4)
    {
        var px = this.pixels.First();
        if ((pixel.X >= px.X - tolerance) && (pixel.X <= px.X + tolerance) &&
            (pixel.Y >= px.Y - tolerance) && (pixel.Y <= px.Y + tolerance))
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
                int hit_test = hit_on_any(mouseDownAction.Pixel, this.Tolerance);
                if (hit_test >= 0)
                {
                    // move the vertix.
                    this.edit_mode = true;
                    this.edit_index = hit_test;
                    return ResponseInfo.NoActionRequired;
                }
                else if(!this.edit_locked) { this.reset_state(plotControl); }
                else return ResponseInfo.NoActionRequired;
            }

            if (this.vertices.Count > 0 && hit_on_first(mouseDownAction.Pixel, this.Tolerance))
            {
                this.closed = true;
                this.apply(plot);
                this.GateDefined?.Invoke(this, EventArgs.Empty);
                return ResponseInfo.Refresh;
            }
            else
            {
                this.vertices.Add(current);
                this.pixels.Add(mouseDownAction.Pixel);
                this.apply(plot);
                this.edit_mode = true;
                this.edit_index = this.vertices.Count - 1;
                return ResponseInfo.Refresh;
            }
        }

        if (userInput is IMouseButtonAction mouseUpAction
            && mouseUpAction.Button == MouseButton
            && !mouseUpAction.IsPressed)
        {
            if (this.edit_mode)
            {
                this.edit_index = -1;
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
            this.vertices[this.edit_index] = current;
            this.pixels[this.edit_index] = mouseMoveAction.Pixel;
            this.apply(plot);
            return ResponseInfo.Refresh;
        }

        return ResponseInfo.NoActionRequired;
    }

    private void apply(Plot plot, int highlight = -1)
    {
        if (polygon != null)
            plot.Remove(this.polygon);

        if (this.vertices.Count > 0)
        {
            this.polygon = plot.Add.Polygon(this.vertices.ToArray());
            this.polygon.FillColor = Color.FromHex("#135CB820");
            
            this.polygon.LineColor = Colors.Black;
            this.polygon.LinePattern = LinePattern.Solid;
            this.polygon.LineWidth = 1;

            this.polygon.MarkerShape = this.closed ? MarkerShape.FilledSquare : MarkerShape.FilledCircle;
            this.polygon.MarkerSize = 2 * Tolerance;
            this.polygon.MarkerFillColor = Colors.White;
            this.polygon.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
            this.polygon.MarkerStyle.OutlineWidth = 1;
            this.polygon.MarkerStyle.OutlinePattern = LinePattern.Solid;
        }
    }
}