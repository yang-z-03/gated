using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public abstract class CPMClusteringAlgorithm : QualityClusteringAlgorithm
{
    public const double DefaultResolution = 1.0;
    protected internal double resolution;

    protected CPMClusteringAlgorithm() : this(DefaultResolution) { }
    protected CPMClusteringAlgorithm(double resolution) => this.resolution = resolution;
    public CPMClusteringAlgorithm Clone() => (CPMClusteringAlgorithm)MemberwiseClone();
    public double GetResolution() => resolution;
    public virtual void SetResolution(double resolution) => this.resolution = resolution;

    public double CalcQuality(Network network, Clustering clustering)
    {
        var quality = network.GetTotalEdgeWeightSelfLinks();
        var clusterWeights = clustering.GetClusterWeights(network);
        for (var i = 0; i < network.n_nodes; i++)
            for (var e = network.first_neighbor_indices[i]; e < network.first_neighbor_indices[i + 1]; e++)
                if (clustering.clusters[i] == clustering.clusters[network.neighbor_array.Get(e)])
                    quality += network.edge_weight_array.Get(e) / 2;
        quality -= clusterWeights.Sum(w => resolution * w * w / 2);
        return quality;
    }

    public int RemoveCluster(Network network, Clustering clustering, int cluster)
    {
        var old = clustering.n_clusters;
        for (var i = 0; i < clustering.n_nodes; i++)
            if (clustering.clusters[i] == cluster)
                clustering.clusters[i] = old;
        clustering.n_clusters = old + 1;
        return old;
    }

    public bool RemoveSmallClustersBasedOnNNodes(Network network, Clustering clustering, int minNNodesPerCluster)
    {
        var changed = false;
        var counts = clustering.GetNNodesPerCluster();
        for (var i = 0; i < clustering.n_nodes; i++)
            if (counts[clustering.clusters[i]] < minNNodesPerCluster)
            {
                clustering.clusters[i] = clustering.n_clusters++;
                changed = true;
            }
        clustering.RemoveEmptyClusters();
        return changed;
    }

    public bool RemoveSmallClustersBasedOnWeight(Network network, Clustering clustering, double minClusterWeight)
    {
        var changed = false;
        var weights = clustering.GetClusterWeights(network);
        for (var i = 0; i < clustering.n_nodes; i++)
            if (weights[clustering.clusters[i]] < minClusterWeight)
            {
                clustering.clusters[i] = clustering.n_clusters++;
                changed = true;
            }
        clustering.RemoveEmptyClusters();
        return changed;
    }
}




