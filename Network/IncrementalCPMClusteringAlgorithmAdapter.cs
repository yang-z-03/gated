using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class IncrementalCPMClusteringAlgorithmAdapter : IncrementalCPMClusteringAlgorithm
{
    private readonly Func<Network, Clustering, bool> improve_clustering;

    public IncrementalCPMClusteringAlgorithmAdapter(double resolution, Func<Network, Clustering, bool> improveClustering)
        : base(resolution)
    {
        improve_clustering = improveClustering;
    }

    public override bool ImproveClustering(Network network, Clustering clustering) => improve_clustering(network, clustering);
}




