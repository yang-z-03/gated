using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class LeidenAlgorithm : IterativeCPMClusteringAlgorithm
{
    public const double DefaultRandomness = LocalMergingAlgorithm.DefaultRandomness;
    internal double randomness;
    internal IncrementalCPMClusteringAlgorithm local_moving_algorithm = null!;
    internal Random random;

    public LeidenAlgorithm() : this(new Random()) { }
    public LeidenAlgorithm(Random random) : this(DefaultResolution, DefaultNIterations, DefaultRandomness, random) { }
    public LeidenAlgorithm(double resolution, int n_iterations, double randomness, Random random)
        : this(resolution, n_iterations, randomness, new FastLocalMovingAlgorithm(resolution, n_iterations, random), random) { }
    public LeidenAlgorithm(double resolution, int n_iterations, double randomness, IncrementalCPMClusteringAlgorithm local_moving_algorithm, Random random) : base(resolution, n_iterations)
    {
        this.randomness = randomness;
        this.random = random;
        SetLocalMovingAlgorithm(local_moving_algorithm);
    }

    public new LeidenAlgorithm Clone() => new(resolution, n_iterations, randomness, local_moving_algorithm, random);
    public double GetRandomness() => randomness;
    public IncrementalCPMClusteringAlgorithm GetLocalMovingAlgorithm() => local_moving_algorithm;
    public override void SetResolution(double resolution) { base.SetResolution(resolution); local_moving_algorithm.SetResolution(resolution); }
    public void SetRandomness(double randomness) => this.randomness = randomness;
    public void SetLocalMovingAlgorithm(IncrementalCPMClusteringAlgorithm local_moving_algorithm) { this.local_moving_algorithm = local_moving_algorithm; this.local_moving_algorithm.SetResolution(resolution); }

    protected override bool ImproveClusteringOneIteration(Network network, Clustering clustering)
    {
        var changed = local_moving_algorithm.ImproveClustering(network, clustering);
        if (clustering.n_clusters < network.n_nodes)
        {
            var refinement = new Clustering(network.n_nodes);
            var localMerging = new LocalMergingAlgorithm(resolution, randomness, random);
            var nodesPerCluster = clustering.GetNodesPerCluster();
            refinement.n_clusters = 0;
            for (var i = 0; i < nodesPerCluster.Length; i++)
            {
                var subnetwork = network.CreateSubnetwork(nodesPerCluster[i]);
                var subClustering = localMerging.FindClustering(subnetwork);
                for (var j = 0; j < nodesPerCluster[i].Length; j++)
                    refinement.clusters[nodesPerCluster[i][j]] = refinement.n_clusters + subClustering.clusters[j];
                refinement.n_clusters += subClustering.n_clusters;
            }
            var reduced = network.CreateReducedNetwork(refinement.n_clusters < network.n_nodes ? refinement : clustering);
            var reducedClustering = new Clustering(reduced.n_nodes);
            changed |= ImproveClustering(reduced, reducedClustering);
            if (refinement.n_clusters < network.n_nodes)
            {
                clustering.clusters = refinement.clusters;
                clustering.n_clusters = refinement.n_clusters;
            }
            clustering.MergeClusters(reducedClustering);
        }
        return changed;
    }
}




