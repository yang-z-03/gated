using System;
using System.Collections.Generic;
using Gated.Models;
using ScottPlot;
using ScottPlot.Interactivity;

namespace Gated.Actions;

public class RangeLines(MouseButton button) : IUserActionResponse, IGatingAction
{
    public RangeLines(MouseButton button, bool editLocked) : this(button)
    {
        this.edit_locked = editLocked;
    }

    public RangeLines(IPlotControl control, RangeGate gate, MouseButton button) : this(button)
    {
        this.lower = (float)gate.XTransform.Transform(gate.Lower);
        this.upper = (float)gate.XTransform.Transform(gate.Upper);
        this.lower_placed = true;
        this.upper_placed = true;
        this.apply(control.Plot);
        this.edit_locked = true;
        this.defined_gate = gate;
    }

    public MouseButton MouseButton { get; } = button;
    public int Tolerance { get; } = 4;
    internal float lower, upper;
    internal bool lower_placed = false, upper_placed = false;
    internal RangeGate? defined_gate = null;
    private List<IPlottable> plottables = new();
    private bool edit_locked = false;
    private bool edit_mode = false;
    private int drag_target = 0; // 1=lower, 2=upper

    public event EventHandler? GateDefined;
    public event EventHandler? GateUpdated;

    public void ResetState(IPlotControl plotControl) { return; }

    private void reset_state(IPlotControl plotControl)
    {
        lower = upper = 0;
        lower_placed = upper_placed = false;
        foreach (var pl in plottables) plotControl.Plot.Remove(pl);
        plottables.Clear();
    }

    public ResponseInfo Execute(IPlotControl plotControl, IUserAction userInput, KeyboardState keys)
    {
        if (userInput is IMouseButtonAction mouseDownAction
            && mouseDownAction.Button == MouseButton
            && mouseDownAction.IsPressed)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseDownAction.Pixel);
            if (plot == null) return ResponseInfo.NoActionRequired;
            Coordinates current = plot.GetCoordinates(mouseDownAction.Pixel);
            var limits = plot.Axes.GetLimits();
            if (!(current.X >= limits.Left && current.X <= limits.Right
                && current.Y >= limits.Bottom && current.Y <= limits.Top))
                return ResponseInfo.NoActionRequired;

            if (lower_placed && upper_placed)
            {
                // Check hit on either line
                var lowerPix = plot.GetPixel(new Coordinates(lower, 0));
                var upperPix = plot.GetPixel(new Coordinates(upper, 0));
                if (Math.Abs(mouseDownAction.Pixel.X - lowerPix.X) <= Tolerance) { drag_target = 1; edit_mode = true; }
                else if (Math.Abs(mouseDownAction.Pixel.X - upperPix.X) <= Tolerance) { drag_target = 2; edit_mode = true; }
                else if (!edit_locked) reset_state(plotControl);
                else return ResponseInfo.NoActionRequired;
                return ResponseInfo.NoActionRequired;
            }

            if (!lower_placed)
            {
                lower = (float)current.X; lower_placed = true;
            }
            else
            {
                upper = (float)current.X; upper_placed = true;
                this.GateDefined?.Invoke(this, EventArgs.Empty);
            }
            this.apply(plot);
            this.edit_mode = true;
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseButtonAction mouseUpAction
            && mouseUpAction.Button == MouseButton
            && !mouseUpAction.IsPressed)
        {
            if (edit_mode && lower_placed && upper_placed)
                this.GateUpdated?.Invoke(this, EventArgs.Empty);
            edit_mode = false; drag_target = 0;
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseAction mouseMoveAction && edit_mode)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseMoveAction.Pixel);
            if (plot == null) return ResponseInfo.NoActionRequired;
            Coordinates current = plot.GetCoordinates(mouseMoveAction.Pixel);
            if (drag_target == 1) lower = (float)current.X;
            else if (drag_target == 2) upper = (float)current.X;
            this.apply(plot);
            return ResponseInfo.Refresh;
        }

        return ResponseInfo.NoActionRequired;
    }

    private void add_line_marker(Plot plot, float x, List<IPlottable> list)
    {
        double centerY = (plot.Axes.GetLimits().Top + plot.Axes.GetLimits().Bottom) / 2;
        var marker = plot.Add.Marker(new Coordinates(x, centerY), MarkerShape.FilledSquare, 6, Colors.White);
        marker.MarkerFillColor = Colors.White;
        marker.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
        marker.MarkerStyle.OutlineWidth = 1;
        marker.MarkerStyle.OutlinePattern = LinePattern.Solid;
        list.Add(marker);
    }

    private void apply(Plot plot)
    {
        foreach (var pl in plottables) plot.Remove(pl);
        plottables.Clear();

        if (lower_placed)
        {
            var l = plot.Add.VerticalLine(lower, 2, Colors.Black, LinePattern.Solid);
            plottables.Add(l);
            add_line_marker(plot, lower, plottables);
        }
        if (upper_placed)
        {
            var u = plot.Add.VerticalLine(upper, 2, Colors.Black, LinePattern.Solid);
            plottables.Add(u);
            add_line_marker(plot, upper, plottables);
        }
    }
}
