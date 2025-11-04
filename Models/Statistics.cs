using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Gated.Models;

public class StatisticsCollection : ObservableCollection<Statistics>, INode
{
    public StatisticsCollection()
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

    public string Name { get; set; } = "Statistics";
    public string Identifier { get; set; } = "statistics";
    private ObservableCollection<INode> children = new();

    public ObservableCollection<INode> Children
    {
        get { return children; }
    }
    
    public bool IsExpanded { get; set; } = true;
}

public abstract class Statistics : INode
{
    public abstract string Name { get; set; }
    public virtual string Identifier { get; set; } = "statistic";
    public ObservableCollection<INode> Children { get; } = new();
    
    public bool IsExpanded { get; set; } = false;
}

public abstract class MultivariateStatistics : Statistics
{
    public abstract void Run(Dictionary<Channel, float[]> data);
    public abstract Dictionary<Embedding, float[]> Results { get; }
}

public abstract class UnivariateStatistics : Statistics
{
    public abstract void Run(float[] data);
    public abstract float Results { get; }
}