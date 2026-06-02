using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class LouvainAlgorithm : IterativeCPMClusteringAlgorithm
{
    internal IncrementalCPMClusteringAlgorithm local_moving_algorithm = null!;

    public LouvainAlgorithm() : this(new Random()) { }
    public LouvainAlgorithm(Random random) : this(DefaultResolution, DefaultNIterations, random) { }
    public LouvainAlgorithm(double resolution, int n_iterations, Random random) : this(resolution, n_iterations, new FastLocalMovingAlgorithm(resolution, n_iterations, random)) { }
    public LouvainAlgorithm(double resolution, int n_iterations, IncrementalCPMClusteringAlgorithm local_moving_algorithm) : base(resolution, n_iterations) => SetLocalMovingAlgorithm(local_moving_algorithm);
    public new LouvainAlgorithm Clone() => new(resolution, n_iterations, local_moving_algorithm);
    public IncrementalCPMClusteringAlgorithm GetLocalMovingAlgorithm() => local_moving_algorithm;
    public override void SetResolution(double resolution) { base.SetResolution(resolution); local_moving_algorithm.SetResolution(resolution); }
    public void SetLocalMovingAlgorithm(IncrementalCPMClusteringAlgorithm local_moving_algorithm) { this.local_moving_algorithm = local_moving_algorithm; this.local_moving_algorithm.SetResolution(resolution); }

    protected override bool ImproveClusteringOneIteration(Network network, Clustering clustering)
    {
        var changed = local_moving_algorithm.ImproveClustering(network, clustering);
        if (clustering.n_clusters < network.n_nodes)
        {
            var reduced = network.CreateReducedNetwork(clustering);
            var reducedClustering = new Clustering(reduced.n_nodes);
            changed |= ImproveClustering(reduced, reducedClustering);
            clustering.MergeClusters(reducedClustering);
        }
        return changed;
    }
}




