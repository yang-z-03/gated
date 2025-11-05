using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;

namespace Gated.Models;

public class Grouping : INode
{
    public Grouping(string name, ICollection<Tube> tubes, string? identifier = null, bool isExpanded = false)
    {
        this.Name = name;
        if (identifier != null)
            this.Identifier = identifier;
        
        this.children.Add(this.Gates);
        this.children.Add(this.Statistics);
        this.Samples.CollectionChanged += (s, e) =>
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
        
        foreach (var tube in tubes)
            this.AddSample(tube);
        this.IsExpanded = isExpanded;
    }
    
    // a grouping can only consist of tubes that have matched dimensions.
    // thus, operations such as compensation can be performed conveniently.

    public string Name { get; set; } = "";
    public string Identifier { get; set; } = "grouping";
    
    public bool IsExpanded { get; set; } = false;
    
    public ObservableCollection<Dimension> Dimensions { get; set; } = new();
    public Compensation Compensation { get; set; } = new();
    
    public ObservableCollection<Subset> Subsets { get; private set; } = new();
    public GatingStrategyCollection Gates { get; private set; } = new();
    public StatisticsCollection Statistics { get; private set; } = new();
    public ObservableCollection<Tube> Samples { get; } = new();

    private ObservableCollection<INode> children = new();

    public ObservableCollection<INode> Children
    {
        get { return children; }
    }

    public bool AddSample(Tube tube)
    {
        if (this.Dimensions.Count == 0)
        {
            foreach(var dim in tube.Channels)
                this.Dimensions.Add(dim.Value);
            
            this.Samples.Add(tube);
        }
        else
        {
            bool match_channels = true;
            foreach (var dim in this.Dimensions)
            {
                if (dim is Channel channel)
                {
                    bool found = false;
                    foreach (var dimc in tube.Channels)
                        if (dimc.Value.IsEqual(channel))
                            found = true;

                    if (!found) match_channels = false;
                }
            }

            if (!match_channels)
                return false;
            else this.Samples.Add(tube);
        }

        return true;
    }
}