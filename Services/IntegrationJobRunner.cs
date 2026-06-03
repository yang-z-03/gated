using System;
using System.Collections.Generic;
using System.Linq;
using gated.Models;
using gated.Reduction;

namespace gated.Services;

public sealed class IntegrationJobRunner
{
    private readonly FlowWorkspace workspace;

    public IntegrationJobRunner(FlowWorkspace workspace)
    {
        this.workspace = workspace;
    }

    public bool Prepare(IntegrationJob job)
    {
        if (!try_build_source(job, out var source, out var batches, out var row_map, out string warning))
        {
            set_warning(job, warning);
            return false;
        }

        job.RowMap.Clear();
        foreach (var row in row_map)
            job.RowMap.Add(row);
        job.SourceData = source;
        job.BatchIds = batches;
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 3);
        return true;
    }

    public bool RunIntegration(IntegrationJob job)
    {
        if (!Prepare(job))
            return false;

        report(job, 0, "Applying logicle normalization");
        job.Status = IntegrationJobStatus.Running;
        var pipeline = new ReductionPipeline(job.SourceData!);
        pipeline.ApplyLogicle(new LogicleNormalizationOptions { Parameters = job.Logicle }, force: true);
        job.LogicleNormalized = copy(pipeline.State.LogicleNormalized);

        if (job.BatchIds.Distinct().Skip(1).Any())
        {
            report(job, 0.35, "Fitting CytoNorm model");
            pipeline.FitCytoNorm(job.LogicleNormalized!, job.BatchIds, job.CytoNormOptions, force: true);
            report(job, 0.7, "Applying CytoNorm integration");
            pipeline.ApplyCytoNorm(job.BatchIds, force: true);
            job.CytoNormNormalized = copy(pipeline.State.CytoNormNormalized);
        }
        else
        {
            job.CytoNormNormalized = null;
        }

        job.InvalidateFromGraph();
        report(job, 1, "Integration complete");
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 4);
        return true;
    }

    public bool RunKnn(IntegrationJob job)
    {
        if (!ensure_current_matrix(job, out var matrix))
            return false;

        report(job, 0, "Building kNN graph");
        job.Status = IntegrationJobStatus.Running;
        var options = clone_knn_options(job.KnnOptions);
        options.Progress = (fraction, text) => report(job, fraction, text);
        options.IsCancellationRequested = () => job.CancellationRequested;
        var graph = KnnGraphBuilder.Build(matrix, options);
        job.KnnIndices = graph.Indices.Select(row => row.ToArray()).ToArray();
        job.KnnDistances = graph.Distances.Select(row => row.ToArray()).ToArray();
        job.UmapEmbedding = null;
        job.LeidenClusters = null;
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 5);
        report(job, 1, "kNN graph complete");
        return true;
    }

    public bool RunUmap(IntegrationJob job)
    {
        if (!ensure_current_matrix(job, out var matrix))
            return false;
        if (!ensure_knn(job, matrix))
            return false;

        job.Status = IntegrationJobStatus.Running;
        report(job, 0, "Running UMAP");
        var data = to_jagged(matrix);
        var umap = new Umap(
            dimensions: job.UmapOptions.Dimensions,
            numberOfNeighbors: job.UmapOptions.NeighborCount,
            customNumberOfEpochs: job.UmapOptions.EpochCount);
        var (indices, distances) = prepare_umap_knn(job.KnnIndices!, job.KnnDistances!, matrix.GetLength(0), job.UmapOptions.NeighborCount);
        int epochs = umap.InitializeFit(data, indices, distances);
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            if (job.CancellationRequested)
                throw new OperationCanceledException("UMAP cancelled.");
            umap.Step();
            if (epoch % 5 == 0)
                report(job, epochs == 0 ? 1 : epoch / (double)epochs, $"UMAP epoch {epoch + 1:N0} of {epochs:N0}");
        }
        job.UmapEmbedding = to_matrix(umap.GetEmbedding());
        WriteBack(job, OutputKind.Umap);
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 6);
        report(job, 1, "UMAP complete");
        return true;
    }

    public bool RunLeiden(IntegrationJob job)
    {
        if (!ensure_current_matrix(job, out var matrix))
            return false;
        if (!ensure_knn(job, matrix))
            return false;

        job.Status = IntegrationJobStatus.Running;
        report(job, 0, "Running Leiden clustering");
        var network = KnnGraphBuilder.BuildNetwork(job.KnnIndices!, job.KnnDistances!, matrix.GetLength(0), job.KnnOptions.Mutual);
        job.LeidenClusters = LeidenClustering.Cluster(network, job.LeidenOptions);
        WriteBack(job, OutputKind.Leiden);
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 6);
        report(job, 1, "Leiden complete");
        return true;
    }

    public bool RunFlowSom(IntegrationJob job)
    {
        if (!ensure_current_matrix(job, out var matrix))
            return false;

        job.Status = IntegrationJobStatus.Running;
        report(job, 0, "Running FlowSOM");
        var clusterer = new FlowSomClusterer(job.FlowSomOptions);
        clusterer.Train(matrix);
        job.FlowSomClusters = clusterer.Predict(matrix);
        job.FlowSomCodes = clusterer.Codes;
        job.FlowSomNodeClusters = clusterer.NodeClusters;
        WriteBack(job, OutputKind.FlowSom);
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 6);
        report(job, 1, "FlowSOM complete");
        return true;
    }

    public bool WriteBack(IntegrationJob job) => WriteBack(job, OutputKind.All);

    public bool WriteBack(IntegrationJob job, OutputKind output)
    {
        if (!validate_output_keys(job, output, out string warning))
        {
            set_warning(job, warning);
            return false;
        }

        var by_sample = job.RowMap
            .Select((row, global_index) => (row.SampleId, row.EventIndex, GlobalIndex: global_index))
            .GroupBy(item => item.SampleId);

        foreach (var group in by_sample)
        {
            var sample = find_sample(group.Key);
            if (sample is null)
                continue;

            if ((output is OutputKind.All or OutputKind.Umap) && job.WriteUmap && job.UmapEmbedding is not null)
                write_embedding(sample, group, job.UmapEmbedding, job);
            if ((output is OutputKind.All or OutputKind.Leiden) && job.WriteLeiden && job.LeidenClusters is not null)
                write_cluster(sample, group, job.LeidenClusters, job.LeidenKey);
            if ((output is OutputKind.All or OutputKind.FlowSom) && job.WriteFlowSom && job.FlowSomClusters is not null)
                write_cluster(sample, group, job.FlowSomClusters, job.FlowSomKey);

            sample.InvalidateNormalizedChannelCache();
        }

        job.WarningText = "";
        if (output == OutputKind.All)
            job.Status = IntegrationJobStatus.Complete;
        job.CurrentStep = 7;
        return true;
    }

    private bool ensure_current_matrix(IntegrationJob job, out float[,] matrix)
    {
        matrix = job.CurrentMatrix ?? new float[0, 0];
        if (matrix.GetLength(0) > 0 && matrix.GetLength(1) > 0)
            return true;

        set_warning(job, "Run integration before this step.");
        return false;
    }

    private bool ensure_knn(IntegrationJob job, float[,] matrix)
    {
        if (job.KnnIndices is not null && job.KnnDistances is not null)
            return true;

        return RunKnn(job);
    }

    private bool try_build_source(
        IntegrationJob job,
        out float[,] source,
        out int[] batches,
        out List<IntegrationJobRowMap> row_map,
        out string warning)
    {
        source = new float[0, 0];
        batches = [];
        row_map = [];
        warning = "";

        var selected_populations = job.Populations
            .Where(item => item.IsSelected && item.IsEnabled && !item.IsIndeterminate)
            .ToArray();
        if (selected_populations.Length == 0)
        {
            warning = "Select at least one subpopulation before running the job.";
            return false;
        }

        var selected_features = job.SelectedFeatureNames;
        if (selected_features.Length == 0)
        {
            warning = "Select at least one shared feature channel before running the job.";
            return false;
        }

        var rows = new List<(FlowSample Sample, IntegrationJobPopulationSelection Selection, int EventIndex)>();
        var seen_events = new HashSet<(Guid SampleId, int EventIndex)>();
        foreach (var selection in selected_populations)
        {
            var sample = find_sample(selection.SampleId);
            if (sample is null)
            {
                warning = "A selected population no longer exists. Refresh the job population selection.";
                return false;
            }

            var event_indices = selection.IsPopulation
                ? find_population(sample, selection.GateId, selection.Region)?.EventIndices
                : Enumerable.Range(0, sample.EventCount).ToArray();
            if (event_indices is null)
            {
                warning = "A selected population no longer exists. Refresh the job population selection.";
                return false;
            }

            foreach (int event_index in event_indices)
            {
                if (!seen_events.Add((sample.Id, event_index)))
                {
                    warning = "Selected populations overlap on the same sample event. Remove one overlapping population before running.";
                    return false;
                }

                rows.Add((sample, selection, event_index));
            }
        }

        if (rows.Count == 0)
        {
            warning = "Selected samples have no events in the selected subpopulations.";
            return false;
        }

        var batch_lookup = build_batch_lookup(job);
        bool missing_batch = rows.Any(row => !batch_lookup.TryGetValue(row.Sample.Id, out string? batch) || string.IsNullOrWhiteSpace(batch));
        if (missing_batch)
        {
            warning = "Every selected sample must have a Batch value.";
            return false;
        }

        var batch_ids = batch_lookup.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select((value, index) => (value, index))
            .ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);

        source = new float[rows.Count, selected_features.Length];
        batches = new int[rows.Count];
        for (int row = 0; row < rows.Count; row++)
        {
            var item = rows[row];
            for (int column = 0; column < selected_features.Length; column++)
            {
                int channel_index = item.Sample.GetChannelIndex(selected_features[column]);
                if (channel_index < 0)
                {
                    warning = "Selected feature channels are not shared by every selected sample.";
                    return false;
                }

                source[row, column] = item.Sample.CompensatedEvents[item.EventIndex, channel_index];
            }

            batches[row] = batch_ids[batch_lookup[item.Sample.Id]];
            row_map.Add(new IntegrationJobRowMap
            {
                GroupId = item.Selection.GroupId,
                SampleId = item.Sample.Id,
                GateId = item.Selection.GateId,
                Region = item.Selection.Region,
                EventIndex = item.EventIndex
            });
        }

        return true;
    }

    private Dictionary<Guid, string> build_batch_lookup(IntegrationJob job)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var metadata in job.SampleMetadata)
        {
            result[metadata.SampleId] = metadata.Batch;
            if (find_sample(metadata.SampleId) is { } sample)
            {
                sample.Metadata["Batch"] = metadata.Batch;
                sample.Metadata["Condition"] = metadata.Condition;
                sample.Metadata["Notes"] = metadata.Notes;
            }
        }

        return result;
    }

    private bool validate_output_keys(IntegrationJob job, OutputKind output, out string warning)
    {
        warning = "";
        var requested = requested_keys(job, output).ToArray();
        if (requested.Length == 0)
        {
            warning = "Run at least one output algorithm before writing results.";
            return false;
        }

        if (requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
        {
            warning = "Output keys duplicate within this job. Rename the job before writing results.";
            return false;
        }

        foreach (var sample_id in job.RowMap.Select(row => row.SampleId).Distinct())
        {
            var sample = find_sample(sample_id);
            if (sample is null)
                continue;
            if (requested.Any(key => sample.Embeddings.ContainsKey(key) && !key.StartsWith(job.Name + " ", StringComparison.Ordinal)))
            {
                warning = "One or more output keys already exist in selected samples. Rename the job before writing results.";
                return false;
            }
        }

        return true;
    }

    private IEnumerable<string> requested_keys(IntegrationJob job, OutputKind output)
    {
        if ((output is OutputKind.All or OutputKind.Umap) && job.WriteUmap && job.UmapEmbedding is not null)
        {
            int dimensions = job.UmapEmbedding.GetLength(1);
            if (dimensions > 0)
                yield return job.UmapXKey;
            if (dimensions > 1)
                yield return job.UmapYKey;
            if (dimensions > 2)
                yield return job.UmapZKey;
        }
        if ((output is OutputKind.All or OutputKind.Leiden) && job.WriteLeiden && job.LeidenClusters is not null)
            yield return job.LeidenKey;
        if ((output is OutputKind.All or OutputKind.FlowSom) && job.WriteFlowSom && job.FlowSomClusters is not null)
            yield return job.FlowSomKey;
    }

    private static void write_embedding(
        FlowSample sample,
        IEnumerable<(Guid SampleId, int EventIndex, int GlobalIndex)> rows,
        float[,] embedding,
        IntegrationJob job)
    {
        string[] keys = [job.UmapXKey, job.UmapYKey, job.UmapZKey];
        int dimensions = Math.Min(embedding.GetLength(1), keys.Length);
        for (int dimension = 0; dimension < dimensions; dimension++)
        {
            var values = fill_nan(sample.EventCount);
            foreach (var row in rows)
                values[row.EventIndex] = embedding[row.GlobalIndex, dimension];
            sample.Embeddings[keys[dimension]] = values;
        }
    }

    private static void write_cluster(
        FlowSample sample,
        IEnumerable<(Guid SampleId, int EventIndex, int GlobalIndex)> rows,
        int[] clusters,
        string key)
    {
        var values = fill_nan(sample.EventCount);
        foreach (var row in rows)
            values[row.EventIndex] = clusters[row.GlobalIndex];
        sample.Embeddings[key] = values;
    }

    private static float[] fill_nan(int length)
    {
        var values = new float[length];
        Array.Fill(values, float.NaN);
        return values;
    }

    private FlowSample? find_sample(Guid sample_id) =>
        workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == sample_id);

    private static PopulationResult? find_population(FlowSample? sample, Guid gate_id, PopulationRegion region)
    {
        if (sample is null)
            return null;
        return find_population(sample.Populations, gate_id, region);
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, Guid gate_id, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate.Id == gate_id && population.Region == region)
                return population;
            var child = find_population(population.Children, gate_id, region);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static void set_warning(IntegrationJob job, string warning)
    {
        job.WarningText = warning;
        job.Status = IntegrationJobStatus.Warning;
    }

    private static float[,]? copy(float[,]? matrix) => matrix is null ? null : (float[,])matrix.Clone();

    private static void report(IntegrationJob job, double fraction, string text)
    {
        job.ProgressFraction = fraction;
        job.ProgressText = text;
    }

    private static KnnGraphOptions clone_knn_options(KnnGraphOptions options) =>
        new()
        {
            NeighborCount = options.NeighborCount,
            Distance = options.Distance,
            SearchMethod = options.SearchMethod,
            Mutual = options.Mutual,
            IterationCount = options.IterationCount,
            MaxCandidates = options.MaxCandidates,
            Random = options.Random
        };

    private static float[][] to_jagged(float[,] matrix)
    {
        int rows = matrix.GetLength(0);
        int columns = matrix.GetLength(1);
        var result = new float[rows][];
        for (int row = 0; row < rows; row++)
        {
            result[row] = new float[columns];
            for (int column = 0; column < columns; column++)
                result[row][column] = matrix[row, column];
        }

        return result;
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

    private static (int[][] Indices, float[][] Distances) prepare_umap_knn(
        int[][] graph_indices,
        float[][] graph_distances,
        int row_count,
        int neighbor_count)
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
            int copied = Math.Min(width - 1, graph_indices[row].Length);
            Array.Copy(graph_indices[row], 0, indices[row], 1, copied);
            Array.Copy(graph_distances[row], 0, distances[row], 1, copied);
            for (int index = copied + 1; index < width; index++)
            {
                indices[row][index] = -1;
                distances[row][index] = float.PositiveInfinity;
            }
        }

        return (indices, distances);
    }
}

public enum OutputKind
{
    All,
    Umap,
    Leiden,
    FlowSom
}
