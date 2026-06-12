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

        report(job, 1, "Integration complete");
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 4);
        return true;
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

        if (string.IsNullOrWhiteSpace(job.BatchColumnName))
        {
            warning = "Select a non-numeric metadata column to use as batch information.";
            return false;
        }

        if (!workspace.MetadataColumns.TryGetValue(job.BatchColumnName, out var batch_column_kind))
        {
            warning = $"Batch metadata column '{job.BatchColumnName}' is not available.";
            return false;
        }

        if (batch_column_kind != MetadataColumnKind.String)
        {
            warning = "Batch metadata must use a non-numeric metadata column.";
            return false;
        }

        var selected_sample_ids = rows.Select(row => row.Sample.Id).Distinct().ToArray();
        var batch_lookup = build_batch_lookup(job, selected_sample_ids);
        bool missing_batch = rows.Any(row => !batch_lookup.TryGetValue(row.Sample.Id, out string? batch) || string.IsNullOrWhiteSpace(batch));
        if (missing_batch)
        {
            warning = $"Every selected sample must have a value in '{job.BatchColumnName}'.";
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

    private Dictionary<Guid, string> build_batch_lookup(IntegrationJob job, IEnumerable<Guid> sample_ids)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var sample_id in sample_ids)
        {
            var batch = find_sample(sample_id) is { } sample &&
                        sample.Metadata.TryGetValue(job.BatchColumnName, out string? sample_batch)
                ? sample_batch
                : "";
            result[sample_id] = batch;
        }

        return result;
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
}
