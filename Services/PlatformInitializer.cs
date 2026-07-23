using System;
using System.Collections.Generic;
using System.Linq;
using gated.Models;

namespace gated.Services;

public static class PlatformInitializer
{
    public static PlatformKind KindFromParameter(object? parameter)
    {
        if (parameter is PlatformKind kind)
            return kind;
        if (parameter is string text && Enum.TryParse(text, ignoreCase: true, out PlatformKind parsed))
            return parsed;
        return PlatformKind.Integration;
    }

    public static Platform Create(FlowWorkspace workspace, PlatformKind kind, FlowGroup? selected_group)
    {
        var implementation = gated.ViewModels.Platforms.PlatformCatalog.Get(kind);
        var job = implementation.CreateModel();
        job.Name = next_platform_name(workspace, kind);
        Populate(workspace, job, selected_group);
        return job;
    }

    public static void Populate(FlowWorkspace workspace, Platform job, FlowGroup? selected_group)
    {
        job.Populations.Clear();
        job.Features.Clear();
        ensure_metadata_schema(workspace);
        if (job is IntegrationPlatform integration)
            integration.BatchColumnName = default_batch_column_name(workspace);

        if (job.Kind == PlatformKind.Integration)
        {
            foreach (var group in workspace.Groups)
            foreach (var sample in group.Samples)
            {
                var sample_row_key = Guid.NewGuid();
                bool is_selected = selected_group is null || ReferenceEquals(group, selected_group);
                job.Populations.Add(new PlatformPopulationInput
                {
                    RowKey = sample_row_key,
                    GroupId = group.Id,
                    SampleId = sample.Id,
                    GroupName = group.Name,
                    SampleName = sample.Name,
                    PopulationName = "All events",
                    EventCount = sample.EventCount,
                    Depth = 0,
                    HasChildren = sample.Populations.Count > 0,
                    IsPopulation = false,
                    IsPlatformDropped = true,
                    IsSelected = is_selected
                });

                foreach (var population in sample.Populations)
                    append_population_selection(job, group, sample, population, sample_row_key, 1, is_selected);
            }

            update_population_selection_states(job);
        }

        RefreshFeatures(workspace, job);
        RefreshTransformations(workspace, job);
    }

    public static void RefreshFeatures(FlowWorkspace workspace, Platform job)
    {
        var selected_sample_ids = SelectedPopulationInputs(job)
            .Select(population => population.SampleId)
            .Distinct()
            .ToArray();
        var samples = workspace.Groups.SelectMany(group => group.Samples)
            .Where(sample => selected_sample_ids.Contains(sample.Id))
            .ToArray();

        var previous = job.Features
            .Where(feature => feature.IsChannel)
            .ToDictionary(feature => feature.ChannelName, feature => feature.IsSelected, StringComparer.Ordinal);
        job.Features.Clear();
        if (samples.Length == 0)
            return;

        var shared = samples
            .Select(sample => sample.Channels.Select(channel => channel.Name).ToHashSet(StringComparer.Ordinal))
            .Aggregate((left, right) =>
            {
                left.IntersectWith(right);
                return left;
            });

        foreach (string channel_name in shared.OrderBy(name => name, StringComparer.Ordinal))
        {
            var channel = samples[0].Channels.First(item => item.Name == channel_name);
            job.Features.Add(new PlatformFeatureSelection
            {
                ChannelName = channel.Name,
                Label = channel.Label,
                IsChannel = true,
                IsSelected = !previous.TryGetValue(channel.Name, out bool was_selected) || was_selected
            });
        }
    }

    public static void RefreshTransformations(FlowWorkspace workspace, Platform job)
    {
        var all_samples = workspace.Groups.SelectMany(group => group.Samples).ToArray();
        var selected_sample_ids = job.RowMap.Sources.Count > 0
            ? job.RowMap.Sources.Select(source => source.SampleId)
            : SelectedPopulationInputs(job).Select(population => population.SampleId);
        var selected_samples = selected_sample_ids
            .Distinct()
            .Select(sample_id => all_samples.FirstOrDefault(sample => sample.Id == sample_id))
            .Where(sample => sample is not null)
            .Cast<FlowSample>()
            .ToArray();
        var channels = (job.Kind == PlatformKind.Integration
                ? job.Features.Where(feature => feature.IsChannel).Select(feature => feature.ChannelName)
                : job.SelectedFeatureNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string channel in channels)
        {
            if (job.Kind == PlatformKind.Integration)
            {
                if (job.Transformations.TryGetValue(channel, out var existing) && !existing.IsAutomatic)
                    continue;
                string cytometer = selected_samples.Select(Configuration.CytometerNameForSample).FirstOrDefault()
                    ?? Configuration.DefaultCytometerName;
                var scale = Configuration.DefaultCoordinateScaleForChannel(channel, cytometer);
                double maximum = selected_samples
                    .Select(sample => sample.Channels.FirstOrDefault(item => item.Name == channel)?.Maximum)
                    .Where(value => value.HasValue && value.Value > 0)
                    .Select(value => (double)value!.Value)
                    .DefaultIfEmpty(job.Axis.Maximum)
                    .Max();
                job.Transformations[channel] = new PlatformChannelTransformation
                {
                    Kind = scale switch
                    {
                        CoordinateScaleKind.Logarithmic => PlatformTransformationKind.Logarithm,
                        CoordinateScaleKind.Logicle => PlatformTransformationKind.Logicle,
                        CoordinateScaleKind.Arcsinh => PlatformTransformationKind.Arcsinh,
                        _ => PlatformTransformationKind.Linear
                    },
                    Minimum = scale == CoordinateScaleKind.Linear ? -0.1 * maximum : -0.01 * maximum,
                    Maximum = maximum,
                    Logicle = job.Axis.Logicle with { T = maximum },
                    ArcsinhCofactor = 5.0,
                    IsAutomatic = true
                };
                continue;
            }

            job.Transformations[channel] = new PlatformChannelTransformation
            {
                Kind = job.Axis.Transform,
                Minimum = job.Axis.Minimum,
                Maximum = job.Axis.Maximum,
                Logicle = job.Axis.Logicle,
                ArcsinhCofactor = job is UnivariatePlatform univariate ? univariate.ArcsinhCofactor : 5.0,
                IsAutomatic = true
            };
        }
    }

    public static IEnumerable<PlatformPopulationInput> SelectedPopulationInputs(Platform job) =>
        job.Kind == PlatformKind.Integration
            ? job.Populations.Where(population =>
                population.IsPlatformDropped &&
                population.IsSelected &&
                population.IsEnabled &&
                !population.IsIndeterminate)
            : job.Populations.Where(population => population.IsPlatformDropped);

    private static string next_platform_name(FlowWorkspace workspace, PlatformKind kind)
    {
        string prefix = gated.ViewModels.Platforms.PlatformCatalog.Get(kind).NamePrefix;
        int index = workspace.Platforms.Count + 1;
        while (workspace.Platforms.Any(job => job.Name == $"{prefix} {index}"))
            index++;
        return $"{prefix} {index}";
    }

    private static void append_population_selection(
        Platform job,
        FlowGroup group,
        FlowSample sample,
        PopulationResult population,
        Guid parent_key,
        int depth,
        bool is_selected)
    {
        var row_key = Guid.NewGuid();
        job.Populations.Add(new PlatformPopulationInput
        {
            RowKey = row_key,
            ParentKey = parent_key,
            GroupId = group.Id,
            SampleId = sample.Id,
            GateId = population.Gate.Id,
            Region = population.Region,
            GroupName = group.Name,
            SampleName = sample.Name,
            PopulationName = population.DisplayName,
            EventCount = population.EventCount,
            Depth = depth,
            HasChildren = population.Children.Count > 0,
            IsPopulation = true,
            IsPlatformDropped = true,
            IsSelected = is_selected
        });

        foreach (var child in population.Children)
            append_population_selection(job, group, sample, child, row_key, depth + 1, is_selected);
    }

    private static void update_population_selection_states(Platform job)
    {
        var rows = job.Populations.ToArray();
        var children = rows.GroupBy(row => row.ParentKey)
            .Where(group => group.Key.HasValue)
            .ToDictionary(group => group.Key!.Value, group => group.ToArray());

        foreach (var row in rows)
        {
            row.IsEnabled = true;
            row.IsIndeterminate = false;
        }

        foreach (var root in rows.Where(row => row.ParentKey is null))
            apply_descendant_states(root, inherited_selected: false);

        bool apply_descendant_states(PlatformPopulationInput row, bool inherited_selected)
        {
            bool selected = row.IsSelected;
            if (inherited_selected)
            {
                row.IsEnabled = false;
                row.IsSelected = true;
                selected = true;
            }

            bool descendant_selected = false;
            if (children.TryGetValue(row.RowKey, out var child_rows))
            {
                foreach (var child in child_rows)
                    descendant_selected |= apply_descendant_states(child, inherited_selected || selected);
                if (!selected && descendant_selected)
                    row.IsIndeterminate = true;
            }

            return selected || descendant_selected;
        }
    }

    private static void ensure_metadata_schema(FlowWorkspace workspace)
    {
        workspace.MetadataColumns["Group"] = MetadataColumnKind.String;
        workspace.MetadataColumns["Sample"] = MetadataColumnKind.String;
        workspace.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
        foreach (var group in workspace.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
            sample.Metadata[Configuration.CytometerMetadataKey] = Configuration.CytometerNameForSample(sample);
        }
    }

    private static string default_batch_column_name(FlowWorkspace workspace)
    {
        var choices = workspace.MetadataColumns
            .Where(column => column.Value == MetadataColumnKind.String)
            .Select(column => column.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return choices.FirstOrDefault(name => string.Equals(name, "Sample", StringComparison.Ordinal)) ??
               choices.FirstOrDefault(name => string.Equals(name, "Batch", StringComparison.Ordinal)) ??
               choices.FirstOrDefault() ??
               "";
    }
}
