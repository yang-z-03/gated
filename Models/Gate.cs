using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
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
    public GatingStrategyCollection Subsets { get; private set; } = new();

    public virtual void Display(IPlotControl plot, Dimension x, Dimension y)
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
    public override string Name { get; set; } = "Quad";
    public float HorizontalCutoff { get; set; }
    public float VerticalCutoff { get; set; }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
    public Dimension Y { get; set; }
    public ITransform YTransform { get; set; } = new LinearTransform();
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
    public PolygonalGate(ScatterConfig config)
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
    }
    
    public PolygonalGate(ScatterConfig config, Actions.Polygon action)
    {
        this.X = config.X;
        this.Y = config.Y;
        this.XTransform = config.XTransform;
        this.YTransform = config.YTransform;
        this.Polygon = action.vertices;
    }
    
    public override string Name { get; set; } = "Polygonal";
    public List<Coordinates> Polygon { get; set; } = new();
    
    // dimension definitions
    public Dimension X { get; init; }
    public ITransform XTransform { get; init; }
    public Dimension Y { get; init; }
    public ITransform YTransform { get; init; }

    public override void Display(IPlotControl plot, Dimension x, Dimension y)
    {
        base.Display(plot, x, y);
        if (x == this.X && y == this.Y)
        {
            var p = plot.Plot.Add.Polygon(this.Polygon.ToArray());
            p.FillColor = Color.FromHex("#00000000");
            // let the palette define the color.
            p.LinePattern = LinePattern.Solid;
            p.LineWidth = 1;
            p.MarkerShape = MarkerShape.None;
        }
    }
}