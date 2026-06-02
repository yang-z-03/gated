using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class Clustering
{
    internal int n_nodes;
    internal int n_clusters;
    internal int[] clusters = [];

    public Clustering(int n_nodes)
    {
        this.n_nodes = n_nodes;
        InitSingletonClusters();
    }

    public Clustering(int[] clusters)
    {
        n_nodes = clusters.Length;
        this.clusters = (int[])clusters.Clone();
        n_clusters = clusters.Length == 0 ? 0 : clusters.Max() + 1;
        RemoveEmptyClusters();
    }

    public Clustering Clone() => new(clusters);
    public int GetNNodes() => n_nodes;
    public int GetNClusters() => n_clusters;
    public int[] GetClusters() => (int[])clusters.Clone();
    public int GetCluster(int node) => clusters[node];
    public void SetCluster(int node, int cluster) => clusters[node] = cluster;

    public bool[] GetClusterIsNotEmpty()
    {
        var result = new bool[Math.Max(n_clusters, clusters.Length == 0 ? 0 : clusters.Max() + 1)];
        foreach (var cluster in clusters)
            if (cluster >= 0)
                result[cluster] = true;
        return result;
    }

    public int GetNNonEmptyClusters() => GetClusterIsNotEmpty().Count(static x => x);

    public int[] GetNNodesPerCluster()
    {
        var counts = new int[n_clusters];
        foreach (var cluster in clusters)
            counts[cluster]++;
        return counts;
    }

    public int[][] GetNodesPerCluster()
    {
        var buckets = Enumerable.Range(0, n_clusters).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < clusters.Length; i++)
            buckets[clusters[i]].Add(i);
        return buckets.Select(static x => x.ToArray()).ToArray();
    }

    public void InitSingletonClusters()
    {
        clusters = new int[n_nodes];
        for (var i = 0; i < n_nodes; i++)
            clusters[i] = i;
        n_clusters = n_nodes;
    }

    public void RemoveEmptyClusters() => RemoveEmptyClustersLargerThan(0);

    public void RemoveEmptyClustersLargerThan(int minimumCluster)
    {
        var map = new Dictionary<int, int>();
        var next = 0;
        for (var i = 0; i < n_nodes; i++)
        {
            var cluster = clusters[i];
            if (cluster < minimumCluster)
            {
                map.TryAdd(cluster, cluster);
                next = Math.Max(next, cluster + 1);
            }
        }

        for (var i = 0; i < n_nodes; i++)
        {
            var cluster = clusters[i];
            if (!map.TryGetValue(cluster, out var mapped))
            {
                mapped = next++;
                map[cluster] = mapped;
            }
            clusters[i] = mapped;
        }
        n_clusters = next;
    }

    public double[] GetClusterWeights(Network network) => GetClusterWeights(network.GetNodeWeights());

    public double[] GetClusterWeights(double[] node_weights)
    {
        var weights = new double[n_clusters];
        for (var i = 0; i < n_nodes; i++)
            weights[clusters[i]] += node_weights[i];
        return weights;
    }

    public void OrderClustersByNNodes() => OrderClustersByWeight(Arrays.CreateDoubleArrayOfOnes(n_nodes));

    public void OrderClustersByWeight(double[] node_weights)
    {
        var weights = GetClusterWeights(node_weights);
        var ordered = Enumerable.Range(0, n_clusters)
            .OrderByDescending(i => weights[i])
            .ThenBy(i => i)
            .ToArray();
        var remap = new int[n_clusters];
        for (var i = 0; i < ordered.Length; i++)
            remap[ordered[i]] = i;
        for (var i = 0; i < n_nodes; i++)
            clusters[i] = remap[clusters[i]];
    }

    public void MergeClusters(Clustering clustering)
    {
        for (var i = 0; i < n_nodes; i++)
            clusters[i] = clustering.clusters[clusters[i]];
        n_clusters = clustering.n_clusters;
        RemoveEmptyClusters();
    }

    public double CalcNormalizedMutualInformation(Clustering clustering)
    {
        var counts = BuildContingency(clustering, out var left, out var right);
        var n = (double)n_nodes;
        var mutual = 0.0;
        foreach (var ((a, b), count) in counts)
            mutual += count / n * Math.Log(count * n / (left[a] * right[b]));
        var hLeft = left.Values.Where(static c => c > 0).Sum(c => -(c / n) * Math.Log(c / n));
        var hRight = right.Values.Where(static c => c > 0).Sum(c => -(c / n) * Math.Log(c / n));
        return hLeft == 0 && hRight == 0 ? 1 : 2 * mutual / (hLeft + hRight);
    }

    public double CalcVariationOfInformation(Clustering clustering)
    {
        var nmi = CalcNormalizedMutualInformation(clustering);
        return 1 - nmi;
    }

    private Dictionary<(int, int), double> BuildContingency(Clustering other, out Dictionary<int, double> left, out Dictionary<int, double> right)
    {
        left = new Dictionary<int, double>();
        right = new Dictionary<int, double>();
        var counts = new Dictionary<(int, int), double>();
        for (var i = 0; i < n_nodes; i++)
        {
            var a = clusters[i];
            var b = other.clusters[i];
            left[a] = left.GetValueOrDefault(a) + 1;
            right[b] = right.GetValueOrDefault(b) + 1;
            counts[(a, b)] = counts.GetValueOrDefault((a, b)) + 1;
        }
        return counts;
    }
}




