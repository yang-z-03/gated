using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public abstract class IncrementalCPMClusteringAlgorithm : CPMClusteringAlgorithm, IncrementalClusteringAlgorithm
{
    protected IncrementalCPMClusteringAlgorithm() { }
    protected IncrementalCPMClusteringAlgorithm(double resolution) : base(resolution) { }

    public Clustering FindClustering(Network network)
    {
        var clustering = new Clustering(network.n_nodes);
        ImproveClustering(network, clustering);
        clustering.RemoveEmptyClusters();
        return clustering;
    }

    public abstract bool ImproveClustering(Network network, Clustering clustering);
}




