using System;
using System.Collections.ObjectModel;
using System.Collections;
using System.Collections.Generic;

namespace Gated.Models;

public abstract class Population : INode
{
    public Dictionary<Channel, float[]> Measurements { get; private set; } = new();
    public Dictionary<Embedding, float[]> Embeddings { get; private set; } = new();
    
    public Population? Parent { get; private set; } = null;
    public Tube? ParentTube { get; private set; } = null;
    public abstract bool IsTube { get; }
    public ObservableCollection<Subset> Subsets { get; private set; } = new();
    
    public string Name { get; set; } = "Population";

    public ObservableCollection<INode> Children
    {
        get
        {
            ObservableCollection<INode> children = new();
            foreach (GatingStrategy child in this.Gates) children.Add(child);
            foreach (Statistics child in this.Statistics) children.Add(child);
            return children;
        }
    }

    public ObservableCollection<GatingStrategy> Gates { get; private set; } = new();
    public ObservableCollection<Statistics> Statistics { get; private set; } = new();
    
    // population-level settings that, if exists, will override the settings
    // from the group. it is supported though, for any sub-population to implement
    // such settings e.g. compensation separately.
    
    public Compensation Compensation { get; private set; } = new();
}

public class Tube : Population
{
    public override bool IsTube { get; } = true;
}

public class Subset : Population
{
    public override bool IsTube { get; } = false;
}