using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;

namespace Gated.Models;

public class Grouping : INode
{
    public Grouping(string name, ICollection<Tube> tubes)
    {
        this.Name = name;
        foreach (var tube in tubes)
            this.Children.Add(tube);
    }
    
    // a grouping can only consist of tubes that have matched dimensions.
    // thus, operations such as compensation can be performed conveniently.

    public string Name { get; set; } = "";
    
    public ObservableCollection<Dimension> Dimensions { get; set; } = new();
    public Compensation Compensation { get; set; } = new();
    
    public ObservableCollection<Subset> Subsets { get; private set; } = new();
    public ObservableCollection<GatingStrategy> Gates { get; private set; } = new();
    public Dictionary<string, Statistics> Statistics { get; private set; } = new();
    
    public ObservableCollection<INode> Children { get; } = new();
}