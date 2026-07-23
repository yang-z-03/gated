using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using gated.Controls;
using gated.Models;
using gated.Reduction;
using gated.Services;

namespace gated.ViewModels.Platforms;

public abstract class PlatformEditorViewModel : NotifyBase
{
    private bool handling_change;
    private AxisChoice? selected_channel;
    private string retained_batch_column_name = "";
    private readonly HashSet<PlatformPopulationInput> subscribed_population_rows = [];
    private readonly HashSet<PlatformFeatureSelection> subscribed_feature_rows = [];

    protected PlatformEditorViewModel(FlowWorkspace workspace, Platform platform)
    {
        Workspace = workspace;
        Platform = platform;
        Platform.PropertyChanged += platform_changed;
        Platform.Populations.CollectionChanged += populations_changed;
        Platform.Features.CollectionChanged += features_changed;
        resubscribe_population_rows();
        resubscribe_feature_rows();
        SelectionChangedCommand = new RelayCommand(_ => population_selection_changed());
        FeatureSelectionChangedCommand = new RelayCommand(_ => feature_selection_changed());
        DropPopulationCommand = new RelayCommand(parameter => drop_population(parameter as ProjectNode), parameter => IsEditable && can_drop_population(parameter as ProjectNode));
        RemovePopulationCommand = new RelayCommand(parameter => remove_population(parameter as PlatformPopulationInput), _ => IsEditable);
        RunCommand = new RelayCommand(_ => _ = run_async(), _ => can_run());
        RunLeidenCommand = new RelayCommand(_ => _ = run_integration_script_async("avares://gated/Python/leiden.py", "Leiden"), _ => can_run_integration_script());
        RunUmapCommand = new RelayCommand(_ => _ = run_integration_script_async("avares://gated/Python/umap.py", "UMAP"), _ => can_run_integration_script());
        CancelCommand = new RelayCommand(_ => cancel(), _ => Platform.IsRunning);
        refresh_choices();
        prepare_preview(preserve_fit_state: true);
    }

    public FlowWorkspace Workspace { get; }
    public Platform Platform { get; }
    public ObservableCollection<AxisChoice> ChannelChoices { get; } = new();
    public ObservableCollection<PlatformTransformationKind> TransformationChoices { get; } = new(Enum.GetValues<PlatformTransformationKind>());
    public ObservableCollection<EnumDisplayChoice<CellCycleModelKind>> CellCycleModelChoices { get; } = new(enum_choices<CellCycleModelKind>());
    public ObservableCollection<EnumDisplayChoice<CytoNormGoal>> CytoNormGoalChoices { get; } = new(enum_choices<CytoNormGoal>());
    public ObservableCollection<string> BatchColumnChoices { get; } = new();
    public ObservableCollection<string> DisplayChoices { get; } = new();
    public ICommand SelectionChangedCommand { get; }
    public ICommand FeatureSelectionChangedCommand { get; }
    public ICommand DropPopulationCommand { get; }
    public ICommand RemovePopulationCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RunLeidenCommand { get; }
    public ICommand RunUmapCommand { get; }
    public ICommand CancelCommand { get; }
    public virtual bool UsesPopulationDrop => true;
    public virtual bool EnablesDataSmoothing => true;
    public virtual string RunCaption => Platform.Kind == PlatformKind.Integration ? "Integrate" : "Run model";
    public bool IsEditable => Platform.IsIdle && !Platform.IsConfigurationLocked;
    public bool IsReadOnly => !IsEditable;
    public bool IsSelectionReadOnly => Platform.Kind == PlatformKind.Integration
        ? Platform.IsRunning || (Platform.Status == PlatformStatus.Complete && Platform.HasIntegrated)
        : IsReadOnly;
    public bool HasWarning => Platform.HasWarning;
    public string KindName => PlatformCatalog.Get(Platform.Kind).EditorTitle;
    public PlatformPlotDocument? PlotDocument => PlatformCatalog.Get(Platform.Kind).CreatePresentation(Platform).Plots.FirstOrDefault();
    public IReadOnlyList<HistogramCurveSeries> PlatformHistogramCurves => PlotDocument?.Series.Select(series => new HistogramCurveSeries
    {
        Name = series.Title,
        Points = series.X.Zip(series.Y, (x, y) => new HistogramPoint(x, y)).ToArray(),
        Color = PlatformPalette.ColorForSeriesKey(series.Key),
        Thickness = series.Role == PlatformSeriesRole.Fit ? 2.2 : 1.4,
        IsDashed = series.Role == PlatformSeriesRole.Component,
        FillOpacity = series.Role == PlatformSeriesRole.Component && platform_fill_components() ? 0.12 : 0
    }).ToArray() ?? [];
    public double PlatformHistogramMinimum => PlotDocument?.Minimum ?? Platform.Axis.Minimum;
    public double PlatformHistogramMaximum => PlotDocument?.Maximum ?? Platform.Axis.Maximum;
    public double PlatformHistogramYMaximum => Math.Max(0.01, (PlotDocument?.Series.SelectMany(series => series.Y).Where(double.IsFinite).DefaultIfEmpty(1).Max() ?? 1) * 1.1);
    public string PlatformHistogramXTitle => PlotDocument?.XLabel ?? "Intensity";
    public string PlatformHistogramYTitle => PlotDocument?.YLabel ?? "Normalized frequency";
    public HistogramAxisScaleKind PlatformHistogramAxisScale => Platform.Axis.Transform switch
    {
        PlatformTransformationKind.Logarithm => HistogramAxisScaleKind.Log,
        PlatformTransformationKind.Logicle => HistogramAxisScaleKind.Logicle,
        PlatformTransformationKind.Arcsinh => HistogramAxisScaleKind.Arcsinh,
        _ => HistogramAxisScaleKind.Linear
    };

    public AxisChoice? SelectedChannel
    {
        get => selected_channel;
        set
        {
            if (value is null || selected_channel?.Name == value.Name)
                return;
            selected_channel = value;
            foreach (var feature in Platform.Features.Where(feature => feature.IsChannel))
                feature.IsSelected = feature.ChannelName == value.Name;
            if (Platform is UnivariatePlatform univariate)
                univariate.Major = value.Name;
            Platform.Axis.Transform = Configuration.DefaultPlatformTransformationForChannel(value.Name);
            reset_x_axis_range();
            Platform.InvalidateFromConfiguration();
            prepare_preview();
            OnPropertyChanged();
        }
    }

    public string SelectedBatchColumnName
    {
        get => Platform is IntegrationPlatform integration ? integration.BatchColumnName : "";
        set
        {
            if (Platform is not IntegrationPlatform integration)
                return;
            if (string.IsNullOrWhiteSpace(value))
            {
                OnPropertyChanged();
                return;
            }
            retained_batch_column_name = value;
            if (integration.BatchColumnName == value)
                return;
            integration.BatchColumnName = value;
            Platform.InvalidateFromConfiguration();
            OnPropertyChanged();
        }
    }

    public PlatformTransformationKind AxisTransform
    {
        get => Platform.Axis.Transform;
        set
        {
            if (Platform.Axis.Transform == value)
                return;
            Platform.Axis.Transform = value;
            AxisMinimum = value == PlatformTransformationKind.Logicle ? -0.01 * AxisMaximum : -0.1 * AxisMaximum;
            display_option_changed(nameof(AxisTransform));
        }
    }

    public double AxisMinimum
    {
        get => Platform.Axis.Minimum;
        set
        {
            if (Platform.Axis.Minimum.Equals(value))
                return;
            Platform.Axis.Minimum = value;
            display_option_changed(nameof(AxisMinimum));
        }
    }

    public double AxisMaximum
    {
        get => Platform.Axis.Maximum;
        set
        {
            if (Platform.Axis.Maximum.Equals(value))
                return;
            Platform.Axis.Maximum = value;
            display_option_changed(nameof(AxisMaximum));
        }
    }

    public double LogicleT
    {
        get => Platform.Axis.Logicle.T;
        set => set_logicle(Platform.Axis.Logicle with { T = value }, nameof(LogicleT));
    }

    public double LogicleW
    {
        get => Platform.Axis.Logicle.W;
        set => set_logicle(Platform.Axis.Logicle with { W = value }, nameof(LogicleW));
    }

    public double LogicleM
    {
        get => Platform.Axis.Logicle.M;
        set => set_logicle(Platform.Axis.Logicle with { M = value }, nameof(LogicleM));
    }

    public double LogicleA
    {
        get => Platform.Axis.Logicle.A;
        set => set_logicle(Platform.Axis.Logicle with { A = value }, nameof(LogicleA));
    }

    public int SmoothingHalfWindow
    {
        get => platform_smoothing()?.HalfWindow ?? 0;
        set
        {
            var smoothing = platform_smoothing();
            if (smoothing is null || smoothing.HalfWindow == value)
                return;
            smoothing.HalfWindow = value;
            display_option_changed(nameof(SmoothingHalfWindow));
        }
    }

    public bool SmoothBeforeFit
    {
        get => platform_smoothing()?.Enabled ?? false;
        set
        {
            var smoothing = platform_smoothing();
            if (smoothing is null || smoothing.Enabled == value)
                return;
            smoothing.Enabled = value;
            display_option_changed(nameof(SmoothBeforeFit));
        }
    }

    public string IntensityReferenceSample
    {
        get => Platform is IntensityComparisonPlatform comparison ? comparison.ReferenceSample : "";
        set
        {
            if (Platform is IntensityComparisonPlatform comparison)
                comparison.ReferenceSample = value ?? "";
            OnPropertyChanged();
        }
    }

    public CellCycleModelKind CellCycleModel
    {
        get => Platform is CellCyclePlatform cell_cycle ? cell_cycle.Model : CellCycleModelKind.WatsonPragmatic;
        set
        {
            if (Platform is CellCyclePlatform cell_cycle)
                cell_cycle.Model = value;
            OnPropertyChanged();
        }
    }

    public EnumDisplayChoice<CellCycleModelKind>? SelectedCellCycleModelChoice
    {
        get => CellCycleModelChoices.FirstOrDefault(choice => choice.Value.Equals(CellCycleModel));
        set
        {
            if (value is null)
                return;
            CellCycleModel = value.Value;
            OnPropertyChanged();
        }
    }

    public bool CellCycleDrawModelSum
    {
        get => Platform is not CellCyclePlatform cell_cycle || cell_cycle.DrawModelSum;
        set
        {
            if (Platform is CellCyclePlatform cell_cycle)
                cell_cycle.DrawModelSum = value;
            OnPropertyChanged();
        }
    }

    public bool CellCycleDrawComponents
    {
        get => Platform is not CellCyclePlatform cell_cycle || cell_cycle.DrawComponents;
        set
        {
            if (Platform is CellCyclePlatform cell_cycle)
                cell_cycle.DrawComponents = value;
            OnPropertyChanged();
        }
    }

    public bool CellCycleFillComponents
    {
        get => Platform is not CellCyclePlatform cell_cycle || cell_cycle.FillComponents;
        set
        {
            if (Platform is CellCyclePlatform cell_cycle)
                cell_cycle.FillComponents = value;
            OnPropertyChanged();
        }
    }

    public int ProliferationMaxGenerations
    {
        get => Platform is ProliferationPlatform proliferation ? proliferation.MaxGenerations : 8;
        set
        {
            if (Platform is ProliferationPlatform proliferation)
                proliferation.MaxGenerations = value;
            OnPropertyChanged();
        }
    }

    public double ProliferationPeakProminence
    {
        get => Platform is ProliferationPlatform proliferation ? proliferation.PeakProminence : 0.03;
        set
        {
            if (Platform is ProliferationPlatform proliferation)
                proliferation.PeakProminence = value;
            OnPropertyChanged();
        }
    }

    public bool ProliferationDrawModelSum
    {
        get => Platform is not ProliferationPlatform proliferation || proliferation.DrawModelSum;
        set
        {
            if (Platform is ProliferationPlatform proliferation)
                proliferation.DrawModelSum = value;
            OnPropertyChanged();
        }
    }

    public bool ProliferationDrawComponents
    {
        get => Platform is not ProliferationPlatform proliferation || proliferation.DrawComponents;
        set
        {
            if (Platform is ProliferationPlatform proliferation)
                proliferation.DrawComponents = value;
            OnPropertyChanged();
        }
    }

    public int CytoNormQuantileCount
    {
        get => Platform is IntegrationPlatform integration ? integration.CytoNormOptions.QuantileCount : 99;
        set
        {
            if (Platform is not IntegrationPlatform integration)
                return;
            int next = Math.Clamp(value, 3, 1000);
            if (integration.CytoNormOptions.QuantileCount == next)
                return;
            integration.CytoNormOptions.QuantileCount = next;
            integration_option_changed(nameof(CytoNormQuantileCount));
        }
    }

    public int CytoNormMinimumCellsPerCluster
    {
        get => Platform is IntegrationPlatform integration ? integration.CytoNormOptions.MinimumCellsPerCluster : 50;
        set
        {
            if (Platform is not IntegrationPlatform integration)
                return;
            int next = Math.Clamp(value, 1, 1000000);
            if (integration.CytoNormOptions.MinimumCellsPerCluster == next)
                return;
            integration.CytoNormOptions.MinimumCellsPerCluster = next;
            integration_option_changed(nameof(CytoNormMinimumCellsPerCluster));
        }
    }

    public CytoNormGoal CytoNormGoal
    {
        get => Platform is IntegrationPlatform integration ? integration.CytoNormOptions.Goal : CytoNormGoal.BatchMean;
        set
        {
            if (Platform is not IntegrationPlatform integration || integration.CytoNormOptions.Goal == value)
                return;
            integration.CytoNormOptions.Goal = value;
            integration_option_changed(nameof(CytoNormGoal));
        }
    }

    public EnumDisplayChoice<CytoNormGoal>? SelectedCytoNormGoalChoice
    {
        get => CytoNormGoalChoices.FirstOrDefault(choice => choice.Value.Equals(CytoNormGoal));
        set
        {
            if (value is null)
                return;
            CytoNormGoal = value.Value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        Platform.PropertyChanged -= platform_changed;
        Platform.Populations.CollectionChanged -= populations_changed;
        Platform.Features.CollectionChanged -= features_changed;
        foreach (var row in subscribed_population_rows) row.PropertyChanged -= population_row_changed;
        foreach (var row in subscribed_feature_rows) row.PropertyChanged -= feature_row_changed;
    }

    private void populations_changed(object? sender, NotifyCollectionChangedEventArgs e) => resubscribe_population_rows();
    private void features_changed(object? sender, NotifyCollectionChangedEventArgs e) => resubscribe_feature_rows();

    private void resubscribe_population_rows()
    {
        foreach (var row in subscribed_population_rows.Where(row => !Platform.Populations.Contains(row)).ToArray())
        {
            row.PropertyChanged -= population_row_changed;
            subscribed_population_rows.Remove(row);
        }
        foreach (var row in Platform.Populations.Where(row => subscribed_population_rows.Add(row)))
            row.PropertyChanged += population_row_changed;
    }

    private void resubscribe_feature_rows()
    {
        foreach (var row in subscribed_feature_rows.Where(row => !Platform.Features.Contains(row)).ToArray())
        {
            row.PropertyChanged -= feature_row_changed;
            subscribed_feature_rows.Remove(row);
        }
        foreach (var row in Platform.Features.Where(row => subscribed_feature_rows.Add(row)))
            row.PropertyChanged += feature_row_changed;
    }

    private void population_row_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlatformPopulationInput.IsSelected))
            return;
        OnPropertyChanged(nameof(PlatformHistogramCurves));
        OnPropertyChanged(nameof(PlatformHistogramYMaximum));
    }

    private void feature_row_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (Platform.Kind == PlatformKind.Integration && e.PropertyName == nameof(PlatformFeatureSelection.IsSelected))
            feature_selection_changed();
    }

    protected virtual bool IsFitParameter(string? property_name) => false;

    protected void refresh_choices()
    {
        refresh_batch_choices();
        refresh_display_choices();
        refresh_channel_choices();
    }

    private void refresh_batch_choices()
    {
        ensure_identity_metadata_schema();
        if (Platform is IntegrationPlatform current_integration && !string.IsNullOrWhiteSpace(current_integration.BatchColumnName))
            retained_batch_column_name = current_integration.BatchColumnName;
        string selected = !string.IsNullOrWhiteSpace(retained_batch_column_name)
            ? retained_batch_column_name
            : SelectedBatchColumnName;
        var choices = Workspace.MetadataColumns
                     .Where(column => column.Value == MetadataColumnKind.String)
                     .Select(column => column.Key)
                     .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (Platform is IntegrationPlatform integration)
        {
            if (!string.IsNullOrWhiteSpace(selected) && !choices.Contains(selected, StringComparer.Ordinal))
                choices.Add(selected);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                integration.BatchColumnName = selected;
                retained_batch_column_name = selected;
            }
            if (string.IsNullOrWhiteSpace(integration.BatchColumnName))
            {
                integration.BatchColumnName = choices.FirstOrDefault() ?? "";
                retained_batch_column_name = integration.BatchColumnName;
            }
        }
        replace_collection(BatchColumnChoices, choices);
        OnPropertyChanged(nameof(SelectedBatchColumnName));
    }

    private void refresh_display_choices()
    {
        DisplayChoices.Clear();
        foreach (var item in Platform.Populations
                     .Where(row => row.IsPlatformDropped)
                     .Select(row => $"{row.SampleName} - {row.PopulationName}")
                     .Distinct(StringComparer.Ordinal))
            DisplayChoices.Add(item);
        if (Platform.Kind == PlatformKind.IntensityComparison &&
            Platform is IntensityComparisonPlatform comparison &&
            (string.IsNullOrWhiteSpace(comparison.ReferenceSample) ||
             !DisplayChoices.Contains(comparison.ReferenceSample)))
            comparison.ReferenceSample = DisplayChoices.FirstOrDefault() ?? "";
        OnPropertyChanged(nameof(IntensityReferenceSample));
    }

    private void refresh_channel_choices()
    {
        ChannelChoices.Clear();
        foreach (var feature in Platform.Features.Where(feature => feature.IsChannel))
            ChannelChoices.Add(new AxisChoice(feature.ChannelName, feature.Label));

        var selected_name = Platform.SelectedFeatureNames.FirstOrDefault() ?? ChannelChoices.FirstOrDefault()?.Name;
        if (Platform.Kind == PlatformKind.Integration)
        {
            selected_channel = ChannelChoices.FirstOrDefault(choice => choice.Name == selected_name);
            OnPropertyChanged(nameof(SelectedChannel));
            return;
        }

        if (!string.IsNullOrWhiteSpace(selected_name))
        {
            foreach (var feature in Platform.Features.Where(feature => feature.IsChannel))
                feature.IsSelected = feature.ChannelName == selected_name;
            selected_channel = ChannelChoices.FirstOrDefault(choice => choice.Name == selected_name);
        }
        OnPropertyChanged(nameof(SelectedChannel));
    }

    private void population_selection_changed()
    {
        refresh_feature_choices();
        prepare_preview(preserve_fit_state: true);
        refresh_choices();
        invalidate_commands();
    }

    private void feature_selection_changed()
    {
        update_feature_selection_states();
        if (Platform.Kind != PlatformKind.Integration)
        {
            reset_x_axis_range();
            Platform.InvalidateFromConfiguration();
            prepare_preview();
            refresh_choices();
        }
        else if (Platform.HasIntegrated)
        {
            Platform.InvalidateFromConfiguration();
            invalidate_commands();
        }
    }

    private void drop_population(ProjectNode? node)
    {
        if (node is null)
            return;
        int added = 0;
        if (node.Kind == ProjectNodeKind.Population && node.Group is not null && node.Sample is not null && node.Population is not null)
            added += add_population(node.Group, node.Sample, node.Population);
        else if (node.Kind == ProjectNodeKind.Sample && node.Group is not null && node.Sample is not null)
            added += add_all_events(node.Group, node.Sample);
        else if (node.Kind is ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot && node.Group is not null && node.Gate is not null)
        {
            var region = node.Kind == ProjectNodeKind.GatePopulationSlot ? node.PopulationRegion : PopulationRegion.Primary;
            foreach (var sample in node.Group.Samples)
                if (find_population(sample.Populations, node.Gate.Id, region) is { } population)
                    added += add_population(node.Group, sample, population);
        }
        else if (node.Kind == ProjectNodeKind.GateFolder && node.Group is not null)
        {
            foreach (var sample in node.Group.Samples)
            foreach (var population in flatten_populations(sample.Populations))
                added += add_population(node.Group, sample, population);
        }

        if (added == 0)
        {
            Platform.WarningText = "The dropped item did not contain any new calculated populations.";
            Platform.Status = PlatformStatus.Warning;
            return;
        }
        refresh_feature_choices();
        reset_x_axis_range();
        Platform.InvalidateFromConfiguration();
        prepare_preview();
        refresh_choices();
        invalidate_commands();
    }

    private bool can_drop_population(ProjectNode? node) =>
        node?.Kind is ProjectNodeKind.Population or ProjectNodeKind.Sample or ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot or ProjectNodeKind.GateFolder;

    private int add_all_events(FlowGroup group, FlowSample sample)
    {
        if (has_population(group.Id, sample.Id, Guid.Empty, PopulationRegion.Primary))
            return 0;
        Platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group.Id,
            SampleId = sample.Id,
            GroupName = group.Name,
            SampleName = sample.Name,
            PopulationName = "All events",
            EventCount = sample.EventCount,
            IsPopulation = false,
            IsPlatformDropped = true,
            IsSelected = true
        });
        return 1;
    }

    private int add_population(FlowGroup group, FlowSample sample, PopulationResult population)
    {
        if (has_population(group.Id, sample.Id, population.Gate.Id, population.Region))
            return 0;
        Platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group.Id,
            SampleId = sample.Id,
            GateId = population.Gate.Id,
            Region = population.Region,
            GroupName = group.Name,
            SampleName = sample.Name,
            PopulationName = population.DisplayName,
            EventCount = population.EventCount,
            IsPopulation = true,
            IsPlatformDropped = true,
            IsSelected = true
        });
        return 1;
    }

    private bool has_population(Guid group_id, Guid sample_id, Guid gate_id, PopulationRegion region) =>
        Platform.Populations.Any(row => row.GroupId == group_id && row.SampleId == sample_id && row.GateId == gate_id && row.Region == region);

    private void remove_population(PlatformPopulationInput? row)
    {
        if (row is null || !Platform.Populations.Remove(row))
            return;
        refresh_feature_choices();
        Platform.InvalidateFromConfiguration();
        prepare_preview();
        refresh_choices();
        invalidate_commands();
    }

    private static IEnumerable<PopulationResult> flatten_populations(IEnumerable<PopulationResult> populations)
    {
        foreach (var population in populations)
        {
            yield return population;
            foreach (var child in flatten_populations(population.Children))
                yield return child;
        }
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, Guid gate_id, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate.Id == gate_id && population.Region == region)
                return population;
            if (find_population(population.Children, gate_id, region) is { } child)
                return child;
        }
        return null;
    }

    private void refresh_feature_choices()
    {
        var selected_sample_ids = Platform.Populations
            .Where(row => row.IsPlatformDropped)
            .Select(row => row.SampleId)
            .Distinct()
            .ToArray();
        var samples = Workspace.Groups.SelectMany(group => group.Samples)
            .Where(sample => selected_sample_ids.Contains(sample.Id))
            .ToArray();
        var previous = Platform.Features
            .Where(feature => feature.IsChannel)
            .ToDictionary(feature => feature.ChannelName, feature => feature.IsSelected, StringComparer.Ordinal);
        Platform.Features.Clear();
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
            var channel = samples[0].Channels.First(channel => channel.Name == channel_name);
            Platform.Features.Add(new PlatformFeatureSelection
            {
                ChannelName = channel.Name,
                Label = channel.Label,
                IsChannel = true,
                IsSelected = !previous.TryGetValue(channel.Name, out bool was_selected) || was_selected
            });
        }
        update_feature_selection_states();
        refresh_channel_choices();
    }

    private void update_integration_population_states()
    {
        var rows = Platform.Populations.ToArray();
        apply_hierarchy_states(
            rows,
            row => row.RowKey,
            row => row.ParentKey,
            row => row.IsSelected,
            (row, value) => row.IsSelected = value,
            (row, value) => row.IsEnabled = value,
            (row, value) => row.IsIndeterminate = value);
    }

    private void update_modeling_population_states()
    {
        foreach (var row in Platform.Populations)
        {
            row.IsEnabled = true;
            row.IsIndeterminate = false;
            if (!row.IsPlatformDropped)
                row.IsSelected = false;
        }
    }

    private void update_feature_selection_states()
    {
        if (Platform.Kind == PlatformKind.Integration)
        {
            foreach (var feature in Platform.Features)
            {
                feature.IsEnabled = true;
                feature.IsIndeterminate = false;
            }
            return;
        }
        string? selected_name = Platform.Features.FirstOrDefault(feature => feature.IsChannel && feature.IsSelected)?.ChannelName ??
                                Platform.Features.FirstOrDefault(feature => feature.IsChannel)?.ChannelName;
        foreach (var feature in Platform.Features)
        {
            feature.IsEnabled = true;
            feature.IsIndeterminate = false;
            feature.IsSelected = feature.IsChannel && feature.ChannelName == selected_name;
        }
    }

    private void reset_x_axis_range()
    {
        string? channel_name = Platform.SelectedFeatureNames.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(channel_name))
            return;
        var sample_ids = Platform.Populations
            .Where(row => row.IsPlatformDropped)
            .Select(row => row.SampleId)
            .Distinct()
            .ToHashSet();
        double maximum = Workspace.Groups
            .SelectMany(group => group.Samples)
            .Where(sample => sample_ids.Count == 0 || sample_ids.Contains(sample.Id))
            .Select(sample => sample.Channels.FirstOrDefault(channel => channel.Name == channel_name)?.Maximum)
            .Where(value => value.HasValue && value.Value > 0)
            .Select(value => (double)value!.Value)
            .DefaultIfEmpty(new LogicleParameters().T)
            .Max();
        Platform.Axis.Maximum = maximum;
        Platform.Axis.Minimum = Platform.Axis.Transform == PlatformTransformationKind.Logicle
            ? -0.01 * maximum
            : -0.1 * maximum;
        Platform.Axis.Logicle = Platform.Axis.Logicle with { T = maximum };
        OnPropertyChanged(nameof(AxisMaximum));
        OnPropertyChanged(nameof(AxisMinimum));
        OnPropertyChanged(nameof(LogicleT));
    }

    private void prepare_preview(bool preserve_fit_state = false)
    {
        if (Platform.Kind == PlatformKind.Integration)
            return;
        try
        {
            var previous_status = Platform.Status;
            string previous_warning = Platform.WarningText;
            int previous_step = Platform.CurrentStep;
            bool preserve = preserve_fit_state && (Platform.ResultTables.Count > 0 || Platform.FitCurves.Count > 0 || Platform.PlatformStatistics.Count > 0);
            _ = PlatformCatalog.Get(Platform.Kind).Prepare(Workspace, Platform);
            if (preserve)
            {
                Platform.Status = previous_status;
                Platform.WarningText = previous_warning;
                Platform.CurrentStep = previous_step;
            }
        }
        catch (Exception exception)
        {
            Platform.WarningText = exception.Message;
            Platform.Status = PlatformStatus.Warning;
        }
    }

    private async Task run_async()
    {
        try
        {
            Platform.CancellationRequested = false;
            Platform.IsRunning = true;
            Platform.ProgressFraction = 0;
            Platform.ProgressText = "Starting";
            Platform.Status = PlatformStatus.Running;
            invalidate_commands();

            await PlatformCatalog.Get(Platform.Kind).ExecuteAsync(Workspace, Platform);

            Platform.WarningText = "";
            Platform.Status = PlatformStatus.Complete;
            Platform.ProgressFraction = 1;
            Platform.ProgressText = "Complete";
            Platform.NotifyIntegrationDataChanged();
        }
        catch (Exception exception)
        {
            Platform.WarningText = exception.Message;
            Platform.Status = PlatformStatus.Failed;
        }
        finally
        {
            Platform.IsRunning = false;
            Platform.CancellationRequested = false;
            invalidate_commands();
        }
    }

    private async Task run_integration_script_async(string resource_path, string label)
    {
        if (Platform.Kind != PlatformKind.Integration || !Platform.HasIntegrated)
            return;
        try
        {
            Platform.CancellationRequested = false;
            Platform.IsRunning = true;
            Platform.ProgressFraction = 0;
            Platform.ProgressText = $"Running {label}";
            Platform.Status = PlatformStatus.Running;
            invalidate_commands();

            await Task.Run(() => gated.Python.PythonExtensionRuntime.ExecutePlatformScript(
                resource_path,
                Workspace,
                Platform,
                $"Platform: {Platform.Name}",
                $"Platform: {Platform.Name}"));

            Platform.WarningText = "";
            Platform.Status = PlatformStatus.Complete;
            Platform.ProgressFraction = 1;
            Platform.ProgressText = $"{label} complete";
            Platform.NotifyIntegrationDataChanged();
        }
        catch (Exception exception)
        {
            Platform.WarningText = exception.Message;
            Platform.Status = PlatformStatus.Failed;
        }
        finally
        {
            Platform.IsRunning = false;
            Platform.CancellationRequested = false;
            invalidate_commands();
        }
    }

    private void cancel()
    {
        Platform.CancellationRequested = true;
        if (!Platform.IsRunning)
            Platform.Status = PlatformStatus.Cancelled;
        Platform.WarningText = "Cancellation requested. Completed intermediate results remain available.";
    }

    protected virtual void platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsSelectionReadOnly));
        OnPropertyChanged(nameof(PlotDocument));
        OnPropertyChanged(nameof(PlatformHistogramCurves));
        OnPropertyChanged(nameof(PlatformHistogramYMaximum));
        OnPropertyChanged(nameof(PlatformHistogramXTitle));
        OnPropertyChanged(nameof(PlatformHistogramYTitle));
        if (handling_change || Platform.Kind == PlatformKind.Integration)
            return;
        bool display_change = e.PropertyName is nameof(CellCyclePlatform.DrawModelSum) or
                              nameof(CellCyclePlatform.DrawComponents) or
                              nameof(ProliferationPlatform.DrawModelSum) or
                              nameof(ProliferationPlatform.DrawComponents);
        bool fit_change = IsFitParameter(e.PropertyName);
        if (!display_change && !fit_change)
            return;
        handling_change = true;
        try
        {
            if (fit_change)
                Platform.InvalidateFitResults("Model parameters changed. Rerun this platform to update fitted results.");
            prepare_preview(preserve_fit_state: display_change && !fit_change);
        }
        finally
        {
            handling_change = false;
        }
    }

    private void invalidate_commands()
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsSelectionReadOnly));
        if (RunCommand is RelayCommand run)
            run.RaiseCanExecuteChanged();
        if (RunLeidenCommand is RelayCommand leiden)
            leiden.RaiseCanExecuteChanged();
        if (RunUmapCommand is RelayCommand umap)
            umap.RaiseCanExecuteChanged();
        if (DropPopulationCommand is RelayCommand drop)
            drop.RaiseCanExecuteChanged();
        if (RemovePopulationCommand is RelayCommand remove)
            remove.RaiseCanExecuteChanged();
        if (CancelCommand is RelayCommand cancel)
            cancel.RaiseCanExecuteChanged();
    }

    private bool can_run() =>
        Platform.IsIdle &&
        (Platform.Kind != PlatformKind.Integration || !Platform.IsConfigurationLocked);

    private bool can_run_integration_script() =>
        Platform.Kind == PlatformKind.Integration &&
        Platform.IsIdle &&
        Platform.HasIntegrated;

    private PlatformSmoothingOptions? platform_smoothing() =>
        Platform switch
        {
            UnivariatePlatform univariate => univariate.Smoothing,
            _ => null
        };

    private bool platform_fill_components() => Platform switch
    {
        CellCyclePlatform cell_cycle => cell_cycle.FillComponents,
        _ => false
    };

    private void set_logicle(LogicleParameters value, string property_name)
    {
        if (Platform.Axis.Logicle.Equals(value))
            return;
        Platform.Axis.Logicle = value;
        display_option_changed(property_name);
    }

    private void display_option_changed(string property_name)
    {
        OnPropertyChanged(property_name);
        if (handling_change)
            return;
        if (Platform.Kind == PlatformKind.Integration)
        {
            Platform.InvalidateFromConfiguration();
            return;
        }
        handling_change = true;
        try
        {
            prepare_preview(preserve_fit_state: true);
        }
        finally
        {
            handling_change = false;
        }
    }

    private void ensure_identity_metadata_schema()
    {
        Workspace.MetadataColumns["Group"] = MetadataColumnKind.String;
        Workspace.MetadataColumns["Sample"] = MetadataColumnKind.String;
        Workspace.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
            sample.Metadata[Configuration.CytometerMetadataKey] = Configuration.CytometerNameForSample(sample);
        }
    }

    private static void apply_hierarchy_states<T>(
        IReadOnlyList<T> rows,
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

    private static void replace_collection<T>(ObservableCollection<T> collection, IReadOnlyList<T> values)
    {
        if (collection.SequenceEqual(values))
            return;
        collection.Clear();
        foreach (var value in values)
            collection.Add(value);
    }

    private void integration_option_changed(string property_name)
    {
        OnPropertyChanged(property_name);
        Platform.InvalidateFromConfiguration();
    }

    private static EnumDisplayChoice<T>[] enum_choices<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(value => new EnumDisplayChoice<T>(value, display_enum(value))).ToArray();

    private static string display_enum<T>(T value) where T : struct, Enum =>
        value switch
        {
            CellCycleModelKind.DeanJettFox => "Dean-Jett-Fox",
            _ => split_pascal_case(value.ToString())
        };

    private static string split_pascal_case(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var words = System.Text.RegularExpressions.Regex.Matches(value, @"[A-Z][a-z0-9]*")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0)
            return value;
        return words[0] + (words.Length > 1 ? " " + string.Join(" ", words.Skip(1)).ToLowerInvariant() : "");
    }
}

public sealed record EnumDisplayChoice<T>(T Value, string DisplayLabel) where T : struct, Enum
{
    public override string ToString() => DisplayLabel;
}

public sealed class IntegrationPlatformEditorViewModel : PlatformEditorViewModel
{
    private static readonly Color[] histogram_palette =
    [
        Color.FromRgb(20, 133, 255),
        Color.FromRgb(230, 126, 34),
        Color.FromRgb(46, 204, 113),
        Color.FromRgb(155, 89, 182),
        Color.FromRgb(231, 76, 60),
        Color.FromRgb(22, 160, 133),
        Color.FromRgb(241, 196, 15),
        Color.FromRgb(52, 152, 219)
    ];

    private AxisChoice? selected_histogram_channel;
    private bool show_normalized_histogram = true;

    public IntegrationPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform)
    {
        PreviousHistogramChannelCommand = new RelayCommand(_ => move_histogram_channel(-1), _ => can_move_histogram_channel(-1));
        NextHistogramChannelCommand = new RelayCommand(_ => move_histogram_channel(1), _ => can_move_histogram_channel(1));
        refresh_integration_histogram();
    }

    public ObservableCollection<HistogramSeries> IntegrationHistogramSeries { get; } = new();
    public ObservableCollection<AxisChoice> IntegrationHistogramChannelChoices { get; } = new();
    public ICommand PreviousHistogramChannelCommand { get; }
    public ICommand NextHistogramChannelCommand { get; }

    public bool HasIntegrationHistogram => Platform.Kind == PlatformKind.Integration && Platform.HasIntegrated;
    public bool CanShowNormalizedHistogram => Platform is MultivariatePlatform { Normalized: not null };
    public int IntegrationHistogramSmoothing => 3;
    public double IntegrationHistogramMinimum { get; private set; }
    public double IntegrationHistogramMaximum { get; private set; } = new LogicleParameters().T;
    public HistogramAxisScaleKind IntegrationHistogramAxisScale { get; private set; } = HistogramAxisScaleKind.Linear;
    public double IntegrationHistogramLogicleT { get; private set; } = new LogicleParameters().T;
    public double IntegrationHistogramLogicleW { get; private set; } = new LogicleParameters().W;
    public double IntegrationHistogramLogicleM { get; private set; } = new LogicleParameters().M;
    public double IntegrationHistogramLogicleA { get; private set; } = new LogicleParameters().A;

    public bool ShowNormalizedHistogram
    {
        get => show_normalized_histogram && CanShowNormalizedHistogram;
        set
        {
            bool next = value && CanShowNormalizedHistogram;
            if (show_normalized_histogram == next)
                return;
            show_normalized_histogram = next;
            refresh_integration_histogram();
            OnPropertyChanged();
        }
    }

    public AxisChoice? SelectedIntegrationHistogramChannel
    {
        get
        {
            ensure_histogram_channel();
            return selected_histogram_channel;
        }
        set
        {
            if (value is null || selected_histogram_channel?.Name == value.Name)
                return;
            selected_histogram_channel = value;
            refresh_integration_histogram();
            OnPropertyChanged();
        }
    }

    protected override void platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        base.platform_changed(sender, e);
        if (e.PropertyName is nameof(Platform.HasIntegrated) or nameof(Platform.HasResults) or nameof(Platform.IsConfigurationLocked) or nameof(Platform.Parameters) or null or "")
            refresh_integration_histogram();
    }

    private void refresh_integration_histogram()
    {
        refresh_histogram_channel_choices();
        ensure_histogram_channel();
        IntegrationHistogramSeries.Clear();
        if (!HasIntegrationHistogram || selected_histogram_channel is null)
        {
            notify_histogram_properties();
            return;
        }

        var matrix = ShowNormalizedHistogram && Platform is MultivariatePlatform { Normalized: not null } multivariate
            ? multivariate.Normalized
            : Platform.Compensated;
        if (matrix is null || Platform.RowMap.Count == 0)
        {
            notify_histogram_properties();
            return;
        }

        string channel_name = selected_histogram_channel.Name;
        int column = Array.IndexOf(Platform.SelectedFeatureNames, channel_name);
        if (column < 0 || column >= matrix.GetLength(1))
        {
            notify_histogram_properties();
            return;
        }

        update_histogram_axis(channel_name, matrix, column);
        var value_transform = histogram_value_transform(channel_name);
        for (int source_id = 0; source_id < Platform.RowMap.Sources.Count; source_id++)
        {
            var values = new List<double>();
            for (int row = 0; row < Platform.RowMap.SourceIds.Length && row < matrix.GetLength(0); row++)
            {
                if (Platform.RowMap.SourceIds[row] != source_id)
                    continue;
                double value = value_transform(matrix[row, column]);
                if (double.IsFinite(value))
                    values.Add(value);
            }

            if (values.Count == 0)
                continue;

            IntegrationHistogramSeries.Add(new HistogramSeries
            {
                Name = source_label(source_id),
                Values = values,
                BinCount = 400,
                Color = histogram_palette[source_id % histogram_palette.Length]
            });
        }

        notify_histogram_properties();
    }

    private void update_histogram_axis(string channel_name, float[,] matrix, int column)
    {
        bool normalized = ShowNormalizedHistogram;
        var scale = histogram_channel_scale(channel_name);
        double maximum = channel_maximum(channel_name);
        IntegrationHistogramLogicleT = maximum;
        IntegrationHistogramLogicleW = Platform.Axis.Logicle.W;
        IntegrationHistogramLogicleM = Platform.Axis.Logicle.M;
        IntegrationHistogramLogicleA = Platform.Axis.Logicle.A;

        if (scale == HistogramAxisScaleKind.Linear)
        {
            IntegrationHistogramAxisScale = HistogramAxisScaleKind.Linear;
            if (normalized)
            {
                IntegrationHistogramMinimum = normalized_observed_minimum(channel_name);
                IntegrationHistogramMaximum = normalized_observed_maximum(channel_name);
            }
            else
            {
                IntegrationHistogramMinimum = -0.1 * maximum;
                IntegrationHistogramMaximum = 1.1 * maximum;
            }
            return;
        }

        if (scale == HistogramAxisScaleKind.Log)
        {
            IntegrationHistogramAxisScale = HistogramAxisScaleKind.Log;
            var log_observed = observed_real_range(matrix, column, ShowNormalizedHistogram ? value => Math.Pow(10, value) : null);
            double log_minimum = Math.Log10(Math.Max(log_observed.Minimum, double.Epsilon));
            double log_maximum = Math.Log10(Math.Max(log_observed.Maximum, double.Epsilon));
            double log_span = log_maximum - log_minimum;
            if (!double.IsFinite(log_span) || log_span <= 0)
                log_span = 1;
            IntegrationHistogramMinimum = Math.Pow(10, log_minimum - log_span * 0.1);
            IntegrationHistogramMaximum = Math.Pow(10, log_maximum + log_span * 0.1);
            return;
        }

        IntegrationHistogramAxisScale = HistogramAxisScaleKind.Logicle;
        var transform = new LogicleTransform(new LogicleParameters(
            IntegrationHistogramLogicleT,
            IntegrationHistogramLogicleW,
            IntegrationHistogramLogicleM,
            IntegrationHistogramLogicleA));
        var observed = observed_real_range(matrix, column, ShowNormalizedHistogram ? value => transform.InverseTransform(value) : null);
        double transformed_minimum = transform.Transform(observed.Minimum);
        double transformed_maximum = transform.Transform(observed.Maximum);
        double transformed_span = transformed_maximum - transformed_minimum;
        if (!double.IsFinite(transformed_span) || transformed_span <= 0)
        {
            transformed_minimum = -0.1;
            transformed_maximum = 1.1;
            transformed_span = 1.2;
        }
        IntegrationHistogramMinimum = transform.InverseTransform(transformed_minimum - transformed_span * 0.1);
        IntegrationHistogramMaximum = transform.InverseTransform(transformed_maximum + transformed_span * 0.1);
    }

    private (double Minimum, double Maximum) observed_real_range(float[,] matrix, int column, Func<double, double>? transform = null)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (int row = 0; row < matrix.GetLength(0); row++)
        {
            double value = matrix[row, column];
            if (transform is not null)
                value = transform(value);
            if (!double.IsFinite(value))
                continue;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return (0, channel_maximum(selected_histogram_channel?.Name ?? ""));
        if (maximum <= minimum)
            maximum = minimum + 1;
        return (minimum, maximum);
    }

    private HistogramAxisScaleKind histogram_channel_scale(string channel_name)
    {
        if (Platform.Axis.Transform == PlatformTransformationKind.Logarithm)
            return HistogramAxisScaleKind.Log;
        if (Platform.Axis.Transform == PlatformTransformationKind.Arcsinh)
            return HistogramAxisScaleKind.Arcsinh;
        return Configuration.DefaultCoordinateScaleForChannel(channel_name) switch
        {
            CoordinateScaleKind.Logicle => HistogramAxisScaleKind.Logicle,
            CoordinateScaleKind.Logarithmic => HistogramAxisScaleKind.Log,
            CoordinateScaleKind.Arcsinh => HistogramAxisScaleKind.Arcsinh,
            _ => HistogramAxisScaleKind.Linear
        };
    }

    private Func<double, double> histogram_value_transform(string channel_name)
    {
        if (!ShowNormalizedHistogram)
            return value => value;

        var scale = histogram_channel_scale(channel_name);
        if (scale == HistogramAxisScaleKind.Log)
            return value => Math.Pow(10, value);
        if (scale == HistogramAxisScaleKind.Logicle)
        {
            var transform = new LogicleTransform(new LogicleParameters(
                IntegrationHistogramLogicleT,
                IntegrationHistogramLogicleW,
                IntegrationHistogramLogicleM,
                IntegrationHistogramLogicleA));
            return value => transform.InverseTransform(value);
        }

        return value => value;
    }

    private double normalized_observed_minimum(string channel_name)
    {
        if (Platform is not MultivariatePlatform { Normalized: not null } multivariate)
            return -0.1;
        int column = Array.IndexOf(Platform.SelectedFeatureNames, channel_name);
        if (column < 0)
            return -0.1;
        double minimum = Enumerable.Range(0, multivariate.Normalized.GetLength(0))
            .Select(row => (double)multivariate.Normalized[row, column])
            .Where(double.IsFinite)
            .DefaultIfEmpty(-0.1)
            .Min();
        return Math.Min(-0.1, minimum);
    }

    private double normalized_observed_maximum(string channel_name)
    {
        if (Platform is not MultivariatePlatform { Normalized: not null } multivariate)
            return 1.1;
        int column = Array.IndexOf(Platform.SelectedFeatureNames, channel_name);
        if (column < 0)
            return 1.1;
        double maximum = Enumerable.Range(0, multivariate.Normalized.GetLength(0))
            .Select(row => (double)multivariate.Normalized[row, column])
            .Where(double.IsFinite)
            .DefaultIfEmpty(1.1)
            .Max();
        return Math.Max(1.1, maximum);
    }

    private double channel_maximum(string channel_name)
    {
        var sample_ids = Platform.RowMap.Sources.Select(source => source.SampleId).ToHashSet();
        return Workspace.Groups
            .SelectMany(group => group.Samples)
            .Where(sample => sample_ids.Count == 0 || sample_ids.Contains(sample.Id))
            .Select(sample => sample.Channels.FirstOrDefault(channel => channel.Name == channel_name)?.Maximum)
            .Where(value => value.HasValue && value.Value > 0)
            .Select(value => (double)value!.Value)
            .DefaultIfEmpty(new LogicleParameters().T)
            .Max();
    }

    private string source_label(int source_id)
    {
        if (source_id < 0 || source_id >= Platform.RowMap.Sources.Count)
            return "";
        var source = Platform.RowMap.Sources[source_id];
        return Platform.Populations.FirstOrDefault(row =>
                   row.GroupId == source.GroupId &&
                   row.SampleId == source.SampleId &&
                   row.GateId == source.GateId &&
                   row.Region == source.Region)?.DisplayName
               ?? $"Population {source_id + 1}";
    }

    private void ensure_histogram_channel()
    {
        var names = Platform.SelectedFeatureNames;
        if (names.Length == 0)
        {
            selected_histogram_channel = null;
            return;
        }

        if (selected_histogram_channel is not null && names.Contains(selected_histogram_channel.Name, StringComparer.Ordinal))
        {
            selected_histogram_channel = IntegrationHistogramChannelChoices.FirstOrDefault(choice => choice.Name == selected_histogram_channel.Name) ?? selected_histogram_channel;
            return;
        }
        string name = names[0];
        selected_histogram_channel = IntegrationHistogramChannelChoices.FirstOrDefault(choice => choice.Name == name);
    }

    private void refresh_histogram_channel_choices()
    {
        var names = Platform.SelectedFeatureNames;
        if (IntegrationHistogramChannelChoices.Count == names.Length &&
            IntegrationHistogramChannelChoices.Select(choice => choice.Name).SequenceEqual(names))
            return;

        IntegrationHistogramChannelChoices.Clear();
        foreach (string name in names)
        {
            var feature = Platform.Features.FirstOrDefault(item => item.IsChannel && item.ChannelName == name);
            IntegrationHistogramChannelChoices.Add(new AxisChoice(name, feature?.Label ?? ""));
        }
    }

    private bool can_move_histogram_channel(int delta)
    {
        var names = Platform.SelectedFeatureNames;
        if (names.Length <= 1)
            return false;
        int index = Array.IndexOf(names, selected_histogram_channel?.Name ?? "");
        return index + delta >= 0 && index + delta < names.Length;
    }

    private void move_histogram_channel(int delta)
    {
        var names = Platform.SelectedFeatureNames;
        int index = Array.IndexOf(names, selected_histogram_channel?.Name ?? "");
        int next = Math.Clamp(index + delta, 0, names.Length - 1);
        if (next < 0 || next >= names.Length)
            return;
        string name = names[next];
        SelectedIntegrationHistogramChannel = IntegrationHistogramChannelChoices.FirstOrDefault(choice => choice.Name == name);
    }

    private void notify_histogram_properties()
    {
        OnPropertyChanged(nameof(HasIntegrationHistogram));
        OnPropertyChanged(nameof(CanShowNormalizedHistogram));
        OnPropertyChanged(nameof(ShowNormalizedHistogram));
        OnPropertyChanged(nameof(IntegrationHistogramChannelChoices));
        OnPropertyChanged(nameof(SelectedIntegrationHistogramChannel));
        OnPropertyChanged(nameof(IntegrationHistogramMinimum));
        OnPropertyChanged(nameof(IntegrationHistogramMaximum));
        OnPropertyChanged(nameof(IntegrationHistogramAxisScale));
        OnPropertyChanged(nameof(IntegrationHistogramLogicleT));
        OnPropertyChanged(nameof(IntegrationHistogramLogicleW));
        OnPropertyChanged(nameof(IntegrationHistogramLogicleM));
        OnPropertyChanged(nameof(IntegrationHistogramLogicleA));
        if (PreviousHistogramChannelCommand is RelayCommand previous)
            previous.RaiseCanExecuteChanged();
        if (NextHistogramChannelCommand is RelayCommand next)
            next.RaiseCanExecuteChanged();
    }
}

public sealed class CellCyclePlatformEditorViewModel : PlatformEditorViewModel
{
    public CellCyclePlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) => property_name is nameof(CellCyclePlatform.Model);
}

public sealed class ProliferationPlatformEditorViewModel : PlatformEditorViewModel
{
    public ProliferationPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) =>
        property_name is nameof(ProliferationPlatform.MaxGenerations) or nameof(ProliferationPlatform.PeakProminence);
}

public sealed class IntensityComparisonPlatformEditorViewModel : PlatformEditorViewModel
{
    public IntensityComparisonPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) => property_name is nameof(IntensityComparisonPlatform.ReferenceSample);
}
