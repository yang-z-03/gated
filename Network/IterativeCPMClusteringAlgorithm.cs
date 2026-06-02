using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public abstract class IterativeCPMClusteringAlgorithm : CPMClusteringAlgorithm, IncrementalClusteringAlgorithm
{
    public const int DefaultNIterations = 2;
    protected internal int n_iterations;

    protected IterativeCPMClusteringAlgorithm() : this(DefaultResolution, DefaultNIterations) { }
    protected IterativeCPMClusteringAlgorithm(double resolution, int n_iterations) : base(resolution) => this.n_iterations = n_iterations;
    public int GetNIterations() => n_iterations;
    public void SetNIterations(int n_iterations) => this.n_iterations = n_iterations;

    public Clustering FindClustering(Network network)
    {
        var clustering = new Clustering(network.n_nodes);
        ImproveClustering(network, clustering);
        return clustering;
    }

    public bool ImproveClustering(Network network, Clustering clustering)
    {
        var changed = false;
        for (var i = 0; i < n_iterations; i++)
        {
            var iterationChanged = ImproveClusteringOneIteration(network, clustering);
            changed |= iterationChanged;
            if (!iterationChanged)
                break;
        }
        clustering.RemoveEmptyClusters();
        return changed;
    }

    protected abstract bool ImproveClusteringOneIteration(Network network, Clustering clustering);
}




