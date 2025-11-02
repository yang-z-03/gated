using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;

namespace Gated.Models;

public interface INode
{
    public string Name { get; set; }
    public ObservableCollection<INode> Children { get; }
}

public class Workspace : INode
{
    public Workspace(string name)
    {
        this.Name = name;
        this.Children.Add(new Grouping("Blank Control", new List<Tube>()));
        this.Children.Add(new Grouping("Single Staining", new List<Tube>()));
        this.Children.Add(new Grouping("FMO Staining", new List<Tube>()));
        this.Children.Add(new Grouping("Isotype Control", new List<Tube>()));
        this.Children.Add(new Grouping("Samples", new List<Tube>()));
    }
    
    public string Name { get; set; } = "Workspace";
    public string FilePath { get; private set; } = string.Empty;
    public bool IsDirty { get; set; } = false;
    public ObservableCollection<INode> Children { get; } = new();
}