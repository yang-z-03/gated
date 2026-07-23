using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
    public ObservableCollection<EnumDisplayChoice<PlatformTransformationKind>> IntensityTransformationChoices { get; } = new(enum_choices<PlatformTransformationKind>());
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
    public string KindDescription => PlatformCatalog.Get(Platform.Kind).EditorDescription;
    public PlatformPlotDocument? PlotDocument => PlatformCatalog.Get(Platform.Kind).CreatePresentation(Platform).Plots.FirstOrDefault();
    public IReadOnlyList<HistogramCurveSeries> PlatformHistogramCurves => PlotDocument?.Series.Select(series => new HistogramCurveSeries
    {
        Name = series.Title,
        Points = series.X.Zip(series.Y, (x, y) => new HistogramPoint(x, y)).ToArray(),
        Color = PlatformPalette.ColorForIndex(series.SourceId >= 0 ? series.SourceId : 0),
        Thickness = series.Role == PlatformSeriesRole.Observed ? 2.4 : 1.1,
        IsDashed = false,
        FillOpacity = series.Role == PlatformSeriesRole.Component && platform_fill_components() ? 0.12 : 0
    }).ToArray() ?? [];
    public double PlatformHistogramMinimum => PlotDocument?.Minimum ?? Platform.Axis.Minimum;
    public double PlatformHistogramMaximum => PlotDocument?.Maximum ?? Platform.Axis.Maximum;
    public double PlatformHistogramYMaximum => Math.Max(0.01, (PlotDocument?.Series.SelectMany(series => series.Y).Where(double.IsFinite).DefaultIfEmpty(1).Max() ?? 1) * 1.1);
    public string PlatformHistogramXTitle => PlotDocument?.XLabel ?? "Intensity";
    public string PlatformHistogramYTitle => PlotDocument?.YLabel ?? "Normalized frequency";
    public bool HasIntensityComparisonResults => intensity_comparison_table() is not null;
    public IReadOnlyList<IntensityComparisonResultRowViewModel> IntensityComparisonResultRows =>
        intensity_comparison_table()?.Rows.Select(row => new IntensityComparisonResultRowViewModel(row)).ToArray() ?? [];
    public bool HasCellCycleResults => result_table("cell_cycle") is not null;
    public IReadOnlyList<CellCycleResultRowViewModel> CellCycleResultRows =>
        result_table("cell_cycle")?.Rows.Select(row => new CellCycleResultRowViewModel(row)).ToArray() ?? [];
    public bool HasProliferationResults => result_table("proliferation") is not null;
    public IReadOnlyList<ProliferationResultRowViewModel> ProliferationResultRows =>
        result_table("proliferation")?.Rows.Select(row => new ProliferationResultRowViewModel(row)).ToArray() ?? [];
    public bool HasProliferationGenerationResults => result_table("proliferation_generations") is not null;
    public IReadOnlyList<ProliferationGenerationResultRowViewModel> ProliferationGenerationResultRows =>
        result_table("proliferation_generations")?.Rows.Select(row => new ProliferationGenerationResultRowViewModel(row)).ToArray() ?? [];
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
            Platform.Axis.Transform = default_transformation_for_channel(value.Name);
            reset_x_axis_range();
            Platform.InvalidateFromConfiguration();
            prepare_preview();
            OnPropertyChanged();
            notify_transformation_properties();
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
            notify_transformation_properties();
        }
    }

    public EnumDisplayChoice<PlatformTransformationKind>? SelectedIntensityTransformationChoice
    {
        get => IntensityTransformationChoices.FirstOrDefault(choice => choice.Value == AxisTransform);
        set
        {
            if (value is null)
                return;
            AxisTransform = value.Value;
            OnPropertyChanged();
        }
    }

    public bool IsLogicleTransformation => AxisTransform == PlatformTransformationKind.Logicle;
    public bool IsArcsinhTransformation => AxisTransform == PlatformTransformationKind.Arcsinh;

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

    public int IntensityDistributionBinning
    {
        get => Platform is IntensityComparisonPlatform comparison ? comparison.DistributionBinning : 100;
        set
        {
            if (Platform is not IntensityComparisonPlatform comparison || comparison.DistributionBinning == value)
                return;
            comparison.DistributionBinning = value;
            OnPropertyChanged();
        }
    }

    public double UnivariateArcsinhA
    {
        get => Platform is UnivariatePlatform univariate ? univariate.ArcsinhCofactor : 5.0;
        set
        {
            if (Platform is not UnivariatePlatform univariate || univariate.ArcsinhCofactor.Equals(value))
                return;
            univariate.ArcsinhCofactor = value;
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

    public bool ProliferationFillComponents
    {
        get => Platform is not ProliferationPlatform proliferation || proliferation.FillComponents;
        set
        {
            if (Platform is ProliferationPlatform proliferation)
                proliferation.FillComponents = value;
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

    public virtual void Dispose()
    {
        Platform.PropertyChanged -= platform_changed;
        Platform.Populations.CollectionChanged -= populations_changed;
        Platform.Features.CollectionChanged -= features_changed;
        foreach (var row in subscribed_population_rows) row.PropertyChanged -= population_row_changed;
        foreach (var row in subscribed_feature_rows) row.PropertyChanged -= feature_row_changed;
    }

    private void populations_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        resubscribe_population_rows();
        refresh_population_color_indices();
    }
    private void features_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        resubscribe_feature_rows();
        OnFeaturesChanged();
    }

    protected virtual void OnFeaturesChanged()
    {
    }

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
        if (e.PropertyName is not (nameof(PlatformPopulationInput.IsSelected) or null or ""))
            return;
        OnPropertyChanged(nameof(PlotDocument));
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
        refresh_population_color_indices();
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

    private void refresh_population_color_indices()
    {
        foreach (var row in Platform.Populations)
            row.PlotColorIndex = -1;

        if (Platform.RowMap.Sources.Count > 0)
        {
            for (int source_id = 0; source_id < Platform.RowMap.Sources.Count; source_id++)
            {
                var source = Platform.RowMap.Sources[source_id];
                var row = Platform.Populations.FirstOrDefault(item =>
                    item.GroupId == source.GroupId &&
                    item.SampleId == source.SampleId &&
                    item.GateId == source.GateId &&
                    item.Region == source.Region);
                if (row is not null)
                    row.PlotColorIndex = source_id;
            }
            return;
        }

        int color_index = 0;
        foreach (var row in Platform.Populations.Where(row => row.IsPlatformDropped))
            row.PlotColorIndex = color_index++;
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
        if (Platform.Kind != PlatformKind.Integration)
        {
            OnPropertyChanged(nameof(PlotDocument));
            OnPropertyChanged(nameof(PlatformHistogramCurves));
            OnPropertyChanged(nameof(PlatformHistogramYMaximum));
            return;
        }
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
        bool had_channel_choices = Platform.Features.Any(feature => feature.IsChannel);
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
        if (!had_channel_choices && Platform.Kind == PlatformKind.IntensityComparison && SelectedChannel is { } initial_channel)
        {
            Platform.Axis.Transform = default_transformation_for_channel(initial_channel.Name);
            notify_transformation_properties();
        }
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
        PlatformInitializer.RefreshFeatures(Workspace, Platform);
        PlatformInitializer.RefreshTransformations(Workspace, Platform);
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
        var sample_ids = PlatformInitializer.SelectedPopulationInputs(Platform)
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

    private PlatformTransformationKind default_transformation_for_channel(string channel_name)
    {
        var selected_sample_ids = PlatformInitializer.SelectedPopulationInputs(Platform)
            .Select(row => row.SampleId)
            .ToHashSet();
        var sample = Workspace.Groups
            .SelectMany(group => group.Samples)
            .FirstOrDefault(item => selected_sample_ids.Contains(item.Id));
        return Configuration.DefaultCoordinateScaleForChannel(channel_name, Configuration.CytometerNameForSample(sample)) switch
        {
            CoordinateScaleKind.Linear => PlatformTransformationKind.Linear,
            CoordinateScaleKind.Logarithmic => PlatformTransformationKind.Logarithm,
            CoordinateScaleKind.Arcsinh => PlatformTransformationKind.Arcsinh,
            _ => PlatformTransformationKind.Logicle
        };
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
            bool prepared = PlatformCatalog.Get(Platform.Kind).Prepare(Workspace, Platform);
            if (prepared)
                refresh_population_color_indices();
            if (prepared && update_automatic_intensity_range())
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

    private bool update_automatic_intensity_range()
    {
        if (Platform is not UnivariatePlatform univariate ||
            Platform.Compensated is not { } matrix ||
            matrix.GetLength(0) == 0 || matrix.GetLength(1) == 0 ||
            Platform.RowMap.Sources.Count == 0)
            return false;

        double observed_minimum = double.PositiveInfinity;
        double observed_maximum = double.NegativeInfinity;
        int row_count = Math.Min(matrix.GetLength(0), Platform.RowMap.SourceIds.Length);
        for (int source_id = 0; source_id < Platform.RowMap.Sources.Count; source_id++)
        {
            double population_minimum = double.PositiveInfinity;
            double population_maximum = double.NegativeInfinity;
            for (int row = 0; row < row_count; row++)
            {
                if (Platform.RowMap.SourceIds[row] != source_id)
                    continue;
                double value = matrix[row, 0];
                if (!double.IsFinite(value))
                    continue;
                population_minimum = Math.Min(population_minimum, value);
                population_maximum = Math.Max(population_maximum, value);
            }

            if (!double.IsFinite(population_minimum) || !double.IsFinite(population_maximum))
                continue;
            observed_minimum = Math.Min(observed_minimum, population_minimum);
            observed_maximum = Math.Max(observed_maximum, population_maximum);
        }

        if (!double.IsFinite(observed_minimum) || !double.IsFinite(observed_maximum))
            return false;
        if (observed_maximum <= observed_minimum)
            observed_maximum = observed_minimum + 1;

        double minimum;
        double maximum;
        if (Platform.Axis.Transform == PlatformTransformationKind.Linear)
        {
            minimum = Math.Min(-0.1, observed_minimum);
            maximum = Math.Max(1.1, observed_maximum);
        }
        else
        {
            var scale = new AxisScale
            {
                Kind = Platform.Axis.Transform switch
                {
                    PlatformTransformationKind.Logarithm => CoordinateScaleKind.Logarithmic,
                    PlatformTransformationKind.Arcsinh => CoordinateScaleKind.Arcsinh,
                    _ => CoordinateScaleKind.Logicle
                },
                Logicle = Platform.Axis.Logicle,
                ArcsinhCofactor = univariate.ArcsinhCofactor
            };
            double transformed_minimum = scale.Transform(observed_minimum);
            double transformed_maximum = scale.Transform(observed_maximum);
            double span = transformed_maximum - transformed_minimum;
            if (!double.IsFinite(span) || span <= 0)
                return false;
            minimum = scale.InverseTransform(transformed_minimum - 0.1 * span);
            maximum = scale.InverseTransform(transformed_maximum + 0.1 * span);
        }

        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return false;
        bool changed = !Platform.Axis.Minimum.Equals(minimum) || !Platform.Axis.Maximum.Equals(maximum);
        if (!changed)
            return false;
        Platform.Axis.Minimum = minimum;
        Platform.Axis.Maximum = maximum;
        OnPropertyChanged(nameof(AxisMinimum));
        OnPropertyChanged(nameof(AxisMaximum));
        OnPropertyChanged(nameof(PlatformHistogramMinimum));
        OnPropertyChanged(nameof(PlatformHistogramMaximum));
        return true;
    }

    private async Task run_async()
    {
        try
        {
            Platform.CancellationRequested = false;
            Platform.IsRunning = true;
            Platform.ProgressFraction = 0;
            Platform.ProgressText = Platform.Kind == PlatformKind.Integration
                ? "Preparing integration in the background"
                : "Starting";
            Platform.Status = PlatformStatus.Running;
            invalidate_commands();

            await PlatformCatalog.Get(Platform.Kind).ExecuteAsync(Workspace, Platform);

            Platform.WarningText = "";
            Platform.Status = PlatformStatus.Complete;
            Platform.ProgressFraction = 1;
            Platform.ProgressText = "Complete";
            Platform.NotifyIntegrationDataChanged();
        }
        catch (OperationCanceledException)
        {
            Platform.WarningText = "Integration was cancelled. Completed intermediate results remain available.";
            Platform.Status = PlatformStatus.Cancelled;
            Platform.ProgressText = "Cancelled";
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
        OnPropertyChanged(nameof(HasIntensityComparisonResults));
        OnPropertyChanged(nameof(IntensityComparisonResultRows));
        OnPropertyChanged(nameof(HasCellCycleResults));
        OnPropertyChanged(nameof(CellCycleResultRows));
        OnPropertyChanged(nameof(HasProliferationResults));
        OnPropertyChanged(nameof(ProliferationResultRows));
        OnPropertyChanged(nameof(HasProliferationGenerationResults));
        OnPropertyChanged(nameof(ProliferationGenerationResultRows));
        if (handling_change || Platform.Kind == PlatformKind.Integration)
            return;
        bool display_change = e.PropertyName is nameof(CellCyclePlatform.DrawModelSum) or
                              nameof(CellCyclePlatform.DrawComponents) or
                              nameof(CellCyclePlatform.FillComponents) or
                              nameof(ProliferationPlatform.DrawModelSum) or
                              nameof(ProliferationPlatform.DrawComponents) or
                              nameof(ProliferationPlatform.FillComponents);
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
        ProliferationPlatform proliferation => proliferation.FillComponents,
        _ => false
    };

    private PlatformResultTable? intensity_comparison_table() =>
        Platform is IntensityComparisonPlatform ? result_table("intensity_comparison") : null;

    private PlatformResultTable? result_table(string key) =>
        Platform.ResultTables.FirstOrDefault(table => string.Equals(table.Key, key, StringComparison.Ordinal));

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

    private void notify_transformation_properties()
    {
        OnPropertyChanged(nameof(AxisTransform));
        OnPropertyChanged(nameof(SelectedIntensityTransformationChoice));
        OnPropertyChanged(nameof(IsLogicleTransformation));
        OnPropertyChanged(nameof(IsArcsinhTransformation));
        OnPropertyChanged(nameof(PlatformHistogramAxisScale));
        OnPropertyChanged(nameof(PlatformHistogramMinimum));
        OnPropertyChanged(nameof(PlatformHistogramMaximum));
        OnPropertyChanged(nameof(PlatformHistogramCurves));
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
            PlatformTransformationKind.Logarithm => "Signed log1p",
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

public abstract class EditablePlatformResultRowViewModel
{
    private readonly string[] values;

    protected EditablePlatformResultRowViewModel(string[] values)
    {
        this.values = values ?? [];
    }

    protected string Value(int index) => index >= 0 && index < values.Length ? values[index] : "";

    protected void SetValue(int index, string? value)
    {
        if (index >= 0 && index < values.Length)
            values[index] = value ?? "";
    }
}

public sealed class IntensityComparisonResultRowViewModel(string[] values) : EditablePlatformResultRowViewModel(values)
{
    public string Sample { get => Value(0); set => SetValue(0, value); }
    public string Population { get => Value(1); set => SetValue(1, value); }
    public string Events { get => Value(2); set => SetValue(2, value); }
    public string Mean { get => Value(3); set => SetValue(3, value); }
    public string Median { get => Value(4); set => SetValue(4, value); }
    public string GeometricMean { get => Value(5); set => SetValue(5, value); }
    public string MeanFold { get => Value(6); set => SetValue(6, value); }
    public string MedianFold { get => Value(7); set => SetValue(7, value); }
    public string GeomeanFold { get => Value(8); set => SetValue(8, value); }
    public string ChiSquareP { get => Value(9); set => SetValue(9, value); }
    public string NormalityP { get => Value(10); set => SetValue(10, value); }
    public string ZTestP { get => Value(11); set => SetValue(11, value); }
    public string MannWhitneyP { get => Value(12); set => SetValue(12, value); }
    public string KsP { get => Value(13); set => SetValue(13, value); }
}

public sealed class CellCycleResultRowViewModel(string[] values) : EditablePlatformResultRowViewModel(values)
{
    public string Sample { get => Value(0); set => SetValue(0, value); }
    public string Population { get => Value(1); set => SetValue(1, value); }
    public string Events { get => Value(2); set => SetValue(2, value); }
    public string G1Percent { get => Value(3); set => SetValue(3, value); }
    public string SPercent { get => Value(4); set => SetValue(4, value); }
    public string G2MPercent { get => Value(5); set => SetValue(5, value); }
    public string G1Mean { get => Value(6); set => SetValue(6, value); }
    public string G1CvPercent { get => Value(7); set => SetValue(7, value); }
    public string G2MMean { get => Value(8); set => SetValue(8, value); }
    public string G2MCvPercent { get => Value(9); set => SetValue(9, value); }
    public string G2G1Ratio { get => Value(10); set => SetValue(10, value); }
}

public sealed class ProliferationResultRowViewModel(string[] values) : EditablePlatformResultRowViewModel(values)
{
    public string Sample { get => Value(0); set => SetValue(0, value); }
    public string Population { get => Value(1); set => SetValue(1, value); }
    public string Events { get => Value(2); set => SetValue(2, value); }
    public string Generations { get => Value(3); set => SetValue(3, value); }
    public string DividedPercent { get => Value(4); set => SetValue(4, value); }
    public string DivisionIndex { get => Value(5); set => SetValue(5, value); }
    public string ProliferationIndex { get => Value(6); set => SetValue(6, value); }
    public string ReplicationIndex { get => Value(7); set => SetValue(7, value); }
    public string ParentM { get => Value(8); set => SetValue(8, value); }
    public string DistanceD { get => Value(9); set => SetValue(9, value); }
    public string PeakSizeS { get => Value(10); set => SetValue(10, value); }
}

public sealed class ProliferationGenerationResultRowViewModel(string[] values) : EditablePlatformResultRowViewModel(values)
{
    public string Sample { get => Value(0); set => SetValue(0, value); }
    public string Population { get => Value(1); set => SetValue(1, value); }
    public string Generation { get => Value(2); set => SetValue(2, value); }
    public string Mean { get => Value(3); set => SetValue(3, value); }
    public string Area { get => Value(4); set => SetValue(4, value); }
    public string FractionPercent { get => Value(5); set => SetValue(5, value); }
    public string PrecursorFrequency { get => Value(6); set => SetValue(6, value); }
}

public sealed class IntegrationChannelRowViewModel : NotifyBase, IDisposable
{
    private static readonly IReadOnlyList<EnumDisplayChoice<PlatformTransformationKind>> normalization_choices =
    [
        new(PlatformTransformationKind.Linear, "Linear"),
        new(PlatformTransformationKind.Logarithm, "Signed log1p"),
        new(PlatformTransformationKind.Logicle, "Logicle"),
        new(PlatformTransformationKind.Arcsinh, "Arcsinh")
    ];
    private readonly Platform platform;
    private readonly PlatformFeatureSelection feature;
    private readonly PlatformChannelTransformation transformation;
    private readonly Action changed;

    public IntegrationChannelRowViewModel(
        Platform platform,
        PlatformFeatureSelection feature,
        PlatformChannelTransformation transformation,
        ChannelSemanticKind channel_kind,
        Action changed)
    {
        this.platform = platform;
        this.feature = feature;
        this.transformation = transformation;
        this.changed = changed;
        ChannelKind = channel_kind;
        feature.PropertyChanged += feature_changed;
        platform.PropertyChanged += platform_changed;
    }

    public IReadOnlyList<EnumDisplayChoice<PlatformTransformationKind>> NormalizationChoices => normalization_choices;
    public string ChannelName => feature.ChannelName;
    public string Label => feature.Label;
    public ChannelSemanticKind ChannelKind { get; }
    public string ChannelType => ChannelKind.ToString();
    public bool IsEditable => platform.IsIdle && !platform.IsConfigurationLocked;
    public bool IsLogicle => transformation.Kind == PlatformTransformationKind.Logicle;
    public bool IsArcsinh => transformation.Kind == PlatformTransformationKind.Arcsinh;
    public bool HasNoParameters => !IsLogicle && !IsArcsinh;

    public bool IsSelected
    {
        get => feature.IsSelected;
        set => feature.IsSelected = value;
    }

    public EnumDisplayChoice<PlatformTransformationKind>? SelectedNormalization
    {
        get => normalization_choices.FirstOrDefault(choice => choice.Value == transformation.Kind);
        set
        {
            if (value is null || value.Value == transformation.Kind)
                return;
            transformation.Kind = value.Value;
            mark_changed();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLogicle));
            OnPropertyChanged(nameof(IsArcsinh));
            OnPropertyChanged(nameof(HasNoParameters));
        }
    }

    public double LogicleT
    {
        get => transformation.Logicle.T;
        set => set_logicle(transformation.Logicle with { T = positive(value, transformation.Logicle.T) });
    }

    public double LogicleW
    {
        get => transformation.Logicle.W;
        set => set_logicle(transformation.Logicle with { W = finite(value, transformation.Logicle.W) });
    }

    public double LogicleM
    {
        get => transformation.Logicle.M;
        set => set_logicle(transformation.Logicle with { M = positive(value, transformation.Logicle.M) });
    }

    public double LogicleA
    {
        get => transformation.Logicle.A;
        set => set_logicle(transformation.Logicle with { A = finite(value, transformation.Logicle.A) });
    }

    public double ArcsinhA
    {
        get => transformation.ArcsinhCofactor;
        set
        {
            double next = positive(value, transformation.ArcsinhCofactor);
            if (transformation.ArcsinhCofactor.Equals(next))
                return;
            transformation.ArcsinhCofactor = next;
            mark_changed();
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        feature.PropertyChanged -= feature_changed;
        platform.PropertyChanged -= platform_changed;
    }

    private void set_logicle(LogicleParameters value)
    {
        if (transformation.Logicle.Equals(value))
            return;
        transformation.Logicle = value;
        transformation.Maximum = value.T;
        mark_changed();
        OnPropertyChanged(nameof(LogicleT));
        OnPropertyChanged(nameof(LogicleW));
        OnPropertyChanged(nameof(LogicleM));
        OnPropertyChanged(nameof(LogicleA));
    }

    private void mark_changed()
    {
        transformation.IsAutomatic = false;
        changed();
    }

    private void feature_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlatformFeatureSelection.IsSelected))
            OnPropertyChanged(nameof(IsSelected));
    }

    private void platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Platform.IsRunning) or nameof(Platform.IsConfigurationLocked) or null or "")
            OnPropertyChanged(nameof(IsEditable));
    }

    private static double finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
    private static double positive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}

public sealed class IntegrationPlatformEditorViewModel : PlatformEditorViewModel
{
    private const int maximum_preview_events_per_population = 20_000;
    private static readonly ConditionalWeakTable<IntegrationPlatform, IntegrationHistogramCache> histogram_caches = new();
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
        refresh_integration_channel_rows();
        refresh_integration_histogram();
    }

    public ObservableCollection<IntegrationChannelRowViewModel> IntegrationChannelRows { get; } = new();
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
    public double IntegrationHistogramArcsinhA { get; private set; } = 5.0;

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

    public override void Dispose()
    {
        foreach (var row in IntegrationChannelRows)
            row.Dispose();
        IntegrationChannelRows.Clear();
        base.Dispose();
    }

    protected override void OnFeaturesChanged()
    {
        PlatformInitializer.RefreshTransformations(Workspace, Platform);
        refresh_integration_channel_rows();
    }

    private void refresh_integration_channel_rows()
    {
        foreach (var row in IntegrationChannelRows)
            row.Dispose();
        IntegrationChannelRows.Clear();
        foreach (var feature in Platform.Features.Where(feature => feature.IsChannel))
        {
            if (!Platform.Transformations.TryGetValue(feature.ChannelName, out var transformation))
                continue;
            IntegrationChannelRows.Add(new IntegrationChannelRowViewModel(
                Platform,
                feature,
                transformation,
                channel_kind(feature.ChannelName),
                channel_normalization_changed));
        }
    }

    private ChannelSemanticKind channel_kind(string channel_name)
    {
        var selected_ids = PlatformInitializer.SelectedPopulationInputs(Platform)
            .Select(row => row.SampleId)
            .ToHashSet();
        var sample = Workspace.Groups.SelectMany(group => group.Samples)
            .FirstOrDefault(item => selected_ids.Contains(item.Id) && item.Channels.Any(channel => channel.Name == channel_name));
        return Configuration.ChannelKind(channel_name, Configuration.CytometerNameForSample(sample));
    }

    private void channel_normalization_changed()
    {
        if (Platform.HasIntegrated)
            Platform.InvalidateFromConfiguration();
        refresh_integration_histogram();
    }

    protected override void platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        base.platform_changed(sender, e);
        if (e.PropertyName is nameof(Platform.HasIntegrated) or nameof(Platform.HasResults) or nameof(Platform.IsConfigurationLocked) or nameof(Platform.Parameters) or null or "")
        {
            refresh_integration_channel_rows();
            refresh_integration_histogram();
        }
    }

    private void refresh_integration_histogram()
    {
        refresh_histogram_channel_choices();
        ensure_histogram_channel();
        if (!HasIntegrationHistogram || selected_histogram_channel is null || Platform is not IntegrationPlatform integration)
        {
            clear_histogram_series();
            notify_histogram_properties();
            return;
        }

        bool normalized = ShowNormalizedHistogram && integration.Normalized is not null;
        var matrix = normalized
            ? integration.Normalized
            : Platform.Compensated;
        if (matrix is null || Platform.RowMap.Count == 0)
        {
            clear_histogram_series();
            notify_histogram_properties();
            return;
        }

        string channel_name = selected_histogram_channel.Name;
        int column = Array.IndexOf(Platform.SelectedFeatureNames, channel_name);
        if (column < 0 || column >= matrix.GetLength(1))
        {
            clear_histogram_series();
            notify_histogram_properties();
            return;
        }

        var scale = histogram_channel_scale(channel_name);
        double channel_maximum = channel_maximum_for(channel_name);
        var transformation = Platform.Transformations.TryGetValue(channel_name, out var configured) ? configured : null;
        IntegrationHistogramLogicleT = transformation?.Logicle.T ?? channel_maximum;
        IntegrationHistogramLogicleW = transformation?.Logicle.W ?? Platform.Axis.Logicle.W;
        IntegrationHistogramLogicleM = transformation?.Logicle.M ?? Platform.Axis.Logicle.M;
        IntegrationHistogramLogicleA = transformation?.Logicle.A ?? Platform.Axis.Logicle.A;
        IntegrationHistogramArcsinhA = transformation?.ArcsinhCofactor ?? 5.0;
        IntegrationHistogramAxisScale = scale;

        var cache = histogram_caches.GetValue(integration, _ => new IntegrationHistogramCache());
        var series = cache.GetSeries(
            integration,
            matrix,
            column,
            normalized,
            scale,
            IntegrationHistogramLogicleT,
            IntegrationHistogramLogicleW,
            IntegrationHistogramLogicleM,
            IntegrationHistogramLogicleA,
            IntegrationHistogramArcsinhA,
            histogram_value_transform(normalized, scale),
            source_label,
            histogram_palette,
            maximum_preview_events_per_population);
        replace_histogram_series(series);
        update_histogram_axis(channel_maximum, normalized, scale, series);
        notify_histogram_properties();
    }

    private void clear_histogram_series()
    {
        if (IntegrationHistogramSeries.Count > 0)
            IntegrationHistogramSeries.Clear();
    }

    private void replace_histogram_series(IReadOnlyList<HistogramSeries> series)
    {
        if (IntegrationHistogramSeries.SequenceEqual(series))
            return;
        IntegrationHistogramSeries.Clear();
        foreach (var item in series)
            IntegrationHistogramSeries.Add(item);
    }

    private void update_histogram_axis(
        double channel_maximum,
        bool normalized,
        HistogramAxisScaleKind scale_kind,
        IReadOnlyList<HistogramSeries> series)
    {
        var observed = observed_real_range(series, channel_maximum);
        if (scale_kind == HistogramAxisScaleKind.Linear)
        {
            if (normalized)
            {
                IntegrationHistogramMinimum = Math.Min(-0.1, observed.Minimum);
                IntegrationHistogramMaximum = Math.Max(1.1, observed.Maximum);
            }
            else
            {
                IntegrationHistogramMinimum = -0.1 * channel_maximum;
                IntegrationHistogramMaximum = 1.1 * channel_maximum;
            }
            return;
        }

        var scale = new AxisScale
        {
            Kind = scale_kind switch
            {
                HistogramAxisScaleKind.Log => CoordinateScaleKind.Logarithmic,
                HistogramAxisScaleKind.Arcsinh => CoordinateScaleKind.Arcsinh,
                _ => CoordinateScaleKind.Logicle
            },
            Logicle = new LogicleParameters(
                IntegrationHistogramLogicleT,
                IntegrationHistogramLogicleW,
                IntegrationHistogramLogicleM,
                IntegrationHistogramLogicleA),
            ArcsinhCofactor = IntegrationHistogramArcsinhA
        };
        double transformed_minimum = scale.Transform(observed.Minimum);
        double transformed_maximum = scale.Transform(observed.Maximum);
        double transformed_span = transformed_maximum - transformed_minimum;
        if (!double.IsFinite(transformed_span) || transformed_span <= 0)
        {
            transformed_minimum = -0.1;
            transformed_maximum = 1.1;
            transformed_span = 1.2;
        }
        IntegrationHistogramMinimum = scale.InverseTransform(transformed_minimum - transformed_span * 0.1);
        IntegrationHistogramMaximum = scale.InverseTransform(transformed_maximum + transformed_span * 0.1);
    }

    private static (double Minimum, double Maximum) observed_real_range(IReadOnlyList<HistogramSeries> series, double fallback_maximum)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        foreach (var item in series)
        {
            var sorted = item.SortedValues;
            if (sorted is null || sorted.Count == 0)
                continue;
            minimum = Math.Min(minimum, sorted[0]);
            maximum = Math.Max(maximum, sorted[^1]);
        }

        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return (0, fallback_maximum);
        if (maximum <= minimum)
            maximum = minimum + 1;
        return (minimum, maximum);
    }

    private HistogramAxisScaleKind histogram_channel_scale(string channel_name)
    {
        var kind = Platform.Transformations.TryGetValue(channel_name, out var transformation)
            ? transformation.Kind
            : Configuration.DefaultPlatformTransformationForChannel(channel_name);
        return kind switch
        {
            PlatformTransformationKind.Logicle => HistogramAxisScaleKind.Logicle,
            PlatformTransformationKind.Logarithm => HistogramAxisScaleKind.Log,
            PlatformTransformationKind.Arcsinh => HistogramAxisScaleKind.Arcsinh,
            _ => HistogramAxisScaleKind.Linear
        };
    }

    private Func<double, double> histogram_value_transform(bool normalized, HistogramAxisScaleKind scale)
    {
        if (!normalized)
            return value => value;

        if (scale == HistogramAxisScaleKind.Log)
            return value => Math.Sign(value) * (Math.Pow(10, Math.Abs(value)) - 1.0);
        if (scale == HistogramAxisScaleKind.Arcsinh)
            return value => IntegrationHistogramArcsinhA * Math.Sinh(value);
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

    private double channel_maximum_for(string channel_name)
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

    private sealed class IntegrationHistogramCache
    {
        private int[]? source_ids;
        private int matrix_row_count = -1;
        private int[][] sampled_rows = [];
        private readonly Dictionary<IntegrationHistogramCacheKey, HistogramSeries[]> series = new();

        public IReadOnlyList<HistogramSeries> GetSeries(
            IntegrationPlatform platform,
            float[,] current_matrix,
            int column,
            bool normalized,
            HistogramAxisScaleKind scale,
            double logicle_t,
            double logicle_w,
            double logicle_m,
            double logicle_a,
            double arcsinh_a,
            Func<double, double> value_transform,
            Func<int, string> source_label,
            IReadOnlyList<Color> colors,
            int maximum_events)
        {
            ensure_samples(platform, current_matrix, maximum_events);
            var key = new IntegrationHistogramCacheKey(
                current_matrix,
                column,
                normalized,
                scale,
                logicle_t,
                logicle_w,
                logicle_m,
                logicle_a,
                arcsinh_a);
            if (series.TryGetValue(key, out var cached))
                return cached;

            var created = new List<HistogramSeries>();
            for (int source_id = 0; source_id < sampled_rows.Length; source_id++)
            {
                var values = sampled_rows[source_id]
                    .Select(row => value_transform(current_matrix[row, column]))
                    .Where(double.IsFinite)
                    .ToArray();
                if (values.Length == 0)
                    continue;
                Array.Sort(values);
                created.Add(new HistogramSeries
                {
                    Name = source_label(source_id),
                    Values = values,
                    SortedValues = values,
                    BinCount = 500,
                    Color = colors[source_id % colors.Count]
                });
            }

            cached = created.ToArray();
            series[key] = cached;
            return cached;
        }

        private void ensure_samples(IntegrationPlatform platform, float[,] current_matrix, int maximum_events)
        {
            if (ReferenceEquals(source_ids, platform.RowMap.SourceIds) &&
                matrix_row_count == current_matrix.GetLength(0) &&
                sampled_rows.Length == platform.RowMap.Sources.Count)
                return;

            source_ids = platform.RowMap.SourceIds;
            matrix_row_count = current_matrix.GetLength(0);
            series.Clear();
            sampled_rows = new int[platform.RowMap.Sources.Count][];
            int initial_capacity = Math.Min(
                maximum_events,
                Math.Max(16, current_matrix.GetLength(0) / Math.Max(1, sampled_rows.Length)));
            var reservoirs = Enumerable.Range(0, sampled_rows.Length)
                .Select(_ => new List<int>(initial_capacity))
                .ToArray();
            var seen = new int[sampled_rows.Length];
            var random = Enumerable.Range(0, sampled_rows.Length)
                .Select(source_id => new Random(HashCode.Combine(platform.Id, source_id, current_matrix.GetLength(0))))
                .ToArray();

            int row_count = Math.Min(source_ids.Length, current_matrix.GetLength(0));
            for (int row = 0; row < row_count; row++)
            {
                int source_id = source_ids[row];
                if (source_id < 0 || source_id >= reservoirs.Length)
                    continue;
                int count = ++seen[source_id];
                var reservoir = reservoirs[source_id];
                if (reservoir.Count < maximum_events)
                {
                    reservoir.Add(row);
                    continue;
                }

                int replacement = random[source_id].Next(count);
                if (replacement < maximum_events)
                    reservoir[replacement] = row;
            }

            for (int source_id = 0; source_id < sampled_rows.Length; source_id++)
                sampled_rows[source_id] = reservoirs[source_id].ToArray();
        }

    }

    private readonly record struct IntegrationHistogramCacheKey(
        float[,] Matrix,
        int Column,
        bool Normalized,
        HistogramAxisScaleKind Scale,
        double LogicleT,
        double LogicleW,
        double LogicleM,
        double LogicleA,
        double ArcsinhA);

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
        OnPropertyChanged(nameof(IntegrationHistogramArcsinhA));
        if (PreviousHistogramChannelCommand is RelayCommand previous)
            previous.RaiseCanExecuteChanged();
        if (NextHistogramChannelCommand is RelayCommand next)
            next.RaiseCanExecuteChanged();
    }
}

public sealed class CellCyclePlatformEditorViewModel : PlatformEditorViewModel
{
    public CellCyclePlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) => property_name is
        nameof(CellCyclePlatform.Model) or nameof(UnivariatePlatform.ArcsinhCofactor);
}

public sealed class ProliferationPlatformEditorViewModel : PlatformEditorViewModel
{
    public ProliferationPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) =>
        property_name is nameof(ProliferationPlatform.MaxGenerations) or
            nameof(ProliferationPlatform.PeakProminence) or
            nameof(UnivariatePlatform.ArcsinhCofactor);
}

public sealed class IntensityComparisonPlatformEditorViewModel : PlatformEditorViewModel
{
    public IntensityComparisonPlatformEditorViewModel(FlowWorkspace workspace, Platform platform) : base(workspace, platform) { }
    protected override bool IsFitParameter(string? property_name) => property_name is
        nameof(IntensityComparisonPlatform.ReferenceSample) or
        nameof(IntensityComparisonPlatform.DistributionBinning) or
        nameof(UnivariatePlatform.ArcsinhCofactor);
}
