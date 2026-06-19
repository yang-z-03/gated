using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using gated.Models;
using gated.Services;

namespace gated.ViewModels.Platforms;

public abstract class PlatformEditorViewModel : NotifyBase
{
    private bool handling_change;
    private AxisChoice? selected_channel;

    protected PlatformEditorViewModel(FlowWorkspace workspace, Platform platform)
    {
        Workspace = workspace;
        Platform = platform;
        Platform.PropertyChanged += platform_changed;
        SelectionChangedCommand = new RelayCommand(_ => population_selection_changed());
        DropPopulationCommand = new RelayCommand(parameter => drop_population(parameter as ProjectNode), _ => IsEditable);
        RunCommand = new RelayCommand(_ => _ = run_async(), _ => Platform.IsIdle);
        CancelCommand = new RelayCommand(_ => cancel(), _ => Platform is not null);
        refresh_choices();
        prepare_preview(preserve_fit_state: true);
    }

    public FlowWorkspace Workspace { get; }
    public Platform Platform { get; }
    public ObservableCollection<AxisChoice> ChannelChoices { get; } = new();
    public ObservableCollection<PlatformTransformationKind> TransformationChoices { get; } = new(Enum.GetValues<PlatformTransformationKind>());
    public ObservableCollection<CellCycleModelKind> CellCycleModelChoices { get; } = new(Enum.GetValues<CellCycleModelKind>());
    public ObservableCollection<KineticsFitKind> KineticsFitChoices { get; } = new(Enum.GetValues<KineticsFitKind>());
    public ObservableCollection<string> BatchColumnChoices { get; } = new();
    public ObservableCollection<string> DisplayChoices { get; } = new();
    public ICommand SelectionChangedCommand { get; }
    public ICommand DropPopulationCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public virtual bool UsesPopulationDrop => Platform.Kind != PlatformKind.Integration;
    public virtual bool EnablesDataSmoothing => Platform.Kind != PlatformKind.Kinetics;
    public virtual string RunCaption => Platform.Kind == PlatformKind.Integration ? "Integrate" : "Run model";
    public bool IsEditable => Platform.IsIdle && !Platform.IsConfigurationLocked;
    public bool IsReadOnly => !IsEditable;
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
            if (Platform is not IntegrationPlatform integration || string.IsNullOrWhiteSpace(value) || integration.BatchColumnName == value)
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
        BatchColumnChoices.Clear();
        foreach (var choice in Workspace.MetadataColumns
                     .Where(column => column.Value == MetadataColumnKind.String && column.Key is not ("Group" or "Sample"))
                     .Select(column => column.Key)
                     .OrderBy(name => name, StringComparer.Ordinal))
            BatchColumnChoices.Add(choice);
        if (Platform is IntegrationPlatform integration && string.IsNullOrWhiteSpace(integration.BatchColumnName))
            integration.BatchColumnName = BatchColumnChoices.FirstOrDefault() ?? "";
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
                ? row.IsSelected && row.IsEnabled
                : row.IsPopulation && row.IsPlatformDropped)
            .Select(row => row.SampleId)
            .Distinct()
            .ToArray();
        var samples = Workspace.Groups.SelectMany(group => group.Samples)
            .Where(sample => selected_sample_ids.Contains(sample.Id))
            .ToArray();
        var previous = Platform.Features.ToDictionary(feature => feature.ChannelName, feature => feature.IsSelected, StringComparer.Ordinal);
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
            IsSelected = true
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
                IsSelected = !previous.TryGetValue(channel.Name, out bool was_selected) || was_selected
            });
        }
        update_modeling_feature_states();
        refresh_channel_choices();
    }

    private void update_integration_population_states()
    {
        foreach (var row in Platform.Populations)
        {
            row.IsEnabled = true;
            row.IsIndeterminate = false;
        }
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

    private void update_modeling_feature_states()
    {
        if (Platform.Kind == PlatformKind.Integration)
            return;
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
                await Task.Run(() => gated.Python.PythonExtensionRuntime.ExecutePlatformScript(Platform.ResourcePath, Workspace, Platform));
            }

            Platform.WarningText = "";
            Platform.Status = IntegrationJobStatus.Complete;
            Platform.ProgressFraction = 1;
            Platform.ProgressText = "Complete";
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
        if (RunCommand is RelayCommand run)
            run.RaiseCanExecuteChanged();
        if (DropPopulationCommand is RelayCommand drop)
            drop.RaiseCanExecuteChanged();
    }

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
        if (handling_change || Platform.Kind == PlatformKind.Integration)
            return;
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
