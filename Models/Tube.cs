using System;
using System.Collections.ObjectModel;
using System.Collections;
using System.Collections.Generic;

namespace Gated.Models;

public abstract class Population
{
    public Dictionary<Channel, float[]> Measurements { get; private set; } = new();
    public Dictionary<Embedding, float[]> Embeddings { get; private set; } = new();
    
    public Dictionary<string, Statistics> Statistics { get; private set; } = new();
    public Population? Parent { get; private set; } = null;
    public Tube? ParentTube { get; private set; } = null;
    public abstract bool IsTube { get; }
    public ObservableCollection<Subset> Subsets { get; private set; } = new();
    
    public string Name { get; private set; } = "";
    public ObservableCollection<Gate> Gates { get; private set; } = new();
    
    // population-level settings that, if exists, will override the settings
    // from the group. it is supported though, for any sub-population t
}

public class Tube : Population
{
    public override bool IsTube { get; } = true;
}

public class Subset : Population
{
    public override bool IsTube { get; } = false;
}