using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class LocalMergingAlgorithm : CPMClusteringAlgorithm, ClusteringAlgorithm
{
    public const double DefaultRandomness = 0.01;
    internal double randomness;
    internal Random random;

    public LocalMergingAlgorithm() : this(new Random()) { }
    public LocalMergingAlgorithm(Random random) : this(DefaultResolution, DefaultRandomness, random) { }
    public LocalMergingAlgorithm(double resolution, double randomness, Random random) : base(resolution) { this.randomness = randomness; this.random = random; }
    public double GetRandomness() => randomness;
    public void SetRandomness(double randomness) => this.randomness = randomness;
    public Clustering FindClustering(Network network) => new FastLocalMovingAlgorithm(resolution, 1, random).FindClustering(network);
}




