using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Gated.Models;

public abstract class Statistics : INode
{
    public abstract string Name { get; set; }
    public virtual string Identifier { get; set; } = "statistics";
    public ObservableCollection<INode> Children { get; } = new();
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