using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Gated.Configurations;
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

    public void Display(IPlotControl plot, Dimension x, Dimension y)
    {
        foreach (var child in this)
            child.Display(plot, x, y);
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

    public virtual void Display(IPlotControl plot, Dimension x, Dimension y)
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
}

public class BinaryGate : GatingStrategy
{
    public override string Name { get; set; } = "Binary";
    public float Threshold { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
}

public class RangeGate : GatingStrategy
{
    public override string Name { get; set; } = "Range";
    public float Lower { get; set; }
    public float Upper { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
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
        
        // get data points
        var data = parent.GetValues(
            parent.EventCount, this.X, this.Y);
        this.XTransform.Transform(data[this.X]!);
        this.YTransform.Transform(data[this.Y]!);

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
    }

    public override void Update()
    {
        base.Update();
        
        foreach(Population parent in this.Populations.Keys) {
            
            // get data points
            var data = parent.GetValues(
                parent.EventCount, this.X, this.Y);
            this.XTransform.Transform(data[this.X]!);
            this.YTransform.Transform(data[this.Y]!);

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
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y)
    {
        base.Display(plot, x, y);
        if (x == this.X && y == this.Y)
        {
            plot.Plot.Add.HorizontalLine(this.VerticalCutoff, 1, this.color, LinePattern.Solid);
            plot.Plot.Add.VerticalLine(this.HorizontalCutoff, 1, this.color, LinePattern.Solid);
        }
    }

    public override void Display(IPlotControl plot)
    {
        base.Display(plot);
        var config = this.ParentGroup!.ScatterConfigs[this.X][this.Y];
        
        double[,] histogram = new double[config.Resolution, config.Resolution];
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

        var hm1 = plot.Plot.Add.Heatmap(histogram);
        hm1.Colormap = new Configurations.Turbo();
        hm1.CellAlignment = Alignment.LowerLeft;
        hm1.CellWidth = xstep;
        hm1.CellHeight = ystep;
    }
}

public class CurlyQuadGate : GatingStrategy
{
    public override string Name { get; set; } = "Curly";
    public float HorizontalCutoff { get; set; }
    public float VerticalCutoff { get; set; }
    public float HorizontalCurliness { get; set; }
    public float VerticalCurliness { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
    public Dimension Y { get; set; }
    public ITransform YTransform { get; set; } = new LinearTransform();
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
        this.Polygon = action.vertices;
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
        
        // get data points
        var data = parent.GetValues(
            parent.EventCount, this.X, this.Y);
        this.XTransform.Transform(data[this.X]!);
        this.YTransform.Transform(data[this.Y]!);

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
    }

    public override void Update()
    {
        base.Update();
        
        foreach(Population parent in this.Populations.Keys) {
            
            // get data points
            var data = parent.GetValues(
                parent.EventCount, this.X, this.Y);
            this.XTransform.Transform(data[this.X]!);
            this.YTransform.Transform(data[this.Y]!);

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
    }

    public override void Display(IPlotControl plot, Dimension x, Dimension y)
    {
        base.Display(plot, x, y);
        if (x == this.X && y == this.Y)
        {
            var p = plot.Plot.Add.Polygon(this.Polygon.ToArray());
            p.FillColor = Color.FromARGB(0);
            // let the palette define the color.
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
        
        double[,] histogram = new double[config.Resolution, config.Resolution];
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

        var hm1 = plot.Plot.Add.Heatmap(histogram);
        hm1.Colormap = new Configurations.Turbo();
        hm1.CellAlignment = Alignment.LowerLeft;
        hm1.CellWidth = xstep;
        hm1.CellHeight = ystep;
    }
}