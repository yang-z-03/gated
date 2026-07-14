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

    public bool Prepare(Platform job)
    {
        if (!try_build_source(job, out var raw, out var source, out var batches, out var row_map, out string warning))
        {
            set_warning(job, warning);
            return false;
        }

        job.RowMap.Set(row_map.Sources, row_map.SourceIds, row_map.EventIndices);
        job.Compensated = source;
        job.Matrix = raw;
        if (job is IntegrationPlatform integration)
            integration.BatchIds = batches;
        if (job is KineticsPlatform kinetics)
            kinetics.TimeValues = build_time_values(kinetics);
        if (job is MultivariatePlatform multivariate && job.Kind != PlatformKind.Integration)
            multivariate.Normalized = null;
        update_platform_api_data(job);
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 3);
        job.NotifyIntegrationDataChanged();
        return true;
    }

    public bool RunIntegration(Platform job)
    {
        if (!Prepare(job))
            return false;

        report(job, 0, "Applying logicle normalization");
        job.Status = IntegrationJobStatus.Running;
        var integration = job as IntegrationPlatform
            ?? throw new InvalidOperationException("Integration can only run on an integration platform.");
        var pipeline = new ReductionPipeline(job.Compensated!);
        pipeline.ApplyLogicle(
            new LogicleNormalizationOptions
            {
                Parameters = job.Axis.Logicle,
                Scales = build_reduction_scales(job)
            },
            force: true);
        var logicle_normalized = copy(pipeline.State.LogicleNormalized);

        if (integration.BatchIds.Distinct().Skip(1).Any())
        {
            report(job, 0.35, "Fitting CytoNorm model");
            pipeline.FitCytoNorm(logicle_normalized!, integration.BatchIds, integration.CytoNormOptions, force: true);
            report(job, 0.7, "Applying CytoNorm integration");
            pipeline.ApplyCytoNorm(integration.BatchIds, force: true);
            integration.Normalized = copy(pipeline.State.CytoNormNormalized);
        }
        else
        {
            integration.Normalized = logicle_normalized;
        }

        report(job, 1, "Integration complete");
        job.WarningText = "";
        job.Status = IntegrationJobStatus.Ready;
        job.CurrentStep = Math.Max(job.CurrentStep, 4);
        job.NotifyIntegrationDataChanged();
        return true;
    }

    private bool try_build_source(
        Platform job,
        out float[,] raw,
        out float[,] source,
        out int[] batches,
        out IntegrationJobRowMap row_map,
        out string warning)
    {
        raw = new float[0, 0];
        source = new float[0, 0];
        batches = [];
        row_map = new IntegrationJobRowMap();
        warning = "";

        var selected_populations = job.Populations
            .Where(item => job.Kind == PlatformKind.Integration
                ? item.IsSelected && item.IsEnabled && !item.IsIndeterminate
                : item.IsPlatformDropped && item.IsPopulation)
            .ToArray();
        if (selected_populations.Length == 0)
        {
            warning = job.Kind == PlatformKind.Integration
                ? "Select at least one subpopulation before running the job."
                : "Drop at least one population before running the platform.";
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
                if (job.Kind == PlatformKind.Integration && !seen_events.Add((sample.Id, event_index)))
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

        if (job.Kind != PlatformKind.Integration)
        {
            source = new float[rows.Count, selected_features.Length];
            raw = new float[rows.Count, selected_features.Length];
            batches = new int[rows.Count];
            var model_row_map_sources = new List<IntegrationJobRowMapSource>();
            var model_row_map_source_lookup = new Dictionary<(Guid GroupId, Guid SampleId, Guid GateId, PopulationRegion Region), int>();
            var model_row_map_source_ids = new int[rows.Count];
            var model_row_map_event_indices = new int[rows.Count];
            fill_source_rows(rows, selected_features, raw, source, batches, model_row_map_sources, model_row_map_source_lookup, model_row_map_source_ids, model_row_map_event_indices, batch_lookup: null, out warning);
            if (!string.IsNullOrWhiteSpace(warning))
                return false;
            row_map.Set(model_row_map_sources, model_row_map_source_ids, model_row_map_event_indices);
            return true;
        }

        var integration = job as IntegrationPlatform;
        if (integration is null)
        {
            warning = "Integration preparation requires an integration platform.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(integration.BatchColumnName))
        {
            warning = "Select a non-numeric metadata column to use as batch information.";
            return false;
        }

        if (!workspace.MetadataColumns.TryGetValue(integration.BatchColumnName, out var batch_column_kind))
        {
            warning = $"Batch metadata column '{integration.BatchColumnName}' is not available.";
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
            warning = $"Every selected sample must have a value in '{integration.BatchColumnName}'.";
            return false;
        }

        source = new float[rows.Count, selected_features.Length];
        raw = new float[rows.Count, selected_features.Length];
        batches = new int[rows.Count];
        var row_map_sources = new List<IntegrationJobRowMapSource>();
        var row_map_source_lookup = new Dictionary<(Guid GroupId, Guid SampleId, Guid GateId, PopulationRegion Region), int>();
        var row_map_source_ids = new int[rows.Count];
        var row_map_event_indices = new int[rows.Count];
        fill_source_rows(rows, selected_features, raw, source, batches, row_map_sources, row_map_source_lookup, row_map_source_ids, row_map_event_indices, batch_lookup, out warning);
        if (!string.IsNullOrWhiteSpace(warning))
            return false;

        row_map.Set(row_map_sources, row_map_source_ids, row_map_event_indices);
        return true;
    }

    private static void fill_source_rows(
        IReadOnlyList<(FlowSample Sample, IntegrationJobPopulationSelection Selection, int EventIndex)> rows,
        IReadOnlyList<string> selected_features,
        float[,] raw,
        float[,] source,
        int[] batches,
        List<IntegrationJobRowMapSource> row_map_sources,
        Dictionary<(Guid GroupId, Guid SampleId, Guid GateId, PopulationRegion Region), int> row_map_source_lookup,
        int[] row_map_source_ids,
        int[] row_map_event_indices,
        IReadOnlyDictionary<Guid, string>? batch_lookup,
        out string warning)
    {
        warning = "";
        var batch_ids = batch_lookup?.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select((value, index) => (value, index))
            .ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);
        for (int row = 0; row < rows.Count; row++)
        {
            var item = rows[row];
            for (int column = 0; column < selected_features.Count; column++)
            {
                int channel_index = item.Sample.GetChannelIndex(selected_features[column]);
                if (channel_index < 0)
                {
                    warning = "Selected feature channels are not shared by every selected sample.";
                    return;
                }

                raw[row, column] = item.Sample.RawEvents[item.EventIndex, channel_index];
                source[row, column] = item.Sample.CompensatedEvents[item.EventIndex, channel_index];
            }

            batches[row] = batch_ids is not null && batch_lookup is not null ? batch_ids[batch_lookup[item.Sample.Id]] : 0;
            (Guid GroupId, Guid SampleId, Guid GateId, PopulationRegion Region) source_key =
                (item.Selection.GroupId, item.Sample.Id, item.Selection.GateId, item.Selection.Region);
            if (!row_map_source_lookup.TryGetValue(source_key, out int source_id))
            {
                source_id = row_map_sources.Count;
                row_map_source_lookup[source_key] = source_id;
                row_map_sources.Add(new IntegrationJobRowMapSource
                {
                    GroupId = source_key.GroupId,
                    SampleId = source_key.SampleId,
                    GateId = source_key.GateId,
                    Region = source_key.Region
                });
            }

            row_map_source_ids[row] = source_id;
            row_map_event_indices[row] = item.EventIndex;
        }
    }

    private CoordinateScaleKind[,] build_reduction_scales(Platform job)
    {
        var features = job.SelectedFeatureNames;
        var scales = new CoordinateScaleKind[job.RowMap.Count, features.Length];
        for (int row = 0; row < job.RowMap.Count; row++)
        {
            int source_id = job.RowMap.SourceIds[row];
            var source = source_id >= 0 && source_id < job.RowMap.Sources.Count
                ? job.RowMap.Sources[source_id]
                : null;
            var sample = source is null ? null : find_sample(source.SampleId);
            string cytometer_name = Configuration.CytometerNameForSample(sample);
            for (int column = 0; column < features.Length; column++)
            {
                string feature = features[column];
                scales[row, column] = sample is not null && sample.Embeddings.ContainsKey(feature)
                    ? CoordinateScaleKind.Linear
                    : Configuration.DefaultCoordinateScaleForChannel(feature, cytometer_name);
            }
        }

        return scales;
    }

    private Dictionary<Guid, string> build_batch_lookup(Platform job, IEnumerable<Guid> sample_ids)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var sample_id in sample_ids)
        {
            var batch = find_sample(sample_id) is { } sample &&
                        sample.Metadata.TryGetValue(job is IntegrationPlatform integration ? integration.BatchColumnName : "", out string? sample_batch)
                ? sample_batch
                : "";
            result[sample_id] = batch;
        }

        return result;
    }

    private void update_platform_api_data(Platform job)
    {
        update_transformations(job);
        job.Transformed = transformed_matrix(job);
        if (job is UnivariatePlatform univariate)
            update_univariate_data(job, univariate);
        if (job is BivariatePlatform bivariate)
            update_bivariate_data(job, bivariate);
        foreach (var series in job.PlotSeries)
            job.Series[series.Key] = series;
        foreach (var curve in job.FitCurves)
            job.Models[curve.Key] = curve;
    }

    private static void update_transformations(Platform job)
    {
        foreach (string channel in job.SelectedFeatureNames)
        {
            job.Transformations[channel] = new PlatformChannelTransformation
            {
                Kind = job.Axis.Transform,
                Minimum = job.Axis.Minimum,
                Maximum = job.Axis.Maximum,
                Logicle = job.Axis.Logicle
            };
        }
    }

    private static float[,]? transformed_matrix(Platform job)
    {
        var source = job.Compensated;
        if (source is null)
            return null;
        if (job.Axis.Transform == PlatformTransformationKind.Linear)
            return copy(source);

        var result = new float[source.GetLength(0), source.GetLength(1)];
        if (job.Axis.Transform == PlatformTransformationKind.Logicle)
        {
            var transform = new LogicleTransform(job.Axis.Logicle);
            for (int row = 0; row < source.GetLength(0); row++)
            for (int column = 0; column < source.GetLength(1); column++)
                result[row, column] = (float)transform.Transform(source[row, column]);
            return result;
        }

        if (job.Axis.Transform == PlatformTransformationKind.Arcsinh)
        {
            for (int row = 0; row < source.GetLength(0); row++)
            for (int column = 0; column < source.GetLength(1); column++)
                result[row, column] = (float)Math.Asinh(source[row, column] / 5.0);
            return result;
        }

        for (int row = 0; row < source.GetLength(0); row++)
        for (int column = 0; column < source.GetLength(1); column++)
        {
            double value = source[row, column];
            result[row, column] = double.IsFinite(value) ? (float)(Math.Sign(value) * Math.Log10(1.0 + Math.Abs(value))) : float.NaN;
        }
        return result;
    }

    private static void update_univariate_data(Platform job, UnivariatePlatform platform)
    {
        platform.Major = job.SelectedFeatureNames.FirstOrDefault() ?? "";
        var matrix = job.Transformed ?? job.Compensated;
        if (matrix is null || matrix.GetLength(1) == 0)
        {
            platform.Histogram = [];
            platform.Smoothed = [];
            return;
        }

        var values = column_values(matrix, 0);
        double minimum = transformed_axis_value(job, job.Axis.Minimum);
        double maximum = transformed_axis_value(job, job.Axis.Maximum);
        platform.Histogram = histogram(values, minimum, maximum, 400);
        platform.Smoothed = smooth(platform.Histogram, platform.EnableSmoothing ? platform.SmoothingWindow : 0);
    }

    private static void update_bivariate_data(Platform job, BivariatePlatform platform)
    {
        platform.Major = "Time";
        platform.Minor = job.SelectedFeatureNames.FirstOrDefault() ?? "";
        var matrix = job.Transformed ?? job.Compensated;
        if (platform is not KineticsPlatform kinetics || matrix is null || matrix.GetLength(1) == 0 || kinetics.TimeValues.Length != matrix.GetLength(0))
        {
            platform.Trend = new double[0, 0];
            platform.Binned = [];
            platform.Smoothed = [];
            return;
        }

        var trend = new double[matrix.GetLength(0), 2];
        for (int row = 0; row < matrix.GetLength(0); row++)
        {
            trend[row, 0] = kinetics.TimeValues[row];
            trend[row, 1] = matrix[row, 0];
        }
        platform.Trend = trend;
        platform.Binned = binned_average(trend, kinetics.TimeWindowCount);
        platform.Smoothed = smooth(platform.Binned, platform.EnableSmoothing ? platform.SmoothingWindow : 0);
    }

    private static double[] column_values(float[,] matrix, int column)
    {
        var values = new double[matrix.GetLength(0)];
        for (int row = 0; row < values.Length; row++)
            values[row] = matrix[row, column];
        return values;
    }

    private static double transformed_axis_value(Platform job, double value)
    {
        return job.Axis.Transform switch
        {
            PlatformTransformationKind.Logicle => new LogicleTransform(job.Axis.Logicle).Transform(value),
            PlatformTransformationKind.Logarithm => Math.Sign(value) * Math.Log10(1.0 + Math.Abs(value)),
            PlatformTransformationKind.Arcsinh => Math.Asinh(value / 5.0),
            _ => value
        };
    }

    private static double[] histogram(double[] values, double minimum, double maximum, int bins)
    {
        bins = Math.Max(1, bins);
        var result = new double[bins];
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return result;
        foreach (double value in values)
        {
            if (!double.IsFinite(value))
                continue;
            int bin = (int)Math.Floor((value - minimum) / (maximum - minimum) * bins);
            if (bin < 0 || bin >= bins)
                continue;
            result[bin]++;
        }
        double total = result.Sum();
        if (total > 0)
            for (int index = 0; index < result.Length; index++)
                result[index] /= total;
        return result;
    }

    private static double[] smooth(double[] values, int half_window)
    {
        if (half_window <= 0 || values.Length == 0)
            return values.ToArray();
        var result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            int start = Math.Max(0, index - half_window);
            int end = Math.Min(values.Length - 1, index + half_window);
            double sum = 0;
            for (int current = start; current <= end; current++)
                sum += values[current];
            result[index] = sum / (end - start + 1);
        }
        return result;
    }

    private static double[] binned_average(double[,] trend, int bins)
    {
        bins = Math.Clamp(bins, 1, 1000);
        var result = new double[bins];
        var counts = new int[bins];
        if (trend.GetLength(0) == 0)
            return result;
        double minimum = Enumerable.Range(0, trend.GetLength(0)).Select(row => trend[row, 0]).Where(double.IsFinite).DefaultIfEmpty(0).Min();
        double maximum = Enumerable.Range(0, trend.GetLength(0)).Select(row => trend[row, 0]).Where(double.IsFinite).DefaultIfEmpty(1).Max();
        if (maximum <= minimum)
            maximum = minimum + 1;
        for (int row = 0; row < trend.GetLength(0); row++)
        {
            double x = trend[row, 0];
            double y = trend[row, 1];
            if (!double.IsFinite(x) || !double.IsFinite(y))
                continue;
            int bin = Math.Clamp((int)Math.Floor((x - minimum) / (maximum - minimum) * bins), 0, bins - 1);
            result[bin] += y;
            counts[bin]++;
        }
        for (int index = 0; index < bins; index++)
            if (counts[index] > 0)
                result[index] /= counts[index];
        return result;
    }

    private FlowSample? find_sample(Guid sample_id) =>
        workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == sample_id);

    private float[] build_time_values(Platform job)
    {
        var result = new float[job.RowMap.Count];
        for (int row = 0; row < result.Length; row++)
        {
            int source_id = job.RowMap.SourceIds[row];
            var source = source_id >= 0 && source_id < job.RowMap.Sources.Count ? job.RowMap.Sources[source_id] : null;
            var sample = source is null ? null : find_sample(source.SampleId);
            int channel_index = sample is null ? -1 : time_channel_index(sample);
            int event_index = job.RowMap.EventIndices[row];
            result[row] = sample is not null &&
                          channel_index >= 0 &&
                          event_index >= 0 &&
                          event_index < sample.EventCount
                ? sample.CompensatedEvents[event_index, channel_index]
                : float.NaN;
        }

        return result;
    }

    private static int time_channel_index(FlowSample sample)
    {
        for (int index = 0; index < sample.Channels.Count; index++)
        {
            var channel = sample.Channels[index];
            if (string.Equals(channel.Name, "Time", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(channel.Label, "Time", StringComparison.OrdinalIgnoreCase) ||
                channel.Name.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
                channel.Label.Contains("Time", StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

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

    private static void set_warning(Platform job, string warning)
    {
        job.WarningText = warning;
        job.Status = IntegrationJobStatus.Warning;
    }

    private static float[,]? copy(float[,]? matrix) => matrix is null ? null : (float[,])matrix.Clone();

    private static void report(Platform job, double fraction, string text)
    {
        job.ProgressFraction = fraction;
        job.ProgressText = text;
    }
}
