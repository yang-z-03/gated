using System;
using System.Collections.Generic;
using Gated.Models;
using ScottPlot;
using ScottPlot.Interactivity;

namespace Gated.Actions;

public class CurlyQuad(MouseButton button) : IUserActionResponse, IGatingAction
{
    public CurlyQuad(MouseButton button, bool editLocked) : this(button)
    {
        this.edit_locked = editLocked;
    }

    public CurlyQuad(IPlotControl control, CurlyQuadGate gate, MouseButton button) : this(button)
    {
        this.crosshair = new Coordinates(
            gate.XTransform.Transform(gate.HorizontalCutoff),
            gate.YTransform.Transform(gate.VerticalCutoff));
        this.topAnchorX = gate.HorizontalCurliness;
        this.rightAnchorY = gate.VerticalCurliness;
        this.placed = true;
        this.crosshairPx = control.Plot.GetPixel(this.crosshair ?? new Coordinates());
        this.edit_locked = true;
        this.defined_gate = gate;
        this.apply(control.Plot);
    }

    public MouseButton MouseButton { get; } = button;
    public int Tolerance { get; } = 4;
    internal Coordinates? crosshair;
    internal float topAnchorX, rightAnchorY;
    internal bool placed = false;
    internal CurlyQuadGate? defined_gate = null;
    private Pixel? crosshairPx;
    private List<IPlottable> plottables = new();
    private bool edit_locked = false;
    private bool edit_mode = false;
    private enum DragTarget { None, Crosshair, TopAnchor, RightAnchor }
    private DragTarget dragTarget = DragTarget.None;

    public event EventHandler? GateDefined;
    public event EventHandler? GateUpdated;

    public void ResetState(IPlotControl plotControl) { return; }

    private void reset_state(IPlotControl plotControl)
    {
        crosshair = null; crosshairPx = null; placed = false;
        topAnchorX = 0; rightAnchorY = 0;
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

            if (placed)
            {
                bool hitCross = crosshairPx != null &&
                    Math.Abs(mouseDownAction.Pixel.X - crosshairPx.Value.X) <= Tolerance &&
                    Math.Abs(mouseDownAction.Pixel.Y - crosshairPx.Value.Y) <= Tolerance;
                bool hitTop = crosshairPx != null &&
                    Math.Abs(mouseDownAction.Pixel.Y - plot.GetPixel(new Coordinates(0, limits.Top)).Y) <= Tolerance * 2 &&
                    Math.Abs(mouseDownAction.Pixel.X - plot.GetPixel(new Coordinates(topAnchorX, 0)).X) <= Tolerance * 2;
                bool hitRight = crosshairPx != null &&
                    Math.Abs(mouseDownAction.Pixel.X - plot.GetPixel(new Coordinates(limits.Right, 0)).X) <= Tolerance * 2 &&
                    Math.Abs(mouseDownAction.Pixel.Y - plot.GetPixel(new Coordinates(0, rightAnchorY)).Y) <= Tolerance * 2;

                if (hitCross) { dragTarget = DragTarget.Crosshair; edit_mode = true; }
                else if (hitTop) { dragTarget = DragTarget.TopAnchor; edit_mode = true; }
                else if (hitRight) { dragTarget = DragTarget.RightAnchor; edit_mode = true; }
                else if (!edit_locked) reset_state(plotControl);
                else return ResponseInfo.NoActionRequired;
                return ResponseInfo.NoActionRequired;
            }

            // First click: define crosshair
            this.crosshair = current;
            this.crosshairPx = mouseDownAction.Pixel;
            this.topAnchorX = (float)current.X;
            this.rightAnchorY = (float)current.Y;
            this.placed = true;
            this.edit_mode = true;
            this.apply(plot);
            this.GateDefined?.Invoke(this, EventArgs.Empty);
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseButtonAction mouseUpAction
            && mouseUpAction.Button == MouseButton
            && !mouseUpAction.IsPressed)
        {
            if (edit_mode && placed)
                this.GateUpdated?.Invoke(this, EventArgs.Empty);

            if (edit_mode)
                edit_mode = false;

            dragTarget = DragTarget.None;
            return ResponseInfo.Refresh;
        }

        if (userInput is IMouseAction mouseMoveAction && edit_mode)
        {
            Plot? plot = plotControl.GetPlotAtPixel(mouseMoveAction.Pixel);
            if (plot == null) return ResponseInfo.NoActionRequired;
            Coordinates current = plot.GetCoordinates(mouseMoveAction.Pixel);

            switch (dragTarget)
            {
                case DragTarget.Crosshair:
                    this.crosshair = current;
                    this.crosshairPx = mouseMoveAction.Pixel;
                    break;
                case DragTarget.TopAnchor:
                    this.topAnchorX = (float)current.X;
                    break;
                case DragTarget.RightAnchor:
                    this.rightAnchorY = (float)current.Y;
                    break;
            }
            this.apply(plot);
            return ResponseInfo.Refresh;
        }

        return ResponseInfo.NoActionRequired;
    }

    private void apply(Plot plot)
    {
        foreach (var pl in plottables) plot.Remove(pl);
        plottables.Clear();
        if (crosshair == null) return;

        float hc = (float)crosshair.Value.X;
        float vc = (float)crosshair.Value.Y;
        var limits = plot.Axes.GetLimits();
        float plotMaxX = (float)limits.Right;
        float plotMaxY = (float)limits.Top;
        int samples = 50;

        // Vertical curved boundary
        Coordinates[] vCurve = new Coordinates[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            float yPos = vc + t * (plotMaxY - vc);
            float xPos = hc + (this.topAnchorX - hc) * t * t;
            vCurve[i] = new Coordinates(Math.Max(0, xPos), yPos);
        }
        var vLine = plot.Add.Scatter(vCurve);
        vLine.LineColor = Colors.Black; vLine.LineWidth = 1; vLine.MarkerSize = 0;
        plottables.Add(vLine);

        // Horizontal curved boundary
        Coordinates[] hCurve = new Coordinates[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            float xPos = hc + t * (plotMaxX - hc);
            float yPos = vc + (this.rightAnchorY - vc) * t * t;
            hCurve[i] = new Coordinates(xPos, Math.Max(0, yPos));
        }
        var hLine = plot.Add.Scatter(hCurve);
        hLine.LineColor = Colors.Black; hLine.LineWidth = 1; hLine.MarkerSize = 0;
        plottables.Add(hLine);

        // Straight lines below and left of crosshair
        if (placed)
        {
            var vl = plot.Add.VerticalLine(hc, 1, Colors.DarkGray, LinePattern.Solid);
            plottables.Add(vl);
            var hl = plot.Add.HorizontalLine(vc, 1, Colors.DarkGray, LinePattern.Solid);
            plottables.Add(hl);
        }

        // Three anchor markers — matching Quad/Polygon style
        if (placed)
        {
            var crossM = plot.Add.Marker(crosshair ?? new(), MarkerShape.FilledSquare, 8, Colors.White);
            crossM.MarkerFillColor = Colors.White;
            crossM.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
            crossM.MarkerStyle.OutlineWidth = 1;
            crossM.MarkerStyle.OutlinePattern = LinePattern.Solid;
            plottables.Add(crossM);

            var topM = plot.Add.Marker(new Coordinates(topAnchorX, plotMaxY), MarkerShape.FilledSquare, 8, Colors.White);
            topM.MarkerFillColor = Colors.White;
            topM.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
            topM.MarkerStyle.OutlineWidth = 1;
            topM.MarkerStyle.OutlinePattern = LinePattern.Solid;
            plottables.Add(topM);

            var rightM = plot.Add.Marker(new Coordinates(plotMaxX, rightAnchorY), MarkerShape.FilledSquare, 8, Colors.White);
            rightM.MarkerFillColor = Colors.White;
            rightM.MarkerStyle.OutlineColor = Color.FromHex("#135CB8");
            rightM.MarkerStyle.OutlineWidth = 1;
            rightM.MarkerStyle.OutlinePattern = LinePattern.Solid;
            plottables.Add(rightM);
        }
    }
}
