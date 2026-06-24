using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using gated.Models;
using gated.Reduction;
using gated.Services;

namespace gated.ViewModels.Platforms;

public abstract class PlatformEditorViewModel : NotifyBase
{
    private bool handling_change;
    private AxisChoice? selected_channel;
    private string retained_batch_column_name = "";

    protected PlatformEditorViewModel(FlowWorkspace workspace, Platform platform)
    {
        Workspace = workspace;
        Platform = platform;
        Platform.PropertyChanged += platform_changed;
        SelectionChangedCommand = new RelayCommand(_ => population_selection_changed());
        FeatureSelectionChangedCommand = new RelayCommand(_ => feature_selection_changed());
        DropPopulationCommand = new RelayCommand(parameter => drop_population(parameter as ProjectNode), _ => IsEditable);
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
    public ObservableCollection<KineticsFitKind> KineticsFitChoices { get; } = new(Enum.GetValues<KineticsFitKind>());
    public ObservableCollection<EnumDisplayChoice<CytoNormGoal>> CytoNormGoalChoices { get; } = new(enum_choices<CytoNormGoal>());
    public ObservableCollection<string> BatchColumnChoices { get; } = new();
    public ObservableCollection<string> DisplayChoices { get; } = new();
    public ICommand SelectionChangedCommand { get; }
    public ICommand FeatureSelectionChangedCommand { get; }
    public ICommand DropPopulationCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RunLeidenCommand { get; }
    public ICommand RunUmapCommand { get; }
    public ICommand CancelCommand { get; }
    public virtual bool UsesPopulationDrop => Platform.Kind != PlatformKind.Integration;
    public virtual bool EnablesDataSmoothing => Platform.Kind != PlatformKind.Kinetics;
    public virtual string RunCaption => Platform.Kind == PlatformKind.Integration ? "Integrate" : "Run model";
    public bool IsEditable => Platform.IsIdle && !Platform.IsConfigurationLocked;
    public bool IsReadOnly => !IsEditable;
    public bool IsSelectionReadOnly => Platform.Kind == PlatformKind.Integration
        ? Platform.IsRunning || (Platform.Status == IntegrationJobStatus.Complete && Platform.HasIntegrated)
        : IsReadOnly;
    public bool HasWarning => Platform.HasWarning;
    public string KindName => Platform.Kind switch
    {
        PlatformKind.CellCycle => "Univariate cell cycle modeling",
        PlatformKind.Proliferation => "Proliferation modeling",
        PlatformKind.IntensityComparison => "Population intensity comparison",
        PlatformKind.Kinetics => "Kinetics analysis",
        _ => "Integration"
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
            if (Platform is BivariatePlatform bivariate)
                bivariate.Minor = value.Name;
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

    public int KineticsTimeWindowCount
    {
        get => Platform is KineticsPlatform kinetics ? kinetics.TimeWindowCount : 64;
        set
        {
            if (Platform is KineticsPlatform kinetics)
                kinetics.TimeWindowCount = value;
            OnPropertyChanged();
        }
    }

    public KineticsFitKind KineticsFit
    {
        get => Platform is KineticsPlatform kinetics ? kinetics.Fit : KineticsFitKind.Linear;
        set
        {
            if (Platform is KineticsPlatform kinetics)
                kinetics.Fit = value;
            OnPropertyChanged();
        }
    }

    public double KineticsChangePointZ
    {
        get => Platform is KineticsPlatform kinetics ? kinetics.ChangePointZ : 3.0;
        set
        {
            if (Platform is KineticsPlatform kinetics)
                kinetics.ChangePointZ = value;
            OnPropertyChanged();
        }
    }

    public int KineticsMinSegmentWindows
    {
        get => Platform is KineticsPlatform kinetics ? kinetics.MinSegmentWindows : 5;
        set
        {
            if (Platform is KineticsPlatform kinetics)
                kinetics.MinSegmentWindows = value;
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
                     .Where(row => row.IsPopulation && row.IsPlatformDropped)
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
        if (Platform.Kind == PlatformKind.Integration)
            update_integration_population_states();
        else
            update_modeling_population_states();
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
        if (node is null || Platform.Kind == PlatformKind.Integration)
            return;

        if (node.Kind == ProjectNodeKind.Population && node.Sample is not null && node.Gate is not null && node.Population is not null)
        {
            foreach (var row in Platform.Populations.Where(row =>
                         row.IsPopulation &&
                         row.SampleId == node.Sample.Id &&
                         row.GateId == node.Gate.Id &&
                         row.Region == node.Population.Region))
                mark_dropped(row);
        }
        else if (node.Kind == ProjectNodeKind.Sample && node.Sample is not null)
        {
            foreach (var row in Platform.Populations.Where(row => row.IsPopulation && row.SampleId == node.Sample.Id && row.Depth <= 1))
                mark_dropped(row);
        }
        else if (node.Kind == ProjectNodeKind.Gate && node.Gate is not null)
        {
            foreach (var row in Platform.Populations.Where(row => row.IsPopulation && row.GateId == node.Gate.Id))
                mark_dropped(row);
        }

        update_modeling_population_states();
        refresh_feature_choices();
        reset_x_axis_range();
        Platform.InvalidateFromConfiguration();
        prepare_preview();
        refresh_choices();
        invalidate_commands();
    }

    private static void mark_dropped(IntegrationJobPopulationSelection row)
    {
        row.IsPlatformDropped = true;
        row.IsSelected = true;
    }

    private void refresh_feature_choices()
    {
        var selected_sample_ids = Platform.Populations
            .Where(row => Platform.Kind == PlatformKind.Integration
                ? row.IsSelected && row.IsEnabled && !row.IsIndeterminate
                : row.IsPopulation && row.IsPlatformDropped)
            .Select(row => row.SampleId)
            .Distinct()
            .ToArray();
        var samples = Workspace.Groups.SelectMany(group => group.Samples)
            .Where(sample => selected_sample_ids.Contains(sample.Id))
            .ToArray();
        var previous = Platform.Features
            .Where(feature => feature.IsChannel)
            .ToDictionary(feature => feature.ChannelName, feature => feature.IsSelected, StringComparer.Ordinal);
        var previous_expanded = Platform.Features.ToDictionary(feature => feature.ChannelName, feature => feature.IsExpanded, StringComparer.Ordinal);
        var previous_root = Platform.Features.FirstOrDefault(feature => !feature.IsChannel);
        bool previous_root_selected = previous_root?.IsSelected ?? true;
        bool previous_root_expanded = previous_root?.IsExpanded ?? true;
        Platform.Features.Clear();
        if (samples.Length == 0)
            return;

        var root_key = Guid.NewGuid();
        Platform.Features.Add(new IntegrationJobFeatureSelection
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
            var channel = samples[0].Channels.First(channel => channel.Name == channel_name);
            Platform.Features.Add(new IntegrationJobFeatureSelection
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
            if (!row.IsPlatformDropped || !row.IsPopulation)
                row.IsSelected = false;
        }
    }

    private void update_feature_selection_states()
    {
        if (Platform.Kind == PlatformKind.Integration)
        {
            var rows = Platform.Features.ToArray();
            apply_hierarchy_states(
                rows,
                row => row.RowKey,
                row => row.ParentKey,
                row => row.IsSelected,
                (row, value) => row.IsSelected = value,
                (row, value) => row.IsEnabled = value,
                (row, value) => row.IsIndeterminate = value);
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
            .Where(row => row.IsPopulation && row.IsPlatformDropped)
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
            _ = new IntegrationJobRunner(Workspace).Prepare(Platform);
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
            Platform.Status = IntegrationJobStatus.Warning;
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
            Platform.Status = IntegrationJobStatus.Running;
            invalidate_commands();

            if (Platform.Kind == PlatformKind.Integration)
            {
                await Dispatcher.UIThread.InvokeAsync(() => new IntegrationJobRunner(Workspace).RunIntegration(Platform));
            }
            else
            {
                bool prepared = new IntegrationJobRunner(Workspace).Prepare(Platform);
                if (!prepared || string.IsNullOrWhiteSpace(Platform.ResourcePath))
                    return;
                Platform.ProgressText = "Running platform script";
                await Task.Run(() => gated.Python.PythonExtensionRuntime.ExecutePlatformScript(
                    Platform.ResourcePath,
                    Workspace,
                    Platform,
                    $"platform:{Platform.Id}",
                    Platform.Name));
            }

            Platform.WarningText = "";
            Platform.Status = IntegrationJobStatus.Complete;
            Platform.ProgressFraction = 1;
            Platform.ProgressText = "Complete";
            Platform.NotifyIntegrationDataChanged();
        }
        catch (Exception exception)
        {
            Platform.WarningText = exception.Message;
            Platform.Status = IntegrationJobStatus.Failed;
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
            Platform.Status = IntegrationJobStatus.Running;
            invalidate_commands();

            await Task.Run(() => gated.Python.PythonExtensionRuntime.ExecutePlatformScript(
                resource_path,
                Workspace,
                Platform,
                $"Platform: {Platform.Name}",
                $"Platform: {Platform.Name}"));

            Platform.WarningText = "";
            Platform.Status = IntegrationJobStatus.Complete;
            Platform.ProgressFraction = 1;
            Platform.ProgressText = $"{label} complete";
            Platform.NotifyIntegrationDataChanged();
        }
        catch (Exception exception)
        {
            Platform.WarningText = exception.Message;
            Platform.Status = IntegrationJobStatus.Failed;
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
            Platform.Status = IntegrationJobStatus.Cancelled;
        Platform.WarningText = "Cancellation requested. Completed intermediate results remain available.";
    }

    private void platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsSelectionReadOnly));
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
            BivariatePlatform bivariate => bivariate.Smoothing,
            _ => null
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
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
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
    public IntegrationPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
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

public sealed class KineticsPlatformEditorViewModel : PlatformEditorViewModel
{
    public KineticsPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) =>
        property_name is nameof(KineticsPlatform.Fit) or
                         nameof(KineticsPlatform.TimeWindowCount) or
                         nameof(KineticsPlatform.ChangePointZ) or
                         nameof(KineticsPlatform.MinSegmentWindows);
}
