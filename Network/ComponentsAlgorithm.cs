using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class ComponentsAlgorithm : ClusteringAlgorithm
{
    public Clustering FindClustering(Network network)
    {
        var cluster = Enumerable.Repeat(-1, network.n_nodes).ToArray();
        var current = 0;
        var queue = new Queue<int>();
        for (var start = 0; start < network.n_nodes; start++)
        {
            if (cluster[start] >= 0)
                continue;
            cluster[start] = current;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (var neighbor in network.Neighbors(node))
                    if (cluster[neighbor] < 0)
                    {
                        cluster[neighbor] = current;
                        queue.Enqueue(neighbor);
                    }
            }
            current++;
        }
        var result = new Clustering(cluster);
        result.OrderClustersByNNodes();
        return result;
    }
}




