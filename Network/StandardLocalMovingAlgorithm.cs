using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public class StandardLocalMovingAlgorithm : IncrementalCPMClusteringAlgorithm
{
    protected internal Random random;

    public StandardLocalMovingAlgorithm() : this(new Random()) { }
    public StandardLocalMovingAlgorithm(Random random) : this(DefaultResolution, random) { }
    public StandardLocalMovingAlgorithm(double resolution, Random random) : base(resolution) => this.random = random;

    public override bool ImproveClustering(Network network, Clustering clustering) => LocalMove(network, clustering, resolution, random);

    internal static bool LocalMove(Network network, Clustering clustering, double resolution, Random random)
    {
        var changed = false;
        var order = Arrays.GenerateRandomPermutation(network.n_nodes, random);
        var clusterWeights = clustering.GetClusterWeights(network);
        foreach (var node in order)
        {
            var oldCluster = clustering.clusters[node];
            var bestCluster = oldCluster;
            var bestGain = 0.0;
            var weights = NeighborClusterWeights(network, clustering, node);
            clusterWeights[oldCluster] -= network.node_weights[node];
            foreach (var (cluster, edgeWeight) in weights)
            {
                var gain = edgeWeight - resolution * network.node_weights[node] * clusterWeights[cluster];
                if (gain > bestGain || (gain == bestGain && cluster != oldCluster && random.Next(2) == 0))
                {
                    bestGain = gain;
                    bestCluster = cluster;
                }
            }
            if (bestCluster != oldCluster)
            {
                clustering.clusters[node] = bestCluster;
                changed = true;
            }
            clusterWeights[clustering.clusters[node]] += network.node_weights[node];
        }
        clustering.RemoveEmptyClusters();
        return changed;
    }

    internal static Dictionary<int, double> NeighborClusterWeights(Network network, Clustering clustering, int node)
    {
        var weights = new Dictionary<int, double> { [clustering.clusters[node]] = 0.0 };
        for (var e = network.first_neighbor_indices[node]; e < network.first_neighbor_indices[node + 1]; e++)
        {
            var cluster = clustering.clusters[network.neighbor_array.Get(e)];
            weights[cluster] = weights.GetValueOrDefault(cluster) + network.edge_weight_array.Get(e);
        }
        return weights;
    }
}




