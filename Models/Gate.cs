using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;

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
            foreach (Gate child in this.Subsets) children.Add(child.Population);
            return children;
        }
    }

    public Population? Parent { get; private set; } = null;
    public ObservableCollection<Gate> Subsets { get; private set; } = new();
}

public class Gate
{
    public string Name { get; set; } = string.Empty;
    public Subset Population { get; private set; } = new();
    public Population? Parent { get; private set; } = null;
    public required GatingStrategy Strategy { get; set; }
}

public class BinaryGate : GatingStrategy
{
    public override string Name { get; set; } = "Binary";
    public float Threshold { get; set; }
}

public class RangeGate : GatingStrategy
{
    public override string Name { get; set; } = "Range";
    public float Lower { get; set; }
    public float Upper { get; set; }
}

public class QuadGate : GatingStrategy
{
    public override string Name { get; set; } = "Quad";
    public float X { get; set; }
    public float Y { get; set; }
}

public class CurlyQuadGate : GatingStrategy
{
    public override string Name { get; set; } = "Curly";
    public float X { get; set; }
    public float Y { get; set; }
    public float HorizontalCurliness { get; set; }
    public float VerticalCurliness { get; set; }
}

public class PolygonalGate : GatingStrategy
{
    public override string Name { get; set; } = "Polygonal";
    public ObservableCollection<PointF> Polygon { get; set; } = new();
}