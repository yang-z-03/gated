using System;

namespace gated.Reduction;

public sealed class UmapReductionOptions
{
    public int Dimensions { get; set; } = 2;
    public int NeighborCount { get; set; } = 15;
    public int? EpochCount { get; set; }
}

public interface IReductionPipelineState
{
    float[,] Input { get; }
    float[,]? LogicleNormalized { get; }
    CytoNormFitResult? CytoNormModel { get; }
    float[,]? CytoNormNormalized { get; }
    KnnGraphResult? KnnGraph { get; }
    float[,]? UmapEmbedding { get; }
    int[]? LeidenClusters { get; }
    FlowSomClusterer? FlowSomClusterer { get; }
    int[]? FlowSomClusters { get; }
}

public sealed class ReductionPipelineState : IReductionPipelineState
{
    public ReductionPipelineState(float[,] input)
    {
        MatrixUtilities.Validate(input, nameof(input));
        Input = MatrixUtilities.Copy(input);
    }

    public float[,] Input { get; }
    public float[,]? LogicleNormalized { get; internal set; }
    public CytoNormFitResult? CytoNormModel { get; internal set; }
    public float[,]? CytoNormNormalized { get; internal set; }
    public KnnGraphResult? KnnGraph { get; internal set; }
    public float[,]? UmapEmbedding { get; internal set; }
    public int[]? LeidenClusters { get; internal set; }
    public FlowSomClusterer? FlowSomClusterer { get; internal set; }
    public int[]? FlowSomClusters { get; internal set; }

    public float[,] CurrentMatrix => CytoNormNormalized ?? LogicleNormalized ?? Input;
}

public sealed class ReductionPipeline
{
    private readonly ReductionPipelineState state;

    public ReductionPipeline(float[,] input)
    {
        state = new ReductionPipelineState(input);
    }

    public IReductionPipelineState State => state;

    public ReductionPipeline ApplyLogicle(LogicleNormalizationOptions? options = null, bool force = false)
    {
        if (force || state.LogicleNormalized is null)
            state.LogicleNormalized = LogicleNormalization.Transform(state.CurrentMatrix, options);
        return this;
    }

    public ReductionPipeline FitCytoNorm(float[,] reference_data, int[] reference_batches, CytoNormOptions? options = null, bool force = false)
    {
        if (force || state.CytoNormModel is null)
            state.CytoNormModel = CytoNorm.Fit(reference_data, reference_batches, options);
        return this;
    }

    public ReductionPipeline ApplyCytoNorm(int[] batches, bool force = false)
    {
        if (state.CytoNormModel is null)
            throw new InvalidOperationException("CytoNorm must be fit before it can be applied.");
        if (force || state.CytoNormNormalized is null)
            state.CytoNormNormalized = CytoNorm.Normalize(state.CytoNormModel, state.CurrentMatrix, batches);
        return this;
    }

    public ReductionPipeline BuildKnnGraph(KnnGraphOptions? options = null, bool force = false)
    {
        if (force || state.KnnGraph is null)
            state.KnnGraph = KnnGraphBuilder.Build(state.CurrentMatrix, options);
        return this;
    }

    public ReductionPipeline RunUmap(UmapReductionOptions? options = null, bool force = false)
    {
        options ??= new UmapReductionOptions();
        if (!force && state.UmapEmbedding is not null)
            return this;

        int graph_neighbors_needed = Math.Max(1, options.NeighborCount - 1);
        if (state.KnnGraph is null || state.KnnGraph.Indices.Length == 0 ||
            state.KnnGraph.Indices[0].Length < Math.Min(graph_neighbors_needed, Math.Max(0, state.CurrentMatrix.GetLength(0) - 1)))
        {
            BuildKnnGraph(new KnnGraphOptions { NeighborCount = graph_neighbors_needed });
        }

        var data = MatrixUtilities.ToJagged(state.CurrentMatrix);
        var umap = new Umap(dimensions: options.Dimensions, numberOfNeighbors: options.NeighborCount, customNumberOfEpochs: options.EpochCount);
        var (umap_indices, umap_distances) = state.KnnGraph is null
            ? (null, null)
            : prepare_umap_knn(state.KnnGraph, state.CurrentMatrix.GetLength(0), options.NeighborCount);

        int epochs = umap_indices is null || umap_distances is null
            ? umap.InitializeFit(data)
            : umap.InitializeFit(data, umap_indices, umap_distances);
        for (int epoch = 0; epoch < epochs; epoch++)
            umap.Step();

        state.UmapEmbedding = to_matrix(umap.GetEmbedding());
        return this;
    }

    public ReductionPipeline RunLeiden(LeidenClusteringOptions? options = null, bool force_graph = false, bool force = false)
    {
        if (!force && state.LeidenClusters is not null)
            return this;

        if (force_graph || state.KnnGraph is null)
            BuildKnnGraph(force: force_graph);

        state.LeidenClusters = LeidenClustering.Cluster(state.KnnGraph!.Network, options);
        return this;
    }

    public ReductionPipeline RunFlowSom(FlowSomClustererOptions? options = null, bool force = false)
    {
        if (!force && state.FlowSomClusters is not null)
            return this;

        var clusterer = new FlowSomClusterer(options);
        clusterer.Train(state.CurrentMatrix);
        state.FlowSomClusters = clusterer.Predict(state.CurrentMatrix);
        state.FlowSomClusterer = clusterer;
        return this;
    }

    private static float[,] to_matrix(float[][] data)
    {
        int rows = data.Length;
        int columns = rows == 0 ? 0 : data[0].Length;
        var result = new float[rows, columns];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            result[row, column] = data[row][column];
        return result;
    }

    private static (int[][] Indices, float[][] Distances) prepare_umap_knn(KnnGraphResult graph, int row_count, int neighbor_count)
    {
        int width = Math.Min(neighbor_count, row_count);
        var indices = new int[row_count][];
        var distances = new float[row_count][];
        for (int row = 0; row < row_count; row++)
        {
            indices[row] = new int[width];
            distances[row] = new float[width];
            indices[row][0] = row;
            distances[row][0] = 0;
            int copied = Math.Min(width - 1, graph.Indices[row].Length);
            Array.Copy(graph.Indices[row], 0, indices[row], 1, copied);
            Array.Copy(graph.Distances[row], 0, distances[row], 1, copied);
            for (int index = copied + 1; index < width; index++)
            {
                indices[row][index] = -1;
                distances[row][index] = float.PositiveInfinity;
            }
        }

        return (indices, distances);
    }
}
