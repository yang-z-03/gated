using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class Network
{
    internal int n_nodes;
    internal long n_edges;
    internal double[] node_weights = [];
    internal long[] first_neighbor_indices = [];
    internal LargeIntArray neighbor_array = new(0);
    internal LargeDoubleArray edge_weight_array = new(0);
    internal double total_edge_weight_self_links;

    public Network(double[] node_weights, LargeIntArray[] edges, bool sortedEdges, bool CheckIntegrity)
        : this(node_weights.Length, node_weights, false, edges, null, sortedEdges, CheckIntegrity) { }

    public Network(double[] node_weights, LargeIntArray[] edges, LargeDoubleArray edge_weight_array, bool sortedEdges, bool CheckIntegrity)
        : this(node_weights.Length, node_weights, false, edges, edge_weight_array, sortedEdges, CheckIntegrity) { }

    public Network(double[] node_weights, long[] first_neighbor_indices, LargeIntArray neighbor_array, bool CheckIntegrity)
        : this(node_weights.Length, node_weights, false, first_neighbor_indices, neighbor_array, null, CheckIntegrity) { }

    public Network(double[] node_weights, long[] first_neighbor_indices, LargeIntArray neighbor_array, LargeDoubleArray edge_weight_array, bool CheckIntegrity)
        : this(node_weights.Length, node_weights, false, first_neighbor_indices, neighbor_array, edge_weight_array, CheckIntegrity) { }

    public Network(int n_nodes, bool setNodeWeightsToTotalEdgeWeights, LargeIntArray[] edges, bool sortedEdges, bool CheckIntegrity)
        : this(n_nodes, null, setNodeWeightsToTotalEdgeWeights, edges, null, sortedEdges, CheckIntegrity) { }

    public Network(int n_nodes, bool setNodeWeightsToTotalEdgeWeights, LargeIntArray[] edges, LargeDoubleArray edge_weight_array, bool sortedEdges, bool CheckIntegrity)
        : this(n_nodes, null, setNodeWeightsToTotalEdgeWeights, edges, edge_weight_array, sortedEdges, CheckIntegrity) { }

    public Network(int n_nodes, bool setNodeWeightsToTotalEdgeWeights, long[] first_neighbor_indices, LargeIntArray neighbor_array, bool CheckIntegrity)
        : this(n_nodes, null, setNodeWeightsToTotalEdgeWeights, first_neighbor_indices, neighbor_array, null, CheckIntegrity) { }

    public Network(int n_nodes, bool setNodeWeightsToTotalEdgeWeights, long[] first_neighbor_indices, LargeIntArray neighbor_array, LargeDoubleArray edge_weight_array, bool CheckIntegrity)
        : this(n_nodes, null, setNodeWeightsToTotalEdgeWeights, first_neighbor_indices, neighbor_array, edge_weight_array, CheckIntegrity) { }

    private Network() { }

    private Network(int n_nodes, double[]? node_weights, bool setNodeWeightsToTotalEdgeWeights, LargeIntArray[] edges, LargeDoubleArray? edge_weight_array, bool sortedEdges, bool CheckIntegrity)
    {
        var count = (int)edges[0].Size();
        var edgeList = new List<(int Source, int Target, double Weight)>(sortedEdges ? count : count * 2);
        for (var i = 0; i < count; i++)
        {
            var source = edges[0].Get(i);
            var target = edges[1].Get(i);
            var weight = edge_weight_array?.Get(i) ?? 1.0;
            if (source == target)
            {
                total_edge_weight_self_links += weight;
                continue;
            }
            edgeList.Add((source, target, weight));
            if (!sortedEdges)
                edgeList.Add((target, source, weight));
        }

        edgeList.Sort(static (a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source) : a.Target.CompareTo(b.Target));
        BuildFromSortedEdges(n_nodes, edgeList);
        this.node_weights = node_weights is not null
            ? (double[])node_weights.Clone()
            : setNodeWeightsToTotalEdgeWeights ? GetTotalEdgeWeightPerNodeHelper() : Arrays.CreateDoubleArrayOfOnes(n_nodes);
        if (CheckIntegrity)
            this.CheckIntegrity();
    }

    private Network(int n_nodes, double[]? node_weights, bool setNodeWeightsToTotalEdgeWeights, long[] first_neighbor_indices, LargeIntArray neighbor_array, LargeDoubleArray? edge_weight_array, bool CheckIntegrity)
    {
        this.n_nodes = n_nodes;
        this.first_neighbor_indices = (long[])first_neighbor_indices.Clone();
        this.neighbor_array = neighbor_array.Clone();
        this.edge_weight_array = edge_weight_array?.Clone() ?? new LargeDoubleArray(neighbor_array.Size(), 1.0);
        n_edges = neighbor_array.Size();
        this.node_weights = node_weights is not null
            ? (double[])node_weights.Clone()
            : setNodeWeightsToTotalEdgeWeights ? GetTotalEdgeWeightPerNodeHelper() : Arrays.CreateDoubleArrayOfOnes(n_nodes);
        if (CheckIntegrity)
            this.CheckIntegrity();
    }

    public int GetNNodes() => n_nodes;
    public double GetTotalNodeWeight() => node_weights.Sum();
    public double[] GetNodeWeights() => (double[])node_weights.Clone();
    public double GetNodeWeight(int node) => node_weights[node];
    public long GetNEdges() => n_edges / 2;
    public int GetNNeighbors(int node) => (int)(first_neighbor_indices[node + 1] - first_neighbor_indices[node]);
    public int[] GetNNeighborsPerNode() => Enumerable.Range(0, n_nodes).Select(GetNNeighbors).ToArray();

    public LargeIntArray[] GetEdges()
    {
        var sources = new LargeIntArray(n_edges);
        for (var i = 0; i < n_nodes; i++)
            sources.Fill(first_neighbor_indices[i], first_neighbor_indices[i + 1], i);
        return [sources, neighbor_array.Clone()];
    }

    public int[][] GetNeighborsPerNode() => Enumerable.Range(0, n_nodes).Select(GetNeighbors).ToArray();
    public int[] GetNeighbors(int node) => neighbor_array.ToArray(first_neighbor_indices[node], first_neighbor_indices[node + 1]);
    public LargeIntArray.FromToIterable NeighborsIterable(int node) => neighbor_array.FromTo(first_neighbor_indices[node], first_neighbor_indices[node + 1]);
    public LargeIntArray.FromToIterable Neighbors(int node) => NeighborsIterable(node);
    public IEnumerable<long> IncidentEdges(int node) => LongRange(first_neighbor_indices[node], first_neighbor_indices[node + 1]);
    public double GetTotalEdgeWeight() => edge_weight_array.CalcSum() / 2 + total_edge_weight_self_links;
    public double[] GetTotalEdgeWeightPerNode() => GetTotalEdgeWeightPerNodeHelper();
    public double GetTotalEdgeWeight(int node) => edge_weight_array.CalcSum(first_neighbor_indices[node], first_neighbor_indices[node + 1]);
    public LargeDoubleArray GetEdgeWeights() => edge_weight_array.Clone();
    public double[][] GetEdgeWeightsPerNode() => Enumerable.Range(0, n_nodes).Select(GetEdgeWeights).ToArray();
    public double[] GetEdgeWeights(int node) => edge_weight_array.ToArray(first_neighbor_indices[node], first_neighbor_indices[node + 1]);
    public LargeDoubleArray.FromToIterable EdgeWeightsIterable(int node) => edge_weight_array.FromTo(first_neighbor_indices[node], first_neighbor_indices[node + 1]);
    public LargeDoubleArray.FromToIterable EdgeWeights(int node) => EdgeWeightsIterable(node);
    public double GetTotalEdgeWeightSelfLinks() => total_edge_weight_self_links;

    public Network CreateNetworkWithoutNodeWeights() => new(n_nodes, false, first_neighbor_indices, neighbor_array, edge_weight_array, false);
    public Network CreateNetworkWithoutEdgeWeights() => new(node_weights, first_neighbor_indices, neighbor_array, false);
    public Network CreateNetworkWithoutNodeAndEdgeWeights() => new(n_nodes, false, first_neighbor_indices, neighbor_array, false);

    public Network CreateNormalizedNetworkUsingAssociationStrength()
    {
        var normalized = edge_weight_array.Clone();
        var totalNodeWeight = GetTotalNodeWeight();
        for (var i = 0; i < n_nodes; i++)
            for (var e = first_neighbor_indices[i]; e < first_neighbor_indices[i + 1]; e++)
                normalized.Set(e, edge_weight_array.Get(e) * totalNodeWeight / (node_weights[i] * node_weights[neighbor_array.Get(e)]));
        return new Network(node_weights, first_neighbor_indices, neighbor_array, normalized, false);
    }

    public Network CreateNormalizedNetworkUsingFractionalization()
    {
        var normalized = edge_weight_array.Clone();
        var totals = GetTotalEdgeWeightPerNode();
        for (var i = 0; i < n_nodes; i++)
            for (var e = first_neighbor_indices[i]; e < first_neighbor_indices[i + 1]; e++)
                normalized.Set(e, totals[i] == 0 ? 0 : edge_weight_array.Get(e) / totals[i]);
        return new Network(node_weights, first_neighbor_indices, neighbor_array, normalized, false);
    }

    public Network CreatePrunedNetwork(int maxNEdges) => CreatePrunedNetwork((long)maxNEdges, new Random());

    public Network CreatePrunedNetwork(long maxNEdges, Random random)
    {
        var undirected = GetUndirectedEdges().OrderByDescending(e => e.Weight).Take((int)maxNEdges).ToArray();
        return FromUndirectedEdges(n_nodes, node_weights, undirected, false);
    }

    public Network CreateSubnetwork(int[] nodes)
    {
        var selected = nodes.Select((node, index) => (node, index)).ToDictionary(x => x.node, x => x.index);
        var edgeList = new List<(int Source, int Target, double Weight)>();
        foreach (var (oldSource, newSource) in selected)
            for (var e = first_neighbor_indices[oldSource]; e < first_neighbor_indices[oldSource + 1]; e++)
                if (selected.TryGetValue(neighbor_array.Get(e), out var newTarget))
                    edgeList.Add((newSource, newTarget, edge_weight_array.Get(e)));
        return FromDirectedEdges(nodes.Length, nodes.Select(n => node_weights[n]).ToArray(), edgeList, 0, false);
    }

    public Network CreateSubnetwork(bool[] nodesInSubnetwork) =>
        CreateSubnetwork(Enumerable.Range(0, nodesInSubnetwork.Length).Where(i => nodesInSubnetwork[i]).ToArray());

    public Network CreateSubnetwork(Clustering clustering, int cluster) =>
        CreateSubnetwork(Enumerable.Range(0, n_nodes).Where(i => clustering.clusters[i] == cluster).ToArray());

    public Network[] CreateSubnetworks(Clustering clustering) =>
        Enumerable.Range(0, clustering.n_clusters).Select(i => CreateSubnetwork(clustering, i)).ToArray();

    public Network CreateSubnetworkLargestComponent() => CreateSubnetwork(IdentifyComponents(), 0);

    public Network CreateReducedNetwork(Clustering clustering)
    {
        var node_weights = clustering.GetClusterWeights(this);
        var edge_weight_array = new Dictionary<(int, int), double>();
        var self = total_edge_weight_self_links;
        for (var i = 0; i < n_nodes; i++)
        {
            var sourceCluster = clustering.clusters[i];
            for (var e = first_neighbor_indices[i]; e < first_neighbor_indices[i + 1]; e++)
            {
                var targetCluster = clustering.clusters[neighbor_array.Get(e)];
                if (sourceCluster == targetCluster)
                    self += this.edge_weight_array.Get(e);
                else
                    edge_weight_array[(sourceCluster, targetCluster)] = edge_weight_array.GetValueOrDefault((sourceCluster, targetCluster)) + this.edge_weight_array.Get(e);
            }
        }
        return FromDirectedEdges(clustering.n_clusters, node_weights, edge_weight_array.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)), self, false);
    }

    public Clustering IdentifyComponents() => new ComponentsAlgorithm().FindClustering(this);

    public void CheckIntegrity()
    {
        if (n_nodes < 0 || n_edges < 0 || n_edges % 2 != 0)
            throw new ArgumentException("Invalid network dimensions.");
        if (node_weights.Length != n_nodes || first_neighbor_indices.Length != n_nodes + 1 || neighbor_array.Size() != n_edges || edge_weight_array.Size() != n_edges)
            throw new ArgumentException("Network array lengths are inconsistent.");
        for (var i = 0; i < n_nodes; i++)
        {
            var previous = -1;
            for (var e = first_neighbor_indices[i]; e < first_neighbor_indices[i + 1]; e++)
            {
                var neighbor = neighbor_array.Get(e);
                if (neighbor < 0 || neighbor >= n_nodes || neighbor <= previous)
                    throw new ArgumentException("Neighbor lists must be sorted, unique, and valid.");
                previous = neighbor;
            }
        }
    }

    public static void SortEdges(LargeIntArray[] edges, LargeDoubleArray edge_weight_array)
    {
        var order = Enumerable.Range(0, (int)edges[0].Size())
            .OrderBy(i => edges[0].Get(i))
            .ThenBy(i => edges[1].Get(i))
            .ToArray();
        var sorted0 = order.Select(i => edges[0].Get(i)).ToArray();
        var sorted1 = order.Select(i => edges[1].Get(i)).ToArray();
        edges[0] = new LargeIntArray(sorted0);
        edges[1] = new LargeIntArray(sorted1);
        if (edge_weight_array is not null)
            edge_weight_array.UpdateFrom(new LargeDoubleArray(order.Select(i => edge_weight_array.Get(i)).ToArray()));
    }

    private void BuildFromSortedEdges(int n_nodes, List<(int Source, int Target, double Weight)> edgeList)
    {
        this.n_nodes = n_nodes;
        var combined = new List<(int Source, int Target, double Weight)>();
        foreach (var group in edgeList.GroupBy(static e => (e.Source, e.Target)))
            combined.Add((group.Key.Source, group.Key.Target, group.Sum(static e => e.Weight)));
        combined.Sort(static (a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source) : a.Target.CompareTo(b.Target));
        n_edges = combined.Count;
        first_neighbor_indices = new long[n_nodes + 1];
        neighbor_array = new LargeIntArray(n_edges);
        edge_weight_array = new LargeDoubleArray(n_edges);
        for (var i = 0; i < combined.Count; i++)
        {
            neighbor_array.Set(i, combined[i].Target);
            edge_weight_array.Set(i, combined[i].Weight);
        }
        var index = 0;
        for (var node = 0; node < n_nodes; node++)
        {
            while (index < combined.Count && combined[index].Source < node)
                index++;
            first_neighbor_indices[node] = index;
        }
        first_neighbor_indices[n_nodes] = combined.Count;
    }

    private double[] GetTotalEdgeWeightPerNodeHelper()
    {
        var totals = new double[n_nodes];
        for (var i = 0; i < n_nodes; i++)
            totals[i] = edge_weight_array.CalcSum(first_neighbor_indices[i], first_neighbor_indices[i + 1]);
        return totals;
    }

    private IEnumerable<(int Source, int Target, double Weight)> GetUndirectedEdges()
    {
        for (var i = 0; i < n_nodes; i++)
            for (var e = first_neighbor_indices[i]; e < first_neighbor_indices[i + 1]; e++)
            {
                var j = neighbor_array.Get(e);
                if (i < j)
                    yield return (i, j, edge_weight_array.Get(e));
            }
    }

    private static Network FromUndirectedEdges(int n_nodes, double[] node_weights, IEnumerable<(int Source, int Target, double Weight)> edges, bool CheckIntegrity)
    {
        var list = edges.ToArray();
        var edgeArrays = new[]
        {
            new LargeIntArray(list.Select(static e => e.Source).ToArray()),
            new LargeIntArray(list.Select(static e => e.Target).ToArray())
        };
        return new Network(node_weights, edgeArrays, new LargeDoubleArray(list.Select(static e => e.Weight).ToArray()), false, CheckIntegrity);
    }

    private static Network FromDirectedEdges(int n_nodes, double[] node_weights, IEnumerable<(int Source, int Target, double Weight)> edges, double selfLinks, bool CheckIntegrity)
    {
        var network = new Network();
        network.total_edge_weight_self_links = selfLinks;
        network.BuildFromSortedEdges(n_nodes, edges.OrderBy(static e => e.Source).ThenBy(static e => e.Target).ToList());
        network.node_weights = node_weights;
        if (CheckIntegrity)
            network.CheckIntegrity();
        return network;
    }

    private static IEnumerable<long> LongRange(long From, long to)
    {
        for (var i = From; i < to; i++)
            yield return i;
    }
}




