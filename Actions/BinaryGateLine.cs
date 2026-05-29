using System;
using System.Collections.Generic;
using Gated.Models;
using ScottPlot;
using ScottPlot.Interactivity;

namespace Gated.Actions;

public class BinaryGateLine(MouseButton button) : IUserActionResponse, IGatingAction
{
    public BinaryGateLine(MouseButton button, bool editLocked) : this(button)
    {
        this.edit_locked = editLocked;
    }

    public BinaryGateLine(IPlotControl control, BinaryGate gate, MouseButton button) : this(button)
    {
        this.threshold = (float)gate.XTransform.Transform(gate.Threshold);
        this.placed = true;
        this.pixel = control.Plot.GetPixel(new Coordinates(this.threshold, 0));
        this.apply(control.Plot);
        this.edit_locked = true;
        this.defined_gate = gate;
    }

    public MouseButton MouseButton { get; } = button;
    public int Tolerance { get; } = 4;
    internal float threshold;
    internal bool placed = false;
    internal BinaryGate? defined_gate = null;
    private Pixel? pixel;
    private List<IPlottable> plottables = new();
    private bool edit_locked = false;
    private bool edit_mode = false;

    public event EventHandler? GateDefined;
    public event EventHandler? GateUpdated;

    public void ResetState(IPlotControl plotControl) { return; }

    private void reset_state(IPlotControl plotControl)
    {
        this.threshold = 0;
        this.placed = false;
        this.pixel = null;
        foreach (var pl in plottables) plotControl.Plot.Remove(pl);
        plottables.Clear();
    }

    private bool hit_on_line(Pixel p, int tolerance = 4)
    {
        if (pixel == null) return false;
        return Math.Abs(p.X - pixel.Value.X) <= tolerance;
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

            if (placed)
            {
                if (hit_on_line(mouseDownAction.Pixel))
                {
                    this.edit_mode = true;
                    return ResponseInfo.NoActionRequired;
                }
                else if (!edit_locked) reset_state(plotControl);
                else return ResponseInfo.NoActionRequired;
            }

            this.threshold = (float)current.X;
            this.placed = true;
            this.pixel = mouseDownAction.Pixel;
            this.apply(plot);
            this.edit_mode = true;
            this.GateDefined?.Invoke(this, EventArgs.Empty);
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseButtonAction mouseUpAction
            && mouseUpAction.Button == MouseButton
            && !mouseUpAction.IsPressed)
        {
            if (this.edit_mode && this.placed)
                this.GateUpdated?.Invoke(this, EventArgs.Empty);
            this.edit_mode = false;
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseAction mouseMoveAction && this.edit_mode)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseMoveAction.Pixel);
            if (plot == null) return ResponseInfo.NoActionRequired;
            Coordinates current = plot.GetCoordinates(mouseMoveAction.Pixel);
            this.threshold = (float)current.X;
            this.pixel = mouseMoveAction.Pixel;
            this.apply(plot);
            return ResponseInfo.Refresh;
        }

        return ResponseInfo.NoActionRequired;
    }

    private void apply(Plot plot)
    {
        foreach (var pl in plottables) plot.Remove(pl);
        plottables.Clear();

        if (placed)
        {
            var line = plot.Add.VerticalLine(threshold, 2, Colors.Black, LinePattern.Solid);
            plottables.Add(line);

            // Marker at center of line — white square with blue border
            double centerY = (plot.Axes.GetLimits().Top + plot.Axes.GetLimits().Bottom) / 2;
            var marker = plot.Add.Marker(new Coordinates(threshold, centerY), MarkerShape.FilledSquare, 6, Colors.White);
            marker.MarkerFillColor = Colors.White;
            marker.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
            marker.MarkerStyle.OutlineWidth = 1;
            marker.MarkerStyle.OutlinePattern = LinePattern.Solid;
            plottables.Add(marker);
        }
    }
}
