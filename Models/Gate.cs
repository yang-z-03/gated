using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Gated.Configurations;
using Gated.Display;
using Gated.Preprocessing;
using ScottPlot;
using Color = ScottPlot.Color;

namespace Gated.Models;

public class GatingStrategyCollection : ObservableCollection<GatingStrategy>, INode
{
    public GatingStrategyCollection()
        : base()
    {
        this.CollectionChanged += (s, e) =>
        {
            if (e.OldItems != null)
            {
                foreach (var o in e.OldItems)
                    if (o is INode node)
                        if (this.children.Contains(node))
                            this.children.Remove(node);
            }
            
            if (e.NewItems != null)
            {
                foreach (var n in e.NewItems)
                    if (n is INode node)
                        if (!this.children.Contains(node))
                            this.children.Add(node);
            }
        };
    }

    public string Name { get; set; } = "Gates";
    public string Identifier { get; set; } = "gates";
    private ObservableCollection<INode> children = new();

    public ObservableCollection<INode> Children
    {
        get { return children; }
    }
    
    public bool IsExpanded { get; set; } = true;

    public void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        foreach (var child in this)
            child.Display(plot, x, y, xtrans, ytrans);
    }
}

public abstract class GatingStrategy : INode
{
    public GatingStrategy()
    {
        this.Name = "Gate " + gate_index.ToString();
        gate_index++;
        this.color = ScottPlot.Color.RandomHue();
    }

    internal static int gate_index = 1;
    internal Color color;
    
    public abstract string Name { get; set; }
    public virtual string Identifier { get; set; } = "gate";
    
    public bool IsExpanded { get; set; } = true;

    public ObservableCollection<INode> Children
    {
        get
        {
            ObservableCollection<INode> children = new();
            foreach (GatingStrategy child in this.Subsets) children.Add(child);
            return children;
        }
    }

    public GatingStrategy? Parent { get; private set; } = null;
    public Grouping? ParentGroup { get; set; } = null;
    public Dictionary<Population, List<Population>> Populations { get; init; } = new();
    public GatingStrategyCollection Subsets { get; private set; } = new();
    public ObservableCollection<StatisticDefinition> StatisticDefinitions { get; set; } = new();

    public virtual void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        return;
    }
    
    public virtual void Display(IPlotControl plot)
    {
        plot.Plot.Clear();
    }

    public virtual void AddPopulation(Population parent)
    {
        return;
    }

    public virtual void Update()
    {
        return;
    }

    protected void ComputeStatistics(Population parent)
    {
        if (Populations.TryGetValue(parent, out var subs))
        {
            foreach (var pop in subs)
            {
                if (pop is Subset subset)
                {
                    subset.StatisticResults = StatisticsComputer.Compute(
                        parent, subset, StatisticDefinitions);
                }
            }
        }
    }

    protected ScatterConfig TryGet1DConfig(Dimension x)
    {
        if (this.ParentGroup!.ScatterConfigs.TryGetValue(x, out var yDict))
            foreach (var kv in yDict)
                return kv.Value;
        return new ScatterConfig(x, x);
    }
}

public class BinaryGate : GatingStrategy
{
    public BinaryGate(Dimension x, ITransform xtransform, Grouping parent) : base()
    {
        this.X = x;
        this.XTransform = xtransform;
        this.ParentGroup = parent;
    }

    public BinaryGate(Dimension x, ITransform xtransform, float threshold, Grouping parent) : base()
    {
        this.X = x;
        this.XTransform = xtransform;
        this.Threshold = threshold;
        this.ParentGroup = parent;
    }

    public override string Name { get; set; } = "Binary";
    public float Threshold { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();

    public override void AddPopulation(Population parent)
    {
        base.AddPopulation(parent);

        var data = parent.GetValues(parent.EventCount, this.X);
        var x = data[this.X]!;
        List<long> low = new();
        List<long> high = new();
        for (long i = 0; i < parent.EventCount; i++)
        {
            if (x[i] < Threshold) low.Add(i);
            else high.Add(i);
        }

        if (parent is Subset s)
        {
            for (int i = 0; i < low.Count; i++) low[i] = s.Selection[low[i]];
            for (int i = 0; i < high.Count; i++) high[i] = s.Selection[high[i]];
        }

        var subsetLow = new Subset(parent, low.ToArray(), this, 0, $"{this.X.Name} low");
        var subsetHigh = new Subset(parent, high.ToArray(), this, 1, $"{this.X.Name} hi");
        this.Populations.Add(parent, new() { subsetLow, subsetHigh });

            ComputeStatistics(parent);
    }

    public override void Update()
    {
        base.Update();
        foreach (Population parent in this.Populations.Keys)
        {
            var data = parent.GetValues(parent.EventCount, this.X);
            var x = data[this.X]!;
            List<long> low = new();
            List<long> high = new();
            for (long i = 0; i < parent.EventCount; i++)
            {
                if (x[i] < Threshold) low.Add(i);
                else high.Add(i);
            }

            if (parent is Subset s)
            {
                for (int i = 0; i < low.Count; i++) low[i] = s.Selection[low[i]];
                for (int i = 0; i < high.Count; i++) high[i] = s.Selection[high[i]];
            }

            if (this.Populations[parent][0] is Subset sLow)
                sLow.Selection = low.ToArray();
            if (this.Populations[parent][1] is Subset sHigh)
                sHigh.Selection = high.ToArray();
        }
        foreach (var subset in this.Subsets)
            subset.Update();

        foreach (Population parent in this.Populations.Keys)
            ComputeStatistics(parent);
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        base.Display(plot, x, y, xtrans, ytrans);
        if (x == this.X)
        {
            float t = (float)xtrans.Transform(this.Threshold);
            plot.Plot.Add.VerticalLine(t, 1, this.color, LinePattern.Solid);
        }
    }

    public override void Display(IPlotControl plot)
    {
        base.Display(plot);
        var config = TryGet1DConfig(this.X);
        plot.Plot.Axes.SetLimitsX(config.XRange.Item1, config.XRange.Item2);
        plot.Plot.Axes.SetLimitsY(config.YRange.Item1, config.YRange.Item2);
        plot.Plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Plot.Axes.Top.FrameLineStyle.Width = 0;
        PlotHelper.SetTicks(plot.Plot.Axes.Left, config.YTransform, config.YRange.Item2);
        PlotHelper.SetTicks(plot.Plot.Axes.Bottom, config.XTransform, config.XRange.Item2);
        float t = (float)config.XTransform.Transform(this.Threshold);
        plot.Plot.Add.VerticalLine(t, 1, this.color, LinePattern.Solid);
    }

}

public class RangeGate : GatingStrategy
{
    public RangeGate(Dimension x, ITransform xtransform, Grouping parent) : base()
    {
        this.X = x;
        this.XTransform = xtransform;
        this.ParentGroup = parent;
    }

    public RangeGate(Dimension x, ITransform xtransform, float lower, float upper, Grouping parent) : base()
    {
        this.X = x;
        this.XTransform = xtransform;
        this.Lower = lower;
        this.Upper = upper;
        this.ParentGroup = parent;
    }

    public override string Name { get; set; } = "Range";
    public float Lower { get; set; }
    public float Upper { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();

    public override void AddPopulation(Population parent)
    {
        base.AddPopulation(parent);

        var data = parent.GetValues(parent.EventCount, this.X);
        var x = data[this.X]!;
        List<long> inside = new();
        List<long> outside = new();
        for (long i = 0; i < parent.EventCount; i++)
        {
            if (x[i] >= Lower && x[i] <= Upper) inside.Add(i);
            else outside.Add(i);
        }

        if (parent is Subset s)
        {
            for (int i = 0; i < inside.Count; i++) inside[i] = s.Selection[inside[i]];
            for (int i = 0; i < outside.Count; i++) outside[i] = s.Selection[outside[i]];
        }

        var subsetIn = new Subset(parent, inside.ToArray(), this, 0, $"{this.X.Name} in-range");
        var subsetOut = new Subset(parent, outside.ToArray(), this, 1, $"{this.X.Name} out-range");
        this.Populations.Add(parent, new() { subsetIn, subsetOut });

        ComputeStatistics(parent);
    }

    public override void Update()
    {
        base.Update();
        foreach (Population parent in this.Populations.Keys)
        {
            var data = parent.GetValues(parent.EventCount, this.X);
            var x = data[this.X]!;
            List<long> inside = new();
            List<long> outside = new();
            for (long i = 0; i < parent.EventCount; i++)
            {
                if (x[i] >= Lower && x[i] <= Upper) inside.Add(i);
                else outside.Add(i);
            }

            if (parent is Subset s)
            {
                for (int i = 0; i < inside.Count; i++) inside[i] = s.Selection[inside[i]];
                for (int i = 0; i < outside.Count; i++) outside[i] = s.Selection[outside[i]];
            }

            if (this.Populations[parent][0] is Subset sIn)
                sIn.Selection = inside.ToArray();
            if (this.Populations[parent][1] is Subset sOut)
                sOut.Selection = outside.ToArray();
        }
        foreach (var subset in this.Subsets)
            subset.Update();

        foreach (Population parent in this.Populations.Keys)
            ComputeStatistics(parent);
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        base.Display(plot, x, y, xtrans, ytrans);
        if (x == this.X)
        {
            float l = (float)xtrans.Transform(this.Lower);
            float u = (float)xtrans.Transform(this.Upper);
            plot.Plot.Add.VerticalLine(l, 1, this.color, LinePattern.Solid);
            plot.Plot.Add.VerticalLine(u, 1, this.color, LinePattern.Solid);
        }
    }

    public override void Display(IPlotControl plot)
    {
        base.Display(plot);
        var config = TryGet1DConfig(this.X);
        plot.Plot.Axes.SetLimitsX(config.XRange.Item1, config.XRange.Item2);
        plot.Plot.Axes.SetLimitsY(config.YRange.Item1, config.YRange.Item2);
        plot.Plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Plot.Axes.Top.FrameLineStyle.Width = 0;
        PlotHelper.SetTicks(plot.Plot.Axes.Left, config.YTransform, config.YRange.Item2);
        PlotHelper.SetTicks(plot.Plot.Axes.Bottom, config.XTransform, config.XRange.Item2);
        float l = (float)config.XTransform.Transform(this.Lower);
        float u = (float)config.XTransform.Transform(this.Upper);
        plot.Plot.Add.VerticalLine(l, 1, this.color, LinePattern.Solid);
        plot.Plot.Add.VerticalLine(u, 1, this.color, LinePattern.Solid);
    }
}

public class QuadGate : GatingStrategy
{
    public QuadGate(ScatterConfig config, Grouping parent) : base()
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        this.ParentGroup = parent;
    }
    
    public QuadGate(ScatterConfig config, float hcutoff, float vcutoff, Grouping parent) : base()
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        this.HorizontalCutoff = hcutoff;
        this.VerticalCutoff = vcutoff;
        this.ParentGroup = parent;
    }
    
    public override string Name { get; set; } = "Quad";
    public float HorizontalCutoff { get; set; }
    public float VerticalCutoff { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
    public Dimension Y { get; set; }
    public ITransform YTransform { get; set; } = new LinearTransform();
    
    public override void AddPopulation(Population parent)
    {
        base.AddPopulation(parent);

        var data = parent.GetValues(
            parent.EventCount, this.X, this.Y);
        var x = data[this.X]!;
        var y = data[this.Y]!;
        List<long> selection_1 = new();
        List<long> selection_2 = new();
        List<long> selection_3 = new();
        List<long> selection_4 = new();
        for(long i = 0; i < parent.EventCount; i++)
            if (x[i] < HorizontalCutoff)
            {
                if (y[i] > VerticalCutoff) selection_1.Add(i);
                else selection_4.Add(i);
            }
            else
            {
                if (y[i] > VerticalCutoff) selection_2.Add(i);
                else selection_3.Add(i);
            }

        if (parent is Subset s)
        {
            for (int i = 0; i < selection_1.Count; i++)
                selection_1[i] = s.Selection[selection_1[i]];
            for (int i = 0; i < selection_2.Count; i++)
                selection_2[i] = s.Selection[selection_2[i]];
            for (int i = 0; i < selection_3.Count; i++)
                selection_3[i] = s.Selection[selection_3[i]];
            for (int i = 0; i < selection_4.Count; i++)
                selection_4[i] = s.Selection[selection_4[i]];
        }

        var subset_1 = new Subset(
            parent, selection_1.ToArray(), this, 0, $"{this.X.Name} low, {this.Y.Name} hi");
        var subset_2 = new Subset(
            parent, selection_2.ToArray(), this, 1, $"{this.X.Name} hi, {this.Y.Name} hi");
        var subset_3 = new Subset(
            parent, selection_3.ToArray(), this, 2, $"{this.X.Name} hi, {this.Y.Name} low");
        var subset_4 = new Subset(
            parent, selection_4.ToArray(), this, 3, $"{this.X.Name} low, {this.Y.Name} low");
        
        this.Populations.Add(parent, new()
        {
            subset_1, subset_2, subset_3, subset_4
        });

        ComputeStatistics(parent);
    }

    public override void Update()
    {
        base.Update();
        
        foreach(Population parent in this.Populations.Keys) {
            
            var data = parent.GetValues(
                parent.EventCount, this.X, this.Y);
            var x = data[this.X]!;
            var y = data[this.Y]!;
            List<long> selection_1 = new();
            List<long> selection_2 = new();
            List<long> selection_3 = new();
            List<long> selection_4 = new();
            for(long i = 0; i < parent.EventCount; i++)
                if (x[i] < HorizontalCutoff)
                {
                    if (y[i] > VerticalCutoff) selection_1.Add(i);
                    else selection_4.Add(i);
                }
                else
                {
                    if (y[i] > VerticalCutoff) selection_2.Add(i);
                    else selection_3.Add(i);
                }

            if (parent is Subset s)
            {
                for (int i = 0; i < selection_1.Count; i++)
                    selection_1[i] = s.Selection[selection_1[i]];
                for (int i = 0; i < selection_2.Count; i++)
                    selection_2[i] = s.Selection[selection_2[i]];
                for (int i = 0; i < selection_3.Count; i++)
                    selection_3[i] = s.Selection[selection_3[i]];
                for (int i = 0; i < selection_4.Count; i++)
                    selection_4[i] = s.Selection[selection_4[i]];
            }

            if (this.Populations[parent][0] is Subset s1)
                s1.Selection = selection_1.ToArray();
            if (this.Populations[parent][1] is Subset s2)
                s2.Selection = selection_2.ToArray();
            if (this.Populations[parent][2] is Subset s3)
                s3.Selection = selection_3.ToArray();
            if (this.Populations[parent][3] is Subset s4)
                s4.Selection = selection_4.ToArray();
            else throw new Exception();
        }

        foreach(var subset in this.Subsets)
            subset.Update();

        foreach (Population parent in this.Populations.Keys)
            ComputeStatistics(parent);
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        base.Display(plot, x, y, xtrans, ytrans);
        if (x == this.X && y == this.Y)
        {
            float hc = (float)xtrans.Transform(this.HorizontalCutoff);
            float vc = (float)ytrans.Transform(this.VerticalCutoff);
            plot.Plot.Add.VerticalLine(hc, 1, this.color, LinePattern.Solid);
            plot.Plot.Add.HorizontalLine(vc, 1, this.color, LinePattern.Solid);
        }
    }

    public override void Display(IPlotControl plot)
    {
        base.Display(plot);
        var config = this.ParentGroup!.ScatterConfigs[this.X][this.Y];
        
        plot.Plot.Axes.SetLimitsX(config.XRange.Item1, config.XRange.Item2);
        plot.Plot.Axes.SetLimitsY(config.YRange.Item1, config.YRange.Item2);
        
        // hide axis edge line
        plot.Plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Plot.Axes.Top.FrameLineStyle.Width = 0;
        
        // set ticks.
        // reverse transform to origin scale.
        PlotHelper.SetTicks(plot.Plot.Axes.Left, config.YTransform, config.YRange.Item2);
        PlotHelper.SetTicks(plot.Plot.Axes.Bottom, config.XTransform, config.XRange.Item2);
        plot.Plot.Axes.Bottom.TickLabelStyle.Rotation = -90;
        plot.Plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;
        plot.Plot.Axes.Bottom.MinimumSize = 45;
        plot.Plot.Axes.Left.MinimumSize = 45;
        
        int[,] histogram = new int[config.Resolution, config.Resolution];
        float xstep = (config.XRange.Item2 - config.XRange.Item1) / config.Resolution;
        float ystep = (config.YRange.Item2 - config.YRange.Item1) / config.Resolution;

        foreach (var pop in this.Populations.Keys)
        {
            // force show all points when editing gates.
            var dict = pop.GetValues(pop.EventCount, this.X, this.Y);
            var xs = dict[this.X]!;
            var ys = dict[this.Y]!;
            for (int i = 0; i < xs.Length; i++)
            {
                histogram[
                    config.Resolution - Math.Max(1,
                        Math.Min(config.Resolution, Convert.ToInt32((ys[i] - config.YRange.Item1) / ystep))),
                    Math.Max(0, Math.Min(config.Resolution - 1, Convert.ToInt32((xs[i] - config.XRange.Item1) / xstep)))
                ]++;
            }
        }

        double[,] ord = PlotHelper.Order(histogram, config.Resolution);
        var hm1 = plot.Plot.Add.Heatmap(ord);
        hm1.Colormap = new Configurations.Turbo();
        hm1.CellAlignment = Alignment.LowerLeft;
        hm1.CellWidth = xstep;
        hm1.CellHeight = ystep;
    }
}

public class CurlyQuadGate : GatingStrategy
{
    public CurlyQuadGate(ScatterConfig config, Grouping parent) : base()
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        this.ParentGroup = parent;
    }

    public CurlyQuadGate(ScatterConfig config, float hcutoff, float vcutoff,
        float hCurliness, float vCurliness, Grouping parent) : base()
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        this.HorizontalCutoff = hcutoff;
        this.VerticalCutoff = vcutoff;
        this.HorizontalCurliness = hCurliness;
        this.VerticalCurliness = vCurliness;
        this.ParentGroup = parent;
    }

    public override string Name { get; set; } = "Curly";
    public float HorizontalCutoff { get; set; }
    public float VerticalCutoff { get; set; }
    // Plot-space anchor positions for curvature:
    // HorizontalCurliness = X position where vertical boundary meets the top plot edge
    // VerticalCurliness   = Y position where horizontal boundary meets the right plot edge
    public float HorizontalCurliness { get; set; }
    public float VerticalCurliness { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
    public Dimension Y { get; set; }
    public ITransform YTransform { get; set; } = new LinearTransform();

    private static bool inside_curved_quadrant(
        float x, float y,
        float hCutoff, float vCutoff,
        float plotMaxX, float plotMaxY,
        float topAnchorX, float rightAnchorY)
    {
        // Determine which side of the curved boundaries the point falls on.
        // The quadrants are defined as in QuadGate but with curved transitions.
        bool below = false; // default: low-zone

        if (y > vCutoff)
        {
            // Above crosshair — use curved vertical boundary
            double normY = (y - vCutoff) / (plotMaxY - vCutoff);
            double xBound = hCutoff + (topAnchorX - hCutoff) * normY * normY;
            if (x >= xBound) below = true; // right of curved boundary
        }
        else
        {
            // Below crosshair — use straight vertical line
            if (x >= hCutoff) below = true;
        }

        if (x > hCutoff)
        {
            // Right of crosshair — use curved horizontal boundary
            double normX = (x - hCutoff) / (plotMaxX - hCutoff);
            double yBound = vCutoff + (rightAnchorY - vCutoff) * normX * normX;
            if (y >= yBound) below = !below; // above curved boundary toggles quad
        }
        else
        {
            // Left of crosshair — use straight horizontal line
            if (y >= vCutoff) below = !below;
        }

        return below;
    }

    public override void AddPopulation(Population parent)
    {
        base.AddPopulation(parent);

        var data = parent.GetValues(parent.EventCount, this.X, this.Y);
        var rawX = data[this.X]!;
        var rawY = data[this.Y]!;

        // Transform data to plot space for curved boundary testing
        float[] xs = (float[])rawX.Clone();
        float[] ys = (float[])rawY.Clone();
        this.XTransform.Transform(xs);
        this.YTransform.Transform(ys);

        float plotMaxX = this.XTransform.Transform(this.X.Maximum);
        float plotMaxY = this.YTransform.Transform(this.Y.Maximum);

        List<long> sel1 = new(), sel2 = new(), sel3 = new(), sel4 = new();
        for (long i = 0; i < parent.EventCount; i++)
        {
            if (inside_curved_quadrant(xs[i], ys[i],
                    this.HorizontalCutoff, this.VerticalCutoff,
                    plotMaxX, plotMaxY,
                    this.HorizontalCurliness, this.VerticalCurliness))
            {
                if (ys[i] >= this.VerticalCutoff) sel2.Add(i);
                else sel4.Add(i);
            }
            else
            {
                if (ys[i] >= this.VerticalCutoff) sel1.Add(i);
                else sel3.Add(i);
            }
        }

        if (parent is Subset s)
        {
            for (int i = 0; i < sel1.Count; i++) sel1[i] = s.Selection[sel1[i]];
            for (int i = 0; i < sel2.Count; i++) sel2[i] = s.Selection[sel2[i]];
            for (int i = 0; i < sel3.Count; i++) sel3[i] = s.Selection[sel3[i]];
            for (int i = 0; i < sel4.Count; i++) sel4[i] = s.Selection[sel4[i]];
        }

        var s1 = new Subset(parent, sel1.ToArray(), this, 0, $"{this.X.Name} lo, {this.Y.Name} hi");
        var s2 = new Subset(parent, sel2.ToArray(), this, 1, $"{this.X.Name} hi, {this.Y.Name} hi");
        var s3 = new Subset(parent, sel3.ToArray(), this, 2, $"{this.X.Name} hi, {this.Y.Name} lo");
        var s4 = new Subset(parent, sel4.ToArray(), this, 3, $"{this.X.Name} lo, {this.Y.Name} lo");
        this.Populations.Add(parent, new() { s1, s2, s3, s4 });

        ComputeStatistics(parent);
    }

    public override void Update()
    {
        base.Update();
        foreach (Population parent in this.Populations.Keys)
        {
            var data = parent.GetValues(parent.EventCount, this.X, this.Y);
            var rawX = data[this.X]!;
            var rawY = data[this.Y]!;

            float[] xs = (float[])rawX.Clone();
            float[] ys = (float[])rawY.Clone();
            this.XTransform.Transform(xs);
            this.YTransform.Transform(ys);

            float plotMaxX = this.XTransform.Transform(this.X.Maximum);
            float plotMaxY = this.YTransform.Transform(this.Y.Maximum);

            List<long> sel1 = new(), sel2 = new(), sel3 = new(), sel4 = new();
            for (long i = 0; i < parent.EventCount; i++)
            {
                if (inside_curved_quadrant(xs[i], ys[i],
                        this.HorizontalCutoff, this.VerticalCutoff,
                        plotMaxX, plotMaxY,
                        this.HorizontalCurliness, this.VerticalCurliness))
                {
                    if (ys[i] >= this.VerticalCutoff) sel2.Add(i);
                    else sel4.Add(i);
                }
                else
                {
                    if (ys[i] >= this.VerticalCutoff) sel1.Add(i);
                    else sel3.Add(i);
                }
            }

            if (parent is Subset s)
            {
                for (int i = 0; i < sel1.Count; i++) sel1[i] = s.Selection[sel1[i]];
                for (int i = 0; i < sel2.Count; i++) sel2[i] = s.Selection[sel2[i]];
                for (int i = 0; i < sel3.Count; i++) sel3[i] = s.Selection[sel3[i]];
                for (int i = 0; i < sel4.Count; i++) sel4[i] = s.Selection[sel4[i]];
            }

            if (this.Populations[parent][0] is Subset s1) s1.Selection = sel1.ToArray();
            if (this.Populations[parent][1] is Subset s2) s2.Selection = sel2.ToArray();
            if (this.Populations[parent][2] is Subset s3) s3.Selection = sel3.ToArray();
            if (this.Populations[parent][3] is Subset s4) s4.Selection = sel4.ToArray();
        }
        foreach (var subset in this.Subsets)
            subset.Update();

        foreach (Population parent in this.Populations.Keys)
            ComputeStatistics(parent);
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        base.Display(plot, x, y, xtrans, ytrans);
        if (x == this.X && y == this.Y)
        {
            float hc = (float)xtrans.Transform(this.HorizontalCutoff);
            float vc = (float)ytrans.Transform(this.VerticalCutoff);
            // Draw curved boundaries by sampling points along the curve
            float plotMaxX = xtrans.Transform(x.Maximum);
            float plotMaxY = ytrans.Transform(y.Maximum);
            int samples = 50;

            // Vertical curved boundary (above crosshair)
            Coordinates[] vCurve = new Coordinates[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / (samples - 1);
                float yPos = vc + t * (plotMaxY - vc);
                float xPos = hc + (this.HorizontalCurliness - hc) * t * t;
                vCurve[i] = new Coordinates(xPos, yPos);
            }
            var vLine = plot.Plot.Add.Scatter(vCurve);
            vLine.LineColor = this.color;
            vLine.LineWidth = 1;
            vLine.MarkerSize = 0;

            // Horizontal curved boundary (right of crosshair)
            Coordinates[] hCurve = new Coordinates[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / (samples - 1);
                float xPos = hc + t * (plotMaxX - hc);
                float yPos = vc + (this.VerticalCurliness - vc) * t * t;
                hCurve[i] = new Coordinates(xPos, yPos);
            }
            var hLine = plot.Plot.Add.Scatter(hCurve);
            hLine.LineColor = this.color;
            hLine.LineWidth = 1;
            hLine.MarkerSize = 0;

            // Straight lines below and left of crosshair
            plot.Plot.Add.VerticalLine(hc, 1, this.color, LinePattern.Solid);
        }
    }

    public override void Display(IPlotControl plot)
    {
        base.Display(plot);
        var config = this.ParentGroup!.ScatterConfigs[this.X][this.Y];
        plot.Plot.Axes.SetLimitsX(config.XRange.Item1, config.XRange.Item2);
        plot.Plot.Axes.SetLimitsY(config.YRange.Item1, config.YRange.Item2);
        plot.Plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Plot.Axes.Top.FrameLineStyle.Width = 0;
        PlotHelper.SetTicks(plot.Plot.Axes.Left, config.YTransform, config.YRange.Item2);
        PlotHelper.SetTicks(plot.Plot.Axes.Bottom, config.XTransform, config.XRange.Item2);

        // Draw concatenated scatter from all parent populations
        int[,] histogram = new int[config.Resolution, config.Resolution];
        float xstep = (config.XRange.Item2 - config.XRange.Item1) / config.Resolution;
        float ystep = (config.YRange.Item2 - config.YRange.Item1) / config.Resolution;

        foreach (var pop in this.Populations.Keys)
        {
            var dict = pop.GetValues(pop.EventCount, this.X, this.Y);
            var xs = dict[this.X]!;
            var ys = dict[this.Y]!;
            for (int i = 0; i < xs.Length; i++)
            {
                histogram[
                    config.Resolution - Math.Max(1,
                        Math.Min(config.Resolution, Convert.ToInt32((ys[i] - config.YRange.Item1) / ystep))),
                    Math.Max(0, Math.Min(config.Resolution - 1, Convert.ToInt32((xs[i] - config.XRange.Item1) / xstep)))
                ]++;
            }
        }

        double[,] ord = PlotHelper.Order(histogram, config.Resolution);
        var hm1 = plot.Plot.Add.Heatmap(ord);
        hm1.Colormap = new Configurations.Turbo();
        hm1.CellAlignment = Alignment.LowerLeft;
        hm1.CellWidth = xstep;
        hm1.CellHeight = ystep;

        // Draw full curved boundaries in the edit view
        float hc = (float)config.XTransform.Transform(this.HorizontalCutoff);
        float vc = (float)config.YTransform.Transform(this.VerticalCutoff);
        float plotMaxX = config.XRange.Item2;
        float plotMaxY = config.YRange.Item2;
        int samples = 50;

        Coordinates[] vCurve = new Coordinates[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            float yPos = vc + t * (plotMaxY - vc);
            float xPos = hc + (this.HorizontalCurliness - hc) * t * t;
            vCurve[i] = new Coordinates(Math.Max(0, xPos), yPos);
        }
        var vLine = plot.Plot.Add.Scatter(vCurve);
        vLine.LineColor = this.color; vLine.LineWidth = 1; vLine.MarkerSize = 0;

        Coordinates[] hCurve = new Coordinates[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            float xPos = hc + t * (plotMaxX - hc);
            float yPos = vc + (this.VerticalCurliness - vc) * t * t;
            hCurve[i] = new Coordinates(xPos, Math.Max(0, yPos));
        }
        var hLine = plot.Plot.Add.Scatter(hCurve);
        hLine.LineColor = this.color; vLine.LineWidth = 1; hLine.MarkerSize = 0;

        plot.Plot.Add.VerticalLine(hc, 1, this.color, LinePattern.Solid);
        plot.Plot.Add.HorizontalLine(vc, 1, this.color, LinePattern.Solid);
    }
}

public class PolygonalGate : GatingStrategy
{
    public PolygonalGate(ScatterConfig config, Grouping parent) : base()
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        this.ParentGroup = parent;
    }
    
    public PolygonalGate(ScatterConfig config, Actions.Polygon action, Grouping parent) : base()
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        // Inverse-transform plot-space vertices to original space
        this.Polygon = action.vertices.Select(v => new Coordinates(
            config.XTransform.InverseTransform(v.X),
            config.YTransform.InverseTransform(v.Y)
        )).ToList();
        this.ParentGroup = parent;
    }
    
    public override string Name { get; set; } = "Polygonal";
    public List<Coordinates> Polygon { get; set; } = new();
    
    // dimension definitions
    public Dimension X { get; init; }
    public ITransform XTransform { get; init; }
    public Dimension Y { get; init; }
    public ITransform YTransform { get; init; }

    private Coordinates center()
    {
        double x = 0;
        double y = 0;
        foreach (var p in this.Polygon)
        {
            x += p.X;
            y += p.Y;
        }

        x /= this.Polygon.Count;
        y /= this.Polygon.Count;
        return new Coordinates(x, y);
    }
    
    internal static bool inside_polygon(
        Coordinates checkPoint, 
        List<Coordinates> polygonPoints)
    {
        bool inside = false;
        int pointCount = polygonPoints.Count;
        Coordinates p1, p2;
        for (int i = 0, j = pointCount - 1; i < pointCount; j = i, i++)
        {
            p1 = polygonPoints[i];
            p2 = polygonPoints[j];
            if (checkPoint.Y < p2.Y)
            {
                if (p1.Y <= checkPoint.Y)
                    if ((checkPoint.Y - p1.Y) * (p2.X - p1.X) > (checkPoint.X - p1.X) * (p2.Y - p1.Y))
                        inside = (!inside);
            }
            else if (checkPoint.Y < p1.Y)
                if ((checkPoint.Y - p1.Y) * (p2.X - p1.X) < (checkPoint.X - p1.X) * (p2.Y - p1.Y))
                    inside = (!inside);
        }
        
        return inside;
    }

    public override void AddPopulation(Population parent)
    {
        base.AddPopulation(parent);

        var data = parent.GetValues(
            parent.EventCount, this.X, this.Y);
        var x = data[this.X]!;
        var y = data[this.Y]!;
        List<long> selection = new();
        for(long i = 0; i < parent.EventCount; i++)
            if (inside_polygon(new Coordinates(x[i], y[i]), this.Polygon))
                selection.Add(i);

        if (parent is Subset s)
        {
            for (int i = 0; i < selection.Count; i++)
                selection[i] = s.Selection[selection[i]];
        }

        var subset = new Subset(
            parent, selection.ToArray(), this, 0, this.Name);
        
        this.Populations.Add(parent, new(){subset});

            ComputeStatistics(parent);
    }

    public override void Update()
    {
        base.Update();
        
        foreach(Population parent in this.Populations.Keys) {

            var data = parent.GetValues(
                parent.EventCount, this.X, this.Y);
            var x = data[this.X]!;
            var y = data[this.Y]!;
            List<long> selection = new();
            for (long i = 0; i < parent.EventCount; i++)
                if (inside_polygon(new Coordinates(x[i], y[i]), this.Polygon))
                    selection.Add(i);

            if (parent is Subset s)
            {
                for (int i = 0; i < selection.Count; i++)
                    selection[i] = s.Selection[selection[i]];
            }

            if (this.Populations[parent][0] is Subset s2)
                s2.Selection = selection.ToArray();
            else throw new Exception();
        }

        foreach(var subset in this.Subsets)
            subset.Update();

        foreach (Population parent in this.Populations.Keys)
            ComputeStatistics(parent);
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y, ITransform xtrans, ITransform ytrans)
    {
        base.Display(plot, x, y, xtrans, ytrans);
        if (x == this.X && y == this.Y)
        {
            var transformedPolygon = this.Polygon.Select(v => new Coordinates(
                xtrans.Transform(v.X),
                ytrans.Transform(v.Y)
            )).ToArray();
            var p = plot.Plot.Add.Polygon(transformedPolygon);
            p.FillColor = Color.FromARGB(0);
            p.LineColor = this.color;
            p.LinePattern = LinePattern.Solid;
            p.LineWidth = 1;
            p.MarkerShape = MarkerShape.None;

            var t = plot.Plot.Add.Text(this.Name, center());
            t.Alignment = Alignment.MiddleCenter;
            t.LabelFontColor = Color.FromHex("#ffffff00");
        }
    }

    public override void Display(IPlotControl plot)
    {
        base.Display(plot);
        var config = this.ParentGroup!.ScatterConfigs[this.X][this.Y];
        
        plot.Plot.Axes.SetLimitsX(config.XRange.Item1, config.XRange.Item2);
        plot.Plot.Axes.SetLimitsY(config.YRange.Item1, config.YRange.Item2);
        
        // hide axis edge line
        plot.Plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Plot.Axes.Top.FrameLineStyle.Width = 0;
        
        // set ticks.
        // reverse transform to origin scale.
        PlotHelper.SetTicks(plot.Plot.Axes.Left, config.YTransform, config.YRange.Item2);
        PlotHelper.SetTicks(plot.Plot.Axes.Bottom, config.XTransform, config.XRange.Item2);
        plot.Plot.Axes.Bottom.TickLabelStyle.Rotation = -90;
        plot.Plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;
        plot.Plot.Axes.Bottom.MinimumSize = 45;
        plot.Plot.Axes.Left.MinimumSize = 45;
        
        int[,] histogram = new int[config.Resolution, config.Resolution];
        float xstep = (config.XRange.Item2 - config.XRange.Item1) / config.Resolution;
        float ystep = (config.YRange.Item2 - config.YRange.Item1) / config.Resolution;

        foreach (var pop in this.Populations.Keys)
        {
            // force show all points when editing gates.
            var dict = pop.GetValues(pop.EventCount, this.X, this.Y);
            var xs = dict[this.X]!;
            var ys = dict[this.Y]!;
            config.XTransform.Transform(xs);
            config.YTransform.Transform(ys);
            for (int i = 0; i < xs.Length; i++)
            {
                histogram[
                    config.Resolution - Math.Max(1,
                        Math.Min(config.Resolution, Convert.ToInt32((ys[i] - config.YRange.Item1) / ystep))),
                    Math.Max(0, Math.Min(config.Resolution - 1, Convert.ToInt32((xs[i] - config.XRange.Item1) / xstep)))
                ]++;
            }
        }

        double[,] ord = PlotHelper.Order(histogram, config.Resolution);
        var hm1 = plot.Plot.Add.Heatmap(ord);
        hm1.Colormap = new Configurations.Turbo();
        hm1.CellAlignment = Alignment.LowerLeft;
        hm1.CellWidth = xstep;
        hm1.CellHeight = ystep;
    }
}
