using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using gated.Models;

namespace gated;

public sealed record ConcatenateSamplesResult(string Name, bool JoinTime, IReadOnlyList<ConcatenateEventSource> Sources);

public sealed record ConcatenateEventSource(FlowSample Sample, int[]? EventIndices);

public partial class ConcatenateSamplesWindow : Window
{
    private FlowGroup group = null!;
    private readonly ObservableCollection<PlatformPopulationInput> rows = new();

    public ConcatenateSamplesWindow()
    {
        InitializeComponent();
    }

    public ConcatenateSamplesWindow(FlowGroup group)
    {
        InitializeComponent();
        this.group = group;
        sampleNameBox.Text = $"{group.Name} concatenated";
        populationTree.Nodes = rows;
        build_rows();
        cancelButton.Click += (_, _) => Close(null);
        okButton.Click += (_, _) => close_with_result();
    }

    private void build_rows()
    {
        rows.Clear();
        foreach (var sample in group.Samples)
        {
            var sample_key = Guid.NewGuid();
            rows.Add(new PlatformPopulationInput
            {
                RowKey = sample_key,
                GroupId = group.Id,
                SampleId = sample.Id,
                GroupName = group.Name,
                SampleName = sample.Name,
                PopulationName = sample.Name,
                Depth = 0,
                HasChildren = sample.Populations.Count > 0,
                IsPopulation = false,
                IsSelected = false
            });

            foreach (var population in sample.Populations)
                append_population_row(sample, population, sample_key, 1);
        }
    }

    private void append_population_row(FlowSample sample, PopulationResult population, Guid parent_key, int depth)
    {
        var row_key = Guid.NewGuid();
        rows.Add(new PlatformPopulationInput
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
            IsSelected = false
        });

        foreach (var child in population.Children)
            append_population_row(sample, child, row_key, depth + 1);
    }

    private void close_with_result()
    {
        string name = sampleNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            statusText.Text = "Enter a sample name.";
            return;
        }

        var sources = selected_sources();
        if (sources.Count == 0)
        {
            statusText.Text = "Select at least one sample or population.";
            return;
        }

        Close(new ConcatenateSamplesResult(name, timeModeBox.SelectedIndex == 1, sources));
    }

    private IReadOnlyList<ConcatenateEventSource> selected_sources()
    {
        var selected = rows.Where(row => row.IsSelected && row.IsEnabled && !row.IsIndeterminate).ToArray();
        var sources = new List<ConcatenateEventSource>();
        foreach (var sample in group.Samples)
        {
            if (selected.Any(row => !row.IsPopulation && row.SampleId == sample.Id))
            {
                sources.Add(new ConcatenateEventSource(sample, null));
                continue;
            }

            var indices = selected
                .Where(row => row.IsPopulation && row.SampleId == sample.Id)
                .SelectMany(row => find_population(sample.Populations, row.GateId, row.Region)?.EventIndices ?? [])
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (indices.Length > 0)
                sources.Add(new ConcatenateEventSource(sample, indices));
        }

        return sources;
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
}
