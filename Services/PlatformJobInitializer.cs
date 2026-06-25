using System;
using System.Linq;
using gated.Models;

namespace gated.Services;

public static class PlatformJobInitializer
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
        var job = PlatformFactory.Create(kind);
        job.Name = next_platform_name(workspace, kind);
        Populate(workspace, job, selected_group);
        return job;
    }

    public static void Populate(FlowWorkspace workspace, Platform job, FlowGroup? selected_group)
    {
        job.Populations.Clear();
        ensure_metadata_schema(workspace);
        if (job is IntegrationPlatform integration)
            integration.BatchColumnName = default_batch_column_name(workspace);

        foreach (var group in workspace.Groups)
        foreach (var sample in group.Samples)
        {
            var sample_row_key = Guid.NewGuid();
            bool is_selected = job.Kind == PlatformKind.Integration &&
                               (selected_group is null || ReferenceEquals(group, selected_group));
            job.Populations.Add(new IntegrationJobPopulationSelection
            {
                RowKey = sample_row_key,
                GroupId = group.Id,
                SampleId = sample.Id,
                GroupName = group.Name,
                SampleName = sample.Name,
                PopulationName = sample.Name,
                Depth = 0,
                HasChildren = sample.Populations.Count > 0,
                IsPopulation = false,
                IsPlatformDropped = job.Kind == PlatformKind.Integration,
                IsSelected = is_selected
            });

            foreach (var population in sample.Populations)
                append_population_selection(job, group, sample, population, sample_row_key, 1, is_selected);
        }

        update_integration_population_states(job);
        RefreshFeatures(workspace, job);
    }

    public static void RefreshFeatures(FlowWorkspace workspace, Platform job)
    {
        var selected_sample_ids = job.Populations
            .Where(population => job.Kind == PlatformKind.Integration
                ? population.IsSelected && population.IsEnabled && !population.IsIndeterminate
                : population.IsPopulation && population.IsPlatformDropped)
            .Select(population => population.SampleId)
            .Distinct()
            .ToArray();
        var samples = workspace.Groups.SelectMany(group => group.Samples)
            .Where(sample => selected_sample_ids.Contains(sample.Id))
            .ToArray();

        var previous = job.Features
            .Where(feature => feature.IsChannel)
            .ToDictionary(feature => feature.ChannelName, feature => feature.IsSelected, StringComparer.Ordinal);
        var previous_expanded = job.Features.ToDictionary(feature => feature.ChannelName, feature => feature.IsExpanded, StringComparer.Ordinal);
        var previous_root = job.Features.FirstOrDefault(feature => !feature.IsChannel);
        bool previous_root_selected = previous_root?.IsSelected ?? true;
        bool previous_root_expanded = previous_root?.IsExpanded ?? true;

        job.Features.Clear();
        if (samples.Length == 0)
            return;

        var root_key = Guid.NewGuid();
        job.Features.Add(new IntegrationJobFeatureSelection
        {
            RowKey = root_key,
            GroupName = "Shared feature channels",
            Depth = 0,
            HasChildren = true,
            IsChannel = false,
            IsSelected = previous_root_selected,
            IsExpanded = previous_root_expanded
        });

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
            job.Features.Add(new IntegrationJobFeatureSelection
            {
                ParentKey = root_key,
                ChannelName = channel.Name,
                Label = channel.Label,
                Depth = 1,
                IsChannel = true,
                IsSelected = !previous.TryGetValue(channel.Name, out bool was_selected) || was_selected,
                IsExpanded = !previous_expanded.TryGetValue(channel.Name, out bool was_expanded) || was_expanded
            });
        }

        update_integration_feature_states(job);
    }

    private static string next_platform_name(FlowWorkspace workspace, PlatformKind kind)
    {
        string prefix = kind switch
        {
            PlatformKind.CellCycle => "Cell cycle",
            PlatformKind.Proliferation => "Proliferation",
            PlatformKind.IntensityComparison => "Intensity comparison",
            PlatformKind.Kinetics => "Kinetics",
            _ => "Integration"
        };
        int index = workspace.IntegrationJobs.Count + 1;
        while (workspace.IntegrationJobs.Any(job => job.Name == $"{prefix} {index}"))
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
        job.Populations.Add(new IntegrationJobPopulationSelection
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
            Depth = depth,
            HasChildren = population.Children.Count > 0,
            IsPopulation = true,
            IsPlatformDropped = job.Kind == PlatformKind.Integration,
            IsSelected = is_selected
        });

        foreach (var child in population.Children)
            append_population_selection(job, group, sample, child, row_key, depth + 1, is_selected);
    }

    private static void update_integration_population_states(Platform job)
    {
        if (job.Kind == PlatformKind.Integration)
            apply_hierarchy_states(job.Populations.ToArray(), row => row.RowKey, row => row.ParentKey, row => row.IsSelected,
                (row, value) => row.IsSelected = value, (row, value) => row.IsEnabled = value, (row, value) => row.IsIndeterminate = value);
    }

    private static void update_integration_feature_states(Platform job)
    {
        if (job.Kind == PlatformKind.Integration)
            apply_hierarchy_states(job.Features.ToArray(), row => row.RowKey, row => row.ParentKey, row => row.IsSelected,
                (row, value) => row.IsSelected = value, (row, value) => row.IsEnabled = value, (row, value) => row.IsIndeterminate = value);
    }

    private static void apply_hierarchy_states<T>(
        T[] rows,
        Func<T, Guid> key,
        Func<T, Guid?> parent_key,
        Func<T, bool> is_selected,
        Action<T, bool> set_selected,
        Action<T, bool> set_enabled,
        Action<T, bool> set_indeterminate)
    {
        var children = rows.GroupBy(parent_key)
            .Where(group => group.Key.HasValue)
            .ToDictionary(group => group.Key!.Value, group => group.ToArray());

        foreach (var row in rows)
        {
            set_enabled(row, true);
            set_indeterminate(row, false);
        }

        foreach (var root in rows.Where(row => parent_key(row) is null))
            apply_descendant_states(root, inherited_selected: false);

        bool apply_descendant_states(T row, bool inherited_selected)
        {
            bool selected = is_selected(row);
            if (inherited_selected)
            {
                set_enabled(row, false);
                set_selected(row, true);
                selected = true;
            }

            bool descendant_selected = false;
            if (children.TryGetValue(key(row), out var child_rows))
            {
                foreach (var child in child_rows)
                    descendant_selected |= apply_descendant_states(child, inherited_selected || selected);
                if (!selected && descendant_selected)
                    set_indeterminate(row, true);
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
        return choices.FirstOrDefault(name => string.Equals(name, "Batch", StringComparison.Ordinal)) ??
               choices.FirstOrDefault() ??
               "";
    }
}
