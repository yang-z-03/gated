using System;
using System.Linq;

namespace gated.Network;

public static class LeidenClusteringUsageTest
{
    public static int[] Run()
    {
        var node_weights = Arrays.CreateDoubleArrayOfOnes(6);
        var edges = new[]
        {
            new LargeIntArray([0, 0, 1, 3, 3, 4, 2]),
            new LargeIntArray([1, 2, 2, 4, 5, 5, 3])
        };
        var edge_weights = new LargeDoubleArray([3.0, 3.0, 3.0, 3.0, 3.0, 3.0, 0.1]);
        var network = new Network(node_weights, edges, edge_weights, false, true);

        var leiden = new LeidenAlgorithm(0.8, 10, LeidenAlgorithm.DefaultRandomness, new Random(7));
        var clustering = leiden.FindClustering(network);
        var clusters = clustering.GetClusters();

        if (clusters.Length != network.GetNNodes())
            throw new InvalidOperationException("Leiden clustering returned an unexpected number of labels.");
        if (clusters.Any(cluster => cluster < 0 || cluster >= clustering.GetNClusters()))
            throw new InvalidOperationException("Leiden clustering returned an invalid cluster label.");

        return clusters;
    }
}
