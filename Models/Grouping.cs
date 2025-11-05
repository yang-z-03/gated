using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;
using Gated.Configurations;

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
    
    public GatingStrategyCollection Gates { get; private set; } = new();
    public StatisticsCollection Statistics { get; private set; } = new();
    public ObservableCollection<Tube> Samples { get; } = new();

    private ObservableCollection<INode> children = new();

    public ObservableCollection<INode> Children
    {
        get { return children; }
    }
    
    public Dictionary<Dimension, Dictionary<Dimension, ScatterConfig>>
        ScatterConfigs { get; private set; } = new();

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
            Dictionary<Dimension, float[]> measurements = new();
            foreach (var dim in this.Dimensions)
            {
                if (dim is Channel channel)
                {
                    bool found = false;
                    foreach (var dimc in tube.Channels)
                        if (dimc.Value.IsEqual(channel))
                        {
                            measurements.Add(channel, tube.Measurements[dimc.Value]);
                            found = true;
                        }

                    if (!found) match_channels = false;
                }
            }
            
            if (!match_channels)
                return false;
            else
            {
                tube.Channels.Clear();
                foreach (var dim in measurements.Keys)
                {
                    var channel = dim as Channel;
                    tube.Channels.Add(channel!.Index, channel!);
                }

                tube.Measurements = measurements;
                this.Samples.Add(tube);
            }
        }

        return true;
    }
    
    public void AddGate(GatingStrategy? parent, GatingStrategy gate)
    {
        foreach (var tube in this.Samples)
        {
            var found = find_corresponding_population(tube, parent);
            if (found != null)
            {
                bool has_gate = false;
                foreach(var subset in found.Subsets)
                    if (subset.AssociatedGate == gate)
                        has_gate = true;
                
                if(!has_gate) found.AddGate(gate);
            }
        }
        
        if (parent == null)  this.Gates.Add(gate);
        else parent.Subsets.Add(gate);
    }

    private Population? find_corresponding_population(Population p, GatingStrategy? parent)
    {
        if (parent == null) return p;
        foreach (var subs in p.Subsets)
        {
            Population? found = null;
            if (subs.AssociatedGate == parent) found = subs;
            else if (subs.Subsets.Count > 0) found = find_corresponding_population(subs, parent);

            if (found != null) return found;
        }

        return null;
    }
}