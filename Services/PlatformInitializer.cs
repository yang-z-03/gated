using System;
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
    }

    public static void RefreshFeatures(FlowWorkspace workspace, Platform job)
    {
        var selected_sample_ids = job.Populations
            .Where(population => population.IsPlatformDropped)
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

    private static string next_platform_name(FlowWorkspace workspace, PlatformKind kind)
    {
        string prefix = gated.ViewModels.Platforms.PlatformCatalog.Get(kind).NamePrefix;
        int index = workspace.Platforms.Count + 1;
        while (workspace.Platforms.Any(job => job.Name == $"{prefix} {index}"))
            index++;
        return $"{prefix} {index}";
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
