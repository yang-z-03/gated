using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using gated.Controls;
using gated.Models;
using gated.Reduction;
using gated.Services;
using PythonWorkspaceContext = gated.Python.PythonWorkspaceContext;

namespace gated.ViewModels;

public enum MainWindowViewState
{
    Analysis,
    Layout,
    Code,
    Metadata,
    GroupMetadata,
    SpilloverCompensation,
    SpectralUnmixing,
    Platform
}

public sealed partial class MainWindowViewModel : NotifyBase
{
    private readonly ObservableCollection<ProjectNode> project_roots = new();
    private readonly Dictionary<string, bool> project_expansion_state = new();
    private ProjectNode? selected_node;
    private FlowGroup? selected_group;
    private FlowSample? selected_sample;
    private GateDefinition? selected_gate;
    private PopulationResult? selected_population;
    private CompensationMatrix? selected_compensation;
    private ControlSample? selected_control_sample;
    private SpilloverControlRowViewModel? selected_spillover_row;
    private ControlGatePreset? selected_spillover_gate_preset;
    private ControlSample? spillover_scatter_sample_cache;
    private string spillover_scatter_sample_cache_key = "";
    private readonly Dictionary<string, int[]> spillover_primary_index_cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpilloverGatedChannelCache> spillover_gated_channel_cache = new(StringComparer.Ordinal);
    private CompensationMatrix? spillover_preview_matrix;
    private bool is_spillover_preview_outdated;
    private bool is_spillover_calculating;
    private bool is_spillover_preparing_row_caches;
    private PageLayout? selected_page_layout;
    private Platform? selected_integration_job;
    private MainWindowViewState view_state = MainWindowViewState.Analysis;
    private bool suppress_analysis_switch_for_context_selection;
    private bool syncing_metadata;
    private int layout_refresh_revision;
    private PlotMode selected_plot_mode = PlotMode.Density;
    private GatingTool active_tool = GatingTool.View;
    private bool show_outlier_points = true;
    private bool draw_large_dots;
    private bool show_gridlines = true;
    private bool show_gate_annotations = true;
    private bool show_gate_annotation_names;
    private int contour_level_count = 10;
    private int density_smoothing = 9;
    private PlotColorPalette density_palette = PlotColorPalette.Turbo;
    private string status_text = "Append samples to grouping to begin analysis";
    private string python_engine_status_text = "Script engine ready.";
    private bool is_python_engine_progress_visible;
    private bool is_python_engine_progress_indeterminate;
    private double python_engine_progress;
    private string python_script_text = "";
    private string python_script_output = "";
    private string python_script_name = "";
    private PythonScriptDefinition? editing_python_script;
    private readonly Dictionary<string, PythonLogTask> python_log_tasks_by_key = new(StringComparer.Ordinal);
    private PythonLogTask? selected_python_log_task;
    private bool show_python_info_logs = true;
    private bool show_python_warning_logs = true;
    private bool show_python_error_logs = true;
    private bool show_python_fatal_logs = true;
    private PythonScriptDefinition? selected_integration_macro;
    private Platform? subscribed_integration_job;
    private bool is_python_script_dirty;
    private bool syncing_python_script;
    private bool is_python_script_running;
    private AxisSettings x_axis = new();
    private AxisSettings y_axis = new();
    private DotColorSettings dot_color = new();
    private readonly HashSet<FlowGroup> groups_pending_root_view_initialization = new();
    private int next_gate_number = 1;
    private PagePlotElement? selected_page_element;
    private PagePlotElement? subscribed_page_menu_element;
    private double export_bitmap_dpi = 300;
    private bool export_bitmap_apply_rasterization_resolution;
    private bool export_bitmap_transparent_background;
    private EquivalentSampleChoice? selected_equivalent_sample_choice;
    private bool syncing_equivalent_sample_choices;
    private bool applying_node_selection;
    private bool applying_gate_view_options;
    private bool is_plot_transform_preparing;
    private CancellationTokenSource? plot_transform_preparation_cancellation;
    private bool is_gate_recalculating;
    private CancellationTokenSource? gate_recalculation_cancellation;
    private bool is_compensation_applying;
    private CancellationTokenSource? compensation_application_cancellation;

    public FlowWorkspace Workspace { get; } = new();
    public ObservableCollection<ProjectNode> ProjectNodes { get; } = new();
    public ObservableCollection<ChannelRow> ChannelRows { get; } = new();
    public ObservableCollection<AxisChoice> AxisChoices { get; } = new();
    public ObservableCollection<AxisChoice> ColorChoices { get; } = new();
    public ObservableCollection<AxisChoice> SelectedPageAxisChoices { get; } = new();
    public ObservableCollection<AxisChoice> SelectedPageColorChoices { get; } = new();
    public ObservableCollection<EquivalentSampleChoice> EquivalentSampleChoices { get; } = new();
    public ObservableCollection<SpilloverControlRowViewModel> SpilloverRows { get; } = new();
    public ObservableCollection<ControlGatePreset> SpilloverGatePresets { get; } = new();
    public ObservableCollection<string> SpilloverParameterChoices { get; } = new();
    public ObservableCollection<string> SpilloverConfiguredParameterChoices { get; } = new();
    public ObservableCollection<HistogramSeries> SpilloverHistogramSeries { get; } = new();
    public ObservableCollection<string> SpilloverPreviewChannels { get; } = new();
    public ObservableCollection<SpilloverPreviewMatrixRow> SpilloverPreviewMatrixRows { get; } = new();
    public ObservableCollection<SpilloverPreviewCell> SpilloverPreviewCells { get; } = new();
    public ObservableCollection<SpilloverPreviewPlotRow> SpilloverPreviewPlotRows { get; } = new();
    public ObservableCollection<string> MetadataColumnChoices { get; } = new();
    private DataTable statistic_table = new();
    private DataTable workspace_metadata_table = new();
    public ObservableCollection<GateDefinition> PlotGates { get; } = new();
    public ObservableCollection<PagePlotElement> PageElements => selected_page_layout?.Elements ?? empty_page_elements;
    private readonly ObservableCollection<PagePlotElement> empty_page_elements = new();
    public ObservableCollection<CoordinateScaleKind> CoordinateScaleChoices { get; } = new(Enum.GetValues<CoordinateScaleKind>());
    public ObservableCollection<PlotMode> PlotModeChoices { get; } = new(Enum.GetValues<PlotMode>());
    public ObservableCollection<PlotColorMap> PlotColorMapChoices { get; } = new(PlotColorMaps.All);
    public ObservableCollection<PythonScriptDefinition> MacroScripts { get; } = new();
    public ObservableCollection<PythonScriptDefinition> StatisticScripts { get; } = new();
    public ObservableCollection<string> RecentFilePaths => Workspace.RecentFilePaths;
    public DataView StatisticTableView => statistic_table.DefaultView;
    public DataTable StatisticTable => statistic_table;
    public DataView WorkspaceMetadataTableView => workspace_metadata_table.DefaultView;
    public DataTable WorkspaceMetadataTable => workspace_metadata_table;
    public SpectralUnmixingViewModel SpectralPanel { get; }

    public ICommand CreateGroupCommand { get; }
    public ICommand CreateLayoutCommand { get; }
    public ICommand CreateIntegrationJobCommand { get; }
    public ICommand RenameWorkspaceCommand { get; }
    public ICommand RenameIntegrationJobCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand RenameGateCommand { get; }
    public ICommand RenameLayoutCommand { get; }
    public ICommand RenameSelectedNodeCommand { get; }
    public ICommand ConcatenateSamplesCommand { get; }
    public ICommand CreateCompensationCommand { get; }
    public ICommand ApplyCompensationCommand { get; }
    public ICommand EditCompensationCommand { get; }
    public ICommand ReapplyCompensationCommand { get; }
    public ICommand OpenSpilloverCompensationCommand { get; }
    public ICommand RecalculateSelectedGroupCommand { get; }
    public ICommand RecalculateSelectedGateCommand { get; }
    public ICommand RecalculateSelectedStatisticCommand { get; }
    public ICommand CopyHierarchyViewOptionsToGroupCommand { get; }
    public ICommand RefreshSelectedLayoutCommand { get; }
    public ICommand ExpandProjectTreeCommand { get; }
    public ICommand CollapseProjectTreeCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand CloseWorkspaceCommand { get; }
    public ICommand AddPolygonGateCommand { get; }
    public ICommand AddRectangleGateCommand { get; }
    public ICommand AddOffsetQuadrantGateCommand { get; }
    public ICommand AddThresholdGateCommand { get; }
    public ICommand AddRangeGateCommand { get; }
    public ICommand AddMergeGateCommand { get; }
    public ICommand AddExcludeGateCommand { get; }
    public ICommand AddOverlapGateCommand { get; }
    public ICommand AddMeanStatisticCommand { get; }
    public ICommand AddMedianStatisticCommand { get; }
    public ICommand AddGeometricMeanStatisticCommand { get; }
    public ICommand AddCoefficientOfVariationStatisticCommand { get; }
    public ICommand AddStandardDeviationStatisticCommand { get; }
    public ICommand AddFrequencyOfParentStatisticCommand { get; }
    public ICommand AddFrequencyOfAllStatisticCommand { get; }
    public ICommand AddCountStatisticCommand { get; }
    public ICommand AddCanvasGateCommand { get; }
    public ICommand GateEditedCommand { get; }
    public ICommand ToggleProjectNodeCommand { get; }
    public ICommand SelectProjectNodeCommand { get; }
    public ICommand DropProjectNodeCommand { get; }
    public ICommand DropSpilloverControlCommand { get; }
    public ICommand CalculateSpilloverCompensationCommand { get; }
    public ICommand ApplySpilloverCompensationCommand { get; }
    public ICommand SelectPreviousSpilloverChannelCommand { get; }
    public ICommand SelectNextSpilloverChannelCommand { get; }
    public ICommand RemoveSpilloverControlCommand { get; }
    public ICommand MarkSpilloverPreviewOutdatedCommand { get; }
    public ICommand NewSpilloverGatePresetCommand { get; }
    public ICommand RemoveChannelCommand { get; }
    public ICommand AddPageElementCommand { get; }
    public ICommand DeletePageElementCommand { get; }
    public ICommand ApplyWorkspaceMetadataCommand { get; }
    public ICommand AddStringMetadataColumnCommand { get; }
    public ICommand AddIntegerMetadataColumnCommand { get; }
    public ICommand AddFloatMetadataColumnCommand { get; }
    public ICommand OpenPythonScriptEditorCommand { get; }
    public ICommand ClosePythonScriptEditorCommand { get; }
    public ICommand RunPythonScriptCommand { get; }
    public ICommand SelectPreviousEquivalentSampleCommand { get; }
    public ICommand SelectNextEquivalentSampleCommand { get; }
    public ICommand SwapEditorAxesCommand { get; }
    public Func<string, string, Task<string?>>? RequestTextInputAsync { get; set; }
    public Func<string, string, Task<ScriptSaveChoice>>? RequestScriptSaveChoiceAsync { get; set; }
    public Func<string, IReadOnlyList<AxisChoice>, Task<string?>>? RequestChoiceInputAsync { get; set; }
    public Func<string, IReadOnlyList<AxisChoice>, Task<IReadOnlyList<string>?>>? RequestMultipleChoiceInputAsync { get; set; }
    public Func<string, IReadOnlyList<BooleanPopulationChoice>, Task<BooleanGateSelection?>>? RequestBooleanGateInputAsync { get; set; }
    public Func<CompensationMatrix, Task<bool>>? RequestCompensationEditorAsync { get; set; }
    public Func<string, string, Task>? RequestMessageAsync { get; set; }
    public Func<string, string, Task<bool>>? RequestConfirmationAsync { get; set; }

    public MainWindowViewModel()
    {
        SpectralPanel = new SpectralUnmixingViewModel(this);
        Python.PythonExtensionRuntime.StatusChanged += UpdatePythonExecutionStatus;
        Python.PythonExtensionRuntime.LogRunStarted += BeginPythonLogRun;
        Python.PythonExtensionRuntime.LogReceived += AppendPythonLog;
        DotColor.PropertyChanged += dot_color_property_changed;
        foreach (string path in RecentFileStore.Load())
            Workspace.RecentFilePaths.Add(path);
        ReloadScriptRepositories();
        CreateGroupCommand = new RelayCommand(_ => create_group());
        CreateLayoutCommand = new RelayCommand(_ => _ = create_layout_async());
        CreateIntegrationJobCommand = new RelayCommand(parameter => _ = create_platform_async(PlatformJobInitializer.KindFromParameter(parameter)), _ => Workspace.Groups.Any(group => group.Samples.Count > 0));
        RenameWorkspaceCommand = new RelayCommand(_ => _ = rename_workspace_async());
        RenameIntegrationJobCommand = new RelayCommand(_ => _ = rename_selected_integration_job_async(), _ => selected_integration_job is not null);
        RenameGroupCommand = new RelayCommand(_ => _ = rename_selected_group_async(), _ => selected_group is not null);
        RenameGateCommand = new RelayCommand(_ => _ = rename_selected_gate_async(), _ => selected_gate is not null);
        RenameLayoutCommand = new RelayCommand(_ => _ = rename_selected_layout_async(), _ => selected_page_layout is not null);
        RenameSelectedNodeCommand = new RelayCommand(_ => _ = rename_selected_node_async(), _ => can_rename_selected_node());
        ConcatenateSamplesCommand = new RelayCommand(_ => { }, _ => selected_group?.Samples.Count > 0);
        CreateCompensationCommand = new RelayCommand(_ => _ = create_compensation_async(), _ => selected_group?.Channels.Count > 0);
        ApplyCompensationCommand = new RelayCommand(_ => apply_selected_compensation(), _ => selected_group is not null && selected_compensation is not null);
        EditCompensationCommand = new RelayCommand(_ => _ = edit_selected_compensation_async(), _ => selected_group is not null && selected_compensation is not null);
        ReapplyCompensationCommand = new RelayCommand(_ => reapply_selected_group_compensation(), _ => selected_group is not null);
        OpenSpilloverCompensationCommand = new RelayCommand(_ => open_spillover_compensation_panel(), _ => selected_group is not null);
        RecalculateSelectedGroupCommand = new RelayCommand(_ => RecalculateSelectedGroup(), _ => selected_group is not null);
        RecalculateSelectedGateCommand = new RelayCommand(_ => RecalculateEditedGate(selected_gate), _ => selected_group is not null);
        RecalculateSelectedStatisticCommand = new RelayCommand(_ => recalculate_selected_statistic(), _ => selected_group is not null && selected_statistic_definition() is not null);
        CopyHierarchyViewOptionsToGroupCommand = new RelayCommand(_ => copy_hierarchy_view_options_to_group(), _ => can_copy_hierarchy_view_options_to_group());
        RefreshSelectedLayoutCommand = new RelayCommand(_ => RefreshLayoutCanvas(), _ => selected_page_layout is not null);
        ExpandProjectTreeCommand = new RelayCommand(_ => set_project_tree_expanded(true));
        CollapseProjectTreeCommand = new RelayCommand(_ => set_project_tree_expanded(false));
        DeleteSelectedCommand = new RelayCommand(_ => delete_selected(), _ => can_delete_selected_node());
        CloseWorkspaceCommand = new RelayCommand(_ => CloseWorkspace());
        AddPolygonGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Polygon), _ => can_create_gate_kind(GateKind.Polygon));
        AddRectangleGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Rectangle), _ => can_create_gate_kind(GateKind.Rectangle));
        AddOffsetQuadrantGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.OffsetQuadrant), _ => can_create_gate_kind(GateKind.OffsetQuadrant));
        AddThresholdGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Threshold), _ => can_create_gate_kind(GateKind.Threshold));
        AddRangeGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Range), _ => can_create_gate_kind(GateKind.Range));
        AddMergeGateCommand = new RelayCommand(_ => _ = add_boolean_gate_async(GateKind.Merge), _ => CanCreateAnyGate && boolean_population_choices().Count >= 2);
        AddExcludeGateCommand = new RelayCommand(_ => _ = add_boolean_gate_async(GateKind.Exclude), _ => CanCreateAnyGate && boolean_population_choices().Count >= 2);
        AddOverlapGateCommand = new RelayCommand(_ => _ = add_boolean_gate_async(GateKind.Overlap), _ => CanCreateAnyGate && boolean_population_choices().Count >= 2);
        AddMeanStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.Mean), _ => selected_group is not null);
        AddMedianStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.Median), _ => selected_group is not null);
        AddGeometricMeanStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.GeometricMean), _ => selected_group is not null);
        AddCoefficientOfVariationStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.CoefficientOfVariation), _ => selected_group is not null);
        AddStandardDeviationStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.StandardDeviation), _ => selected_group is not null);
        AddFrequencyOfParentStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.FrequencyOfParent), _ => selected_group is not null);
        AddFrequencyOfAllStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.FrequencyOfAll), _ => selected_group is not null);
        AddCountStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.NumberOfEvents), _ => selected_group is not null);
        AddCanvasGateCommand = new RelayCommand(parameter => _ = add_canvas_gate_async(parameter as GateDefinition));
        GateEditedCommand = new RelayCommand(parameter => ScheduleEditedGateRecalculation(parameter as GateDefinition));
        ToggleProjectNodeCommand = new RelayCommand(parameter => toggle_project_node(parameter as ProjectNode));
        SelectProjectNodeCommand = new RelayCommand(parameter => _ = select_project_node_async(parameter as ProjectNode));
        DropProjectNodeCommand = new RelayCommand(parameter => _ = drop_project_node_async(parameter as ProjectNodeDropRequest), can_drop_project_node);
        DropSpilloverControlCommand = new RelayCommand(parameter => _ = drop_spillover_control_async(parameter as ProjectNode), can_drop_spillover_control);
        CalculateSpilloverCompensationCommand = new RelayCommand(_ => _ = calculate_spillover_compensation_async(), _ => can_calculate_spillover_compensation());
        ApplySpilloverCompensationCommand = new RelayCommand(_ => apply_spillover_compensation(), _ => can_apply_spillover_compensation());
        SelectPreviousSpilloverChannelCommand = new RelayCommand(_ => select_relative_spillover_parameter(-1), _ => can_select_relative_spillover_parameter());
        SelectNextSpilloverChannelCommand = new RelayCommand(_ => select_relative_spillover_parameter(1), _ => can_select_relative_spillover_parameter());
        RemoveSpilloverControlCommand = new RelayCommand(parameter => remove_spillover_control(parameter as SpilloverControlRowViewModel), parameter => parameter is SpilloverControlRowViewModel);
        MarkSpilloverPreviewOutdatedCommand = new RelayCommand(_ => spillover_gate_committed());
        NewSpilloverGatePresetCommand = new RelayCommand(_ => new_spillover_gate_preset(), _ => selected_group is not null);
        RemoveChannelCommand = new RelayCommand(parameter => remove_channel(parameter as ChannelRow), parameter => can_remove_channel(parameter as ChannelRow));
        AddPageElementCommand = new RelayCommand(parameter => add_page_element(parameter as PageDropRequest));
        DeletePageElementCommand = new RelayCommand(_ => delete_selected_page_element(), _ => selected_page_element is not null);
        ApplyWorkspaceMetadataCommand = new RelayCommand(_ => CommitWorkspaceSampleMetadata(), _ => IsWorkspaceMetadataMode);
        AddStringMetadataColumnCommand = new RelayCommand(_ => _ = add_metadata_column_async(MetadataColumnKind.String));
        AddIntegerMetadataColumnCommand = new RelayCommand(_ => _ = add_metadata_column_async(MetadataColumnKind.Integer));
        AddFloatMetadataColumnCommand = new RelayCommand(_ => _ = add_metadata_column_async(MetadataColumnKind.Float));
        OpenPythonScriptEditorCommand = new RelayCommand(_ => ShowPythonScriptEditor());
        ClosePythonScriptEditorCommand = new RelayCommand(_ => _ = ClosePythonScriptEditorAsync());
        RunPythonScriptCommand = new RelayCommand(_ => _ = run_python_script_async(), _ => !is_python_script_running);
        SelectPreviousEquivalentSampleCommand = new RelayCommand(_ => select_relative_equivalent_sample(-1), _ => EquivalentSampleChoices.Count > 1);
        SelectNextEquivalentSampleCommand = new RelayCommand(_ => select_relative_equivalent_sample(1), _ => EquivalentSampleChoices.Count > 1);
        SwapEditorAxesCommand = new RelayCommand(_ => swap_editor_axes());
        ensure_default_layout();
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
    }

    public ProjectNode? SelectedNode
    {
        get => selected_node;
        set
        {
            if (ReferenceEquals(selected_node, value))
                return;
            if (selected_node is not null)
                selected_node.IsSelected = false;
            if (!SetField(ref selected_node, value))
                return;
            if (selected_node is not null)
                selected_node.IsSelected = true;
            OnPropertyChanged(nameof(IsGroupMetadataMode));
            OnPropertyChanged(nameof(IsPlotPropertiesMode));
            applying_node_selection = true;
            try
            {
                apply_node_selection(value);
            }
            finally
            {
                applying_node_selection = false;
            }
        }
    }

    public void SelectNodeForContextMenu(ProjectNode node)
    {
        suppress_analysis_switch_for_context_selection = true;
        try
        {
            SelectedNode = node;
        }
        finally
        {
            suppress_analysis_switch_for_context_selection = false;
        }
    }

    public FlowGroup? SelectedGroup
    {
        get => selected_group;
        private set
        {
            if (!SetField(ref selected_group, value))
                return;

            refresh_selection_sidebars();
            refresh_equivalent_sample_choices();
            schedule_plot_transform_preparation();
        }
    }

    public FlowSample? SelectedSample
    {
        get => selected_sample;
        private set
        {
            if (!SetField(ref selected_sample, value))
                return;
            refresh_axis_choices();
            refresh_equivalent_sample_choices();
            schedule_plot_transform_preparation();
        }
    }

    public GateDefinition? SelectedGate
    {
        get => selected_gate;
        private set
        {
            if (!SetField(ref selected_gate, value))
                return;
            OnPropertyChanged(nameof(PlotGate));
            OnPropertyChanged(nameof(SelectedGateName));
            OnPropertyChanged(nameof(EffectivePlotMode));
            OnPropertyChanged(nameof(IsYAxisEnabled));
            OnPropertyChanged(nameof(CanCreateAnyGate));
            OnPropertyChanged(nameof(CanCreateOneDimensionalGate));
            OnPropertyChanged(nameof(CanCreateTwoDimensionalGate));
            raise_command_states();
            refresh_selected_statistics();
            refresh_equivalent_sample_choices();
            schedule_plot_transform_preparation();
        }
    }

    public PopulationResult? SelectedPopulation
    {
        get => selected_population;
        private set
        {
            if (!SetField(ref selected_population, value))
                return;
            OnPropertyChanged(nameof(PlotPopulation));
            OnPropertyChanged(nameof(CanCreateAnyGate));
            OnPropertyChanged(nameof(CanCreateOneDimensionalGate));
            OnPropertyChanged(nameof(CanCreateTwoDimensionalGate));
            enforce_active_tool_allowed();
            refresh_axis_choices();
            refresh_selected_statistics();
            refresh_equivalent_sample_choices();
            schedule_plot_transform_preparation();
        }
    }

    public GateDefinition? PlotGate => selected_gate;
    public PopulationResult? PlotPopulation => selected_population;
    public string SelectedGateName => PlotGate?.Name ?? "No gate selected";

    public bool IsPlotTransformPreparing
    {
        get => is_plot_transform_preparing;
        private set
        {
            if (!SetField(ref is_plot_transform_preparing, value))
                return;
            OnPropertyChanged(nameof(IsEditorPlotCalculating));
        }
    }

    public bool IsGateRecalculating
    {
        get => is_gate_recalculating;
        private set
        {
            if (!SetField(ref is_gate_recalculating, value))
                return;
            OnPropertyChanged(nameof(IsEditorPlotCalculating));
        }
    }

    public bool IsCompensationApplying
    {
        get => is_compensation_applying;
        private set
        {
            if (!SetField(ref is_compensation_applying, value))
                return;
            OnPropertyChanged(nameof(IsEditorPlotCalculating));
        }
    }

    public bool IsEditorPlotCalculating => IsPlotTransformPreparing || IsGateRecalculating || IsCompensationApplying;

    public EquivalentSampleChoice? SelectedEquivalentSampleChoice
    {
        get => selected_equivalent_sample_choice;
        set
        {
            if (ReferenceEquals(selected_equivalent_sample_choice, value))
                return;

            selected_equivalent_sample_choice = value;
            OnPropertyChanged();
            if (!syncing_equivalent_sample_choices && value is not null)
                select_equivalent_sample(value);
        }
    }

    public CompensationMatrix? SelectedCompensation
    {
        get => selected_compensation;
        private set
        {
            if (!SetField(ref selected_compensation, value))
                return;
            raise_command_states();
        }
    }

    public ControlSample? SelectedControlSample
    {
        get => selected_control_sample;
        private set
        {
            if (!SetField(ref selected_control_sample, value))
                return;
            OnPropertyChanged(nameof(SpilloverScatterSample));
            refresh_spillover_histogram();
        }
    }

    public SpilloverControlRowViewModel? SelectedSpilloverRow
    {
        get => selected_spillover_row;
        set
        {
            if (!SetField(ref selected_spillover_row, value))
                return;
            if (value is not null)
                SelectedSpilloverGatePreset = SpilloverGatePresets.FirstOrDefault(preset => preset.Id == value.State.GatePresetId) ?? SpilloverGatePresets.FirstOrDefault();
            if (value is not null)
                SelectedControlSample = value.Sample;
            OnPropertyChanged(nameof(SpilloverSelection));
            OnPropertyChanged(nameof(SelectedSpilloverParameterChoice));
            refresh_spillover_histogram();
            raise_command_states();
        }
    }

    public ControlGatePreset? SelectedSpilloverGatePreset
    {
        get => selected_spillover_gate_preset;
        set
        {
            if (!SetField(ref selected_spillover_gate_preset, value)) return;
            if (selected_spillover_row is not null && value is not null && selected_spillover_row.State.GatePresetId != value.Id)
            {
                selected_spillover_row.State.GatePresetId = value.Id;
                refresh_spillover_histogram();
                refresh_spillover_population_text();
            }
            OnPropertyChanged(nameof(SpilloverPrimaryVertices));
            OnPropertyChanged(nameof(SpilloverFscChannel)); OnPropertyChanged(nameof(SpilloverSscChannel));
            OnPropertyChanged(nameof(SpilloverScatterXMinimum)); OnPropertyChanged(nameof(SpilloverScatterXMaximum)); OnPropertyChanged(nameof(SpilloverScatterXScale));
            OnPropertyChanged(nameof(SpilloverScatterYMinimum)); OnPropertyChanged(nameof(SpilloverScatterYMaximum)); OnPropertyChanged(nameof(SpilloverScatterYScale));
            mark_spillover_preview_outdated();
        }
    }

    public ControlSample? SpilloverScatterSample => spillover_scatter_sample();
    public bool IsSpilloverPreparingRowCaches
    {
        get => is_spillover_preparing_row_caches;
        private set => SetField(ref is_spillover_preparing_row_caches, value);
    }
    public ObservableCollection<Point>? SpilloverPrimaryVertices => SelectedSpilloverGatePreset?.Vertices ?? selected_group?.SpilloverCompensation.PrimaryVertices;
    public string SpilloverFscChannel => SelectedSpilloverGatePreset?.XChannel ?? selected_group?.Channels.FirstOrDefault(channel => Configuration.IsFscChannel(channel.Name))?.Name ?? selected_group?.Channels.FirstOrDefault()?.Name ?? "FSC-A";
    public string SpilloverSscChannel => SelectedSpilloverGatePreset?.YChannel ?? selected_group?.Channels.FirstOrDefault(channel => Configuration.IsSscChannel(channel.Name))?.Name ?? selected_group?.Channels.Skip(1).FirstOrDefault()?.Name ?? SpilloverFscChannel;
    public double SpilloverScatterXMinimum => spillover_axis_range(SpilloverFscChannel, x_axis: true).Minimum;
    public double SpilloverScatterXMaximum => spillover_axis_range(SpilloverFscChannel, x_axis: true).Maximum;
    public AxisScale SpilloverScatterXScale => spillover_axis_settings(SpilloverFscChannel, x_axis: true).Scale.Clone();
    public double SpilloverScatterYMinimum => spillover_axis_range(SpilloverSscChannel, x_axis: false).Minimum;
    public double SpilloverScatterYMaximum => spillover_axis_range(SpilloverSscChannel, x_axis: false).Maximum;
    public AxisScale SpilloverScatterYScale => spillover_axis_settings(SpilloverSscChannel, x_axis: false).Scale.Clone();
    public string SpilloverMatrixName
    {
        get => selected_group?.SpilloverCompensation.MatrixName ?? "Auto Comp";
        set
        {
            if (selected_group is null)
                return;
            selected_group.SpilloverCompensation.MatrixName = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedSpilloverParameterChoice
    {
        get => selected_spillover_row?.ParameterName;
        set
        {
            if (selected_spillover_row is null || string.IsNullOrWhiteSpace(value))
                return;
            if (SpilloverRows.FirstOrDefault(row => row.ParameterName == value) is { } row)
            {
                SelectedSpilloverRow = row;
                return;
            }
            selected_spillover_row.ParameterName = value;
            OnPropertyChanged();
            mark_spillover_preview_outdated();
            refresh_spillover_configured_choices();
            refresh_spillover_histogram();
            raise_command_states();
        }
    }

    public HistogramRangeSelection? SpilloverSelection
    {
        get => selected_spillover_row?.PositiveSelection is { } selection
            ? new HistogramRangeSelection(selection.Minimum, selection.Maximum)
            : null;
        set
        {
            if (selected_spillover_row is null)
                return;
            selected_spillover_row.PositiveSelection = value is null
                ? null
                : new SpilloverRangeSelection(value.Minimum, value.Maximum);
            OnPropertyChanged();
            mark_spillover_preview_outdated();
            refresh_spillover_population_text();
            raise_command_states();
        }
    }

    public double SpilloverHistogramMinimum { get; private set; }
    public double SpilloverHistogramMaximum { get; private set; } = new LogicleParameters().T;
    public HistogramAxisScaleKind SpilloverHistogramAxisScale { get; private set; } = HistogramAxisScaleKind.Logicle;
    public double SpilloverHistogramLogicleT { get; private set; } = new LogicleParameters().T;
    public double SpilloverHistogramLogicleW { get; private set; } = new LogicleParameters().W;
    public double SpilloverHistogramLogicleM { get; private set; } = new LogicleParameters().M;
    public double SpilloverHistogramLogicleA { get; private set; } = new LogicleParameters().A;
    public bool HasSpilloverControls => SpilloverRows.Count > 0;
    public bool HasSpilloverPreviewMatrix => spillover_preview_matrix is not null;
    public bool IsSpilloverPreviewOutdated
    {
        get => is_spillover_preview_outdated;
        private set => SetField(ref is_spillover_preview_outdated, value);
    }

    public PlotMode SelectedPlotMode
    {
        get => selected_plot_mode;
        set
        {
            if (!SetField(ref selected_plot_mode, value))
                return;
            OnPropertyChanged(nameof(EffectivePlotMode));
            OnPropertyChanged(nameof(IsYAxisEnabled));
            OnPropertyChanged(nameof(IsDensityPlotMode));
            OnPropertyChanged(nameof(IsDotplotPlotMode));
            OnPropertyChanged(nameof(IsContourPlotMode));
            OnPropertyChanged(nameof(IsZebraPlotMode));
            OnPropertyChanged(nameof(IsHistogramPlotMode));
            OnPropertyChanged(nameof(ShowDensityStyleOptions));
            OnPropertyChanged(nameof(ShowDotplotStyleOptions));
            OnPropertyChanged(nameof(ShowContourDensityStyleOptions));
            OnPropertyChanged(nameof(CanCreateOneDimensionalGate));
            OnPropertyChanged(nameof(CanCreateTwoDimensionalGate));
            enforce_active_tool_allowed();
            sync_selected_gate_preferred_view();
            refresh_plot_gates();
        }
    }

    public PlotMode EffectivePlotMode => selected_gate?.IsOneDimensional == true ? PlotMode.Histogram : SelectedPlotMode;
    public bool IsYAxisEnabled => EffectivePlotMode != PlotMode.Histogram;
    public bool IsDensityPlotMode => SelectedPlotMode == PlotMode.Density;
    public bool IsDotplotPlotMode => SelectedPlotMode == PlotMode.Dotplot;
    public bool IsContourPlotMode => SelectedPlotMode == PlotMode.Contour;
    public bool IsZebraPlotMode => SelectedPlotMode == PlotMode.Zebra;
    public bool IsHistogramPlotMode => SelectedPlotMode == PlotMode.Histogram;
    public bool ShowDensityStyleOptions => SelectedPlotMode == PlotMode.Density;
    public bool ShowDotplotStyleOptions => SelectedPlotMode == PlotMode.Dotplot;
    public bool ShowContourDensityStyleOptions => SelectedPlotMode is PlotMode.Zebra or PlotMode.Contour;
    public bool IsEditorXAxisLinearScale { get => XAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value) XAxis.ScaleKind = CoordinateScaleKind.Linear; } }
    public bool IsEditorXAxisLogicleScale { get => XAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value) XAxis.ScaleKind = CoordinateScaleKind.Logicle; } }
    public bool IsEditorXAxisLogScale { get => XAxis.ScaleKind == CoordinateScaleKind.Logarithmic; set { if (value) XAxis.ScaleKind = CoordinateScaleKind.Logarithmic; } }
    public bool IsEditorXAxisArcsinhScale { get => XAxis.ScaleKind == CoordinateScaleKind.Arcsinh; set { if (value) XAxis.ScaleKind = CoordinateScaleKind.Arcsinh; } }
    public bool IsEditorYAxisLinearScale { get => YAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value) YAxis.ScaleKind = CoordinateScaleKind.Linear; } }
    public bool IsEditorYAxisLogicleScale { get => YAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value) YAxis.ScaleKind = CoordinateScaleKind.Logicle; } }
    public bool IsEditorYAxisLogScale { get => YAxis.ScaleKind == CoordinateScaleKind.Logarithmic; set { if (value) YAxis.ScaleKind = CoordinateScaleKind.Logarithmic; } }
    public bool IsEditorYAxisArcsinhScale { get => YAxis.ScaleKind == CoordinateScaleKind.Arcsinh; set { if (value) YAxis.ScaleKind = CoordinateScaleKind.Arcsinh; } }

    public bool IsLayoutDensityPlotMode { get => selected_page_element?.PlotMode == PlotMode.Density; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Density; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutDotplotPlotMode { get => selected_page_element?.PlotMode == PlotMode.Dotplot; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Dotplot; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutContourPlotMode { get => selected_page_element?.PlotMode == PlotMode.Contour; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Contour; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutZebraPlotMode { get => selected_page_element?.PlotMode == PlotMode.Zebra; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Zebra; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutHistogramPlotMode { get => selected_page_element?.PlotMode == PlotMode.Histogram; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Histogram; refresh_selected_page_menu_state(); } } }
    public bool ShowSelectedPageDensityStyleOptions => selected_page_element?.PlotMode == PlotMode.Density;
    public bool ShowSelectedPageDotplotStyleOptions => selected_page_element?.PlotMode == PlotMode.Dotplot;
    public bool ShowSelectedPageContourDensityStyleOptions => selected_page_element?.PlotMode is PlotMode.Zebra or PlotMode.Contour;
    public bool IsLayoutXAxisLinearScale { get => selected_page_element?.XAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value && selected_page_element is not null) { selected_page_element.XAxis.ScaleKind = CoordinateScaleKind.Linear; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutXAxisLogicleScale { get => selected_page_element?.XAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value && selected_page_element is not null) { selected_page_element.XAxis.ScaleKind = CoordinateScaleKind.Logicle; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutXAxisLogScale { get => selected_page_element?.XAxis.ScaleKind == CoordinateScaleKind.Logarithmic; set { if (value && selected_page_element is not null) { selected_page_element.XAxis.ScaleKind = CoordinateScaleKind.Logarithmic; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutXAxisArcsinhScale { get => selected_page_element?.XAxis.ScaleKind == CoordinateScaleKind.Arcsinh; set { if (value && selected_page_element is not null) { selected_page_element.XAxis.ScaleKind = CoordinateScaleKind.Arcsinh; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutYAxisLinearScale { get => selected_page_element?.YAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value && selected_page_element is not null) { selected_page_element.YAxis.ScaleKind = CoordinateScaleKind.Linear; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutYAxisLogicleScale { get => selected_page_element?.YAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value && selected_page_element is not null) { selected_page_element.YAxis.ScaleKind = CoordinateScaleKind.Logicle; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutYAxisLogScale { get => selected_page_element?.YAxis.ScaleKind == CoordinateScaleKind.Logarithmic; set { if (value && selected_page_element is not null) { selected_page_element.YAxis.ScaleKind = CoordinateScaleKind.Logarithmic; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutYAxisArcsinhScale { get => selected_page_element?.YAxis.ScaleKind == CoordinateScaleKind.Arcsinh; set { if (value && selected_page_element is not null) { selected_page_element.YAxis.ScaleKind = CoordinateScaleKind.Arcsinh; refresh_selected_page_menu_state(); } } }

    public GatingTool ActiveTool
    {
        get => active_tool;
        set
        {
            if (!SetField(ref active_tool, value))
            {
                refresh_active_tool_state();
                return;
            }
            if (!can_use_gating_tool(active_tool))
            {
                active_tool = GatingTool.View;
                OnPropertyChanged();
            }

            refresh_active_tool_state();
        }
    }

    public bool IsViewTool => ActiveTool == GatingTool.View;
    public bool IsPolygonTool => ActiveTool == GatingTool.Polygon;
    public bool IsRectangleTool => ActiveTool == GatingTool.Rectangle;
    public bool IsQuadrantTool => ActiveTool == GatingTool.Quadrant;
    public bool IsCurlyQuadrantTool => ActiveTool == GatingTool.CurlyQuadrant;
    public bool IsOffsetQuadrantTool => ActiveTool == GatingTool.OffsetQuadrant;
    public bool IsThresholdTool => ActiveTool == GatingTool.Threshold;
    public bool IsRangeTool => ActiveTool == GatingTool.Range;
    public bool CanCreateAnyGate => selected_node?.Kind is ProjectNodeKind.GateFolder
            or ProjectNodeKind.Sample
            or ProjectNodeKind.Population
            or ProjectNodeKind.GatePopulationSlot ||
        selected_node?.Kind == ProjectNodeKind.Gate && selected_gate?.PopulationRegions.Count == 1;
    public bool CanCreateOneDimensionalGate => CanCreateAnyGate && EffectivePlotMode == PlotMode.Histogram;
    public bool CanCreateTwoDimensionalGate => CanCreateAnyGate && EffectivePlotMode != PlotMode.Histogram;

    private bool can_use_gating_tool(GatingTool tool) =>
        tool == GatingTool.View ||
        can_create_gate_kind(gate_kind_for_tool(tool));

    private static GateKind gate_kind_for_tool(GatingTool tool) =>
        tool switch
        {
            GatingTool.Polygon => GateKind.Polygon,
            GatingTool.Rectangle => GateKind.Rectangle,
            GatingTool.Quadrant => GateKind.Quadrant,
            GatingTool.CurlyQuadrant => GateKind.CurlyQuadrant,
            GatingTool.OffsetQuadrant => GateKind.OffsetQuadrant,
            GatingTool.Threshold => GateKind.Threshold,
            GatingTool.Range => GateKind.Range,
            _ => GateKind.Rectangle
        };

    private void enforce_active_tool_allowed()
    {
        if (!can_use_gating_tool(active_tool))
            ActiveTool = GatingTool.View;
    }

    private void refresh_active_tool_state()
    {
        OnPropertyChanged(nameof(IsViewTool));
        OnPropertyChanged(nameof(IsPolygonTool));
        OnPropertyChanged(nameof(IsRectangleTool));
        OnPropertyChanged(nameof(IsQuadrantTool));
        OnPropertyChanged(nameof(IsCurlyQuadrantTool));
        OnPropertyChanged(nameof(IsOffsetQuadrantTool));
        OnPropertyChanged(nameof(IsThresholdTool));
        OnPropertyChanged(nameof(IsRangeTool));
    }

    public bool ShowOutlierPoints
    {
        get => show_outlier_points;
        set
        {
            if (SetField(ref show_outlier_points, value))
                sync_selected_gate_preferred_view();
        }
    }

    public bool DrawLargeDots
    {
        get => draw_large_dots;
        set
        {
            if (SetField(ref draw_large_dots, value))
                sync_selected_gate_preferred_view();
        }
    }

    public bool ShowGridlines
    {
        get => show_gridlines;
        set
        {
            if (SetField(ref show_gridlines, value))
                sync_selected_gate_preferred_view();
        }
    }

    public bool ShowGateAnnotations
    {
        get => show_gate_annotations;
        set
        {
            if (SetField(ref show_gate_annotations, value))
                sync_selected_gate_preferred_view();
        }
    }

    public bool ShowGateAnnotationNames
    {
        get => show_gate_annotation_names;
        set
        {
            if (SetField(ref show_gate_annotation_names, value))
                sync_selected_gate_preferred_view();
        }
    }

    public int ContourLevelCount
    {
        get => contour_level_count;
        set
        {
            if (SetField(ref contour_level_count, Math.Clamp(value, 2, 80)))
                sync_selected_gate_preferred_view();
        }
    }

    public int DensitySmoothing
    {
        get => density_smoothing;
        set
        {
            if (SetField(ref density_smoothing, Math.Clamp(value, 0, 12)))
                sync_selected_gate_preferred_view();
        }
    }

    public PlotColorMap SelectedDensityColorMap
    {
        get => PlotColorMaps.Get(density_palette);
        set
        {
            if (value is null || density_palette == value.Palette)
                return;

            density_palette = value.Palette;
            OnPropertyChanged();
            sync_selected_gate_preferred_view();
            refresh_plot_gates();
        }
    }

    public AxisSettings XAxis
    {
        get => x_axis;
        private set
        {
            set_axis(ref x_axis, value, x_axis_property_changed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedXAxisChoice));
            OnPropertyChanged(nameof(IsEditorXAxisLinearScale));
            OnPropertyChanged(nameof(IsEditorXAxisLogicleScale));
        }
    }

    public AxisSettings YAxis
    {
        get => y_axis;
        private set
        {
            set_axis(ref y_axis, value, y_axis_property_changed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedYAxisChoice));
            OnPropertyChanged(nameof(IsEditorYAxisLinearScale));
            OnPropertyChanged(nameof(IsEditorYAxisLogicleScale));
        }
    }

    public DotColorSettings DotColor
    {
        get => dot_color;
        private set
        {
            dot_color.PropertyChanged -= dot_color_property_changed;
            if (!SetField(ref dot_color, value))
            {
                dot_color.PropertyChanged += dot_color_property_changed;
                return;
            }

            dot_color.PropertyChanged += dot_color_property_changed;
            refresh_dot_color_range(dot_color, selected_group, selected_sample, selected_population);
            OnPropertyChanged(nameof(SelectedDotColorChoice));
            OnPropertyChanged(nameof(SelectedDotColorMap));
            OnPropertyChanged(nameof(CanUseDotColorLogScale));
        }
    }

    public AxisChoice? SelectedDotColorChoice
    {
        get => ColorChoices.FirstOrDefault(choice => choice.Name == DotColor.ChannelName);
        set
        {
            if (value is null || DotColor.ChannelName == value.Name)
                return;

            DotColor.ChannelName = value.Name;
            refresh_dot_color_range(DotColor, selected_group, selected_sample, selected_population, reset_selection: true);
            OnPropertyChanged(nameof(CanUseDotColorLogScale));
            OnPropertyChanged();
            refresh_axis_menu_state();
        }
    }

    public PlotColorMap SelectedDotColorMap
    {
        get => PlotColorMaps.Get(DotColor.Palette);
        set
        {
            if (value is null || DotColor.Palette == value.Palette)
                return;

            DotColor.Palette = value.Palette;
            OnPropertyChanged();
        }
    }

    public bool CanUseDotColorLogScale => DotColor.CanUseLogScale;

    public AxisChoice? SelectedXAxisChoice
    {
        get => AxisChoices.FirstOrDefault(choice => choice.Name == XAxis.ChannelName);
        set
        {
            if (value is null || XAxis.ChannelName == value.Name)
                return;

            set_editor_axis_channel(is_x_axis: true, value.Name);
            OnPropertyChanged();
            refresh_axis_menu_state();
        }
    }

    public AxisChoice? SelectedYAxisChoice
    {
        get => AxisChoices.FirstOrDefault(choice => choice.Name == YAxis.ChannelName);
        set
        {
            if (value is null || YAxis.ChannelName == value.Name)
                return;

            set_editor_axis_channel(is_x_axis: false, value.Name);
            OnPropertyChanged();
            refresh_axis_menu_state();
        }
    }

    public string StatusText
    {
        get => status_text;
        set => SetField(ref status_text, value);
    }

    public string PythonEngineStatusText
    {
        get => python_engine_status_text;
        private set => SetField(ref python_engine_status_text, value);
    }

    public bool IsPythonEngineProgressVisible
    {
        get => is_python_engine_progress_visible;
        private set => SetField(ref is_python_engine_progress_visible, value);
    }

    public bool IsPythonEngineProgressIndeterminate
    {
        get => is_python_engine_progress_indeterminate;
        private set => SetField(ref is_python_engine_progress_indeterminate, value);
    }

    public double PythonEngineProgress
    {
        get => python_engine_progress;
        private set => SetField(ref python_engine_progress, value);
    }

    public MainWindowViewState ViewState
    {
        get => view_state;
        private set => set_view_state(value);
    }

    public bool IsPageEditorMode
    {
        get => ViewState == MainWindowViewState.Layout;
        set
        {
            if (value)
            {
                SelectedIntegrationJob = null;
                set_view_state(MainWindowViewState.Layout, "Page editor: drag gate definitions or sample populations onto the canvas");
            }
            else if (IsPageEditorMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
        }
    }

    public bool IsIntegrationJobMode => ViewState == MainWindowViewState.Platform;
    public bool IsSpilloverCompensationMode
    {
        get => ViewState == MainWindowViewState.SpilloverCompensation;
        private set
        {
            if (value)
            {
                SelectedIntegrationJob = null;
                set_view_state(MainWindowViewState.SpilloverCompensation, "Spillover compensation");
            }
            else if (IsSpilloverCompensationMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
        }
    }

    public bool IsWorkspaceMetadataMode
    {
        get => ViewState == MainWindowViewState.Metadata;
        private set
        {
            if (value)
            {
                SelectedIntegrationJob = null;
                set_view_state(MainWindowViewState.Metadata, "Workspace sample metadata");
            }
            else if (IsWorkspaceMetadataMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
        }
    }

    public bool IsGroupMetadataMode
    {
        get => ViewState == MainWindowViewState.GroupMetadata;
        private set
        {
            if (value)
            {
                SelectedIntegrationJob = null;
                set_view_state(MainWindowViewState.GroupMetadata, "Grouping metadata");
            }
            else if (IsGroupMetadataMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
        }
    }

    public bool IsPythonScriptEditorMode
    {
        get => ViewState == MainWindowViewState.Code;
        private set
        {
            if (value)
            {
                SelectedIntegrationJob = null;
                set_view_state(MainWindowViewState.Code, "Python script editor");
            }
            else if (IsPythonScriptEditorMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
        }
    }

    public bool IsDefaultAnalysisMode => ViewState == MainWindowViewState.Analysis;
    public bool IsPlotPropertiesMode => IsDefaultAnalysisMode;

    private bool set_view_state(MainWindowViewState value, string? status_text = null)
    {
        if (view_state == value)
        {
            if (!string.IsNullOrWhiteSpace(status_text))
                StatusText = status_text;
            return false;
        }

        view_state = value;
        OnPropertyChanged(nameof(ViewState));
        OnPropertyChanged(nameof(IsDefaultAnalysisMode));
        OnPropertyChanged(nameof(IsPageEditorMode));
        OnPropertyChanged(nameof(IsPythonScriptEditorMode));
        OnPropertyChanged(nameof(IsWorkspaceMetadataMode));
        OnPropertyChanged(nameof(IsIntegrationJobMode));
        OnPropertyChanged(nameof(IsGroupMetadataMode));
        OnPropertyChanged(nameof(IsSpilloverCompensationMode));
        OnPropertyChanged(nameof(IsSpectralUnmixingMode));
        OnPropertyChanged(nameof(IsPlotPropertiesMode));
        if (!string.IsNullOrWhiteSpace(status_text))
            StatusText = status_text;
        raise_command_states();
        return true;
    }

    public async Task ShowAnalysisModeAsync()
    {
        if (!await TryLeavePythonScriptEditorAsync())
            return;

        SelectedIntegrationJob = null;
        set_view_state(MainWindowViewState.Analysis, "Analysis view");
    }

    public async Task ShowLayoutModeAsync()
    {
        if (!await TryLeavePythonScriptEditorAsync())
            return;

        SelectedIntegrationJob = null;
        set_view_state(MainWindowViewState.Layout, "Page editor: drag gate definitions or sample populations onto the canvas");
    }
    public int LayoutRefreshRevision
    {
        get => layout_refresh_revision;
        private set => SetField(ref layout_refresh_revision, value);
    }

    private void RefreshLayoutCanvas() => LayoutRefreshRevision++;

    public string PythonScriptText
    {
        get => python_script_text;
        set
        {
            if (!SetField(ref python_script_text, value ?? ""))
                return;
            if (!syncing_python_script)
                IsPythonScriptDirty = true;
        }
    }

    public string PythonScriptName
    {
        get => python_script_name;
        set
        {
            if (!SetField(ref python_script_name, value ?? ""))
                return;
            OnPropertyChanged(nameof(PythonScriptFileName));
            if (!syncing_python_script)
                IsPythonScriptDirty = true;
        }
    }

    public string PythonScriptFileName => editing_python_script is null
        ? ""
        : PythonScriptRepository.PreviewFileName(editing_python_script.Kind, PythonScriptName);

    public bool HasEditingPythonScript => editing_python_script is not null;

    public ObservableCollection<PythonLogTask> PythonLogTasks { get; } = new();

    public PythonLogTask? SelectedPythonLogTask
    {
        get => selected_python_log_task;
        set => SetField(ref selected_python_log_task, value);
    }

    public bool ShowPythonInfoLogs
    {
        get => show_python_info_logs;
        set => SetField(ref show_python_info_logs, value);
    }

    public bool ShowPythonWarningLogs
    {
        get => show_python_warning_logs;
        set => SetField(ref show_python_warning_logs, value);
    }

    public bool ShowPythonErrorLogs
    {
        get => show_python_error_logs;
        set => SetField(ref show_python_error_logs, value);
    }

    public bool ShowPythonFatalLogs
    {
        get => show_python_fatal_logs;
        set => SetField(ref show_python_fatal_logs, value);
    }

    public bool IsPythonScriptDirty
    {
        get => is_python_script_dirty;
        private set => SetField(ref is_python_script_dirty, value);
    }

    public string PythonScriptOutput
    {
        get => python_script_output;
        private set => SetField(ref python_script_output, value);
    }

    public bool IsPythonScriptRunning
    {
        get => is_python_script_running;
        private set
        {
            if (!SetField(ref is_python_script_running, value))
                return;
            if (RunPythonScriptCommand is RelayCommand command)
                command.RaiseCanExecuteChanged();
        }
    }

    public bool CanSavePythonScript => editing_python_script is not null;

    public PagePlotElement? SelectedPageElement
    {
        get => selected_page_element;
        set
        {
            unsubscribe_selected_page_menu_element();
            if (!SetField(ref selected_page_element, value))
            {
                subscribe_selected_page_menu_element();
                return;
            }
            subscribe_selected_page_menu_element();
            refresh_selected_page_axis_choices();
            OnPropertyChanged(nameof(HasSelectedPageElement));
            OnPropertyChanged(nameof(CanEditSelectedPageTitle));
            OnPropertyChanged(nameof(CanEditSelectedPageWidth));
            OnPropertyChanged(nameof(CanEditSelectedPageHeight));
            OnPropertyChanged(nameof(CanEditSelectedPagePlotMode));
            OnPropertyChanged(nameof(CanEditSelectedPageFlowOptions));
            OnPropertyChanged(nameof(CanEditSelectedPageGridTickOptions));
            OnPropertyChanged(nameof(CanEditSelectedPageAxes));
            OnPropertyChanged(nameof(SelectedPageXAxisChoice));
            OnPropertyChanged(nameof(SelectedPageYAxisChoice));
            OnPropertyChanged(nameof(SelectedPageDotColorChoice));
            OnPropertyChanged(nameof(SelectedPageDotColorMap));
            OnPropertyChanged(nameof(SelectedPageDensityColorMap));
            OnPropertyChanged(nameof(CanUseSelectedPageDotColorLogScale));
            refresh_selected_page_menu_state();
            if (DeletePageElementCommand is RelayCommand relay)
                relay.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedPageElement => SelectedPageElement is not null;
    public bool CanEditSelectedPageTitle => selected_page_element is { ElementKind: PageElementKind.FlowPlot or PageElementKind.PlatformPlot };
    public bool CanEditSelectedPageWidth => selected_page_element is not null;
    public bool CanEditSelectedPageHeight => selected_page_element is { HasFixedHeight: false };
    public bool CanEditSelectedPagePlotMode => selected_page_element?.ElementKind == PageElementKind.FlowPlot;

    public double ExportBitmapDpi
    {
        get => export_bitmap_dpi;
        set => SetField(ref export_bitmap_dpi, Math.Clamp(Math.Round(double.IsFinite(value) ? value : 300), 72, 1200), nameof(ExportBitmapDpi));
    }

    public bool ExportBitmapApplyRasterizationResolution
    {
        get => export_bitmap_apply_rasterization_resolution;
        set => SetField(ref export_bitmap_apply_rasterization_resolution, value, nameof(ExportBitmapApplyRasterizationResolution));
    }

    public bool ExportBitmapTransparentBackground
    {
        get => export_bitmap_transparent_background;
        set => SetField(ref export_bitmap_transparent_background, value, nameof(ExportBitmapTransparentBackground));
    }
    public bool CanEditSelectedPageFlowOptions => selected_page_element?.ElementKind == PageElementKind.FlowPlot;
    public bool CanEditSelectedPageGridTickOptions => selected_page_element?.ElementKind is PageElementKind.FlowPlot or PageElementKind.PlatformPlot;
    public bool CanEditSelectedPageAxes => selected_page_element?.ElementKind == PageElementKind.FlowPlot;

    public PageLayout? SelectedPageLayout
    {
        get => selected_page_layout;
        set
        {
            if (!SetField(ref selected_page_layout, value))
                return;
            SelectedPageElement = selected_page_layout?.Elements.LastOrDefault();
            OnPropertyChanged(nameof(PageElements));
            if (RenameLayoutCommand is RelayCommand rename_layout)
                rename_layout.RaiseCanExecuteChanged();
            if (RefreshSelectedLayoutCommand is RelayCommand refresh_layout)
                refresh_layout.RaiseCanExecuteChanged();
            if (DeleteSelectedCommand is RelayCommand delete_selected)
                delete_selected.RaiseCanExecuteChanged();
        }
    }

    public PythonScriptDefinition? SelectedIntegrationMacro
    {
        get => selected_integration_macro;
        set
        {
            if (!SetField(ref selected_integration_macro, value))
                return;
            raise_command_states();
        }
    }

    public AxisChoice? SelectedPageXAxisChoice
    {
        get => selected_page_element is null ? null : SelectedPageAxisChoices.FirstOrDefault(choice => choice.Name == selected_page_element.XAxis.ChannelName);
        set
        {
            if (selected_page_element is null || value is null || selected_page_element.XAxis.ChannelName == value.Name)
                return;
            selected_page_element.XAxis.ChannelName = value.Name;
            apply_page_axis_channel_defaults(selected_page_element.XAxis);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageElement));
            refresh_axis_menu_state();
        }
    }

    public AxisChoice? SelectedPageYAxisChoice
    {
        get => selected_page_element is null ? null : SelectedPageAxisChoices.FirstOrDefault(choice => choice.Name == selected_page_element.YAxis.ChannelName);
        set
        {
            if (selected_page_element is null || value is null || selected_page_element.YAxis.ChannelName == value.Name)
                return;
            selected_page_element.YAxis.ChannelName = value.Name;
            apply_page_axis_channel_defaults(selected_page_element.YAxis);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageElement));
            refresh_axis_menu_state();
        }
    }

    public AxisChoice? SelectedPageDotColorChoice
    {
        get => selected_page_element is null ? null : SelectedPageColorChoices.FirstOrDefault(choice => choice.Name == selected_page_element.DotColor.ChannelName);
        set
        {
            if (selected_page_element is null || value is null || selected_page_element.DotColor.ChannelName == value.Name)
                return;
            selected_page_element.DotColor.ChannelName = value.Name;
            refresh_dot_color_range(selected_page_element.DotColor, selected_page_element.Group, selected_page_element.Sample, selected_page_element.Population, reset_selection: true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageElement));
            OnPropertyChanged(nameof(CanUseSelectedPageDotColorLogScale));
            refresh_axis_menu_state();
        }
    }

    public PlotColorMap? SelectedPageDotColorMap
    {
        get => selected_page_element is null ? null : PlotColorMaps.Get(selected_page_element.DotColor.Palette);
        set
        {
            if (selected_page_element is null || value is null || selected_page_element.DotColor.Palette == value.Palette)
                return;

            selected_page_element.DotColor.Palette = value.Palette;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageElement));
        }
    }

    public PlotColorMap? SelectedPageDensityColorMap
    {
        get => selected_page_element is null ? null : PlotColorMaps.Get(selected_page_element.DensityPalette);
        set
        {
            if (selected_page_element is null || value is null || selected_page_element.DensityPalette == value.Palette)
                return;
            selected_page_element.DensityPalette = value.Palette;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageElement));
            refresh_selected_page_menu_state();
        }
    }

    public bool CanUseSelectedPageDotColorLogScale => selected_page_element?.DotColor.CanUseLogScale == true;

    public void AddFiles(IEnumerable<string> file_paths)
    {
        var reader = new FcsReader();
        var samples = file_paths.Select(file_path => reader.Read(file_path)).ToArray();
        var groups = AddSamples(samples);
        RecalculateImportedGroups(groups);
        FinishSampleImport(samples.Length);
    }

    public IReadOnlyList<FlowGroup> AddSamples(IEnumerable<FlowSample> samples)
    {
        var groups_to_recalculate = new HashSet<FlowGroup>();
        FlowGroup? last_group = null;
        FlowSample? last_sample = null;
        int loaded_count = 0;
        foreach (var sample in samples)
        {
            var (group, was_empty) = add_sample_to_compatible_group(sample, recalculate: false);
            if (was_empty)
                groups_pending_root_view_initialization.Add(group);
            groups_to_recalculate.Add(group);
            last_group = group;
            last_sample = sample;
            loaded_count++;
        }

        return finish_adding_samples(groups_to_recalculate, last_group, last_sample, loaded_count);
    }

    public IReadOnlyList<FlowGroup> RecalculateImportedGroups(IEnumerable<FlowGroup> groups)
    {
        var recalculated_groups = groups.Distinct().ToArray();
        foreach (var group in recalculated_groups)
        {
            group.RecalculateSamples();
            groups_pending_root_view_initialization.Remove(group);
        }
        return recalculated_groups;
    }

    public void FinishSampleImport(int loaded_count)
    {
        refresh_workspace_sample_metadata();
        refresh_project_tree();
        if (loaded_count > 0 && selected_group is not null)
            apply_axes_from_group_root_view(selected_group);
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = loaded_count == 0 ? StatusText : $"Loaded {loaded_count} FCS sample(s)";
        raise_command_states();
    }

    private IReadOnlyList<FlowGroup> finish_adding_samples(
        HashSet<FlowGroup> groups_to_recalculate,
        FlowGroup? last_group,
        FlowSample? last_sample,
        int loaded_count)
    {
        if (last_group is not null)
            SelectedGroup = last_group;
        if (last_sample is not null)
            SelectedSample = last_sample;
        return groups_to_recalculate.ToArray();
    }

    public void AddFilesToGroup(IEnumerable<string> file_paths, FlowGroup target_group)
    {
        var reader = new FcsReader();
        var samples = file_paths.Select(file_path => reader.Read(file_path)).ToArray();
        var groups = AddSamplesToGroup(samples, target_group);
        RecalculateImportedGroups(groups);
        FinishSampleImport(samples.Length);
    }

    public IReadOnlyList<FlowGroup> AddSamplesToGroup(IEnumerable<FlowSample> samples, FlowGroup target_group)
    {
        var fallback_groups = new List<FlowGroup>();
        var groups_to_recalculate = new HashSet<FlowGroup>();
        FlowGroup? last_group = null;
        FlowSample? last_sample = null;
        int loaded_count = 0;
        foreach (var sample in samples)
        {
            var group = target_group.CanAccept(sample)
                ? target_group
                : fallback_groups.FirstOrDefault(item => item.CanAccept(sample));
            if (group is null)
            {
                group = new FlowGroup { Name = $"Group {Workspace.Groups.Count + 1}" };
                Workspace.Groups.Add(group);
                fallback_groups.Add(group);
            }

            bool was_empty = group.Samples.Count == 0;
            group.AddSample(sample, recalculate: false);
            if (was_empty)
                groups_pending_root_view_initialization.Add(group);
            groups_to_recalculate.Add(group);
            last_group = group;
            last_sample = sample;
            loaded_count++;
        }

        return finish_adding_samples(groups_to_recalculate, last_group, last_sample, loaded_count);
    }

    public async Task<int> AddControlSamplesAsync(IEnumerable<ControlSample> samples, FlowGroup target_group)
    {
        if (target_group.Channels.Count == 0)
        {
            if (RequestMessageAsync is not null)
                await RequestMessageAsync("Add control samples failed", "Add regular samples to the grouping before adding control samples.");
            return 0;
        }

        int loaded_count = 0;
        foreach (var sample in samples)
        {
            var required_names = target_group.Channels.Select(channel => channel.Name).ToArray();
            var sample_names = sample.Channels.Select(channel => channel.Name).ToArray();
            var missing = required_names.Where(name => !sample_names.Contains(name, StringComparer.Ordinal)).ToArray();
            if (missing.Length > 0)
            {
                string message = $"Fail to add control samples to the grouping {target_group.Name}. Such channels expected but not exist in the sample to be added: {string.Join("， ", missing)}";
                if (RequestMessageAsync is not null)
                    await RequestMessageAsync("Add control sample failed", message);
                StatusText = message;
                continue;
            }

            if (!sample_names.SequenceEqual(required_names, StringComparer.Ordinal))
                sample.ProjectChannels(target_group.Channels);

            target_group.ControlSamples.Add(sample);
            loaded_count++;
        }

        if (loaded_count == 0)
            return 0;

        refresh_spillover_choices();
        refresh_project_tree();
        SelectedGroup = target_group;
        SelectedControlSample = target_group.ControlSamples.LastOrDefault();
        StatusText = $"Loaded {loaded_count} control sample(s)";
        raise_command_states();
        return loaded_count;
    }

    public void AddRecentFilePaths(IEnumerable<string> file_paths)
    {
        foreach (string file_path in file_paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string normalized = normalize_recent_file_path(file_path);
            for (int index = Workspace.RecentFilePaths.Count - 1; index >= 0; index--)
            {
                if (string.Equals(Workspace.RecentFilePaths[index], normalized, StringComparison.OrdinalIgnoreCase))
                    Workspace.RecentFilePaths.RemoveAt(index);
            }

            Workspace.RecentFilePaths.Insert(0, normalized);
        }

        while (Workspace.RecentFilePaths.Count > 20)
            Workspace.RecentFilePaths.RemoveAt(Workspace.RecentFilePaths.Count - 1);

        RecentFileStore.Save(Workspace.RecentFilePaths);
        OnPropertyChanged(nameof(RecentFilePaths));
    }

    public void ClearRecentFilePaths()
    {
        Workspace.RecentFilePaths.Clear();
        RecentFileStore.Save(Workspace.RecentFilePaths);
        OnPropertyChanged(nameof(RecentFilePaths));
    }

    public void SaveWorkspace(string file_path)
    {
        capture_project_expansion_state();
        new WorkspaceBinarySerializer().Save(Workspace, file_path);
        StatusText = $"Saved workspace: {System.IO.Path.GetFileName(file_path)}";
    }

    public void LoadWorkspace(string file_path)
    {
        var loaded = new WorkspaceBinarySerializer().Load(file_path);
        LoadWorkspace(loaded, file_path);
    }

    public void LoadWorkspace(FlowWorkspace loaded, string file_path)
    {
        cancel_pending_workspace_work();
        Python.PythonExtensionRuntime.DisposeWorkspaceStorage(Workspace);
        ClearPythonLogs();
        Workspace.Name = loaded.Name;
        Workspace.Groups.Clear();
        Workspace.PageLayouts.Clear();
        Workspace.IntegrationJobs.Clear();
        Workspace.MetadataColumns.Clear();
        foreach (var group in loaded.Groups)
            Workspace.Groups.Add(group);
        foreach (var layout in loaded.PageLayouts)
            Workspace.PageLayouts.Add(layout);
        foreach (var job in loaded.IntegrationJobs)
            Workspace.IntegrationJobs.Add(job);
        foreach (var column in loaded.MetadataColumns)
            Workspace.MetadataColumns[column.Key] = column.Value;
        ensure_default_layout();
        groups_pending_root_view_initialization.Clear();

        project_expansion_state.Clear();
        SelectedNode = null;
        SelectedGroup = Workspace.Groups.FirstOrDefault();
        SelectedSample = selected_group?.Samples.FirstOrDefault();
        SelectedGate = null;
        SelectedPopulation = null;
        SelectedCompensation = null;
        SelectedIntegrationJob = null;
        SelectedPageLayout = Workspace.PageLayouts.FirstOrDefault();
        SelectedPageElement = selected_page_layout?.Elements.LastOrDefault();
        next_gate_number = Math.Max(1, count_gates(Workspace.Groups.SelectMany(group => group.Gates)) + 1);
        if (selected_group is not null)
            apply_axes_from_group_root_view(selected_group);
        refresh_workspace_sample_metadata();
        seed_loaded_workspace_expansion_state();
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Loaded workspace: {System.IO.Path.GetFileName(file_path)}";
        raise_command_states();
        schedule_loaded_workspace_recalculation(Workspace.Groups.ToArray(), System.IO.Path.GetFileName(file_path));
    }

    public void CloseWorkspace()
    {
        cancel_pending_workspace_work();
        Python.PythonExtensionRuntime.DisposeWorkspaceStorage(Workspace);
        ClearPythonLogs();
        Workspace.Name = "Untitled Workspace";
        Workspace.Groups.Clear();
        Workspace.PageLayouts.Clear();
        Workspace.IntegrationJobs.Clear();
        Workspace.MetadataColumns.Clear();
        ensure_default_layout();
        groups_pending_root_view_initialization.Clear();
        project_expansion_state.Clear();
        SelectedNode = null;
        SelectedGroup = null;
        SelectedSample = null;
        SelectedGate = null;
        SelectedPopulation = null;
        SelectedCompensation = null;
        SelectedIntegrationJob = null;
        SelectedPageLayout = Workspace.PageLayouts.FirstOrDefault();
        SelectedPageElement = null;
        next_gate_number = 1;
        refresh_workspace_sample_metadata();
        refresh_project_tree();
        refresh_axis_choices();
        refresh_selected_page_axis_choices();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = "Closed workspace";
        raise_command_states();
    }

    private void cancel_pending_workspace_work()
    {
        plot_transform_preparation_cancellation?.Cancel();
        plot_transform_preparation_cancellation = null;
        gate_recalculation_cancellation?.Cancel();
        gate_recalculation_cancellation = null;
        compensation_application_cancellation?.Cancel();
        compensation_application_cancellation = null;
        IsPlotTransformPreparing = false;
        IsGateRecalculating = false;
        IsCompensationApplying = false;
    }

    private void ClearPythonLogs()
    {
        python_log_tasks_by_key.Clear();
        PythonLogTasks.Clear();
        SelectedPythonLogTask = null;
    }

    public void RecalculateSelectedGroup()
    {
        selected_group?.RecalculateSamples();
        refresh_selected_population_reference();
        refresh_project_tree();
        OnPropertyChanged(nameof(PlotGate));
        OnPropertyChanged(nameof(PlotPopulation));
        if (selected_group is not null)
            reapply_current_view_after_group_recalculation([selected_group]);
        refresh_plot_gates();
        refresh_selected_statistics();
    }

    public void RecalculateEditedGate(GateDefinition? gate)
    {
        if (selected_group is null || gate is null)
        {
            RecalculateSelectedGroup();
            return;
        }

        if (has_external_boolean_dependency(selected_group, gate) || !selected_group.RecalculateGateSubtree(gate))
            selected_group.RecalculateSamples();

        refresh_selected_population_reference();
        refresh_project_tree();
        OnPropertyChanged(nameof(PlotGate));
        OnPropertyChanged(nameof(PlotPopulation));
        refresh_plot_gates();
        refresh_selected_statistics();
    }

    private void ScheduleEditedGateRecalculation(GateDefinition? gate)
    {
        gate_recalculation_cancellation?.Cancel();

        if (selected_group is null || gate is null)
        {
            RecalculateSelectedGroup();
            return;
        }

        var group = selected_group;
        var cancellation = new CancellationTokenSource();
        gate_recalculation_cancellation = cancellation;
        IsGateRecalculating = true;
        StatusText = "Recalculating gate ...";

        _ = recalculate_edited_gate_async(group, gate, cancellation);
    }

    private async Task recalculate_edited_gate_async(FlowGroup group, GateDefinition gate, CancellationTokenSource cancellation)
    {
        bool succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (has_external_boolean_dependency(group, gate) || !group.RecalculateGateSubtree(gate, cancellation.Token))
                    group.RecalculateSamples(force_compensation: false, cancellation.Token);
            }, cancellation.Token);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(gate_recalculation_cancellation, cancellation))
                    return;
                StatusText = $"Gate recalculation failed: {exception.Message}";
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(gate_recalculation_cancellation, cancellation))
                {
                    cancellation.Dispose();
                    return;
                }

                gate_recalculation_cancellation.Dispose();
                gate_recalculation_cancellation = null;
                IsGateRecalculating = false;
                refresh_selected_population_reference();
                refresh_project_tree();
                OnPropertyChanged(nameof(PlotGate));
                OnPropertyChanged(nameof(PlotPopulation));
                reapply_current_view_after_group_recalculation([group]);
                refresh_plot_gates();
                refresh_selected_statistics();
                if (succeeded)
                    StatusText = "Gate recalculation complete";
            });
        }
    }

    private void refresh_selected_population_reference()
    {
        if (selected_population is null || selected_sample is null)
            return;

        var refreshed = find_population(selected_sample.Populations, selected_population.Gate, selected_population.Region);
        if (!ReferenceEquals(refreshed, selected_population))
            SelectedPopulation = refreshed;
    }

    public void RunPythonExtension(string code, string task_key = "Interactive code", string task_name = "Interactive code")
    {
        var metadata_snapshot = snapshot_workspace_metadata();
        var context = new PythonWorkspaceContext(Workspace);
        context.execute(code, task_key, task_name);
        void refresh()
        {
            // The refresh calculation should happen during script execution.
            // no need to recalculate everything again.
            // This code is kept here for debugging purposes.
            // foreach (var group in Workspace.Groups)
            //     group.RecalculateSamples();

            refresh_selected_population_reference();
            bool metadata_changed = !metadata_snapshot.SequenceEqual(snapshot_workspace_metadata());
            SelectedGroup ??= Workspace.Groups.FirstOrDefault();
            SelectedSample ??= selected_group?.Samples.FirstOrDefault();
            refresh_project_tree();
            refresh_selection_sidebars();
            refresh_workspace_sample_metadata();
            if (metadata_changed)
                invalidate_integration_jobs_from_metadata();
            OnPropertyChanged(nameof(PlotGate));
            OnPropertyChanged(nameof(PlotPopulation));
            refresh_plot_gates();
            refresh_selected_statistics();
            StatusText = "Python extension completed";
            raise_command_states();
        }

        if (Dispatcher.UIThread.CheckAccess())
            refresh();
        else
            Dispatcher.UIThread.InvokeAsync(refresh).GetAwaiter().GetResult();
    }

    private string[] snapshot_workspace_metadata() =>
        Workspace.Groups
            .SelectMany(group => group.Samples.Select(sample => new
            {
                sample.Id,
                Metadata = sample.Metadata
                    .Where(item => item.Key is not ("Group" or "Sample"))
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}")
                    .ToArray()
            }))
            .OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{string.Join("\u001f", item.Metadata)}")
            .ToArray();

    public void ReloadScriptRepositories()
    {
        MacroScripts.Clear();
        foreach (var macro in PythonScriptRepository.LoadMacros())
            MacroScripts.Add(macro);

        StatisticScripts.Clear();
        foreach (var statistic in PythonScriptRepository.LoadStatistics())
            StatisticScripts.Add(statistic);
    }

    public async Task CreateMacroAsync()
    {
        string? name = RequestTextInputAsync is null
            ? $"Macro {MacroScripts.Count + 1}"
            : await RequestTextInputAsync("Create macro", "Macro name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var macro = PythonScriptRepository.NewMacro(name.Trim());
        OpenPythonScriptEditor(macro, dirty: true);
        StatusText = $"Created macro: {macro.Name}";
    }

    public async Task CreateStatisticScriptAsync()
    {
        string? name = RequestTextInputAsync is null
            ? $"Python statistic {StatisticScripts.Count + 1}"
            : await RequestTextInputAsync("Create Python statistic", "Statistic name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var statistic = PythonScriptRepository.NewStatistic(name.Trim());
        OpenPythonScriptEditor(statistic, dirty: true);
        StatusText = $"Created Python statistic script: {statistic.Name}";
    }

    public Task OpenPythonScriptEditorAsync(PythonScriptDefinition? script = null)
    {
        OpenPythonScriptEditor(script);
        return Task.CompletedTask;
    }

    public void ShowPythonScriptEditor()
    {
        if (editing_python_script is not null)
            SelectedPythonLogTask = python_log_task_for_script(editing_python_script);
        else
            SelectedPythonLogTask ??= python_log_task("code:interactive", "Interactive code");

        IsPythonScriptEditorMode = true;
    }

    public void OpenPythonScriptEditor(PythonScriptDefinition? script = null, bool dirty = false)
    {
        editing_python_script = script;
        syncing_python_script = true;
        if (script is not null)
        {
            python_script_name = script.Name;
            python_script_text = script.Source;
            OnPropertyChanged(nameof(PythonScriptName));
            OnPropertyChanged(nameof(PythonScriptText));
            OnPropertyChanged(nameof(PythonScriptFileName));
            OnPropertyChanged(nameof(HasEditingPythonScript));
            OnPropertyChanged(nameof(CanSavePythonScript));
            PythonScriptOutput = $"{script.Kind}: {script.Name}";
            SelectedPythonLogTask = python_log_task_for_script(script);
        }
        else
            SelectedPythonLogTask ??= python_log_task("code:interactive", "Interactive code");
        syncing_python_script = false;
        IsPythonScriptDirty = dirty;
        IsPythonScriptEditorMode = true;
    }

    public async Task ClosePythonScriptEditorAsync()
    {
        if (!await TryLeavePythonScriptEditorAsync())
            return;
        IsPythonScriptEditorMode = false;
        editing_python_script = null;
        OnPropertyChanged(nameof(HasEditingPythonScript));
        OnPropertyChanged(nameof(CanSavePythonScript));
        OnPropertyChanged(nameof(PythonScriptFileName));
        IsPythonScriptDirty = false;
        StatusText = "Analysis view";
    }

    public bool SavePythonScript()
    {
        if (editing_python_script is null)
            return true;

        editing_python_script.Name = PythonScriptName.Trim();
        editing_python_script.Source = PythonScriptText;
        string? validation_error = PythonScriptRepository.ValidateForSave(editing_python_script);
        if (!string.IsNullOrWhiteSpace(validation_error))
        {
            StatusText = validation_error;
            return false;
        }

        PythonScriptRepository.Save(editing_python_script);
        ReloadScriptRepositories();
        IsPythonScriptDirty = false;
        OnPropertyChanged(nameof(PythonScriptFileName));
        StatusText = $"Saved script: {PythonScriptFileName}";
        return true;
    }

    public async Task RunMacroAsync(PythonScriptDefinition macro)
    {
        if (macro.Kind != PythonScriptRepositoryKind.Macro)
            return;
        SelectedPythonLogTask = python_log_task_for_script(macro);
        await Task.Run(() => RunPythonExtension(macro.Source, python_log_task_key(macro), python_log_task_display_name(macro)));
        StatusText = $"Ran macro: {macro.Name}";
    }

    public void ApplyStatisticScript(PythonScriptDefinition script, string parameters_json = "[]")
    {
        if (selected_group is null || script.Kind != PythonScriptRepositoryKind.Statistic)
            return;

        Python.PythonExtensionRuntime.ValidateStatisticSource(script.Source, "entry");
        var definition = new StatisticDefinition();
        definition.SetPythonMethod(script.Source, "entry", script.Name, parameters_json);
        var definitions = selected_gate?.Statistics ?? selected_group.Statistics;
        definitions.Add(definition);
        selected_group.RecalculateSamples();
        refresh_selected_population_reference();
        refresh_project_tree();
        refresh_selected_statistics();
        StatusText = $"Added Python statistic: {script.Name}";
    }

    private async Task run_python_script_async()
    {
        if (IsPythonScriptRunning)
            return;

        IsPythonScriptRunning = true;
        PythonScriptOutput = "Running ...";
        var log_task = current_editor_python_log_task();
        string task_key = log_task.Key;
        string task_name = log_task.DisplayName;
        SelectedPythonLogTask = python_log_task(task_key, task_name);
        try
        {
            await Task.Run(() => RunPythonExtension(PythonScriptText, task_key, task_name));
            PythonScriptOutput = "Completed.";
        }
        catch (Exception exception)
        {
            PythonScriptOutput = exception.Message;
            StatusText = $"Python extension failed: {exception.Message}";
        }
        finally
        {
            IsPythonScriptRunning = false;
        }
    }

    public async Task<bool> TryLeavePythonScriptEditorAsync()
    {
        if (!IsPythonScriptEditorMode)
            return true;
        if (!IsPythonScriptDirty || editing_python_script is null)
            return true;

        var choice = RequestScriptSaveChoiceAsync is null
            ? ScriptSaveChoice.Save
            : await RequestScriptSaveChoiceAsync("Unsaved script changes", $"Save changes to '{PythonScriptName}'?");

        if (choice == ScriptSaveChoice.Cancel)
            return false;
        if (choice == ScriptSaveChoice.Discard)
            return true;
        return SavePythonScript();
    }

    public void BeginPythonLogRun(Python.PythonLogRunStarted run)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => BeginPythonLogRun(run));
            return;
        }

        var task = python_log_task(run.TaskKey, run.TaskName);
        task.AddRun(run.RunId, run.StartedAt);
        OnPropertyChanged(nameof(SelectedPythonLogTask));
    }

    public void AppendPythonLog(Python.PythonLogMessage message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendPythonLog(message));
            return;
        }

        var task = python_log_task(message.TaskKey, message.TaskKey);
        task.AddMessage(message);
        OnPropertyChanged(nameof(SelectedPythonLogTask));
    }

    private PythonLogTask python_log_task_for_script(PythonScriptDefinition script) =>
        python_log_task(python_log_task_key(script), python_log_task_display_name(script));

    private static string python_log_task_key(PythonScriptDefinition script)
    {
        return $"{script.Kind}: {script.Name}";
    }

    private static string python_log_task_display_name(PythonScriptDefinition script)
    {
        string name = string.IsNullOrWhiteSpace(script.Name) ? "Untitled" : script.Name.Trim();
        return python_log_task_display_name(script.Kind, name);
    }

    private static string python_log_task_display_name(PythonScriptRepositoryKind kind, string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
        return kind == PythonScriptRepositoryKind.Macro
            ? $"Macro: {name}"
            : $"Statistic: {name}";
    }

    private (string Key, string DisplayName) current_editor_python_log_task()
    {
        if (editing_python_script is null)
            return ("code:interactive", "Interactive code");

        string name = string.IsNullOrWhiteSpace(PythonScriptName)
            ? Path.GetFileNameWithoutExtension(PythonScriptRepository.PreviewFileName(editing_python_script.Kind, "Untitled"))
            : PythonScriptName.Trim();
        string key = $"{editing_python_script.Kind}: {name}";
        return (key, python_log_task_display_name(editing_python_script.Kind, name));
    }

    private PythonLogTask python_log_task(string key, string display_name)
    {
        if (python_log_tasks_by_key.TryGetValue(key, out var task))
        {
            task.DisplayName = string.IsNullOrWhiteSpace(display_name) ? task.DisplayName : display_name;
            return task;
        }

        task = new PythonLogTask(key, string.IsNullOrWhiteSpace(display_name) ? "Python task" : display_name);
        python_log_tasks_by_key[key] = task;
        PythonLogTasks.Add(task);
        return task;
    }

    public void UpdatePythonExecutionStatus(Python.PythonExecutionStatus status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdatePythonExecutionStatus(status));
            return;
        }

        PythonEngineStatusText = status.Description;
        IsPythonEngineProgressVisible = status.IsVisible;
        IsPythonEngineProgressIndeterminate = status.IsIndeterminate;
        PythonEngineProgress = status.Progress ?? 0;
    }

    public void SwapAxes()
    {
        (XAxis.ChannelName, YAxis.ChannelName) = (YAxis.ChannelName, XAxis.ChannelName);
        (XAxis.Minimum, YAxis.Minimum) = (YAxis.Minimum, XAxis.Minimum);
        (XAxis.Maximum, YAxis.Maximum) = (YAxis.Maximum, XAxis.Maximum);
        (XAxis.Scale, YAxis.Scale) = (YAxis.Scale.Clone(), XAxis.Scale.Clone());
        OnPropertyChanged(nameof(XAxis));
        OnPropertyChanged(nameof(YAxis));
        OnPropertyChanged(nameof(PlotGate));
        OnPropertyChanged(nameof(IsYAxisEnabled));
        sync_selected_gate_preferred_view();
        refresh_plot_gates();
        schedule_plot_transform_preparation();
    }

    private (FlowGroup Group, bool WasEmpty) add_sample_to_compatible_group(FlowSample sample, bool recalculate = true)
    {
        var group = Workspace.Groups.FirstOrDefault(item => item.CanAccept(sample));
        if (group is null)
        {
            group = new FlowGroup { Name = $"Group {Workspace.Groups.Count + 1}" };
            Workspace.Groups.Add(group);
        }

        bool was_empty = group.Samples.Count == 0;
        group.AddSample(sample, recalculate);
        if (recalculate)
        {
            if (was_empty)
                ensure_group_root_view_defaults(group);
            SelectedGroup = group;
            SelectedSample = sample;
        }

        return (group, was_empty);
    }

    private void create_group()
    {
        var group = new FlowGroup { Name = $"Group {Workspace.Groups.Count + 1}" };
        Workspace.Groups.Add(group);
        SelectedGroup = group;
        SelectedSample = null;
        SelectedGate = null;
        refresh_project_tree();
        refresh_selection_sidebars();
        raise_command_states();
    }

    private async Task create_layout_async()
    {
        if (!await TryLeavePythonScriptEditorAsync())
            return;

        var layout = new PageLayout { Name = next_layout_name() };
        Workspace.PageLayouts.Add(layout);
        SelectedPageLayout = layout;
        IsPageEditorMode = true;
        refresh_project_tree();
        raise_command_states();
    }

    private void refresh_after_embedding_changes()
    {
        refresh_axis_choices();
        refresh_selected_page_axis_choices();
        refresh_project_tree();
        refresh_selection_sidebars();
        OnPropertyChanged(nameof(PlotPopulation));
        raise_command_states();
    }

    private async Task rename_selected_group_async()
    {
        if (selected_group is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename group", selected_group.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_group.Name = name.Trim();
        sync_identity_metadata();
        rebuild_workspace_metadata_table();
        refresh_project_tree();
    }

    private async Task rename_workspace_async()
    {
        if (RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename workspace", Workspace.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        Workspace.Name = name.Trim();
        refresh_project_tree();
    }

    private async Task rename_selected_gate_async()
    {
        if (selected_gate is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename gate", selected_gate.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_gate.Name = name.Trim();
        if (selected_group is not null)
            refresh_population_embeddings(selected_group);
        refresh_project_tree();
        refresh_plot_gates();
        OnPropertyChanged(nameof(SelectedGateName));
    }

    private async Task rename_selected_layout_async()
    {
        if (selected_page_layout is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename layout", selected_page_layout.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_page_layout.Name = name.Trim();
        refresh_project_tree();
    }

    private bool can_rename_selected_node() =>
        selected_node?.Kind is ProjectNodeKind.Group
            or ProjectNodeKind.Workspace
            or ProjectNodeKind.Compensation
            or ProjectNodeKind.Layout
            or ProjectNodeKind.Platform
            or ProjectNodeKind.StatisticDefinition
            or ProjectNodeKind.StatisticValue
            or ProjectNodeKind.Embedding
            or ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.Population
            or ProjectNodeKind.ControlSample
            or ProjectNodeKind.Sample;

    private async Task rename_selected_node_async()
    {
        if (selected_node is null || RequestTextInputAsync is null)
            return;

        switch (selected_node.Kind)
        {
            case ProjectNodeKind.Group:
                await rename_selected_group_async();
                break;
            case ProjectNodeKind.Workspace:
                await rename_workspace_async();
                break;
            case ProjectNodeKind.Compensation:
                await rename_selected_compensation_async();
                break;
            case ProjectNodeKind.Layout:
                await rename_selected_layout_async();
                break;
            case ProjectNodeKind.Platform:
                await rename_selected_integration_job_async();
                break;
            case ProjectNodeKind.StatisticDefinition:
            case ProjectNodeKind.StatisticValue:
                await rename_selected_statistic_async();
                break;
            case ProjectNodeKind.Embedding:
                await rename_selected_embedding_async();
                break;
            case ProjectNodeKind.Gate:
                await rename_selected_gate_async();
                break;
            case ProjectNodeKind.GatePopulationSlot:
            case ProjectNodeKind.Population:
                await rename_selected_population_async();
                break;
            case ProjectNodeKind.Sample:
                await rename_selected_sample_async();
                break;
            case ProjectNodeKind.ControlSample:
                await rename_selected_control_sample_async();
                break;
        }
    }

    private async Task rename_selected_control_sample_async()
    {
        if (selected_control_sample is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename control sample", selected_control_sample.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_control_sample.Name = name.Trim();
        refresh_spillover_workspace();
        refresh_project_tree();
    }

    private async Task rename_selected_compensation_async()
    {
        if (selected_compensation is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename compensation matrix", selected_compensation.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_compensation.Name = name.Trim();
        refresh_project_tree();
    }

    private async Task rename_selected_statistic_async()
    {
        var statistic = selected_statistic_definition();
        if (statistic is null || RequestTextInputAsync is null)
            return;

        string current_name = statistic_name(statistic);
        string? name = await RequestTextInputAsync("Rename statistics", current_name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        statistic.DisplayName = name.Trim();
        refresh_project_tree();
        refresh_selected_statistics();
    }

    public void RefreshConfigurationAssumptions()
    {
        refresh_axis_choices();
        refresh_selected_page_axis_choices();
        refresh_selection_sidebars();
        OnPropertyChanged(nameof(SelectedXAxisChoice));
        OnPropertyChanged(nameof(SelectedYAxisChoice));
        OnPropertyChanged(nameof(IsEditorXAxisLinearScale));
        OnPropertyChanged(nameof(IsEditorYAxisLinearScale));
        foreach (var group in Workspace.Groups.Where(group => group.SpectralUnmixing.Rows.Count > 0)) group.SpectralUnmixing.IsStale = true;
        if (IsSpectralUnmixingMode) SpectralPanel.SetGroup(selected_group);
    }

    private async Task rename_selected_embedding_async()
    {
        if (selected_sample is null ||
            string.IsNullOrWhiteSpace(selected_node?.EmbeddingName) ||
            !selected_sample.Embeddings.TryGetValue(selected_node.EmbeddingName, out var embedding) ||
            RequestTextInputAsync is null)
            return;

        string old_name = selected_node.EmbeddingName;
        string? name = await RequestTextInputAsync("Rename embedding", old_name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        string new_name = name.Trim();
        if (string.Equals(old_name, new_name, StringComparison.Ordinal) || selected_sample.Embeddings.ContainsKey(new_name))
            return;

        selected_sample.Embeddings.Remove(old_name);
        selected_sample.Embeddings[new_name] = embedding;
        rename_embedding_references(selected_sample, old_name, new_name);
        refresh_after_embedding_changes();
        StatusText = $"Renamed embedding: {new_name}";
    }

    private void rename_embedding_references(FlowSample sample, string old_name, string new_name)
    {
        if (ReferenceEquals(selected_sample, sample))
        {
            if (XAxis.ChannelName == old_name)
                XAxis.ChannelName = new_name;
            if (YAxis.ChannelName == old_name)
                YAxis.ChannelName = new_name;
            if (DotColor.ChannelName == old_name)
                DotColor.ChannelName = new_name;
        }

        foreach (var layout in Workspace.PageLayouts)
        foreach (var element in layout.Elements.Where(element => ReferenceEquals(element.Sample, sample)))
        {
            if (element.XAxis.ChannelName == old_name)
                element.XAxis.ChannelName = new_name;
            if (element.YAxis.ChannelName == old_name)
                element.YAxis.ChannelName = new_name;
            if (element.DotColor.ChannelName == old_name)
                element.DotColor.ChannelName = new_name;
        }
    }

    private async Task rename_selected_sample_async()
    {
        if (selected_sample is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename sample", selected_sample.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        string old_name = selected_sample.Name;
        selected_sample.Name = name.Trim();
        if (selected_group is not null)
            rename_sample_preferred_views(selected_group, old_name, selected_sample.Name);
        sync_identity_metadata();
        rebuild_workspace_metadata_table();
        refresh_project_tree();
    }

    private async Task rename_selected_population_async()
    {
        if (selected_gate is null || RequestTextInputAsync is null)
            return;

        var region = selected_population?.Region ??
            (selected_node?.Kind == ProjectNodeKind.GatePopulationSlot ? selected_node.PopulationRegion : PopulationRegion.Primary);
        if (region == PopulationRegion.Primary)
        {
            await rename_selected_gate_async();
            return;
        }

        string? name = await RequestTextInputAsync("Rename population", selected_gate.PopulationName(region));
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_gate.PopulationNames[region] = name.Trim();
        if (selected_group is not null)
            refresh_population_embeddings(selected_group);
        refresh_project_tree();
        refresh_plot_gates();
    }

    private static void refresh_population_embeddings(FlowGroup group)
    {
        foreach (var sample in group.Samples)
            sample.RefreshPopulationEmbedding();
    }

    private void ensure_default_layout()
    {
        if (Workspace.PageLayouts.Count == 0)
            Workspace.PageLayouts.Add(new PageLayout { Name = next_layout_name() });
        selected_page_layout ??= Workspace.PageLayouts[0];
        OnPropertyChanged(nameof(PageElements));
    }

    private string next_layout_name()
    {
        int index = Workspace.PageLayouts.Count + 1;
        while (Workspace.PageLayouts.Any(layout => layout.Name == $"Layout {index}"))
            index++;
        return $"Layout {index}";
    }

    public void AddArtificialSample(FlowGroup group, FlowSample sample, string status_text)
    {
        bool was_empty = group.Samples.Count == 0;
        group.AddSample(sample);
        group.RecalculateSamples();
        if (was_empty)
            ensure_group_root_view_defaults(group);
        SelectedGroup = group;
        SelectedSample = sample;
        SelectedPopulation = null;
        refresh_workspace_sample_metadata();
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = status_text;
        raise_command_states();
    }

    public void AddArtificialSamples(FlowGroup group, IReadOnlyList<FlowSample> samples, string status_text)
    {
        bool was_empty = group.Samples.Count == 0;
        foreach (var sample in samples)
            group.AddSample(sample, recalculate: false);
        group.RecalculateSamples();
        if (was_empty)
            ensure_group_root_view_defaults(group);
        SelectedGroup = group;
        SelectedSample = samples.LastOrDefault();
        SelectedPopulation = null;
        refresh_workspace_sample_metadata();
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = status_text;
        raise_command_states();
    }

    private void delete_selected()
    {
        var replacement_key = selected_node is null ? null : replacement_key_after_deleted_node(selected_node);
        var group_to_recalculate = selected_group;
        if (selected_node?.Kind == ProjectNodeKind.Sample && selected_group is not null && selected_sample is not null)
        {
            remove_sample_preferred_views(selected_group, selected_sample.Name);
            selected_group.Samples.Remove(selected_sample);
        }
        else if (selected_node?.Kind == ProjectNodeKind.ControlSample && selected_group is not null && selected_control_sample is not null)
        {
            selected_group.ControlSamples.Remove(selected_control_sample);
            for (int index = selected_group.SpilloverCompensation.Rows.Count - 1; index >= 0; index--)
                if (selected_group.SpilloverCompensation.Rows[index].ControlSampleId == selected_control_sample.Id)
                    selected_group.SpilloverCompensation.Rows.RemoveAt(index);
            group_to_recalculate = null;
            refresh_spillover_workspace();
        }
        else if (selected_node?.Kind is ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot && selected_group is not null && selected_gate is not null)
            remove_gate(selected_group, selected_gate);
        else if (selected_node?.Kind == ProjectNodeKind.Population && selected_group is not null && selected_gate is not null)
            remove_gate(selected_group, selected_gate);
        else if (selected_node?.Kind == ProjectNodeKind.Group && selected_group is not null)
        {
            Workspace.Groups.Remove(selected_group);
            group_to_recalculate = null;
        }
        else if (selected_node?.Kind == ProjectNodeKind.Platform && selected_integration_job is not null)
        {
            Workspace.IntegrationJobs.Remove(selected_integration_job);
            group_to_recalculate = null;
        }
        else if (selected_node?.Kind == ProjectNodeKind.Layout && selected_page_layout is not null)
        {
            Workspace.PageLayouts.Remove(selected_page_layout);
            ensure_default_layout();
            SelectedPageLayout = Workspace.PageLayouts.FirstOrDefault();
            group_to_recalculate = null;
        }
        else if (selected_node?.Kind == ProjectNodeKind.Compensation && selected_group is not null && selected_compensation is not null)
        {
            bool was_applied = ReferenceEquals(selected_group.AppliedCompensation, selected_compensation);
            selected_group.CompensationCandidates.Remove(selected_compensation);
            if (was_applied && selected_group.CompensationCandidates.FirstOrDefault() is { } replacement)
                selected_group.SetAppliedCompensation(replacement, manual: true, recalculate: false);
            group_to_recalculate = was_applied ? selected_group : null;
        }
        else if (selected_node?.Kind == ProjectNodeKind.Embedding &&
                 selected_sample is not null &&
                 !string.IsNullOrWhiteSpace(selected_node.EmbeddingName) &&
                 !string.Equals(selected_node.EmbeddingName, "Populations", StringComparison.Ordinal))
        {
            string embedding_name = selected_node.EmbeddingName;
            selected_sample.Embeddings.Remove(embedding_name);
            clear_removed_embedding_references(selected_sample, embedding_name);
            refresh_after_embedding_changes();
            select_replacement_node(replacement_key);
            StatusText = $"Deleted embedding: {embedding_name}";
            return;
        }
        else if (selected_node?.Kind is ProjectNodeKind.StatisticDefinition or ProjectNodeKind.StatisticValue &&
                 selected_group is not null &&
                 selected_statistic_definition() is { } statistic)
        {
            if (selected_node.Gate is not null)
                selected_node.Gate.Statistics.Remove(statistic);
            else
                selected_group.Statistics.Remove(statistic);
        }
        else
        {
            return;
        }

        group_to_recalculate?.RecalculateSamples();
        SelectedNode = null;
        refresh_project_tree();
        select_replacement_node(replacement_key);
        refresh_axis_choices();
        refresh_selected_page_axis_choices();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
    }

    private bool can_delete_selected_node() =>
        selected_node?.Kind switch
        {
            ProjectNodeKind.Sample => selected_group is not null && selected_sample is not null,
            ProjectNodeKind.ControlSample => selected_group is not null && selected_control_sample is not null,
            ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot or ProjectNodeKind.Population => selected_group is not null && selected_gate is not null,
            ProjectNodeKind.Group => selected_group is not null,
            ProjectNodeKind.Platform => selected_integration_job is not null,
            ProjectNodeKind.Layout => selected_page_layout is not null,
            ProjectNodeKind.Compensation => selected_group is not null && selected_compensation is not null,
            ProjectNodeKind.Embedding => selected_sample is not null &&
                                         !string.IsNullOrWhiteSpace(selected_node.EmbeddingName) &&
                                         !string.Equals(selected_node.EmbeddingName, "Populations", StringComparison.Ordinal),
            ProjectNodeKind.StatisticDefinition or ProjectNodeKind.StatisticValue => selected_group is not null && selected_statistic_definition() is not null,
            _ => false
        };

    private bool can_drop_project_node(object? parameter)
    {
        if (parameter is not ProjectNodeDropRequest request)
            return false;
        return request.Source.Kind == ProjectNodeKind.Sample &&
               request.Source.Sample is not null &&
               request.Source.Group is not null &&
               request.Target.Kind == ProjectNodeKind.Group &&
               request.Target.Group is not null &&
               !ReferenceEquals(request.Source.Group, request.Target.Group);
    }

    private async Task drop_project_node_async(ProjectNodeDropRequest? request)
    {
        if (request is null || !can_drop_project_node(request))
            return;

        var sample = request.Source.Sample!;
        var source_group = request.Source.Group!;
        var target_group = request.Target.Group!;
        var required_names = target_group.Channels.Select(channel => channel.Name).ToArray();
        var sample_names = sample.Channels.Select(channel => channel.Name).ToArray();
        var missing = required_names.Where(name => !sample_names.Contains(name, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            string message = $"Fail to move samples to the grouping {target_group.Name}. Such channels expected but not exist in the sample to be moved: {string.Join("， ", missing)}";
            if (RequestMessageAsync is not null)
                await RequestMessageAsync("Move sample failed", message);
            StatusText = message;
            return;
        }

        var extra = required_names.Length == 0
            ? Array.Empty<string>()
            : sample_names.Where(name => !required_names.Contains(name, StringComparer.Ordinal)).ToArray();
        if (extra.Length > 0)
        {
            string message = $"The sample contains the following channels, but not declared by the group. Such channels will be permanently deleted if you proceed moving so. {Environment.NewLine}{string.Join(", ", extra)}";
            bool proceed = RequestConfirmationAsync is not null && await RequestConfirmationAsync("Move sample", message);
            if (!proceed)
                return;
        }

        remove_sample_preferred_views(source_group, sample.Name);
        source_group.Samples.Remove(sample);
        source_group.RefreshChannelProfile();
        if (required_names.Length > 0 && !sample_names.SequenceEqual(required_names, StringComparer.Ordinal))
            sample.ProjectChannels(target_group.Channels);
        sample.Populations.Clear();
        target_group.AddSample(sample);
        sync_identity_metadata();
        rebuild_workspace_metadata_table();
        refresh_project_tree();
        SelectedGroup = target_group;
        SelectedSample = sample;
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Moved sample {sample.Name} to {target_group.Name}";
    }

    private void clear_removed_embedding_references(FlowSample sample, string embedding_name)
    {
        if (ReferenceEquals(selected_sample, sample))
        {
            if (XAxis.ChannelName == embedding_name)
                XAxis.ChannelName = selected_group?.Channels.FirstOrDefault()?.Name ?? "";
            if (YAxis.ChannelName == embedding_name)
                YAxis.ChannelName = selected_group?.Channels.Skip(1).FirstOrDefault()?.Name ?? selected_group?.Channels.FirstOrDefault()?.Name ?? "";
            if (DotColor.ChannelName == embedding_name)
                DotColor.ChannelName = "";
        }

        foreach (var layout in Workspace.PageLayouts)
        foreach (var element in layout.Elements.Where(element => ReferenceEquals(element.Sample, sample)))
        {
            var group = element.Group;
            if (element.XAxis.ChannelName == embedding_name)
                element.XAxis.ChannelName = group?.Channels.FirstOrDefault()?.Name ?? "";
            if (element.YAxis.ChannelName == embedding_name)
                element.YAxis.ChannelName = group?.Channels.Skip(1).FirstOrDefault()?.Name ?? group?.Channels.FirstOrDefault()?.Name ?? "";
            if (element.DotColor.ChannelName == embedding_name)
                element.DotColor.ChannelName = "";
        }
    }

    private async Task create_compensation_async()
    {
        if (selected_group is null || selected_group.Channels.Count == 0)
            return;

        var choices = selected_group.Channels
            .Select(channel => new AxisChoice(channel.Name, channel.Label))
            .ToArray();
        IReadOnlyList<string>? channel_names = RequestMultipleChoiceInputAsync is null
            ? choices.Select(choice => choice.Name).ToArray()
            : await RequestMultipleChoiceInputAsync("Create compensation", choices);
        if (channel_names is null)
            return;

        channel_names = channel_names
            .Where(name => selected_group.Channels.Any(channel => channel.Name == name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (channel_names.Count == 0)
            return;

        var compensation = new CompensationMatrix { Name = selected_group.CompensationCandidates.Count == 0 ? "Identity" : "Compensation" };
        compensation.ResetIdentity(channel_names);
        compensation = selected_group.RegisterCompensation(compensation, make_applied_if_first: false);
        SelectedCompensation = compensation;
        refresh_project_tree();
        StatusText = $"Created compensation: {compensation.Name}";
        raise_command_states();
    }

    private void apply_selected_compensation()
    {
        if (selected_group is null || selected_compensation is null)
            return;

        selected_group.SetAppliedCompensation(selected_compensation, manual: true, recalculate: false);
        schedule_compensation_application(selected_group, $"Applied compensation: {selected_compensation.Name}", force_compensation: true);
    }

    private void reapply_selected_group_compensation()
    {
        if (selected_group is null)
            return;

        schedule_compensation_application(selected_group, "Re-applied compensation", force_compensation: true);
    }

    private void open_spillover_compensation_panel()
    {
        if (selected_group is null)
            return;

        SelectedIntegrationJob = null;
        IsWorkspaceMetadataMode = false;
        SelectedSample = null;
        SelectedPopulation = null;
        SelectedGate = null;
        SelectedControlSample = selected_group.ControlSamples.FirstOrDefault();
        SelectedCompensation = null;
        IsSpilloverCompensationMode = true;
        refresh_spillover_workspace();
    }

    private void recalculate_selected_statistic()
    {
        var statistic = selected_statistic_definition();
        if (selected_group is null || statistic is null)
            return;

        selected_group.RecalculateSamples();
        refresh_selected_population_reference();
        refresh_project_tree();
        refresh_selected_statistics();
        StatusText = $"Recalculated statistics: {statistic_name(statistic)}";
    }

    private StatisticDefinition? selected_statistic_definition()
    {
        if (selected_node?.StatisticDefinition is not null)
            return selected_node.StatisticDefinition;
        if (selected_node?.Kind != ProjectNodeKind.StatisticValue ||
            selected_node.StatisticResult is null ||
            selected_node.Gate is null)
            return null;

        return selected_node.Gate.Statistics.FirstOrDefault(definition =>
            statistic_matches_definition(selected_node.StatisticResult, definition));
    }

    private async Task edit_selected_compensation_async()
    {
        if (selected_group is null || selected_compensation is null || RequestCompensationEditorAsync is null)
            return;

        bool updated = await RequestCompensationEditorAsync(selected_compensation);
        if (!updated)
            return;

        schedule_compensation_application(selected_group, $"Edited compensation: {selected_compensation.Name}", force_compensation: true);
    }

    private void schedule_compensation_application(FlowGroup group, string completion_status, bool force_compensation)
    {
        compensation_application_cancellation?.Cancel();

        var cancellation = new CancellationTokenSource();
        compensation_application_cancellation = cancellation;
        IsCompensationApplying = true;
        StatusText = "Applying compensation ...";

        _ = apply_compensation_async(group, completion_status, force_compensation, cancellation);
    }

    private void schedule_loaded_workspace_recalculation(IReadOnlyList<FlowGroup> groups, string file_name)
    {
        groups = groups.Where(group => group.Samples.Count > 0).ToArray();
        if (groups.Count == 0)
            return;

        bool has_compensation = groups.Any(has_applied_compensation);
        compensation_application_cancellation?.Cancel();

        var cancellation = new CancellationTokenSource();
        compensation_application_cancellation = cancellation;
        IsCompensationApplying = true;
        StatusText = has_compensation ? "Applying compensation ..." : "Calculating statistics ...";

        _ = recalculate_loaded_workspace_async(groups, $"Loaded workspace: {file_name}", cancellation);
    }

    private async Task recalculate_loaded_workspace_async(
        IReadOnlyList<FlowGroup> groups,
        string completion_status,
        CancellationTokenSource cancellation)
    {
        bool succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                foreach (var group in groups)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    group.RecalculateSamples(has_applied_compensation(group), cancellation.Token);
                }
            }, cancellation.Token);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(compensation_application_cancellation, cancellation))
                    return;
                StatusText = $"Load recalculation failed: {exception.Message}";
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(compensation_application_cancellation, cancellation))
                {
                    cancellation.Dispose();
                    return;
                }

                compensation_application_cancellation.Dispose();
                compensation_application_cancellation = null;
                IsCompensationApplying = false;
                refresh_selected_population_reference();
                refresh_project_tree();
                OnPropertyChanged(nameof(PlotGate));
                OnPropertyChanged(nameof(PlotPopulation));
                reapply_current_view_after_group_recalculation(groups);
                refresh_plot_gates();
                refresh_selected_statistics();
                if (succeeded)
                    StatusText = completion_status;
            });
        }
    }

    private static bool has_applied_compensation(FlowGroup group) =>
        group.AppliedCompensation?.Values.GetLength(0) > 0;

    private async Task apply_compensation_async(FlowGroup group, string completion_status, bool force_compensation, CancellationTokenSource cancellation)
    {
        bool succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                group.RecalculateSamples(force_compensation, cancellation.Token);
            }, cancellation.Token);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(compensation_application_cancellation, cancellation))
                    return;
                StatusText = $"Compensation failed: {exception.Message}";
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(compensation_application_cancellation, cancellation))
                {
                    cancellation.Dispose();
                    return;
                }

                compensation_application_cancellation.Dispose();
                compensation_application_cancellation = null;
                IsCompensationApplying = false;
                refresh_selected_population_reference();
                refresh_project_tree();
                OnPropertyChanged(nameof(PlotGate));
                OnPropertyChanged(nameof(PlotPopulation));
                reapply_current_view_after_group_recalculation([group]);
                refresh_plot_gates();
                refresh_selected_statistics();
                if (succeeded)
                    StatusText = completion_status;
            });
        }
    }

    private static void remove_gate(FlowGroup group, GateDefinition gate)
    {
        if (gate.Parent is not null)
        {
            gate.Parent.Children.Remove(gate);
            return;
        }

        group.Gates.Remove(gate);
    }

    private static void remove_sample_preferred_views(FlowGroup group, string sample_name)
    {
        group.SampleRootViewOptions.Remove(sample_name);
        foreach (var gate in all_gates(group.Gates))
        {
            gate.SamplePreferredViews.Remove(sample_name);
            foreach (var key in gate.SamplePreferredViews.Keys.Where(key => key.StartsWith(sample_name + "\t", StringComparison.Ordinal)).ToArray())
                gate.SamplePreferredViews.Remove(key);
        }
    }

    private static void rename_sample_preferred_views(FlowGroup group, string old_name, string new_name)
    {
        if (old_name == new_name)
            return;

        if (group.SampleRootViewOptions.Remove(old_name, out var root_view))
            group.SampleRootViewOptions[new_name] = root_view;

        foreach (var gate in all_gates(group.Gates))
        {
            if (!gate.SamplePreferredViews.Remove(old_name, out var view))
                view = null;
            if (view is not null)
                gate.SamplePreferredViews[new_name] = view;

            foreach (var key in gate.SamplePreferredViews.Keys.Where(key => key.StartsWith(old_name + "\t", StringComparison.Ordinal)).ToArray())
            {
                var region_view = gate.SamplePreferredViews[key];
                gate.SamplePreferredViews.Remove(key);
                gate.SamplePreferredViews[new_name + key[old_name.Length..]] = region_view;
            }
        }
    }

    private static IEnumerable<GateDefinition> all_gates(IEnumerable<GateDefinition> gates)
    {
        foreach (var gate in gates)
        {
            yield return gate;
            foreach (var child in all_gates(gate.Children))
                yield return child;
        }
    }

    private static bool has_external_boolean_dependency(FlowGroup group, GateDefinition edited_gate)
    {
        var edited_subtree_ids = all_gates([edited_gate]).Select(gate => gate.Id).ToHashSet();
        foreach (var gate in all_gates(group.Gates))
        {
            if (edited_subtree_ids.Contains(gate.Id))
                continue;
            if (gate.Kind is not (GateKind.Merge or GateKind.Exclude or GateKind.Overlap))
                continue;
            if ((gate.BooleanFirstGateId is Guid first_id && edited_subtree_ids.Contains(first_id)) ||
                (gate.BooleanSecondGateId is Guid second_id && edited_subtree_ids.Contains(second_id)))
                return true;
        }

        return false;
    }

    private static int count_gates(IEnumerable<GateDefinition> gates)
    {
        int count = 0;
        foreach (var gate in gates)
            count += 1 + count_gates(gate.Children);
        return count;
    }

    private async Task add_gate_async(GateKind kind)
    {
        if (selected_group is null || selected_group.Channels.Count == 0)
            return;
        if (!can_create_gate_kind(kind))
            return;

        var first = get_default_x_channel(selected_group)!;
        var second = get_default_y_channel(selected_group, first);
        var gate = new GateDefinition
        {
            Name = await request_gate_name_async(),
            Kind = kind,
            XChannel = XAxis.ChannelName,
            YChannel = kind is GateKind.Threshold or GateKind.Range or GateKind.Merge or GateKind.Exclude or GateKind.Overlap ? null : YAxis.ChannelName,
            ParentPopulationRegion = current_parent_population_region()
        };
        copy_current_view_to_gate(gate);

        if (kind is GateKind.Polygon)
        {
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.2, second.Maximum * 0.2));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.55, second.Maximum * 0.25));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.7, second.Maximum * 0.65));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.25, second.Maximum * 0.75));
        }
        else if (kind == GateKind.OffsetQuadrant)
        {
            var center = new Avalonia.Point(first.Maximum * 0.5, second.Maximum * 0.5);
            gate.Vertices.Add(center);
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.6, center.Y));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.4, center.Y));
        }
        else
        {
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.25, second.Maximum * 0.25));
            if (kind is not GateKind.Threshold and not GateKind.Quadrant and not GateKind.CurlyQuadrant)
                gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.75, second.Maximum * 0.75));
        }

        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = gate.XChannel });
        if (selected_gate is null)
            selected_group.Gates.Add(gate);
        else
        {
            gate.Parent = selected_gate;
            selected_gate.Children.Add(gate);
        }
        SelectedGate = gate;
        recalculate_new_gate(gate);
        refresh_project_tree();
        OnPropertyChanged(nameof(PlotGate));
        refresh_plot_gates();
        refresh_selected_statistics();
        raise_command_states();
    }

    private async Task add_boolean_gate_async(GateKind kind)
    {
        if (selected_group is null)
            return;
        if (!CanCreateAnyGate)
            return;

        var choices = boolean_population_choices();
        if (choices.Count < 2)
            return;

        var selection = RequestBooleanGateInputAsync is null
            ? new BooleanGateSelection(choices[0], choices[1])
            : await RequestBooleanGateInputAsync($"{kind} populations", choices);
        if (selection is null)
            return;

        var gate = new GateDefinition
        {
            Name = await request_gate_name_async(),
            Kind = kind,
            XChannel = XAxis.ChannelName,
            ParentPopulationRegion = current_parent_population_region(),
            BooleanFirstGateId = selection.First.GateId,
            BooleanFirstRegion = selection.First.Region,
            BooleanSecondGateId = selection.Second.GateId,
            BooleanSecondRegion = selection.Second.Region
        };
        apply_boolean_gate_view_from_first_population(gate, selection.First);
        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = gate.XChannel });
        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.FrequencyOfParent, ChannelName = gate.XChannel });

        var siblings = selected_gate is null ? selected_group.Gates : selected_gate.Children;
        gate.Parent = selected_gate;
        siblings.Add(gate);
        SelectedGate = gate;
        recalculate_new_gate(gate);
        refresh_project_tree();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"{kind} boolean gate created";
        raise_command_states();
    }

    private void apply_boolean_gate_view_from_first_population(GateDefinition gate, BooleanPopulationChoice first_population)
    {
        if (selected_group is null)
            return;

        var source_gate = all_gates(selected_group.Gates).FirstOrDefault(item => item.Id == first_population.GateId);
        if (source_gate is null)
        {
            copy_current_view_to_gate(gate);
            return;
        }

        gate.XChannel = source_gate.XChannel;
        gate.XMinimum = source_gate.XMinimum;
        gate.XMaximum = source_gate.XMaximum;
        gate.XScale = source_gate.XScale.Clone();
        gate.YChannel = source_gate.YChannel;
        gate.YMinimum = source_gate.YMinimum;
        gate.YMaximum = source_gate.YMaximum;
        gate.YScale = source_gate.YScale.Clone();
        gate.PreferredXChannel = string.IsNullOrWhiteSpace(source_gate.PreferredXChannel) ? source_gate.XChannel : source_gate.PreferredXChannel;
        gate.PreferredXMinimum = string.IsNullOrWhiteSpace(source_gate.PreferredXChannel) ? source_gate.XMinimum : source_gate.PreferredXMinimum;
        gate.PreferredXMaximum = string.IsNullOrWhiteSpace(source_gate.PreferredXChannel) ? source_gate.XMaximum : source_gate.PreferredXMaximum;
        gate.PreferredXScale = (string.IsNullOrWhiteSpace(source_gate.PreferredXChannel) ? source_gate.XScale : source_gate.PreferredXScale).Clone();
        gate.PreferredYChannel = string.IsNullOrWhiteSpace(source_gate.PreferredYChannel) ? source_gate.YChannel : source_gate.PreferredYChannel;
        gate.PreferredYMinimum = string.IsNullOrWhiteSpace(source_gate.PreferredYChannel) ? source_gate.YMinimum : source_gate.PreferredYMinimum;
        gate.PreferredYMaximum = string.IsNullOrWhiteSpace(source_gate.PreferredYChannel) ? source_gate.YMaximum : source_gate.PreferredYMaximum;
        gate.PreferredYScale = (string.IsNullOrWhiteSpace(source_gate.PreferredYChannel) ? source_gate.YScale : source_gate.PreferredYScale).Clone();
        gate.PreferredPlotMode = source_gate.PreferredPlotMode;
        gate.PreferredShowOutlierPoints = source_gate.PreferredShowOutlierPoints;
        gate.PreferredDrawLargeDots = source_gate.PreferredDrawLargeDots;
        gate.PreferredShowGridlines = source_gate.PreferredShowGridlines;
        gate.PreferredShowGateAnnotations = source_gate.PreferredShowGateAnnotations;
        gate.PreferredShowGateAnnotationNames = source_gate.PreferredShowGateAnnotationNames;
        gate.PreferredContourLevelCount = source_gate.PreferredContourLevelCount;
        gate.PreferredDensitySmoothing = source_gate.PreferredDensitySmoothing;
        gate.PreferredDensityPalette = source_gate.PreferredDensityPalette;
        gate.PreferredDotColor = clone_dot_color(source_gate.PreferredDotColor);

        foreach (var item in source_gate.SamplePreferredViews)
            gate.SamplePreferredViews[item.Key] = clone_gate_view_options(item.Value);
    }

    private async Task add_statistic_async(StatisticKind kind)
    {
        if (selected_group is null)
            return;

        string? dimension_name = await request_statistic_dimension_async(kind);
        if (string.IsNullOrWhiteSpace(dimension_name))
            return;

        var definitions = selected_gate?.Statistics ?? selected_group.Statistics;
        definitions.Add(new StatisticDefinition { Kind = kind, ChannelName = dimension_name });
        selected_group.RecalculateSamples();
        refresh_project_tree();
        refresh_selected_statistics();
    }

    private async Task<string?> request_statistic_dimension_async(StatisticKind kind)
    {
        if (kind is StatisticKind.NumberOfEvents or StatisticKind.FrequencyOfParent or StatisticKind.FrequencyOfAll)
            return selected_gate?.XChannel ?? XAxis.ChannelName;

        var choices = available_statistic_dimensions();
        if (choices.Count == 0)
            return null;

        if (RequestChoiceInputAsync is null)
            return choices[0].Name;

        // Friendly names
        string friendly = kind switch
        {
            StatisticKind.Mean => "Mean",
            StatisticKind.Median => "Median",
            StatisticKind.GeometricMean => "Geometric mean",
            StatisticKind.CoefficientOfVariation => "Coefficient of variation",
            StatisticKind.StandardDeviation => "Standard deviation",
            StatisticKind.FrequencyOfParent => "Frequency of parent population",
            StatisticKind.FrequencyOfAll => "Frequency of all events",
            StatisticKind.NumberOfEvents => "Count",
            _ => kind.ToString()
        };

        return await RequestChoiceInputAsync($"Select dimension for {friendly}", choices);
    }

    private IReadOnlyList<AxisChoice> available_statistic_dimensions()
    {
        if (selected_group is null)
            return Array.Empty<AxisChoice>();

        var choices = new List<AxisChoice>();
        choices.AddRange(selected_group.Channels.Select(channel => new AxisChoice(channel.Name, channel.Label)));
        if (selected_sample is not null && selected_population is not null)
        {
            choices.AddRange(available_embedding_axis_names(selected_sample, selected_population)
                .Where(embedding_name => choices.All(choice => choice.Name != embedding_name))
                .Select(embedding_name => new AxisChoice(embedding_name, "")));
            return choices;
        }

        foreach (var sample in selected_group.Samples)
        foreach (var population in sample.Populations)
        foreach (string embedding_name in available_embedding_axis_names(sample, population))
        {
            if (choices.All(choice => choice.Name != embedding_name))
                choices.Add(new AxisChoice(embedding_name, ""));
        }

        return choices;
    }

    private IReadOnlyList<BooleanPopulationChoice> boolean_population_choices()
    {
        if (selected_group is null)
            return Array.Empty<BooleanPopulationChoice>();

        var choices = new List<BooleanPopulationChoice>();
        foreach (var gate in all_gates(selected_group.Gates).Where(gate => gate.Kind is not (GateKind.Merge or GateKind.Exclude or GateKind.Overlap)))
        foreach (var region in gate.PopulationRegions)
            choices.Add(new BooleanPopulationChoice(gate.Id, region, gate.PopulationName(region)));

        return choices;
    }

    private int count_gate_region_events(FlowGroup group, GateDefinition gate, PopulationRegion region)
    {
        int count = 0;
        foreach (var sample in group.Samples)
            count += find_populations(sample.Populations, gate).Where(population => population.Region == region).Sum(population => population.EventCount);
        return count;
    }

    private async Task add_canvas_gate_async(GateDefinition? gate)
    {
        if (gate is null || selected_group is null)
            return;
        if (!can_create_gate_kind(gate.Kind))
            return;

        var siblings = selected_gate is null ? selected_group.Gates : selected_gate.Children;
        gate.Name = await request_gate_name_async();
        if (string.IsNullOrWhiteSpace(gate.XChannel))
            gate.XChannel = XAxis.ChannelName;
        if (!gate.IsOneDimensional && string.IsNullOrWhiteSpace(gate.YChannel))
            gate.YChannel = YAxis.ChannelName;
        gate.ParentPopulationRegion = current_parent_population_region();
        copy_current_view_to_gate(gate);

        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = gate.XChannel });
        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.FrequencyOfParent, ChannelName = gate.XChannel });
        gate.Parent = selected_gate;
        siblings.Add(gate);
        recalculate_new_gate(gate);
        refresh_project_tree();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"{gate.Kind} gate created from canvas";
        raise_command_states();
    }

    private void recalculate_new_gate(GateDefinition gate)
    {
        if (selected_group is null)
            return;

        if (!selected_group.RecalculateGateSubtree(gate))
            selected_group.RecalculateSamples();
    }

    private bool can_create_gate_kind(GateKind kind) =>
        kind is GateKind.Threshold or GateKind.Range
            ? CanCreateOneDimensionalGate
            : CanCreateTwoDimensionalGate;

    private PopulationRegion current_parent_population_region() =>
        selected_population?.Region ??
        (selected_node?.Kind == ProjectNodeKind.GatePopulationSlot ? selected_node.PopulationRegion : PopulationRegion.Primary);

    private async Task<string> request_gate_name_async()
    {
        string default_name = $"Gate {next_gate_number++}";
        if (RequestTextInputAsync is null)
            return default_name;

        string? name = await RequestTextInputAsync("Name gate", default_name);
        return string.IsNullOrWhiteSpace(name) ? default_name : name.Trim();
    }

    private void apply_node_selection(ProjectNode? node)
    {
        if (node is null)
        {
            SelectedIntegrationJob = null;
            if (ViewState is MainWindowViewState.Metadata or MainWindowViewState.GroupMetadata)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
            SelectedGroup = null;
            SelectedSample = null;
            SelectedPopulation = null;
            SelectedGate = null;
            SelectedCompensation = null;
            SelectedControlSample = null;
            refresh_selection_sidebars();
            refresh_plot_gates();
            refresh_selected_statistics();
            raise_command_states();
            return;
        }

        IsPythonScriptEditorMode = false;
        bool passive_control_selection = node.Kind is ProjectNodeKind.ControlFolder or ProjectNodeKind.ControlSample;
        bool analysis_node_selection = node.Kind is ProjectNodeKind.GateFolder
            or ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.Sample
            or ProjectNodeKind.Population;
        if (IsPageEditorMode && analysis_node_selection && !suppress_analysis_switch_for_context_selection)
            set_view_state(MainWindowViewState.Analysis, "Analysis view");
        if (node.Kind is not ProjectNodeKind.Metadata and not ProjectNodeKind.Group &&
            !passive_control_selection &&
            ViewState is MainWindowViewState.Metadata or MainWindowViewState.GroupMetadata)
            set_view_state(MainWindowViewState.Analysis, "Analysis view");
        if (node.Kind is not ProjectNodeKind.ControlFolder and not ProjectNodeKind.SpilloverCompensation and not ProjectNodeKind.SpectralUnmixing and not ProjectNodeKind.ControlSample &&
            ViewState is MainWindowViewState.SpilloverCompensation or MainWindowViewState.SpectralUnmixing)
            set_view_state(MainWindowViewState.Analysis, "Analysis view");
        var previous_group = selected_group;
        var previous_gate = selected_gate;
        var previous_population = selected_population;
        bool needs_plot_refresh = false;
        bool needs_statistics_refresh = false;

        switch (node.Kind)
        {
            case ProjectNodeKind.Workspace:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                SelectedGroup = Workspace.Groups.FirstOrDefault();
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.Metadata:
                IsPageEditorMode = false;
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = true;
                refresh_workspace_sample_metadata();
                SelectedGroup = Workspace.Groups.FirstOrDefault();
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                StatusText = "Workspace sample metadata";
                break;
            case ProjectNodeKind.LayoutFolder:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                SelectedPageLayout = Workspace.PageLayouts.FirstOrDefault();
                IsPageEditorMode = true;
                break;
            case ProjectNodeKind.Layout:
                SelectedIntegrationJob = null;
                SelectedPageLayout = node.Layout;
                IsPageEditorMode = true;
                break;
            case ProjectNodeKind.IntegrationJobFolder:
                IsPageEditorMode = false;
                SelectedIntegrationJob = Workspace.IntegrationJobs.FirstOrDefault();
                SelectedGroup = Workspace.Groups.FirstOrDefault();
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                break;
            case ProjectNodeKind.Platform:
                IsPageEditorMode = false;
                SelectedIntegrationJob = node.Platform;
                SelectedGroup = Workspace.Groups.FirstOrDefault();
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                StatusText = node.Platform?.StatusText ?? "Platform";
                break;
            case ProjectNodeKind.GateFolder:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                needs_plot_refresh = true;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.CompensationFolder:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedCompensation = null;
                SelectedControlSample = null;
                break;
            case ProjectNodeKind.ControlFolder:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedControlSample = selected_group?.ControlSamples.FirstOrDefault();
                SelectedCompensation = null;
                IsSpilloverCompensationMode = true;
                refresh_spillover_workspace();
                break;
            case ProjectNodeKind.SpilloverCompensation:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedControlSample = selected_group?.ControlSamples.FirstOrDefault();
                SelectedCompensation = null;
                IsSpilloverCompensationMode = true;
                refresh_spillover_workspace();
                break;
            case ProjectNodeKind.ControlSample:
                SelectedIntegrationJob = null;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedControlSample = node.ControlSample;
                SelectedCompensation = null;
                break;
            case ProjectNodeKind.SpectralUnmixing:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedControlSample = selected_group?.ControlSamples.FirstOrDefault();
                SelectedCompensation = null;
                SpectralPanel.SetGroup(selected_group);
                IsSpectralUnmixingMode = true;
                break;
            case ProjectNodeKind.Compensation:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedCompensation = node.Compensation;
                SelectedControlSample = null;
                break;
            case ProjectNodeKind.Sample:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = node.Sample;
                SelectedPopulation = node.Population;
                SelectedGate = node.Population?.Gate;
                SelectedCompensation = null;
                apply_root_axis_context();
                needs_plot_refresh = true;
                break;
            case ProjectNodeKind.Embedding:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = node.Sample;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                StatusText = string.IsNullOrWhiteSpace(node.EmbeddingName)
                    ? StatusText
                    : $"Embedding: {node.EmbeddingName}";
                break;
            case ProjectNodeKind.StatisticDefinition:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedGate = node.Gate;
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = node.Gate is null ? null : resolve_population_for_slot(selected_sample, node.Gate, PopulationRegion.Primary);
                SelectedCompensation = null;
                if (node.Gate is not null)
                    apply_axis_from_gate_context(node.Gate);
                StatusText = $"Statistic: {node.Name}";
                needs_plot_refresh = node.Gate is not null;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.StatisticValue:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = node.Sample;
                SelectedPopulation = node.Population;
                SelectedGate = node.Gate;
                SelectedCompensation = null;
                if (node.Population is not null)
                    apply_axis_from_gate_context(node.Population.Gate);
                StatusText = $"Statistic: {node.Name}";
                needs_plot_refresh = true;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.Gate:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = node.Gate?.PopulationRegions.Count == 1 ? selected_group?.Samples.FirstOrDefault() : null;
                SelectedPopulation = node.Gate?.PopulationRegions.Count == 1
                    ? resolve_population_for_slot(selected_sample, node.Gate, PopulationRegion.Primary)
                    : null;
                SelectedGate = node.Gate;
                SelectedCompensation = null;
                if (node.Gate is not null)
                    apply_axis_from_gate_context(node.Gate);
                needs_plot_refresh = true;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.GatePopulationSlot:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedGate = node.Gate;
                SelectedPopulation = resolve_population_for_slot(selected_sample, node.Gate, node.PopulationRegion);
                SelectedCompensation = null;
                if (node.Gate is not null)
                    apply_axis_from_gate_context(node.Gate);
                needs_plot_refresh = true;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.Population:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = node.Sample;
                SelectedPopulation = node.Population;
                SelectedGate = node.Population?.Gate;
                SelectedCompensation = null;
                if (node.Population is not null)
                    apply_axis_from_gate_context(node.Population.Gate);
                needs_plot_refresh = true;
                needs_statistics_refresh = true;
                break;
            case ProjectNodeKind.Group:
                IsPageEditorMode = false;
                SelectedIntegrationJob = null;
                IsGroupMetadataMode = true;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                needs_plot_refresh = true;
                needs_statistics_refresh = true;
                break;
        }

        if (!ReferenceEquals(previous_group, selected_group))
            refresh_selection_sidebars();
        if (needs_plot_refresh)
            refresh_plot_gates();
        if (needs_statistics_refresh ||
            !ReferenceEquals(previous_group, selected_group) ||
            !ReferenceEquals(previous_gate, selected_gate) ||
            !ReferenceEquals(previous_population, selected_population))
            refresh_selected_statistics();
        OnPropertyChanged(nameof(CanCreateAnyGate));
        OnPropertyChanged(nameof(CanCreateOneDimensionalGate));
        OnPropertyChanged(nameof(CanCreateTwoDimensionalGate));
        enforce_active_tool_allowed();
        raise_command_states();
    }

    private void apply_root_axis_context()
    {
        if (selected_group is null)
            return;

        apply_axes_from_group_root_view(selected_group);
    }

    private void apply_axis_from_gate_context(GateDefinition gate)
    {
        set_axes_from_gate(gate);
    }

    private void reapply_current_view_after_group_recalculation(IReadOnlyList<FlowGroup> groups)
    {
        if (selected_group is null || !groups.Contains(selected_group))
            return;

        if (selected_gate is not null)
        {
            apply_axis_from_gate_context(selected_gate);
            return;
        }

        if (selected_population is null &&
            is_root_view_node_kind(selected_node?.Kind))
            apply_root_axis_context();
    }

    private void set_axes_from_gate(GateDefinition gate)
    {
        if (selected_group is null)
            return;

        var view = preferred_view_for_current_context(gate);
        bool has_preferred_view = view?.HasView == true;
        string preferred_x_channel_name = has_preferred_view ? view!.XChannel : gate.XChannel;
        if (!axis_choice_exists(preferred_x_channel_name))
            return;

        string? preferred_y_channel_name = has_preferred_view ? view!.YChannel : gate.YChannel;
        bool has_y_channel = !string.IsNullOrWhiteSpace(preferred_y_channel_name) && axis_choice_exists(preferred_y_channel_name);

        applying_gate_view_options = true;
        try
        {
            XAxis = new AxisSettings
            {
                ChannelName = preferred_x_channel_name,
                Minimum = has_preferred_view ? view!.XMinimum : gate.XMinimum,
                Maximum = has_preferred_view ? view!.XMaximum : gate.XMaximum,
                Scale = (has_preferred_view ? view!.XScale : gate.XScale).Clone()
            };
            if (has_y_channel)
            {
                YAxis = new AxisSettings
                {
                    ChannelName = preferred_y_channel_name!,
                    Minimum = has_preferred_view ? view!.YMinimum : gate.YMinimum,
                    Maximum = has_preferred_view ? view!.YMaximum : gate.YMaximum,
                    Scale = (has_preferred_view ? view!.YScale : gate.YScale).Clone()
                };
            }

            if (has_preferred_view)
                apply_gate_plot_options(view!);
        }
        finally
        {
            applying_gate_view_options = false;
        }
    }

    private bool axis_choice_exists(string? channel_name)
    {
        if (string.IsNullOrWhiteSpace(channel_name))
            return false;
        if (selected_group?.Channels.Any(channel => channel.Name == channel_name) == true)
            return true;
        return selected_sample is not null &&
            selected_population is not null &&
            available_embedding_axis_names(selected_sample, selected_population).Contains(channel_name, StringComparer.Ordinal);
    }

    private GateViewOptions? preferred_view_for_current_context(GateDefinition gate)
    {
        var region = selected_population?.Region ??
            (selected_node?.Kind == ProjectNodeKind.GatePopulationSlot ? selected_node.PopulationRegion : PopulationRegion.Primary);

        if (selected_node?.Kind == ProjectNodeKind.Population && selected_sample is not null && selected_population is not null)
        {
            if (gate.SamplePreferredViews.TryGetValue(sample_preferred_view_key(selected_sample.Name, region), out var sample_view) ||
                gate.SamplePreferredViews.TryGetValue(selected_sample.Name, out sample_view))
                return sample_view;
        }

        if (gate.PopulationPreferredViews.TryGetValue(region, out var population_view))
            return population_view;

        return legacy_preferred_view(gate);
    }

    private void apply_gate_plot_options(GateViewOptions view)
    {
        selected_plot_mode = view.PlotMode;
        show_outlier_points = view.ShowOutlierPoints;
        draw_large_dots = view.DrawLargeDots;
        show_gridlines = view.ShowGridlines;
        show_gate_annotations = view.ShowGateAnnotations;
        show_gate_annotation_names = view.ShowGateAnnotationNames;
        contour_level_count = Math.Clamp(view.ContourLevelCount, 2, 80);
        density_smoothing = Math.Clamp(view.DensitySmoothing, 0, 12);
        density_palette = view.DensityPalette;
        DotColor = clone_dot_color(view.DotColor);
        OnPropertyChanged(nameof(SelectedPlotMode));
        OnPropertyChanged(nameof(EffectivePlotMode));
        OnPropertyChanged(nameof(IsYAxisEnabled));
        OnPropertyChanged(nameof(IsDensityPlotMode));
        OnPropertyChanged(nameof(IsDotplotPlotMode));
        OnPropertyChanged(nameof(IsContourPlotMode));
        OnPropertyChanged(nameof(IsZebraPlotMode));
        OnPropertyChanged(nameof(IsHistogramPlotMode));
        OnPropertyChanged(nameof(CanCreateOneDimensionalGate));
        OnPropertyChanged(nameof(CanCreateTwoDimensionalGate));
        OnPropertyChanged(nameof(ShowOutlierPoints));
        OnPropertyChanged(nameof(DrawLargeDots));
        OnPropertyChanged(nameof(ShowGridlines));
        OnPropertyChanged(nameof(ShowGateAnnotations));
        OnPropertyChanged(nameof(ShowGateAnnotationNames));
        OnPropertyChanged(nameof(ContourLevelCount));
        OnPropertyChanged(nameof(DensitySmoothing));
        OnPropertyChanged(nameof(SelectedDensityColorMap));
        enforce_active_tool_allowed();
    }

    private void reset_axes_from_group(FlowGroup group)
    {
        var first = get_default_x_channel(group);
        if (first is null)
            return;

        var second = get_default_y_channel(group, first);
        XAxis = new AxisSettings { ChannelName = first.Name };
        YAxis = new AxisSettings { ChannelName = second.Name };
        apply_data_implied_axis_defaults(XAxis, group, fallback_sample: null, fallback_population: null);
        apply_data_implied_axis_defaults(YAxis, group, fallback_sample: null, fallback_population: null);
    }

    private static GateViewOptions create_default_root_view(FlowGroup group)
    {
        var first = get_default_x_channel(group);
        if (first is null)
            return new GateViewOptions();

        var second = get_default_y_channel(group, first);
        var x_axis = new AxisSettings { ChannelName = first.Name };
        var y_axis = new AxisSettings { ChannelName = second.Name };
        apply_data_implied_axis_defaults(x_axis, group, fallback_sample: null, fallback_population: null);
        apply_data_implied_axis_defaults(y_axis, group, fallback_sample: null, fallback_population: null);
        return new GateViewOptions
        {
            XChannel = x_axis.ChannelName,
            XMinimum = x_axis.Minimum,
            XMaximum = x_axis.Maximum,
            XScale = x_axis.Scale.Clone(),
            YChannel = y_axis.ChannelName,
            YMinimum = y_axis.Minimum,
            YMaximum = y_axis.Maximum,
            YScale = y_axis.Scale.Clone()
        };
    }

    private static void ensure_group_root_view_defaults(FlowGroup group)
    {
        group.RecalculateDataImpliedViewOptions();
    }

    private static GateViewOptions clone_gate_view_options(GateViewOptions view) =>
        new()
        {
            XChannel = view.XChannel,
            XMinimum = view.XMinimum,
            XMaximum = view.XMaximum,
            XScale = view.XScale.Clone(),
            YChannel = view.YChannel,
            YMinimum = view.YMinimum,
            YMaximum = view.YMaximum,
            YScale = view.YScale.Clone(),
            PlotMode = view.PlotMode,
            ShowOutlierPoints = view.ShowOutlierPoints,
            DrawLargeDots = view.DrawLargeDots,
            ShowGridlines = view.ShowGridlines,
            ShowGateAnnotations = view.ShowGateAnnotations,
            ShowGateAnnotationNames = view.ShowGateAnnotationNames,
            ContourLevelCount = view.ContourLevelCount,
            DensitySmoothing = view.DensitySmoothing,
            DensityPalette = view.DensityPalette,
            DotColor = clone_dot_color(view.DotColor)
        };

    private bool can_copy_hierarchy_view_options_to_group() =>
        selected_group is not null &&
        selected_sample is not null &&
        selected_node?.Kind is ProjectNodeKind.Sample or ProjectNodeKind.Population;

    private void copy_hierarchy_view_options_to_group()
    {
        if (!can_copy_hierarchy_view_options_to_group() ||
            selected_group is null ||
            selected_sample is null ||
            selected_node is null)
            return;

        int copied_count = 0;
        if (selected_node.Kind == ProjectNodeKind.Sample)
        {
            if (selected_group.SampleRootViewOptions.TryGetValue(selected_sample.Name, out var root_view) &&
                root_view.HasView)
            {
                selected_group.RootViewOptions = clone_gate_view_options(root_view);
                copied_count++;
            }

            foreach (var population in selected_sample.Populations)
                copied_count += copy_population_hierarchy_view_options_to_group(selected_sample.Name, population);
        }
        else if (selected_population is not null)
        {
            copied_count += copy_population_hierarchy_view_options_to_group(selected_sample.Name, selected_population);
        }

        refresh_plot_gates();
        refresh_project_tree();
        raise_command_states();
        StatusText = copied_count == 1
            ? "Copied 1 hierarchy view option to grouping defaults"
            : $"Copied {copied_count} hierarchy view options to grouping defaults";
    }

    private static int copy_population_hierarchy_view_options_to_group(string sample_name, PopulationResult population)
    {
        int copied_count = 0;
        var gate = population.Gate;
        string key = sample_preferred_view_key(sample_name, population.Region);
        if (gate.SamplePreferredViews.TryGetValue(key, out var view) && view.HasView)
        {
            set_gate_population_default_view(gate, population.Region, clone_gate_view_options(view));
            copied_count++;
        }

        foreach (var child in population.Children)
            copied_count += copy_population_hierarchy_view_options_to_group(sample_name, child);

        return copied_count;
    }

    private static void set_gate_population_default_view(GateDefinition gate, PopulationRegion region, GateViewOptions view)
    {
        if (region != PopulationRegion.Primary || gate.PopulationRegions.Count != 1)
        {
            gate.PopulationPreferredViews[region] = view;
            return;
        }

        gate.PreferredXChannel = view.XChannel;
        gate.PreferredXMinimum = view.XMinimum;
        gate.PreferredXMaximum = view.XMaximum;
        gate.PreferredXScale = view.XScale.Clone();
        gate.PreferredYChannel = view.YChannel;
        gate.PreferredYMinimum = view.YMinimum;
        gate.PreferredYMaximum = view.YMaximum;
        gate.PreferredYScale = view.YScale.Clone();
        gate.PreferredPlotMode = view.PlotMode;
        gate.PreferredShowOutlierPoints = view.ShowOutlierPoints;
        gate.PreferredDrawLargeDots = view.DrawLargeDots;
        gate.PreferredShowGridlines = view.ShowGridlines;
        gate.PreferredShowGateAnnotations = view.ShowGateAnnotations;
        gate.PreferredShowGateAnnotationNames = view.ShowGateAnnotationNames;
        gate.PreferredContourLevelCount = view.ContourLevelCount;
        gate.PreferredDensitySmoothing = view.DensitySmoothing;
        gate.PreferredDensityPalette = view.DensityPalette;
        gate.PreferredDotColor = clone_dot_color(view.DotColor);
    }

    private static GateViewOptions? root_view_for_current_selection(FlowGroup group, ProjectNodeKind? node_kind, FlowSample? sample) =>
        node_kind switch
        {
            ProjectNodeKind.Sample when sample is not null => sample_root_view_or_default(group, sample.Name),
            _ => group_root_view_or_default(group)
        };

    private void apply_axes_from_group_root_view(FlowGroup group)
    {
        var root_view = root_view_for_current_selection(group, selected_node?.Kind, selected_sample);
        if (root_view?.HasView == true && group.Channels.Any(channel => channel.Name == root_view.XChannel))
        {
            var view = root_view;
            applying_gate_view_options = true;
            try
            {
                XAxis = new AxisSettings
                {
                    ChannelName = view.XChannel,
                    Minimum = view.XMinimum,
                    Maximum = view.XMaximum,
                    Scale = view.XScale.Clone()
                };
                if (!string.IsNullOrWhiteSpace(view.YChannel) && group.Channels.Any(channel => channel.Name == view.YChannel))
                {
                    YAxis = new AxisSettings
                    {
                        ChannelName = view.YChannel!,
                        Minimum = view.YMinimum,
                        Maximum = view.YMaximum,
                        Scale = view.YScale.Clone()
                    };
                }
                apply_gate_plot_options(view);
            }
            finally
            {
                applying_gate_view_options = false;
            }
            return;
        }

        applying_gate_view_options = true;
        try
        {
            reset_axes_from_group(group);
        }
        finally
        {
            applying_gate_view_options = false;
        }
    }

    private GateViewOptions? root_view_for_current_context(FlowGroup group) =>
        root_view_for_current_selection(group, selected_node?.Kind, selected_sample);

    private static GateViewOptions? root_view_for_node(FlowGroup group, ProjectNode node) =>
        root_view_for_current_selection(group, node.Kind, node.Sample);

    private static GateViewOptions? group_root_view_or_default(FlowGroup group)
    {
        if (group.RootViewOptions.HasView)
            return group.RootViewOptions;

        return first_sample_root_view(group);
    }

    private static GateViewOptions? sample_root_view_or_default(FlowGroup group, string sample_name)
    {
        if (group.SampleRootViewOptions.TryGetValue(sample_name, out var sample_view) && sample_view.HasView)
            return sample_view;
        if (group.RootViewOptions.HasView)
            return group.RootViewOptions;
        return first_sample_root_view(group);
    }

    private static GateViewOptions? first_sample_root_view(FlowGroup group) =>
        group.SampleRootViewOptions
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value)
            .FirstOrDefault(view => view.HasView);

    private void set_root_view_for_current_context(FlowGroup group, GateViewOptions view)
    {
        if (selected_node?.Kind == ProjectNodeKind.Sample && selected_sample is not null)
        {
            group.SampleRootViewOptions[selected_sample.Name] = view;
            return;
        }

        group.RootViewOptions = view;
    }

    private void set_axis(ref AxisSettings axis_field, AxisSettings value, PropertyChangedEventHandler handler)
    {
        axis_field.PropertyChanged -= handler;
        if (!SetField(ref axis_field, value))
        {
            axis_field.PropertyChanged += handler;
            return;
        }

        axis_field.PropertyChanged += handler;
    }

    private void x_axis_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AxisSettings.ChannelName))
        {
            apply_axis_channel_defaults(XAxis);
            OnPropertyChanged(nameof(SelectedXAxisChoice));
            refresh_axis_menu_state();
        }
        OnPropertyChanged(nameof(IsEditorXAxisLinearScale));
        OnPropertyChanged(nameof(IsEditorXAxisLogicleScale));
        sync_selected_gate_preferred_view();
        refresh_plot_gates();
        schedule_plot_transform_preparation();
    }

    private void y_axis_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AxisSettings.ChannelName))
        {
            apply_axis_channel_defaults(YAxis);
            OnPropertyChanged(nameof(SelectedYAxisChoice));
            refresh_axis_menu_state();
        }
        OnPropertyChanged(nameof(IsEditorYAxisLinearScale));
        OnPropertyChanged(nameof(IsEditorYAxisLogicleScale));
        sync_selected_gate_preferred_view();
        refresh_plot_gates();
    }

    private void set_editor_axis_channel(bool is_x_axis, string channel_name)
    {
        var axis = editor_axis_defaults(is_x_axis, channel_name);
        if (is_x_axis)
            XAxis = axis;
        else
            YAxis = axis;

        refresh_axis_menu_state();
        sync_selected_gate_preferred_view();
        refresh_plot_gates();
        schedule_plot_transform_preparation();
    }

    public bool IsSpectralUnmixingMode
    {
        get => ViewState == MainWindowViewState.SpectralUnmixing;
        private set
        {
            if (value)
            {
                SelectedIntegrationJob = null;
                set_view_state(MainWindowViewState.SpectralUnmixing, "Spectral unmixing");
            }
            else if (IsSpectralUnmixingMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
        }
    }

    private void swap_editor_axes()
    {
        if (!IsYAxisEnabled)
            return;

        var old_x_axis = XAxisClone();
        var old_y_axis = YAxisClone();
        XAxis = old_y_axis;
        YAxis = old_x_axis;

        refresh_axis_menu_state();
        sync_selected_gate_preferred_view();
        refresh_plot_gates();
        schedule_plot_transform_preparation();
    }

    private AxisSettings editor_axis_defaults(bool is_x_axis, string channel_name)
    {
        var axis = new AxisSettings { ChannelName = channel_name };
        if (try_create_inherited_gate_axis_defaults(is_x_axis, channel_name) is { } inherited)
            return inherited;

        apply_data_implied_axis_defaults(axis, selected_group, selected_sample, selected_population);
        return axis;
    }

    private void copy_current_view_to_gate(GateDefinition gate)
    {
        gate.XChannel = XAxis.ChannelName;
        gate.XMinimum = XAxis.Minimum;
        gate.XMaximum = XAxis.Maximum;
        gate.XScale = XAxis.Scale.Clone();
        copy_current_view_to_preferred_view(gate);
        if (gate.IsOneDimensional)
        {
            gate.YChannel = null;
            return;
        }

        gate.YChannel = YAxis.ChannelName;
        gate.YMinimum = YAxis.Minimum;
        gate.YMaximum = YAxis.Maximum;
        gate.YScale = YAxis.Scale.Clone();
        copy_current_view_to_preferred_view(gate);
    }

    private void copy_current_view_to_preferred_view(GateDefinition gate)
    {
        var view = current_gate_view();
        var region = selected_population?.Region ??
            (selected_node?.Kind == ProjectNodeKind.GatePopulationSlot ? selected_node.PopulationRegion : PopulationRegion.Primary);
        if (selected_node?.Kind == ProjectNodeKind.Population && selected_sample is not null && selected_population is not null)
        {
            gate.SamplePreferredViews[sample_preferred_view_key(selected_sample.Name, region)] = view;
            return;
        }

        if (region != PopulationRegion.Primary ||
            selected_node?.Kind is ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot)
        {
            gate.PopulationPreferredViews[region] = view;
            return;
        }

        gate.PreferredXChannel = view.XChannel;
        gate.PreferredXMinimum = view.XMinimum;
        gate.PreferredXMaximum = view.XMaximum;
        gate.PreferredXScale = view.XScale.Clone();
        gate.PreferredYChannel = view.YChannel;
        gate.PreferredYMinimum = view.YMinimum;
        gate.PreferredYMaximum = view.YMaximum;
        gate.PreferredYScale = view.YScale.Clone();
        gate.PreferredPlotMode = view.PlotMode;
        gate.PreferredShowOutlierPoints = view.ShowOutlierPoints;
        gate.PreferredDrawLargeDots = view.DrawLargeDots;
        gate.PreferredShowGridlines = view.ShowGridlines;
        gate.PreferredShowGateAnnotations = view.ShowGateAnnotations;
        gate.PreferredShowGateAnnotationNames = view.ShowGateAnnotationNames;
        gate.PreferredContourLevelCount = view.ContourLevelCount;
        gate.PreferredDensitySmoothing = view.DensitySmoothing;
        gate.PreferredDensityPalette = view.DensityPalette;
        gate.PreferredDotColor = clone_dot_color(view.DotColor);
    }

    private static string sample_preferred_view_key(string sample_name, PopulationRegion region) =>
        region == PopulationRegion.Primary ? sample_name : $"{sample_name}\t{(int)region}";

    private static GateViewOptions? legacy_preferred_view(GateDefinition gate) =>
        string.IsNullOrWhiteSpace(gate.PreferredXChannel)
            ? null
            : new GateViewOptions
            {
                XChannel = gate.PreferredXChannel,
                YChannel = gate.PreferredYChannel,
                XMinimum = gate.PreferredXMinimum,
                XMaximum = gate.PreferredXMaximum,
                XScale = gate.PreferredXScale,
                YMinimum = gate.PreferredYMinimum,
                YMaximum = gate.PreferredYMaximum,
                YScale = gate.PreferredYScale,
                PlotMode = gate.PreferredPlotMode,
                ShowOutlierPoints = gate.PreferredShowOutlierPoints,
                DrawLargeDots = gate.PreferredDrawLargeDots,
                ShowGridlines = gate.PreferredShowGridlines,
                ShowGateAnnotations = gate.PreferredShowGateAnnotations,
                ShowGateAnnotationNames = gate.PreferredShowGateAnnotationNames,
                ContourLevelCount = gate.PreferredContourLevelCount,
                DensitySmoothing = gate.PreferredDensitySmoothing,
                DensityPalette = gate.PreferredDensityPalette,
                DotColor = clone_dot_color(gate.PreferredDotColor)
            };

    private GateViewOptions current_gate_view()
    {
        var view = new GateViewOptions
        {
            XChannel = XAxis.ChannelName,
            XMinimum = XAxis.Minimum,
            XMaximum = XAxis.Maximum,
            XScale = XAxis.Scale.Clone(),
            PlotMode = EffectivePlotMode,
            ShowOutlierPoints = ShowOutlierPoints,
            DrawLargeDots = DrawLargeDots,
            ShowGridlines = ShowGridlines,
            ShowGateAnnotations = ShowGateAnnotations,
            ShowGateAnnotationNames = ShowGateAnnotationNames,
            ContourLevelCount = ContourLevelCount,
            DensitySmoothing = DensitySmoothing,
            DensityPalette = density_palette,
            DotColor = DotColorClone()
        };
        if (IsYAxisEnabled)
        {
            view.YChannel = YAxis.ChannelName;
            view.YMinimum = YAxis.Minimum;
            view.YMaximum = YAxis.Maximum;
            view.YScale = YAxis.Scale.Clone();
        }
        return view;
    }

    private void sync_selected_gate_preferred_view()
    {
        if (applying_node_selection || applying_gate_view_options)
            return;

        if (selected_gate is not null)
        {
            bool had_marker = current_population_context_has_own_view_option();
            copy_current_view_to_preferred_view(selected_gate);
            if (!had_marker)
                refresh_project_tree_preserving_selection();
            return;
        }

        if (selected_group is not null &&
            selected_population is null &&
            selected_node is not null &&
            is_root_view_node_kind(selected_node.Kind))
        {
            bool had_marker = current_root_context_has_own_view_option(selected_group);
            set_root_view_for_current_context(selected_group, current_gate_view());
            if (!had_marker)
                refresh_project_tree_preserving_selection();
        }
    }

    private static bool is_root_view_node_kind(ProjectNodeKind? kind) =>
        kind is null or ProjectNodeKind.Group or ProjectNodeKind.Sample or ProjectNodeKind.GateFolder;

    private bool current_root_context_has_own_view_option(FlowGroup group) =>
        selected_node?.Kind == ProjectNodeKind.Sample && selected_sample is not null
            ? group.SampleRootViewOptions.TryGetValue(selected_sample.Name, out var sample_view) && sample_view.HasView
            : group.RootViewOptions.HasView;

    private bool current_population_context_has_own_view_option()
    {
        if (selected_gate is null)
            return false;

        var region = selected_population?.Region ??
            (selected_node?.Kind == ProjectNodeKind.GatePopulationSlot ? selected_node.PopulationRegion : PopulationRegion.Primary);
        if (selected_node?.Kind == ProjectNodeKind.Population &&
            selected_sample is not null &&
            selected_population is not null)
        {
            return selected_gate.SamplePreferredViews.TryGetValue(sample_preferred_view_key(selected_sample.Name, region), out var sample_view) &&
                sample_view.HasView;
        }

        return selected_gate.PopulationPreferredViews.TryGetValue(region, out var population_view) && population_view.HasView ||
            region == PopulationRegion.Primary && legacy_preferred_view(selected_gate)?.HasView == true;
    }

    private void refresh_project_tree_preserving_selection()
    {
        string? selected_key = selected_node?.Key;
        refresh_project_tree();
        if (string.IsNullOrWhiteSpace(selected_key))
            return;

        var replacement = find_project_node(selected_key);
        if (replacement is null)
            return;

        selected_node = replacement;
        replacement.IsSelected = true;
        OnPropertyChanged(nameof(SelectedNode));
    }

    private void schedule_plot_transform_preparation()
    {
        plot_transform_preparation_cancellation?.Cancel();

        var samples = selected_group?.Samples.ToArray() ?? [];
        if (selected_sample is not null)
            samples = samples.Where(sample => ReferenceEquals(sample, selected_sample)).ToArray();

        if (samples.Length == 0 || string.IsNullOrWhiteSpace(XAxis.ChannelName))
        {
            IsPlotTransformPreparing = false;
            return;
        }

        var x_axis = XAxisClone();
        var y_axis = IsYAxisEnabled ? YAxisClone() : null;
        var population_gate = selected_population?.Gate;
        var population_region = selected_population?.Region;
        var parent_gate = selected_gate?.Parent;
        var parent_region = selected_gate?.ParentPopulationRegion ?? PopulationRegion.Primary;
        var targets = samples
            .Select(sample => (
                Sample: sample,
                Population: resolve_transform_population(sample, population_gate, population_region, parent_gate, parent_region)))
            .ToArray();

        var cancellation = new CancellationTokenSource();
        plot_transform_preparation_cancellation = cancellation;
        IsPlotTransformPreparing = true;

        _ = prepare_plot_transforms_async(targets, x_axis, y_axis, cancellation);
    }

    private static PopulationResult? resolve_transform_population(
        FlowSample sample,
        GateDefinition? population_gate,
        PopulationRegion? population_region,
        GateDefinition? parent_gate,
        PopulationRegion parent_region)
    {
        if (population_gate is not null && population_region is { } region)
            return find_population(sample.Populations, population_gate, region);
        if (parent_gate is not null)
            return find_population(sample.Populations, parent_gate, parent_region);
        return null;
    }

    private async Task prepare_plot_transforms_async(
        (FlowSample Sample, PopulationResult? Population)[] targets,
        AxisSettings x_axis,
        AxisSettings? y_axis,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await Task.Run(() =>
            {
                foreach (var target in targets)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    prepare_axis_transform(target.Sample, target.Population, x_axis, cancellation.Token);
                    if (y_axis is not null && !string.IsNullOrWhiteSpace(y_axis.ChannelName))
                        prepare_axis_transform(target.Sample, target.Population, y_axis, cancellation.Token);
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(plot_transform_preparation_cancellation, cancellation))
                {
                    cancellation.Dispose();
                    return;
                }

                plot_transform_preparation_cancellation.Dispose();
                plot_transform_preparation_cancellation = null;
                IsPlotTransformPreparing = false;
            });
        }
    }

    private static void prepare_axis_transform(FlowSample sample, PopulationResult? population, AxisSettings axis, CancellationToken cancellation_token)
    {
        if (string.IsNullOrWhiteSpace(axis.ChannelName))
            return;

        if (population is null)
            sample.GetNormalizedChannelValues(axis.ChannelName, axis.Minimum, axis.Maximum, axis.Scale, cancellation_token);
        else
            population.GetNormalizedChannelValues(sample, axis.ChannelName, axis.Minimum, axis.Maximum, axis.Scale, cancellation_token);
    }

    private static ChannelDefinition? get_default_x_channel(FlowGroup group) =>
        find_channel(group, "FSC-A") ?? group.Channels.FirstOrDefault();

    private static ChannelDefinition get_default_y_channel(FlowGroup group, ChannelDefinition x_channel) =>
        find_channel(group, "SSC-A")
        ?? group.Channels.FirstOrDefault(channel => channel.Name != x_channel.Name)
        ?? x_channel;

    private static ChannelDefinition? find_channel(FlowGroup group, string channel_name) =>
        group.Channels.FirstOrDefault(channel => string.Equals(channel.Name, channel_name, StringComparison.OrdinalIgnoreCase));

    private void refresh_selection_sidebars()
    {
        ChannelRows.Clear();
        if (selected_group is null)
        {
            refresh_axis_choices();
            refresh_equivalent_sample_choices();
            return;
        }

        bool can_remove_channel = selected_group.Gates.Count == 0 && selected_group.Channels.Count > 1;
        string remove_tip = selected_group.Gates.Count == 0
            ? "Remove channel"
            : "Channels cannot be removed after child strategies exist";
        foreach (var channel in selected_group.Channels)
            ChannelRows.Add(new ChannelRow(
                channel,
                RemoveChannelCommand,
                can_remove_channel,
                remove_tip,
                update_channel_name,
                update_channel_label));

        refresh_axis_choices();
        refresh_equivalent_sample_choices();
    }

    private void refresh_equivalent_sample_choices()
    {
        syncing_equivalent_sample_choices = true;
        try
        {
            EquivalentSampleChoices.Clear();
            if (selected_group is not null)
            {
                foreach (var sample in selected_group.Samples)
                {
                    var equivalent_population = equivalent_population_for_sample(sample);
                    if (selected_gate is not null && selected_population is not null && equivalent_population is null)
                        continue;

                    EquivalentSampleChoices.Add(new EquivalentSampleChoice(sample, equivalent_population));
                }
            }

            selected_equivalent_sample_choice = EquivalentSampleChoices.FirstOrDefault(choice => ReferenceEquals(choice.Sample, selected_sample));
            OnPropertyChanged(nameof(SelectedEquivalentSampleChoice));
        }
        finally
        {
            syncing_equivalent_sample_choices = false;
        }

        raise_command_states();
    }

    private PopulationResult? equivalent_population_for_sample(FlowSample sample)
    {
        if (selected_gate is null || selected_population is null)
            return null;

        return resolve_population_for_slot(sample, selected_gate, selected_population.Region);
    }

    private void select_relative_equivalent_sample(int direction)
    {
        if (EquivalentSampleChoices.Count == 0)
            return;

        int current_index = selected_equivalent_sample_choice is null
            ? -1
            : EquivalentSampleChoices.IndexOf(selected_equivalent_sample_choice);
        int next_index = current_index < 0
            ? 0
            : (current_index + direction + EquivalentSampleChoices.Count) % EquivalentSampleChoices.Count;
        SelectedEquivalentSampleChoice = EquivalentSampleChoices[next_index];
    }

    private void select_equivalent_sample(EquivalentSampleChoice choice)
    {
        SelectedSample = choice.Sample;
        if (selected_gate is not null && selected_population is not null)
            SelectedPopulation = choice.Population;

        if (selected_gate is not null)
            apply_axis_from_gate_context(selected_gate);

        OnPropertyChanged(nameof(PlotPopulation));
        refresh_axis_choices();
        refresh_selected_statistics();
        refresh_plot_gates();
    }

    private void dot_color_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DotColorSettings.ChannelName))
        {
            refresh_dot_color_range(DotColor, selected_group, selected_sample, selected_population, reset_selection: true);
            OnPropertyChanged(nameof(SelectedDotColorChoice));
        }

        if (e.PropertyName is nameof(DotColorSettings.Palette))
            OnPropertyChanged(nameof(SelectedDotColorMap));

        if (e.PropertyName is nameof(DotColorSettings.HasAvailableRange)
            or nameof(DotColorSettings.AvailableMinimum)
            or nameof(DotColorSettings.AvailableMaximum)
            or nameof(DotColorSettings.UseLogScale)
            or nameof(DotColorSettings.ChannelName))
            OnPropertyChanged(nameof(CanUseDotColorLogScale));

        sync_selected_gate_preferred_view();
        refresh_plot_gates();
    }

    private bool can_remove_channel(ChannelRow? row) =>
        selected_group is not null &&
        row is not null &&
        selected_group.Gates.Count == 0 &&
        selected_group.Channels.Count > 1 &&
        selected_group.Channels.Any(channel => string.Equals(channel.Name, row.Name, StringComparison.Ordinal));

    private void remove_channel(ChannelRow? row)
    {
        if (!can_remove_channel(row) || selected_group is null || row is null)
            return;

        string removed_name = row.Name;
        var remaining_channels = selected_group.Channels
            .Where(channel => !string.Equals(channel.Name, removed_name, StringComparison.Ordinal))
            .Select((channel, index) => new ChannelDefinition(index, channel.Name, channel.Label, channel.Maximum, channel.Gain))
            .ToArray();
        if (remaining_channels.Length == 0)
            return;

        foreach (var sample in selected_group.Samples)
            sample.ProjectChannels(remaining_channels);
        foreach (var sample in selected_group.ControlSamples)
            sample.ProjectChannels(remaining_channels);
        foreach (var spillover_row in selected_group.SpilloverCompensation.Rows.Where(item => item.ParameterName == removed_name))
            spillover_row.ParameterName = SpilloverControlRowViewModel.BlankParameterName;
        selected_group.RefreshChannelProfile();
        selected_group.ResetIdentityCompensation();
        selected_group.SampleRootViewOptions.Clear();
        repair_group_root_view_after_channel_removal(selected_group, removed_name);
        repair_page_elements_after_channel_removal(selected_group, removed_name);
        apply_axes_from_group_root_view(selected_group);
        refresh_selection_sidebars();
        refresh_spillover_workspace();
        refresh_workspace_sample_metadata();
        refresh_project_tree();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Removed channel: {removed_name}";
        raise_command_states();
    }

    private void repair_group_root_view_after_channel_removal(FlowGroup group, string removed_name)
    {
        var channels = group.Channels.ToArray();
        if (channels.Length == 0)
        {
            group.RootViewOptions = new GateViewOptions();
            return;
        }

        var fsc = channels.FirstOrDefault(channel => Configuration.IsFscChannel(channel.Name));
        var ssc = channels.FirstOrDefault(channel => Configuration.IsSscChannel(channel.Name));
        bool root_still_valid = group.RootViewOptions.HasView &&
            channels.Any(channel => channel.Name == group.RootViewOptions.XChannel) &&
            (string.IsNullOrWhiteSpace(group.RootViewOptions.YChannel) ||
             channels.Any(channel => channel.Name == group.RootViewOptions.YChannel));

        if (fsc is not null && ssc is not null && root_still_valid && !string.Equals(removed_name, group.RootViewOptions.XChannel, StringComparison.Ordinal) &&
            !string.Equals(removed_name, group.RootViewOptions.YChannel, StringComparison.Ordinal))
        {
            clear_removed_dot_color_channels(group, removed_name);
            return;
        }

        if (fsc is not null && ssc is not null)
        {
            group.RootViewOptions = create_default_root_view(group);
            clear_removed_dot_color_channels(group, removed_name);
            return;
        }

        var histogram_channel = fsc ?? ssc ?? channels[0];
        var x_axis = new AxisSettings { ChannelName = histogram_channel.Name };
        apply_channel_range_defaults(x_axis, group, sample: null, population: null);
        group.RootViewOptions = new GateViewOptions
        {
            XChannel = x_axis.ChannelName,
            XMinimum = x_axis.Minimum,
            XMaximum = x_axis.Maximum,
            XScale = x_axis.Scale.Clone(),
            YChannel = null,
            PlotMode = PlotMode.Histogram
        };
        clear_removed_dot_color_channels(group, removed_name);
    }

    private static void clear_removed_dot_color_channels(FlowGroup group, string removed_name)
    {
        foreach (var gate in flatten_gates(group.Gates))
        {
            if (gate.PreferredDotColor.ChannelName == removed_name)
                gate.PreferredDotColor.ChannelName = "";
            clear_removed_dot_color_channels(gate.PopulationPreferredViews.Values, removed_name);
            clear_removed_dot_color_channels(gate.SamplePreferredViews.Values, removed_name);
        }

        clear_removed_dot_color_channels([group.RootViewOptions], removed_name);
        clear_removed_dot_color_channels(group.SampleRootViewOptions.Values, removed_name);
    }

    private static void clear_removed_dot_color_channels(IEnumerable<GateViewOptions> views, string removed_name)
    {
        foreach (var view in views)
        {
            if (view.DotColor.ChannelName == removed_name)
                view.DotColor.ChannelName = "";
        }
    }

    private void repair_page_elements_after_channel_removal(FlowGroup group, string removed_name)
    {
        var channels = group.Channels.ToArray();
        if (channels.Length == 0)
            return;

        foreach (var element in Workspace.PageLayouts.SelectMany(layout => layout.Elements).Where(element => ReferenceEquals(element.Group, group)))
        {
            bool x_invalid = string.IsNullOrWhiteSpace(element.XAxis.ChannelName) ||
                !channels.Any(channel => channel.Name == element.XAxis.ChannelName);
            bool y_invalid = !string.IsNullOrWhiteSpace(element.YAxis.ChannelName) &&
                !channels.Any(channel => channel.Name == element.YAxis.ChannelName);
            if (x_invalid || y_invalid)
            {
                element.PlotMode = group.RootViewOptions.PlotMode;
                element.XAxis.ChannelName = group.RootViewOptions.XChannel;
                element.XAxis.Minimum = group.RootViewOptions.XMinimum;
                element.XAxis.Maximum = group.RootViewOptions.XMaximum;
                element.XAxis.Scale = group.RootViewOptions.XScale.Clone();
                element.YAxis.ChannelName = group.RootViewOptions.YChannel ?? "";
                element.YAxis.Minimum = group.RootViewOptions.YMinimum;
                element.YAxis.Maximum = group.RootViewOptions.YMaximum;
                element.YAxis.Scale = group.RootViewOptions.YScale.Clone();
            }

            if (element.DotColor.ChannelName == removed_name)
                element.DotColor.ChannelName = "";
        }
    }

    private void update_channel_label(ChannelRow row, string label)
    {
        if (selected_group is null)
            return;

        foreach (var channel in selected_group.Samples.SelectMany(sample => sample.Channels).Where(channel => channel.Name == row.Name))
            channel.Label = label;
        foreach (var channel in selected_group.ControlSamples.SelectMany(sample => sample.Channels).Where(channel => channel.Name == row.Name))
            channel.Label = label;

        refresh_axis_choices();
        refresh_spillover_workspace();
    }

    private string update_channel_name(ChannelRow row, string requested_name)
    {
        if (selected_group is null)
            return row.Name;

        string old_name = row.Name;
        string new_name = requested_name.Trim();
        if (string.IsNullOrWhiteSpace(new_name) || string.Equals(old_name, new_name, StringComparison.Ordinal))
            return old_name;
        if (selected_group.Channels.Any(channel => string.Equals(channel.Name, new_name, StringComparison.Ordinal)))
        {
            StatusText = $"Channel already exists: {new_name}";
            return old_name;
        }

        foreach (var channel in selected_group.Samples.SelectMany(sample => sample.Channels).Where(channel => channel.Name == old_name))
            channel.Name = new_name;
        foreach (var channel in selected_group.ControlSamples.SelectMany(sample => sample.Channels).Where(channel => channel.Name == old_name))
            channel.Name = new_name;

        foreach (var compensation in selected_group.CompensationCandidates)
            compensation.RenameChannel(old_name, new_name);

        rename_channel_references(selected_group, old_name, new_name);
        foreach (var spillover_row in selected_group.SpilloverCompensation.Rows.Where(item => item.ParameterName == old_name))
            spillover_row.ParameterName = new_name;
        selected_group.RenameChannelInProfile(old_name, new_name);
        refresh_spillover_workspace();
        StatusText = $"Renamed channel: {old_name} to {new_name}";
        return new_name;
    }

    private void rename_channel_references(FlowGroup group, string old_name, string new_name)
    {
        replace_axis_channel(XAxis, old_name, new_name);
        replace_axis_channel(YAxis, old_name, new_name);
        if (DotColor.ChannelName == old_name)
            DotColor.ChannelName = new_name;

        foreach (var gate in flatten_gates(group.Gates))
        {
            if (gate.XChannel == old_name)
                gate.XChannel = new_name;
            if (gate.YChannel == old_name)
                gate.YChannel = new_name;
            if (gate.PreferredXChannel == old_name)
                gate.PreferredXChannel = new_name;
            if (gate.PreferredYChannel == old_name)
                gate.PreferredYChannel = new_name;
            if (gate.PreferredDotColor.ChannelName == old_name)
                gate.PreferredDotColor.ChannelName = new_name;
            rename_channel_in_view_options(gate.PopulationPreferredViews.Values, old_name, new_name);
            rename_channel_in_view_options(gate.SamplePreferredViews.Values, old_name, new_name);
            foreach (var statistic in gate.Statistics.Where(statistic => statistic.ChannelName == old_name))
                statistic.ChannelName = new_name;
        }

        foreach (var statistic in group.Statistics.Where(statistic => statistic.ChannelName == old_name))
            statistic.ChannelName = new_name;

        rename_channel_in_view_options([group.RootViewOptions], old_name, new_name);
        rename_channel_in_view_options(group.SampleRootViewOptions.Values, old_name, new_name);

        foreach (var element in Workspace.PageLayouts.SelectMany(layout => layout.Elements).Where(element => ReferenceEquals(element.Group, group)))
        {
            replace_axis_channel(element.XAxis, old_name, new_name);
            replace_axis_channel(element.YAxis, old_name, new_name);
            if (element.DotColor.ChannelName == old_name)
                element.DotColor.ChannelName = new_name;
        }
    }

    private static void replace_axis_channel(AxisSettings axis, string old_name, string new_name)
    {
        if (axis.ChannelName == old_name)
            axis.ChannelName = new_name;
    }

    private static void rename_channel_in_view_options(IEnumerable<GateViewOptions> views, string old_name, string new_name)
    {
        foreach (var view in views)
        {
            if (view.XChannel == old_name)
                view.XChannel = new_name;
            if (view.YChannel == old_name)
                view.YChannel = new_name;
            if (view.DotColor.ChannelName == old_name)
                view.DotColor.ChannelName = new_name;
        }
    }

    private static IEnumerable<GateDefinition> flatten_gates(IEnumerable<GateDefinition> gates)
    {
        foreach (var gate in gates)
        {
            yield return gate;
            foreach (var child in flatten_gates(gate.Children))
                yield return child;
        }
    }

    private void refresh_axis_choices()
    {
        AxisChoices.Clear();
        ColorChoices.Clear();
        ColorChoices.Add(new AxisChoice("", "None"));
        if (selected_group is not null)
        {
            foreach (var channel in selected_group.Channels)
            {
                AxisChoices.Add(new AxisChoice(channel.Name, channel.Label));
                ColorChoices.Add(new AxisChoice(channel.Name, channel.Label));
            }
            foreach (string embedding_name in available_embedding_axis_names(selected_sample, selected_population))
            {
                AxisChoices.Add(new AxisChoice(embedding_name, ""));
                ColorChoices.Add(new AxisChoice(embedding_name, ""));
            }
        }

        refresh_selected_page_axis_choices();
        OnPropertyChanged(nameof(SelectedXAxisChoice));
        OnPropertyChanged(nameof(SelectedYAxisChoice));
        OnPropertyChanged(nameof(SelectedDotColorChoice));
        refresh_dot_color_range(DotColor, selected_group, selected_sample, selected_population);
        refresh_axis_menu_state();
    }

    private void refresh_selected_page_axis_choices()
    {
        SelectedPageAxisChoices.Clear();
        SelectedPageColorChoices.Clear();
        SelectedPageColorChoices.Add(new AxisChoice("", "None"));
        if (selected_page_element?.Group is { } group)
        {
            foreach (var channel in group.Channels)
            {
                SelectedPageAxisChoices.Add(new AxisChoice(channel.Name, channel.Label));
                SelectedPageColorChoices.Add(new AxisChoice(channel.Name, channel.Label));
            }
            foreach (string embedding_name in available_embedding_axis_names(selected_page_element.Sample, selected_page_element.Population))
            {
                SelectedPageAxisChoices.Add(new AxisChoice(embedding_name, ""));
                SelectedPageColorChoices.Add(new AxisChoice(embedding_name, ""));
            }
        }
        else
        {
            foreach (var choice in AxisChoices)
                SelectedPageAxisChoices.Add(choice);
            foreach (var choice in ColorChoices)
                SelectedPageColorChoices.Add(choice);
        }

        OnPropertyChanged(nameof(SelectedPageAxisChoices));
        OnPropertyChanged(nameof(SelectedPageColorChoices));
        OnPropertyChanged(nameof(SelectedPageXAxisChoice));
        OnPropertyChanged(nameof(SelectedPageYAxisChoice));
        OnPropertyChanged(nameof(SelectedPageDotColorChoice));
        if (selected_page_element is not null)
            refresh_dot_color_range(selected_page_element.DotColor, selected_page_element.Group, selected_page_element.Sample, selected_page_element.Population);
        OnPropertyChanged(nameof(SelectedPageDotColorMap));
        OnPropertyChanged(nameof(SelectedPageDensityColorMap));
        OnPropertyChanged(nameof(CanUseSelectedPageDotColorLogScale));
        refresh_axis_menu_state();
    }

    private IEnumerable<string> available_embedding_axis_names(FlowSample? sample, PopulationResult? population)
    {
        if (sample is null)
            return Array.Empty<string>();

        if (population is null)
            return sample.Embeddings.Keys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        return sample.Embeddings
            .Where(item => population.EventIndices.Any(index =>
                index >= 0 &&
                index < item.Value.Values.Length &&
                !float.IsNaN(item.Value.Values[index]) &&
                !float.IsInfinity(item.Value.Values[index])))
            .Select(item => item.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private void refresh_plot_gates()
    {
        PlotGates.Clear();
        IEnumerable<GateDefinition>? source = selected_gate?.Children ?? selected_group?.Gates;
        if (source is null)
            return;

        var region = selected_population?.Region ??
            (selected_node?.Kind == ProjectNodeKind.GatePopulationSlot ? selected_node.PopulationRegion : PopulationRegion.Primary);
        if (selected_gate is not null && region != PopulationRegion.Primary)
            source = source.Where(gate => gate.ParentPopulationRegion == region);

        foreach (var gate in source.Where(gate => gate_matches_current_axes(gate)))
            PlotGates.Add(gate);
    }

    private bool gate_matches_current_axes(GateDefinition gate)
    {
        if (gate.XChannel != XAxis.ChannelName)
            return false;
        if (gate.IsOneDimensional || string.IsNullOrWhiteSpace(gate.YChannel))
            return EffectivePlotMode == PlotMode.Histogram;

        return gate.YChannel == YAxis.ChannelName;
    }

    private void refresh_project_tree()
    {
        capture_project_expansion_state();
        project_roots.Clear();
        var workspace_node = create_project_node(ProjectNodeKind.Workspace, Workspace.Name, "workspace", depth: 0);
        workspace_node.Children.Add(create_project_node(ProjectNodeKind.Metadata, "Metadata", "workspace:metadata", count: Workspace.Groups.SelectMany(group => group.Samples).Count(), depth: 1));
        var layouts_node = create_project_node(ProjectNodeKind.LayoutFolder, "Layouts", "workspace:layouts", depth: 1);
        foreach (var layout in Workspace.PageLayouts)
            layouts_node.Children.Add(create_project_node(ProjectNodeKind.Layout, layout.Name, $"workspace:layout:{layout.Id}", layout: layout, count: layout.Elements.Count, depth: 2));
        workspace_node.Children.Add(layouts_node);
        var jobs_node = create_project_node(ProjectNodeKind.IntegrationJobFolder, "Platforms", "workspace:integration-jobs", depth: 1);
        foreach (var job in Workspace.IntegrationJobs)
            jobs_node.Children.Add(create_project_node(ProjectNodeKind.Platform, job.Name, $"workspace:integration-job:{job.Id}", integration_job: job, count: job.RowMap.Count, depth: 2));
        workspace_node.Children.Add(jobs_node);
        foreach (var group in Workspace.Groups)
        {
            string group_key = $"group:{group.Id}";
            var group_node = create_project_node(ProjectNodeKind.Group, group.Name, group_key, group: group, depth: 1);
            for (int index = 0; index < group.Statistics.Count; index++)
            {
                var statistic = group.Statistics[index];
                group_node.Children.Add(create_project_node(
                    ProjectNodeKind.StatisticDefinition,
                    statistic_name(statistic),
                    $"{group_key}:stat:{index}:{statistic.Kind}:{statistic.ChannelName}",
                    group: group,
                    statistic_definition: statistic,
                    depth: 2));
            }

            var gates_node = create_project_node(ProjectNodeKind.GateFolder, "Gating strategies", $"{group_key}:gates", group: group, depth: 2);
            foreach (var gate in group.Gates)
                append_gate_node(gates_node, gate, group, $"{group_key}:gate:{gate.Id}", 3);

            group_node.Children.Add(gates_node);
            var controls_node = create_project_node(ProjectNodeKind.ControlFolder, "Controls", $"{group_key}:controls", group: group, depth: 2);
            controls_node.Children.Add(create_project_node(
                ProjectNodeKind.SpilloverCompensation,
                "Spillover compensation",
                $"{group_key}:controls:spillover",
                group: group,
                count: group.SpilloverCompensation.Rows.Count,
                depth: 3));
            controls_node.Children.Add(create_project_node(
                ProjectNodeKind.SpectralUnmixing,
                "Spectral unmixing",
                $"{group_key}:controls:spectral",
                group: group,
                count: group.SpectralUnmixing.Rows.Count,
                depth: 3));
            foreach (var control_sample in group.ControlSamples)
            {
                controls_node.Children.Add(create_project_node(
                    ProjectNodeKind.ControlSample,
                    control_sample.Name,
                    $"{group_key}:control-sample:{control_sample.Id}",
                    group: group,
                    control_sample: control_sample,
                    count: control_sample.EventCount,
                    depth: 3));
            }
            group_node.Children.Add(controls_node);
            var compensations_node = create_project_node(ProjectNodeKind.CompensationFolder, "Compensations", $"{group_key}:compensations", group: group, depth: 2);
            foreach (var compensation in group.CompensationCandidates)
            {
                compensations_node.Children.Add(create_project_node(
                    ProjectNodeKind.Compensation,
                    compensation.Name,
                    $"{group_key}:compensation:{compensation.Id}",
                    group: group,
                    compensation: compensation,
                    is_applied_compensation: group.IsAppliedCompensation(compensation),
                    depth: 3));
            }
            group_node.Children.Add(compensations_node);
            foreach (var sample in group.Samples)
            {
                string sample_key = $"{group_key}:sample:{sample.Id}";
                var sample_node = create_project_node(ProjectNodeKind.Sample, sample.Name, sample_key, sample: sample, group: group, count: sample.EventCount, depth: 2);
                foreach (var embedding in sample.Embeddings.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    sample_node.Children.Add(create_project_node(
                        ProjectNodeKind.Embedding,
                        embedding.Key,
                        $"{sample_key}:embedding:{embedding.Key}",
                        group: group,
                        sample: sample,
                        embedding_name: embedding.Key,
                        count: count_embedding_values(embedding.Value),
                        depth: 3));
                }
                foreach (var population in sample.Populations)
                    append_population_node(sample_node, sample, population, group, $"{sample_key}:population:{population.Gate.Id}:{population.Region}", 3);
                group_node.Children.Add(sample_node);
            }
            workspace_node.Children.Add(group_node);
        }

        project_roots.Add(workspace_node);
        refresh_visible_project_nodes();
    }

    private ProjectNode create_project_node(
        ProjectNodeKind kind,
        string name,
        string key,
        FlowGroup? group = null,
        FlowSample? sample = null,
        GateDefinition? gate = null,
        PopulationResult? population = null,
        StatisticDefinition? statistic_definition = null,
        StatisticResult? statistic_result = null,
        CompensationMatrix? compensation = null,
        ControlSample? control_sample = null,
        PageLayout? layout = null,
        Platform? integration_job = null,
        string? embedding_name = null,
        PopulationRegion population_region = PopulationRegion.Primary,
        int? count = null,
        bool is_applied_compensation = false,
        int depth = 0)
    {
        return new ProjectNode(kind, name, key, group, sample, gate, population, statistic_definition, statistic_result, compensation, control_sample, layout, integration_job, embedding_name, population_region, count, is_applied_compensation, depth)
        {
            IsExpanded = project_expansion_state.TryGetValue(key, out bool is_expanded) ? is_expanded : default_project_node_expanded(kind)
        };
    }

    private static bool default_project_node_expanded(ProjectNodeKind kind) => kind switch
    {
        ProjectNodeKind.ControlFolder or
        ProjectNodeKind.CompensationFolder or
        ProjectNodeKind.Sample or
        ProjectNodeKind.ControlSample => false,
        _ => true
    };

    internal void RefreshProjectTreeForSpectral() => refresh_project_tree();

    private void append_gate_node(ProjectNode parent, GateDefinition gate, FlowGroup group, string key, int depth)
    {
        var gate_node = create_project_node(ProjectNodeKind.Gate, gate.Name, key, gate: gate, group: group, count: count_gate_events(group, gate), depth: depth);
        bool has_population_slots = gate.PopulationRegions.Any(region => region != PopulationRegion.Primary);
        foreach (var region in gate.PopulationRegions.Where(region => region != PopulationRegion.Primary))
        {
            var slot_node = create_project_node(
                ProjectNodeKind.GatePopulationSlot,
                gate.PopulationName(region),
                $"{key}:slot:{region}",
                gate: gate,
                group: group,
                population_region: region,
                count: count_gate_region_events(group, gate, region),
                depth: depth + 1);
            foreach (var child in gate.Children.Where(child => child.ParentPopulationRegion == region))
                append_gate_node(slot_node, child, group, $"{key}:slot:{region}:gate:{child.Id}", depth + 2);
            gate_node.Children.Add(slot_node);
        }

        for (int index = 0; index < gate.Statistics.Count; index++)
        {
            var statistic = gate.Statistics[index];
            gate_node.Children.Add(create_project_node(
                ProjectNodeKind.StatisticDefinition,
                statistic_name(statistic),
                $"{key}:stat:{index}:{statistic.Kind}:{statistic.ChannelName}",
                gate: gate,
                group: group,
                statistic_definition: statistic,
                depth: depth + 1));
        }

        foreach (var child in gate.Children.Where(child => !has_population_slots || child.ParentPopulationRegion == PopulationRegion.Primary))
            append_gate_node(gate_node, child, group, $"{key}:gate:{child.Id}", depth + 1);
        parent.Children.Add(gate_node);
    }

    private static string statistic_name(StatisticDefinition statistic)
    {
        if (!string.IsNullOrWhiteSpace(statistic.DisplayName))
            return statistic.DisplayName;

        switch (statistic.Kind)
        {
            case StatisticKind.Mean:
                return $"Mean of {statistic.ChannelName}";
            case StatisticKind.Median:
                return $"Median of {statistic.ChannelName}";
            case StatisticKind.GeometricMean:
                return $"Geometric Mean of {statistic.ChannelName}";
            case StatisticKind.StandardDeviation:
                return $"Standard Deviation of {statistic.ChannelName}";
            case StatisticKind.CoefficientOfVariation:
                return $"Coefficient of Variation of {statistic.ChannelName}";

            case StatisticKind.NumberOfEvents:
                return $"Number of Events";
            
            case StatisticKind.FrequencyOfParent:
                return $"Frequency of Parent (%)";
            case StatisticKind.FrequencyOfAll:
                return $"Frequency of All (%)";
            case StatisticKind.Python:
                return python_statistic_name(statistic);

            default: return $"{statistic.Kind}";
        }  
    }

    private void append_population_node(ProjectNode parent, FlowSample sample, PopulationResult population, FlowGroup group, string key, int depth)
    {
        var population_node = create_project_node(ProjectNodeKind.Population, population.DisplayName, key, group: group, sample: sample, gate: population.Gate, population: population, count: population.EventCount, depth: depth);
        for (int index = 0; index < population.Statistics.Count; index++)
        {
            var statistic = population.Statistics[index];
            population_node.Children.Add(create_project_node(
                ProjectNodeKind.StatisticValue,
                statistic.DisplayName,
                $"{key}:stat-value:{index}:{statistic.Kind}:{statistic.ChannelName}",
                group: group,
                sample: sample,
                gate: population.Gate,
                population: population,
                statistic_result: statistic,
                depth: depth + 1));
        }

        foreach (var child in population.Children)
            append_population_node(population_node, sample, child, group, $"{key}:population:{child.Gate.Id}:{child.Region}", depth + 1);
        parent.Children.Add(population_node);
    }

    private static int count_embedding_values(EmbeddingData embedding) =>
        embedding.Values.Count(value => !float.IsNaN(value) && !float.IsInfinity(value));

    private void refresh_visible_project_nodes()
    {
        ProjectNodes.Clear();
        foreach (var node in project_roots)
            append_visible_project_node(node);
    }

    private void append_visible_project_node(ProjectNode node)
    {
        ProjectNodes.Add(node);
        if (!node.IsExpanded)
            return;

        foreach (var child in node.Children)
            append_visible_project_node(child);
    }

    private void toggle_project_node(ProjectNode? node)
    {
        if (node is null || !node.HasChildren)
            return;

        node.IsExpanded = !node.IsExpanded;
        project_expansion_state[node.Key] = node.IsExpanded;
        sync_project_node_expansion(node);
        refresh_visible_project_nodes();
    }

    private void set_project_tree_expanded(bool is_expanded)
    {
        if (SelectedNode is not null)
            set_project_node_expanded(SelectedNode, is_expanded);
        else
            foreach (var root in project_roots)
                set_project_node_expanded(root, is_expanded);

        refresh_visible_project_nodes();
    }

    private void set_project_node_expanded(ProjectNode node, bool is_expanded)
    {
        node.IsExpanded = is_expanded;
        project_expansion_state[node.Key] = is_expanded;
        sync_project_node_expansion(node);
        foreach (var child in node.Children)
            set_project_node_expanded(child, is_expanded);
    }

    private void capture_project_expansion_state()
    {
        foreach (var node in project_roots)
            capture_project_expansion_state(node);
    }

    private void capture_project_expansion_state(ProjectNode node)
    {
        project_expansion_state[node.Key] = node.IsExpanded;
        sync_project_node_expansion(node);
        foreach (var child in node.Children)
            capture_project_expansion_state(child);
    }

    private static void sync_project_node_expansion(ProjectNode node)
    {
        if (node.Kind == ProjectNodeKind.Gate && node.Gate is not null)
            node.Gate.IsTreeExpanded = node.IsExpanded;
    }

    private void seed_loaded_workspace_expansion_state()
    {
        project_expansion_state["workspace"] = true;
        foreach (var group in Workspace.Groups)
        {
            string group_key = $"group:{group.Id}";
            project_expansion_state[group_key] = true;
            project_expansion_state[$"{group_key}:gates"] = true;
            project_expansion_state[$"{group_key}:controls"] = false;
            project_expansion_state[$"{group_key}:compensations"] = false;
            foreach (var gate in group.Gates)
                seed_gate_expansion_state(gate, $"{group_key}:gate:{gate.Id}");
            foreach (var sample in group.Samples)
                project_expansion_state[$"{group_key}:sample:{sample.Id}"] = false;
        }
    }

    private ProjectNode? find_project_node(string key)
    {
        foreach (var root in project_roots)
        {
            var found = find_project_node(root, key);
            if (found is not null)
                return found;
        }

        return null;
    }

    public ProjectNode? FindProjectNodeByKey(string key) =>
        string.IsNullOrWhiteSpace(key) ? null : find_project_node(key);

    private static ProjectNode? find_project_node(ProjectNode node, string key)
    {
        if (node.Key == key)
            return node;
        foreach (var child in node.Children)
        {
            var found = find_project_node(child, key);
            if (found is not null)
                return found;
        }

        return null;
    }

    private string? replacement_key_after_deleted_node(ProjectNode node)
    {
        if (!try_find_project_node_family(node, out var parent, out var siblings) || parent is null)
            return null;

        int index = siblings.IndexOf(node);
        if (index < 0)
            return parent.Key;
        if (index + 1 < siblings.Count)
            return siblings[index + 1].Key;
        if (index > 0)
            return siblings[index - 1].Key;
        return parent.Key;
    }

    private bool try_find_project_node_family(ProjectNode node, out ProjectNode? parent, out IList<ProjectNode> siblings)
    {
        parent = null;
        siblings = project_roots;
        if (project_roots.Contains(node))
            return true;

        foreach (var root in project_roots)
            if (try_find_project_node_family(root, node, out parent, out siblings))
                return true;

        siblings = Array.Empty<ProjectNode>();
        return false;
    }

    private static bool try_find_project_node_family(ProjectNode parent_node, ProjectNode node, out ProjectNode? parent, out IList<ProjectNode> siblings)
    {
        if (parent_node.Children.Contains(node))
        {
            parent = parent_node;
            siblings = parent_node.Children;
            return true;
        }

        foreach (var child in parent_node.Children)
            if (try_find_project_node_family(child, node, out parent, out siblings))
                return true;

        parent = null;
        siblings = Array.Empty<ProjectNode>();
        return false;
    }

    private void select_replacement_node(string? key)
    {
        SelectedNode = string.IsNullOrWhiteSpace(key) ? null : find_project_node(key);
    }

    private void seed_gate_expansion_state(GateDefinition gate, string key)
    {
        project_expansion_state[key] = gate.IsTreeExpanded;
        foreach (var child in gate.Children)
            seed_gate_expansion_state(child, $"{key}:gate:{child.Id}");
    }

    private async Task select_project_node_async(ProjectNode? node)
    {
        if (node?.Kind == ProjectNodeKind.Metadata && ReferenceEquals(selected_node, node))
        {
            refresh_workspace_sample_metadata();
            StatusText = "Workspace sample metadata";
            return;
        }

        if (!await TryLeavePythonScriptEditorAsync())
            return;

        SelectedNode = node;
    }

    private void raise_command_states()
    {
        foreach (var command in new[]
        {
            RenameGroupCommand,
            RenameWorkspaceCommand,
            RenameGateCommand,
            RenameLayoutCommand,
            RenameSelectedNodeCommand,
            ConcatenateSamplesCommand,
            CreateCompensationCommand,
            ApplyCompensationCommand,
            EditCompensationCommand,
            ReapplyCompensationCommand,
            OpenSpilloverCompensationCommand,
            RecalculateSelectedGroupCommand,
            RecalculateSelectedGateCommand,
            RecalculateSelectedStatisticCommand,
            CopyHierarchyViewOptionsToGroupCommand,
            RefreshSelectedLayoutCommand,
            DeleteSelectedCommand,
            CloseWorkspaceCommand,
            CreateIntegrationJobCommand,
            RenameIntegrationJobCommand,
            ApplyWorkspaceMetadataCommand,
            SelectPreviousEquivalentSampleCommand,
            SelectNextEquivalentSampleCommand,
            DropProjectNodeCommand,
            DropSpilloverControlCommand,
            CalculateSpilloverCompensationCommand,
            ApplySpilloverCompensationCommand,
            SelectPreviousSpilloverChannelCommand,
            SelectNextSpilloverChannelCommand,
            RemoveSpilloverControlCommand,
            MarkSpilloverPreviewOutdatedCommand,
            RemoveChannelCommand,
            AddMeanStatisticCommand,
            AddMedianStatisticCommand,
            AddGeometricMeanStatisticCommand,
            AddCoefficientOfVariationStatisticCommand,
            AddStandardDeviationStatisticCommand,
            AddFrequencyOfParentStatisticCommand,
            AddFrequencyOfAllStatisticCommand,
            AddCountStatisticCommand,
            AddPolygonGateCommand,
            AddRectangleGateCommand,
            AddOffsetQuadrantGateCommand,
            AddThresholdGateCommand,
            AddRangeGateCommand,
            AddMergeGateCommand,
            AddExcludeGateCommand,
            AddOverlapGateCommand
        })
        {
            if (command is RelayCommand relay)
                relay.RaiseCanExecuteChanged();
        }
    }

    private static int count_gate_events(FlowGroup group, GateDefinition gate)
    {
        int count = 0;
        foreach (var sample in group.Samples)
            count += find_populations(sample.Populations, gate).Sum(population => population.EventCount);
        return count;
    }

    private void refresh_selected_statistics()
    {
        statistic_table = new DataTable();
        statistic_table.Columns.Add("Sample", typeof(string));

        if (selected_group is null)
        {
            OnPropertyChanged(nameof(StatisticTable));
            OnPropertyChanged(nameof(StatisticTableView));
            return;
        }

        var definitions = selected_gate?.Statistics.ToArray() ?? selected_group.Statistics.ToArray();
        var column_names = new List<string>();
        foreach (var definition in definitions)
        {
            string column_name = unique_column_name(statistic_name(definition), column_names);
            column_names.Add(column_name);
            statistic_table.Columns.Add(column_name, typeof(string));
        }

        foreach (var sample in selected_group.Samples)
        {
            var row = statistic_table.NewRow();
            row["Sample"] = sample.Name;
            for (int index = 0; index < definitions.Length; index++)
            {
                if (selected_gate is not null)
                {
                    var population = selected_population is not null && selected_sample == sample
                        ? selected_population
                        : find_population(sample.Populations, selected_gate, selected_population?.Region);
                    var statistic = population?.Statistics.FirstOrDefault(item => statistic_matches_definition(item, definitions[index]));
                    row[column_names[index]] = statistic?.DisplayValue ?? "";
                }
                else
                {
                    var all_indices = Enumerable.Range(0, sample.EventCount).ToArray();
                    var statistic = StatisticsCalculator.Calculate(sample, definitions[index], all_indices, sample.EventCount, sample.EventCount);
                    row[column_names[index]] = statistic.DisplayValue;
                }
            }
            statistic_table.Rows.Add(row);
        }

        OnPropertyChanged(nameof(StatisticTable));
        OnPropertyChanged(nameof(StatisticTableView));
    }

    private static bool statistic_matches_definition(StatisticResult result, StatisticDefinition definition) =>
        result.Kind == definition.Kind &&
        result.ChannelName == definition.ChannelName &&
        (definition.Kind != StatisticKind.Python ||
         result.PythonDisplayName == python_statistic_name(definition) &&
         result.PythonSource == definition.PythonSource &&
         result.PythonCallableName == definition.PythonCallableName &&
         result.PythonParametersJson == definition.PythonParametersJson);

    private static string python_statistic_name(StatisticDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.PythonDisplayName) ? definition.PythonCallableName : definition.PythonDisplayName;

    private static string unique_column_name(string preferred_name, IReadOnlyCollection<string> existing_names)
    {
        if (!existing_names.Contains(preferred_name))
            return preferred_name;

        int index = 2;
        while (existing_names.Contains($"{preferred_name} {index}"))
            index++;

        return $"{preferred_name} {index}";
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, GateDefinition gate, PopulationRegion? region = null)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate && (region is null || population.Region == region))
                return population;
            var child = find_population(population.Children, gate, region);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static PopulationResult? resolve_population_for_slot(FlowSample? sample, GateDefinition? gate, PopulationRegion region) =>
        sample is null || gate is null ? null : find_population(sample.Populations, gate, region);

    private static IEnumerable<PopulationResult> find_populations(IEnumerable<PopulationResult> populations, GateDefinition gate)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate)
                yield return population;
            foreach (var child in find_populations(population.Children, gate))
                yield return child;
        }
    }

    private void add_page_element(PageDropRequest? request)
    {
        ensure_default_layout();
        if (request?.Node is not { } node)
            return;
        if (node.Kind == ProjectNodeKind.Platform && node.Platform is not null)
        {
            add_platform_page_elements(node.Platform, request.PagePoint);
            return;
        }
        if (node.Kind == ProjectNodeKind.StatisticDefinition && node.StatisticDefinition is not null && node.Group is not null)
        {
            add_or_extend_statistic_table(node, request.PagePoint);
            return;
        }
        if ((node.Kind is ProjectNodeKind.Group or ProjectNodeKind.Sample or ProjectNodeKind.GateFolder) && node.Group is not null)
        {
            add_root_page_element(node, request.PagePoint);
            return;
        }
        if (node.Kind is not (ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot or ProjectNodeKind.Population) || node.Gate is null || node.Group is null)
            return;

        var gate = node.Gate;
        var population_region = layout_population_region_for_node(node, gate);
        var preferred_view = preferred_view_for_node(gate, node);
        var (x_axis, y_axis) = axes_for_page_node(gate, node, preferred_view);

        if (string.IsNullOrWhiteSpace(x_axis.ChannelName))
            x_axis = XAxisClone();
        if (string.IsNullOrWhiteSpace(y_axis.ChannelName))
            y_axis = YAxisClone();

        var element = new PagePlotElement
        {
            Group = node.Group,
            Sample = node.Kind == ProjectNodeKind.Population ? node.Sample : null,
            Gate = gate,
            Population = node.Kind == ProjectNodeKind.Population ? node.Population : null,
            UsesPopulation = true,
            PopulationRegion = population_region,
            XAxis = x_axis,
            YAxis = y_axis,
            X = Math.Max(0, request.PagePoint.X - 130),
            Y = Math.Max(0, request.PagePoint.Y - 130),
            Size = 260,
            Title = node.Kind == ProjectNodeKind.Population && node.Sample is not null
                ? $"{node.Sample.Name} - {node.Name}"
                : $"{node.Group.Name} - {gate.PopulationName(population_region)}",
            PlotMode = gate.IsOneDimensional ? PlotMode.Histogram : preferred_view?.PlotMode ?? gate.PreferredPlotMode,
            ShowGridlines = preferred_view?.ShowGridlines ?? gate.PreferredShowGridlines,
            ShowOutlierPoints = preferred_view?.ShowOutlierPoints ?? gate.PreferredShowOutlierPoints,
            DrawLargeDots = preferred_view?.DrawLargeDots ?? gate.PreferredDrawLargeDots,
            ShowGateAnnotations = preferred_view?.ShowGateAnnotations ?? gate.PreferredShowGateAnnotations,
            ShowGateAnnotationNames = preferred_view?.ShowGateAnnotationNames ?? gate.PreferredShowGateAnnotationNames,
            ShowTickLabels = false,
            UsePseudocolor = true,
            DotColor = preferred_view is not null ? clone_dot_color(preferred_view.DotColor) : clone_dot_color(gate.PreferredDotColor),
            ContourLevelCount = preferred_view?.ContourLevelCount ?? gate.PreferredContourLevelCount,
            DensitySmoothing = preferred_view?.DensitySmoothing ?? gate.PreferredDensitySmoothing,
            DensityPalette = preferred_view?.DensityPalette ?? gate.PreferredDensityPalette
        };
        PageElements.Add(element);
        SelectedPageElement = element;
        refresh_project_tree();
        StatusText = $"Added page plot: {element.Title}";
    }

    private static PopulationRegion layout_population_region_for_node(ProjectNode node, GateDefinition gate) =>
        node.Kind switch
        {
            ProjectNodeKind.Population when node.Population is not null => node.Population.Region,
            ProjectNodeKind.GatePopulationSlot => node.PopulationRegion,
            _ => gate.PopulationRegions.FirstOrDefault()
        };

    private void add_root_page_element(ProjectNode node, Point page_point)
    {
        if (node.Group is null)
            return;

        var (x_axis, y_axis, view) = axes_for_root_page_node(node);
        var element = new PagePlotElement
        {
            Group = node.Group,
            Sample = node.Kind == ProjectNodeKind.Sample ? node.Sample : null,
            Gate = null,
            Population = null,
            XAxis = x_axis,
            YAxis = y_axis,
            X = Math.Max(0, page_point.X - 130),
            Y = Math.Max(0, page_point.Y - 130),
            Size = 260,
            Title = node.Kind == ProjectNodeKind.Sample && node.Sample is not null
                ? $"{node.Sample.Name} - Root"
                : $"{node.Group.Name} - Root",
            PlotMode = view?.PlotMode ?? EffectivePlotMode,
            ShowGridlines = view?.ShowGridlines ?? ShowGridlines,
            ShowOutlierPoints = view?.ShowOutlierPoints ?? ShowOutlierPoints,
            DrawLargeDots = view?.DrawLargeDots ?? DrawLargeDots,
            ShowGateAnnotations = view?.ShowGateAnnotations ?? ShowGateAnnotations,
            ShowGateAnnotationNames = view?.ShowGateAnnotationNames ?? ShowGateAnnotationNames,
            ShowTickLabels = false,
            UsePseudocolor = true,
            DotColor = view is not null ? clone_dot_color(view.DotColor) : DotColorClone(),
            ContourLevelCount = view?.ContourLevelCount ?? ContourLevelCount,
            DensitySmoothing = view?.DensitySmoothing ?? DensitySmoothing,
            DensityPalette = view?.DensityPalette ?? density_palette
        };
        PageElements.Add(element);
        SelectedPageElement = element;
        refresh_project_tree();
        StatusText = $"Added page plot: {element.Title}";
    }

    private void add_platform_page_elements(Platform platform, Point page_point)
    {
        bool has_graphics = platform_has_layout_graphics(platform);
        bool has_statistics = platform_has_layout_statistics(platform);
        if (!has_graphics && !has_statistics)
        {
            StatusText = "The selected platform cannot be placed onto a layout canvas.";
            return;
        }

        PlatformPlotElement? plot = null;
        if (has_graphics)
        {
            plot = new PlatformPlotElement
            {
                Platform = platform,
                PlotKey = "",
                X = Math.Max(0, page_point.X - 130),
                Y = Math.Max(0, page_point.Y - 98),
                Size = 260,
                Width = 260,
                Height = 195,
                Title = platform.Name
            };
            PageElements.Add(plot);
        }

        PlatformStatisticTableElement? table = null;
        if (has_statistics)
        {
            table = new PlatformStatisticTableElement
            {
                Platform = platform,
                ParentElementId = plot?.Id,
                X = plot is null ? Math.Max(0, page_point.X - 160) : plot.X + plot.Width + 18,
                Y = plot?.Y ?? Math.Max(0, page_point.Y - 100),
                Size = 240,
                Width = platform_layout_table_width(platform),
                Height = platform_layout_table_height(platform),
                Title = $"{platform.Name} statistics"
            };
            PageElements.Add(table);
        }

        SelectedPageElement = plot ?? (PagePlotElement?)table;
        refresh_project_tree();
        StatusText = has_graphics && has_statistics
            ? $"Added platform plot and statistics: {platform.Name}"
            : has_graphics
                ? $"Added platform plot: {platform.Name}"
                : $"Added platform statistics: {platform.Name}";
    }

    private static bool platform_has_layout_graphics(Platform platform)
    {
        if (platform.Kind == PlatformKind.Integration)
            return platform.PlotSeries.Any(series => series.X.Length > 0 && series.Y.Length > 0);
        if (platform.FitCurves.Count > 0 ||
            platform.PlotSeries.Any(series => series.X.Length > 0 && series.Y.Length > 0))
            return true;
        return platform.Compensated is not null &&
               platform.Compensated.GetLength(0) > 0 &&
               platform.Compensated.GetLength(1) > 0 &&
               platform.RowMap.SourceIds.Length > 0 &&
               platform.Populations.Any(row => row.IsPopulation && row.IsPlatformDropped);
    }

    private static bool platform_has_layout_statistics(Platform platform) =>
        platform.ResultTables.Any(table => table.Columns.Length > 0 && table.Rows.Count > 0) ||
        platform.PlatformStatistics.Count > 0;

    private void add_or_extend_statistic_table(ProjectNode node, Point page_point)
    {
        var target = PageElements
            .OfType<StatisticTableElement>()
            .LastOrDefault(element => element.Bounds.Contains(page_point));
        if (target is null)
        {
            target = new StatisticTableElement
            {
                Group = node.Group,
                Gate = node.Gate,
                X = Math.Max(0, page_point.X - 150),
                Y = Math.Max(0, page_point.Y - 100),
                Size = 320,
                Title = node.Group?.Name is { Length: > 0 } group_name ? $"{group_name} statistics" : "Statistics"
            };
            target.Width = target.MinimumWidth;
            target.Height = target.MinimumHeight;
            PageElements.Add(target);
        }

        target.Columns.Add(new StatisticTableColumn
        {
            Group = node.Group,
            Gate = node.Gate,
            Statistic = node.StatisticDefinition,
            Title = statistic_name(node.StatisticDefinition!)
        });
        target.Width = Math.Max(target.Width, target.MinimumWidth);
        target.Height = target.MinimumHeight;
        SelectedPageElement = target;
        refresh_project_tree();
        StatusText = $"Added statistic table column: {statistic_name(node.StatisticDefinition!)}";
    }

    public bool CanAddProjectNodeToLayout(ProjectNode? node) =>
        node?.Kind is ProjectNodeKind.Group
            or ProjectNodeKind.Sample
            or ProjectNodeKind.GateFolder
            or ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.Population
            or ProjectNodeKind.Platform;

    public void AddProjectNodeToLayout(ProjectNode node, PageLayout layout)
    {
        if (!CanAddProjectNodeToLayout(node) || !Workspace.PageLayouts.Contains(layout))
            return;

        SelectedPageLayout = layout;
        add_page_element(new PageDropRequest(node, next_layout_insertion_point(layout)));
        SelectedPageLayout = layout;
    }

    private static Point next_layout_insertion_point(PageLayout layout)
    {
        int index = layout.Elements.Count;
        double offset = (index % 8) * 24;
        return new Point(170 + offset, 170 + offset);
    }

    private static double platform_layout_table_width(Platform platform)
    {
        var element = new PlatformStatisticTableElement { Platform = platform };
        return element.MinimumWidth;
    }

    private static double platform_layout_table_height(Platform platform)
    {
        var element = new PlatformStatisticTableElement { Platform = platform };
        return element.MinimumHeight;
    }

    private GateViewOptions? preferred_view_for_node(GateDefinition gate, ProjectNode node)
    {
        var region = node.Kind switch
        {
            ProjectNodeKind.Population when node.Population is not null => node.Population.Region,
            ProjectNodeKind.GatePopulationSlot => node.PopulationRegion,
            _ => gate.PopulationRegions.FirstOrDefault()
        };

        if (node.Kind == ProjectNodeKind.Population && node.Sample is not null)
        {
            if (gate.SamplePreferredViews.TryGetValue(sample_preferred_view_key(node.Sample.Name, region), out var sample_view) ||
                gate.SamplePreferredViews.TryGetValue(node.Sample.Name, out sample_view))
                return sample_view;
        }

        if (gate.PopulationPreferredViews.TryGetValue(region, out var population_view))
            return population_view;

        return legacy_preferred_view(gate);
    }

    private (AxisSettings XAxis, AxisSettings YAxis) axes_for_page_node(GateDefinition gate, ProjectNode node, GateViewOptions? preferred_view)
    {
        bool has_preferred_view = preferred_view?.HasView == true;
        var x_axis = new AxisSettings
        {
            ChannelName = has_preferred_view ? preferred_view!.XChannel : gate.XChannel,
            Minimum = has_preferred_view ? preferred_view!.XMinimum : gate.XMinimum,
            Maximum = has_preferred_view ? preferred_view!.XMaximum : gate.XMaximum,
            Scale = (has_preferred_view ? preferred_view!.XScale : gate.XScale).Clone()
        };
        var y_axis = new AxisSettings
        {
            ChannelName = has_preferred_view ? preferred_view!.YChannel ?? gate.YChannel ?? "" : gate.YChannel ?? "",
            Minimum = has_preferred_view ? preferred_view!.YMinimum : gate.YMinimum,
            Maximum = has_preferred_view ? preferred_view!.YMaximum : gate.YMaximum,
            Scale = (has_preferred_view ? preferred_view!.YScale : gate.YScale).Clone()
        };

        if (string.IsNullOrWhiteSpace(x_axis.ChannelName))
            x_axis = XAxisClone();
        if (string.IsNullOrWhiteSpace(y_axis.ChannelName))
            y_axis = YAxisClone();

        return (x_axis, y_axis);
    }

    private (AxisSettings XAxis, AxisSettings YAxis, GateViewOptions? View) axes_for_root_page_node(ProjectNode node)
    {
        var group = node.Group!;
        var root_view = root_view_for_node(group, node);
        if (root_view?.HasView == true && group.Channels.Any(channel => channel.Name == root_view.XChannel))
        {
            var view = root_view;
            var x_axis = new AxisSettings
            {
                ChannelName = view.XChannel,
                Minimum = view.XMinimum,
                Maximum = view.XMaximum,
                Scale = view.XScale.Clone()
            };
            var y_axis = new AxisSettings
            {
                ChannelName = !string.IsNullOrWhiteSpace(view.YChannel) && group.Channels.Any(channel => channel.Name == view.YChannel)
                    ? view.YChannel!
                    : get_default_y_channel(group, group.Channels.First(channel => channel.Name == view.XChannel))?.Name ?? "",
                Minimum = view.YMinimum,
                Maximum = view.YMaximum,
                Scale = view.YScale.Clone()
            };
            if (string.IsNullOrWhiteSpace(y_axis.ChannelName))
                y_axis = YAxisClone();
            return (x_axis, y_axis, view);
        }

        var first = get_default_x_channel(group);
        if (first is null)
            return (XAxisClone(), YAxisClone(), null);
        var second = get_default_y_channel(group, first);
        var default_x = new AxisSettings { ChannelName = first.Name };
        var default_y = new AxisSettings { ChannelName = second.Name };
        apply_data_implied_axis_defaults(default_x, group, fallback_sample: null, fallback_population: null);
        apply_data_implied_axis_defaults(default_y, group, fallback_sample: null, fallback_population: null);
        return (default_x, default_y, null);
    }

    private bool node_matches_current_plot_context(ProjectNode node)
    {
        if (!ReferenceEquals(selected_node, node))
            return false;

        return node.Kind switch
        {
            ProjectNodeKind.Population =>
                ReferenceEquals(selected_sample, node.Sample) &&
                ReferenceEquals(selected_population, node.Population) &&
                ReferenceEquals(selected_gate, node.Gate),
            ProjectNodeKind.Gate =>
                ReferenceEquals(selected_gate, node.Gate),
            _ => false
        };
    }

    private AxisSettings XAxisClone() =>
        new() { ChannelName = XAxis.ChannelName, Minimum = XAxis.Minimum, Maximum = XAxis.Maximum, Scale = XAxis.Scale.Clone() };

    private AxisSettings YAxisClone() =>
        new() { ChannelName = YAxis.ChannelName, Minimum = YAxis.Minimum, Maximum = YAxis.Maximum, Scale = YAxis.Scale.Clone() };

    private DotColorSettings DotColorClone() =>
        clone_dot_color(DotColor);

    private static DotColorSettings clone_dot_color(DotColorSettings source)
    {
        var clone = new DotColorSettings { ChannelName = source.ChannelName, Palette = source.Palette };
        clone.SetAvailableRangeForChannel(source.ChannelName, source.AvailableMinimum, source.AvailableMaximum, source.ClampNegativeValuesToZero);
        clone.SetRange(source.RangeMinimum, source.RangeMaximum);
        clone.UseLogScale = source.UseLogScale;
        return clone;
    }

    private static string normalize_recent_file_path(string file_path)
    {
        try
        {
            return System.IO.Path.GetFullPath(file_path);
        }
        catch
        {
            return file_path;
        }
    }

    private void delete_selected_page_element()
    {
        if (selected_page_element is null)
            return;
        int removed_index = PageElements.IndexOf(selected_page_element);
        var removed_id = selected_page_element.Id;
        PageElements.Remove(selected_page_element);
        foreach (var linked in PageElements.Where(element => element.ParentElementId == removed_id).ToArray())
            PageElements.Remove(linked);
        SelectedPageElement = PageElements.Count == 0
            ? null
            : PageElements[Math.Clamp(removed_index, 0, PageElements.Count - 1)];
        refresh_project_tree();
    }

    private void apply_page_axis_channel_defaults(AxisSettings axis)
    {
        apply_data_implied_axis_defaults(
            axis,
            selected_page_element?.Group ?? selected_group,
            selected_page_element?.Sample,
            selected_page_element?.Population);
    }

    private void apply_axis_channel_defaults(AxisSettings axis)
    {
        if (try_apply_inherited_gate_axis_defaults(axis))
            return;

        apply_data_implied_axis_defaults(axis, selected_group, selected_sample, selected_population);
    }

    private bool try_apply_inherited_gate_axis_defaults(AxisSettings axis)
    {
        bool is_x_axis = ReferenceEquals(axis, XAxis);
        if (try_create_inherited_gate_axis_defaults(is_x_axis, axis.ChannelName) is not { } inherited)
            return false;

        axis.Minimum = inherited.Minimum;
        axis.Maximum = inherited.Maximum;
        axis.Scale = inherited.Scale.Clone();
        return true;
    }

    private AxisSettings? try_create_inherited_gate_axis_defaults(bool is_x_axis, string channel_name)
    {
        if (selected_gate is null)
            return null;

        var view = preferred_view_for_current_context(selected_gate);
        bool has_preferred_view = view?.HasView == true;
        string? inherited_channel = is_x_axis ? selected_gate.XChannel : selected_gate.YChannel;
        if (string.IsNullOrWhiteSpace(inherited_channel) ||
            !string.Equals(channel_name, inherited_channel, StringComparison.Ordinal))
            return null;

        bool preferred_matches_inherited = has_preferred_view &&
            string.Equals(is_x_axis ? view!.XChannel : view!.YChannel, inherited_channel, StringComparison.Ordinal);

        return new AxisSettings
        {
            ChannelName = channel_name,
            Minimum = is_x_axis
                ? (preferred_matches_inherited ? view!.XMinimum : selected_gate.XMinimum)
                : (preferred_matches_inherited ? view!.YMinimum : selected_gate.YMinimum),
            Maximum = is_x_axis
                ? (preferred_matches_inherited ? view!.XMaximum : selected_gate.XMaximum)
                : (preferred_matches_inherited ? view!.YMaximum : selected_gate.YMaximum),
            Scale = (is_x_axis
                ? (preferred_matches_inherited ? view!.XScale : selected_gate.XScale)
                : (preferred_matches_inherited ? view!.YScale : selected_gate.YScale)).Clone()
        };
    }

    private static void apply_data_implied_axis_defaults(
        AxisSettings axis,
        FlowGroup? group,
        FlowSample? fallback_sample,
        PopulationResult? fallback_population)
    {
        if (group?.DataImpliedViewOptions.TryGetValue(axis.ChannelName, out var preset) == true)
        {
            axis.Minimum = preset.Minimum;
            axis.Maximum = preset.Maximum;
            axis.Scale = preset.Scale.Clone();
            return;
        }

        apply_channel_range_defaults(axis, group, fallback_sample, fallback_population);
    }

    private static void apply_channel_range_defaults(AxisSettings axis, FlowGroup? group, FlowSample? sample, PopulationResult? population)
    {
        var channel = group?.Channels.FirstOrDefault(item => item.Name == axis.ChannelName);
        if (channel is null)
        {
            var values = embedding_values(sample, population, axis.ChannelName)
                .Where(value => !float.IsNaN(value) && !float.IsInfinity(value))
                .Select(value => (double)value)
                .ToArray();
            if (values is { Length: > 0 })
            {
                double minimum = values.Min();
                double maximum = values.Max();
                double margin = Math.Max((maximum - minimum) * 0.05, 1e-6);
                axis.Minimum = minimum - margin;
                axis.Maximum = maximum + margin;
                axis.ScaleKind = CoordinateScaleKind.Linear;
            }
            return;
        }

        var range = Configuration.DefaultChannelRange(channel.Maximum);
        axis.Minimum = range.Minimum;
        axis.Maximum = range.Maximum;
        axis.ScaleKind = Configuration.DefaultCoordinateScaleForChannel(
            channel.Name,
            Configuration.CytometerNameForSample(sample ?? group?.Samples.FirstOrDefault()));
        double theoretical_maximum = double.IsFinite(channel.Maximum) && channel.Maximum > 0
            ? channel.Maximum
            : range.Maximum;
        axis.Maximum = Math.Min(axis.Maximum, theoretical_maximum);
        if (actual_channel_range(group, sample, population, channel.Name) is not { } actual_range ||
            !double.IsFinite(actual_range.Minimum) ||
            !double.IsFinite(actual_range.Maximum))
            return;

        if (Configuration.IsTimeChannel(channel.Name))
        {
            axis.ScaleKind = CoordinateScaleKind.Linear;
            axis.Minimum = actual_range.Minimum;
            axis.Maximum = actual_range.Maximum > actual_range.Minimum
                ? actual_range.Maximum
                : actual_range.Minimum + 1e-6;
            return;
        }

        double actual_maximum = actual_range.Maximum;
        if (
            actual_maximum <= 0 ||
            !double.IsFinite(theoretical_maximum) ||
            theoretical_maximum <= 0)
            return;

        if (axis.ScaleKind == CoordinateScaleKind.Linear && actual_maximum < theoretical_maximum / 3.0)
        {
            axis.Maximum = Math.Min(Math.Max(actual_maximum * 1.5, axis.Minimum + 1e-6), theoretical_maximum);
        }
        else if (axis.ScaleKind == CoordinateScaleKind.Logicle && actual_maximum < theoretical_maximum / 100.0)
        {
            axis.Maximum = Math.Min(Math.Max(actual_maximum * 10.0, axis.Minimum + 1e-6), theoretical_maximum);
        }
    }

    private static (double Minimum, double Maximum)? actual_channel_range(FlowGroup? group, FlowSample? sample, PopulationResult? population, string channel_name)
    {
        if (string.IsNullOrWhiteSpace(channel_name))
            return null;

        var range = new ChannelRangeAccumulator();
        if (sample is not null)
        {
            accumulate_channel_range(sample, population?.EventIndices, channel_name, range);
        }
        else if (group is not null)
        {
            foreach (var group_sample in group.Samples)
                accumulate_channel_range(group_sample, event_indices: null, channel_name, range);
        }

        return range.TryGetRange(out var minimum, out var maximum) ? (minimum, maximum) : null;
    }

    private static void accumulate_channel_range(FlowSample sample, int[]? event_indices, string channel_name, ChannelRangeAccumulator range)
    {
        var values = sample.GetChannelValues(channel_name, event_indices);
        foreach (float value in values)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                continue;

            range.Add(value);
        }
    }

    private sealed class ChannelRangeAccumulator
    {
        private const int high_outlier_count = 50;
        private readonly PriorityQueue<double, double> largest_values = new();
        private readonly bool ignore_high_outliers;
        private double minimum = double.PositiveInfinity;
        private double raw_maximum = double.NegativeInfinity;
        private int valid_count;

        public ChannelRangeAccumulator(bool ignore_high_outliers = true)
        {
            this.ignore_high_outliers = ignore_high_outliers;
        }

        public void Add(double value)
        {
            valid_count++;
            if (value < minimum)
                minimum = value;
            if (value > raw_maximum)
                raw_maximum = value;

            largest_values.Enqueue(value, value);
            if (largest_values.Count > high_outlier_count + 1)
                largest_values.Dequeue();
        }

        public bool TryGetRange(out double range_minimum, out double range_maximum)
        {
            range_minimum = minimum;
            range_maximum = ignore_high_outliers && valid_count > high_outlier_count && largest_values.Count > high_outlier_count
                ? largest_values.UnorderedItems.Min(item => item.Element)
                : raw_maximum;
            return valid_count > 0;
        }
    }

    private static IEnumerable<float> embedding_values(FlowSample? sample, PopulationResult? population, string embedding_name)
    {
        if (sample is null ||
            population is null ||
            string.IsNullOrWhiteSpace(embedding_name) ||
            !sample.Embeddings.TryGetValue(embedding_name, out var embedding))
            return Array.Empty<float>();

        var values = embedding.Values;
        return population.EventIndices
            .Where(index => index >= 0 && index < values.Length)
            .Select(index => values[index]);
    }

    private void refresh_selected_page_menu_state()
    {
        OnPropertyChanged(nameof(IsLayoutDensityPlotMode));
        OnPropertyChanged(nameof(IsLayoutDotplotPlotMode));
        OnPropertyChanged(nameof(IsLayoutContourPlotMode));
        OnPropertyChanged(nameof(IsLayoutZebraPlotMode));
        OnPropertyChanged(nameof(IsLayoutHistogramPlotMode));
        OnPropertyChanged(nameof(ShowSelectedPageDensityStyleOptions));
        OnPropertyChanged(nameof(ShowSelectedPageDotplotStyleOptions));
        OnPropertyChanged(nameof(ShowSelectedPageContourDensityStyleOptions));
        OnPropertyChanged(nameof(IsLayoutXAxisLinearScale));
        OnPropertyChanged(nameof(IsLayoutXAxisLogicleScale));
        OnPropertyChanged(nameof(IsLayoutYAxisLinearScale));
        OnPropertyChanged(nameof(IsLayoutYAxisLogicleScale));
    }

    private void subscribe_selected_page_menu_element()
    {
        subscribed_page_menu_element = selected_page_element;
        if (subscribed_page_menu_element is null)
            return;

        subscribed_page_menu_element.PropertyChanged += selected_page_menu_element_changed;
        subscribed_page_menu_element.XAxis.PropertyChanged += selected_page_menu_axis_changed;
        subscribed_page_menu_element.YAxis.PropertyChanged += selected_page_menu_axis_changed;
        subscribed_page_menu_element.DotColor.PropertyChanged += selected_page_menu_axis_changed;
    }

    private void unsubscribe_selected_page_menu_element()
    {
        if (subscribed_page_menu_element is null)
            return;

        subscribed_page_menu_element.PropertyChanged -= selected_page_menu_element_changed;
        subscribed_page_menu_element.XAxis.PropertyChanged -= selected_page_menu_axis_changed;
        subscribed_page_menu_element.YAxis.PropertyChanged -= selected_page_menu_axis_changed;
        subscribed_page_menu_element.DotColor.PropertyChanged -= selected_page_menu_axis_changed;
        subscribed_page_menu_element = null;
    }

    private void selected_page_menu_element_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PagePlotElement.PlotMode))
            refresh_selected_page_menu_state();
        if (e.PropertyName == nameof(PagePlotElement.DensityPalette))
            OnPropertyChanged(nameof(SelectedPageDensityColorMap));
    }

    private void selected_page_menu_axis_changed(object? sender, PropertyChangedEventArgs e)
    {
        refresh_selected_page_menu_state();
        var element = selected_page_element;
        if (element is not null && ReferenceEquals(sender, element.XAxis))
        {
            if (e.PropertyName == nameof(AxisSettings.ChannelName))
                apply_page_axis_channel_defaults(element.XAxis);
            OnPropertyChanged(nameof(SelectedPageXAxisChoice));
            refresh_axis_menu_state();
        }
        if (element is not null && ReferenceEquals(sender, element.YAxis))
        {
            if (e.PropertyName == nameof(AxisSettings.ChannelName))
                apply_page_axis_channel_defaults(element.YAxis);
            OnPropertyChanged(nameof(SelectedPageYAxisChoice));
            refresh_axis_menu_state();
        }
        if (sender is DotColorSettings page_dot_color && ReferenceEquals(page_dot_color, selected_page_element?.DotColor))
        {
            if (e.PropertyName == nameof(DotColorSettings.ChannelName))
                refresh_dot_color_range(page_dot_color, selected_page_element.Group, selected_page_element.Sample, selected_page_element.Population, reset_selection: true);
            if (e.PropertyName == nameof(DotColorSettings.Palette))
                OnPropertyChanged(nameof(SelectedPageDotColorMap));
            OnPropertyChanged(nameof(CanUseSelectedPageDotColorLogScale));
            OnPropertyChanged(nameof(SelectedPageDotColorChoice));
            refresh_axis_menu_state();
        }
    }

    private static void refresh_dot_color_range(DotColorSettings settings, FlowGroup? group, FlowSample? sample, PopulationResult? population, bool reset_selection = false)
    {
        if (string.IsNullOrWhiteSpace(settings.ChannelName) ||
            actual_dot_color_range(group, sample, population, settings.ChannelName) is not { } range)
        {
            settings.SetAvailableRange(double.NaN, double.NaN);
            return;
        }

        settings.SetAvailableRangeForChannel(settings.ChannelName, range.Minimum, range.Maximum, range.ClampNegativeValuesToZero, force_reset_selection: reset_selection);
    }

    private static (double Minimum, double Maximum, bool ClampNegativeValuesToZero)? actual_dot_color_range(FlowGroup? group, FlowSample? sample, PopulationResult? population, string channel_name)
    {
        if (dot_color_channel_definition(group, sample, channel_name) is { } channel)
        {
            var theoretical_range = Configuration.DefaultChannelRange(channel.Maximum);
            double theoretical_maximum = double.IsFinite(channel.Maximum) && channel.Maximum > 0
                ? channel.Maximum
                : theoretical_range.Maximum;
            return (0, theoretical_maximum, true);
        }

        var range = new ChannelRangeAccumulator(ignore_high_outliers: false);
        if (sample is not null)
        {
            accumulate_dot_color_range(sample, population?.EventIndices, channel_name, range);
        }
        else if (group is not null)
        {
            foreach (var group_sample in group.Samples)
                accumulate_dot_color_range(group_sample, event_indices: null, channel_name, range);
        }

        return range.TryGetRange(out var minimum, out var maximum)
            ? (minimum, maximum, false)
            : null;
    }

    private static void accumulate_dot_color_range(FlowSample sample, int[]? event_indices, string channel_name, ChannelRangeAccumulator range)
    {
        var values = sample.GetChannelValues(channel_name, event_indices);
        foreach (float value in values)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                continue;

            range.Add(value);
        }
    }

    private static ChannelDefinition? dot_color_channel_definition(FlowGroup? group, FlowSample? sample, string channel_name) =>
        sample?.Channels.FirstOrDefault(channel => channel.Name == channel_name) ??
        group?.Channels.FirstOrDefault(channel => channel.Name == channel_name);

    private void refresh_axis_menu_state()
    {
        foreach (var choice in AxisChoices)
        {
            choice.IsEditorXSelected = choice.Name == XAxis.ChannelName;
            choice.IsEditorYSelected = choice.Name == YAxis.ChannelName;
        }

        foreach (var choice in ColorChoices)
            choice.IsEditorColorSelected = choice.Name == DotColor.ChannelName;

        foreach (var choice in SelectedPageAxisChoices)
        {
            choice.IsLayoutXSelected = selected_page_element is not null && choice.Name == selected_page_element.XAxis.ChannelName;
            choice.IsLayoutYSelected = selected_page_element is not null && choice.Name == selected_page_element.YAxis.ChannelName;
        }

        foreach (var choice in SelectedPageColorChoices)
            choice.IsLayoutColorSelected = selected_page_element is not null && choice.Name == selected_page_element.DotColor.ChannelName;
    }

    private void refresh_spillover_workspace()
    {
        var selected_id = selected_spillover_row?.Sample.Id;
        SpilloverGatePresets.Clear();
        if (selected_group is not null)
        {
            _ = selected_group.SpilloverCompensation.DefaultGatePreset;
            string fsc = selected_group.Channels.FirstOrDefault(channel => Configuration.IsFscChannel(channel.Name))?.Name ?? selected_group.Channels.FirstOrDefault()?.Name ?? "FSC-A";
            string ssc = selected_group.Channels.FirstOrDefault(channel => Configuration.IsSscChannel(channel.Name))?.Name ?? selected_group.Channels.Skip(1).FirstOrDefault()?.Name ?? fsc;
            foreach (var preset in selected_group.SpilloverCompensation.GatePresets)
            {
                if (selected_group.Channels.All(channel => channel.Name != preset.XChannel)) preset.XChannel = fsc;
                if (selected_group.Channels.All(channel => channel.Name != preset.YChannel) ||
                    string.Equals(preset.YChannel, preset.XChannel, StringComparison.Ordinal))
                    preset.YChannel = distinct_channel(selected_group, ssc, preset.XChannel);
            }
            foreach (var preset in selected_group.SpilloverCompensation.GatePresets) SpilloverGatePresets.Add(preset);
        }
        refresh_spillover_choices();
        refresh_spillover_rows();
        SelectedSpilloverRow = selected_id.HasValue
            ? SpilloverRows.FirstOrDefault(row => row.Sample.Id == selected_id.Value) ?? SpilloverRows.FirstOrDefault()
            : SpilloverRows.FirstOrDefault();
        OnPropertyChanged(nameof(SpilloverPrimaryVertices));
        OnPropertyChanged(nameof(SpilloverFscChannel));
        OnPropertyChanged(nameof(SpilloverSscChannel));
        OnPropertyChanged(nameof(SpilloverScatterXMinimum));
        OnPropertyChanged(nameof(SpilloverScatterXMaximum));
        OnPropertyChanged(nameof(SpilloverScatterXScale));
        OnPropertyChanged(nameof(SpilloverScatterYMinimum));
        OnPropertyChanged(nameof(SpilloverScatterYMaximum));
        OnPropertyChanged(nameof(SpilloverScatterYScale));
        OnPropertyChanged(nameof(SpilloverMatrixName));
        OnPropertyChanged(nameof(SpilloverScatterSample));
        refresh_spillover_configured_choices();
        refresh_spillover_population_text();
        refresh_spillover_histogram();
        raise_command_states();
    }

    private void new_spillover_gate_preset()
    {
        if (selected_group is null) return;
        var source = SelectedSpilloverGatePreset ?? selected_group.SpilloverCompensation.DefaultGatePreset;
        int index = selected_group.SpilloverCompensation.GatePresets.Count + 1;
        var preset = new ControlGatePreset
        {
            Name = $"Gate {index}", XChannel = source.XChannel, YChannel = source.YChannel,
            XAxis = new AxisSettings { ChannelName = source.XAxis.ChannelName, Minimum = source.XAxis.Minimum, Maximum = source.XAxis.Maximum, Scale = source.XAxis.Scale.Clone() },
            YAxis = new AxisSettings { ChannelName = source.YAxis.ChannelName, Minimum = source.YAxis.Minimum, Maximum = source.YAxis.Maximum, Scale = source.YAxis.Scale.Clone() }
        };
        foreach (var point in source.Vertices) preset.Vertices.Add(point);
        selected_group.SpilloverCompensation.GatePresets.Add(preset); SpilloverGatePresets.Add(preset); SelectedSpilloverGatePreset = preset;
    }

    private void refresh_spillover_choices()
    {
        SpilloverParameterChoices.Clear();
        SpilloverParameterChoices.Add(SpilloverControlRowViewModel.BlankParameterName);
        if (selected_group is null)
            return;

        foreach (var channel in compensable_channels(selected_group))
            SpilloverParameterChoices.Add(channel.Name);
    }

    private void refresh_spillover_rows()
    {
        foreach (var previous_row in SpilloverRows)
            previous_row.PropertyChanged -= spillover_row_changed;
        SpilloverRows.Clear();
        if (selected_group is null)
        {
            invalidate_spillover_scatter_cache();
            OnPropertyChanged(nameof(HasSpilloverControls));
            return;
        }

        var valid_ids = selected_group.ControlSamples.Select(sample => sample.Id).ToHashSet();
        for (int index = selected_group.SpilloverCompensation.Rows.Count - 1; index >= 0; index--)
            if (!valid_ids.Contains(selected_group.SpilloverCompensation.Rows[index].ControlSampleId))
                selected_group.SpilloverCompensation.Rows.RemoveAt(index);

        foreach (var row in selected_group.SpilloverCompensation.Rows)
        {
            var sample = selected_group.ControlSamples.FirstOrDefault(item => item.Id == row.ControlSampleId);
            if (sample is null)
                continue;
            var view_row = new SpilloverControlRowViewModel(sample, row, SpilloverParameterChoices, RemoveSpilloverControlCommand);
            view_row.PropertyChanged += spillover_row_changed;
            SpilloverRows.Add(view_row);
        }
        invalidate_spillover_scatter_cache();
        OnPropertyChanged(nameof(HasSpilloverControls));
    }

    private void invalidate_spillover_scatter_cache()
    {
        spillover_scatter_sample_cache = null;
        spillover_scatter_sample_cache_key = "";
        OnPropertyChanged(nameof(SpilloverScatterSample));
    }

    private void invalidate_spillover_gate_cache(Guid? gate_preset_id)
    {
        if (gate_preset_id is null)
        {
            spillover_primary_index_cache.Clear();
            spillover_gated_channel_cache.Clear();
            return;
        }

        string marker = $"{gate_preset_id.Value:N}\u001f";
        foreach (string key in spillover_primary_index_cache.Keys.Where(key => key.StartsWith(marker, StringComparison.Ordinal)).ToArray())
            spillover_primary_index_cache.Remove(key);
        foreach (string key in spillover_gated_channel_cache.Keys.Where(key => key.StartsWith(marker, StringComparison.Ordinal)).ToArray())
            spillover_gated_channel_cache.Remove(key);
    }

    private static string spillover_primary_cache_key(ControlSample sample, ControlGatePreset preset)
    {
        return $"{preset.Id:N}\u001f{sample.Id:N}";
    }

    private static string spillover_channel_cache_key(ControlSample sample, ControlGatePreset preset, string channel_name)
    {
        return $"{preset.Id:N}\u001f{sample.Id:N}\u001f{channel_name}";
    }

    private static int[] compute_primary_indices(ControlSample sample, SpilloverGateSnapshot gate)
    {
        if (gate.Vertices.Length < 3)
            return Enumerable.Range(0, sample.EventCount).ToArray();

        var x_values = sample.GetChannelValues(gate.XChannel);
        var y_values = sample.GetChannelValues(gate.YChannel);
        var selected = new List<int>();
        for (int index = 0; index < sample.EventCount && index < x_values.Length && index < y_values.Length; index++)
            if (contains_polygon(gate.Vertices, x_values[index], y_values[index]))
                selected.Add(index);
        return selected.ToArray();
    }

    private static SpilloverGatedChannelCache build_gated_channel_cache(ControlSample sample, int[] primary, string channel_name)
    {
        var source = sample.GetChannelValues(channel_name);
        var pairs = new List<(double Value, int Index)>(Math.Min(primary.Length, source.Length));
        foreach (int index in primary)
        {
            if (index < 0 || index >= source.Length)
                continue;
            double value = source[index];
            if (double.IsFinite(value))
                pairs.Add((value, index));
        }
        pairs.Sort(static (left, right) => left.Value.CompareTo(right.Value));

        var sorted_values = new double[pairs.Count];
        var sorted_indices = new int[pairs.Count];
        for (int index = 0; index < pairs.Count; index++)
        {
            sorted_values[index] = pairs[index].Value;
            sorted_indices[index] = pairs[index].Index;
        }

        var values = new double[sorted_values.Length];
        Array.Copy(sorted_values, values, sorted_values.Length);
        return new SpilloverGatedChannelCache(values, sorted_values, sorted_indices);
    }

    private static SpilloverScatterPreparation build_spillover_scatter_sample(
        IReadOnlyList<ChannelDefinition> group_channels,
        IReadOnlyList<ControlSample> existing_samples,
        ControlSample appended_sample,
        ControlSample? existing_scatter,
        string existing_key)
    {
        var samples = existing_samples.Concat(new[] { appended_sample }).DistinctBy(sample => sample.Id).ToArray();
        string key = spillover_scatter_key(samples, group_channels);

        var channels = group_channels
            .Select((channel, index) => new ChannelDefinition(index, channel.Name, channel.Label, channel.Maximum, channel.Gain))
            .ToArray();
        int channel_count = channels.Length;
        if (channel_count == 0)
            return new SpilloverScatterPreparation(null, key);

        var appended_raw = sampled_control_rows(appended_sample, channels);
        if (existing_scatter is not null &&
            string.Equals(existing_key, spillover_scatter_key(existing_samples, group_channels), StringComparison.Ordinal) &&
            existing_scatter.Channels.Select(channel => channel.Name).SequenceEqual(channels.Select(channel => channel.Name)))
        {
            var raw = new float[existing_scatter.EventCount + appended_raw.GetLength(0), channel_count];
            for (int row = 0; row < existing_scatter.EventCount; row++)
            for (int column = 0; column < channel_count; column++)
                raw[row, column] = existing_scatter.RawEvents[row, column];
            for (int row = 0; row < appended_raw.GetLength(0); row++)
            for (int column = 0; column < channel_count; column++)
                raw[existing_scatter.EventCount + row, column] = appended_raw[row, column];
            return new SpilloverScatterPreparation(new ControlSample("Spillover control gate source", channels, raw), key);
        }

        var matrices = samples.Select(sample => sampled_control_rows(sample, channels)).Where(matrix => matrix.GetLength(0) > 0).ToArray();
        int event_count = matrices.Sum(matrix => matrix.GetLength(0));
        if (event_count <= 0)
            return new SpilloverScatterPreparation(null, key);

        var combined = new float[event_count, channel_count];
        int target_row = 0;
        foreach (var matrix in matrices)
        {
            for (int row = 0; row < matrix.GetLength(0); row++, target_row++)
            for (int column = 0; column < channel_count; column++)
                combined[target_row, column] = matrix[row, column];
        }
        return new SpilloverScatterPreparation(new ControlSample("Spillover control gate source", channels, combined), key);
    }

    private static string spillover_scatter_key(IReadOnlyList<ControlSample> samples, IReadOnlyList<ChannelDefinition> channels) =>
        string.Join("|", samples.Select(sample => $"{sample.Id:N}:{sample.EventCount}")) +
        "::" + string.Join("|", channels.Select(channel => channel.Name));

    private static float[,] sampled_control_rows(ControlSample sample, IReadOnlyList<ChannelDefinition> channels)
    {
        const int maximum_scatter_events_per_control = 12000;
        var source_columns = channels.Select(channel => sample.GetChannelIndex(channel.Name)).ToArray();
        if (source_columns.All(index => index < 0) || sample.EventCount <= 0)
            return new float[0, channels.Count];

        int count = Math.Min(maximum_scatter_events_per_control, sample.EventCount);
        var raw = new float[count, channels.Count];
        for (int row = 0; row < count; row++)
        {
            int source_row = count == sample.EventCount
                ? row
                : count <= 1
                    ? 0
                : (int)Math.Round(row * ( (sample.EventCount - 1.0) / (double)(count - 1.0) ));
            for (int column = 0; column < channels.Count; column++)
                raw[row, column] = source_columns[column] >= 0
                    ? sample.RawEvents[source_row, source_columns[column]]
                    : float.NaN;
        }
        return raw;
    }

    private ControlSample? spillover_scatter_sample()
    {
        if (selected_group is null || selected_group.Channels.Count == 0 || SpilloverRows.Count == 0)
            return null;

        var samples = SpilloverRows.Select(row => row.Sample).DistinctBy(sample => sample.Id).ToArray();
        string key = spillover_scatter_key(samples, selected_group.Channels);
        if (spillover_scatter_sample_cache is not null &&
            string.Equals(spillover_scatter_sample_cache_key, key, StringComparison.Ordinal))
            return spillover_scatter_sample_cache;

        if (samples.Length == 0)
            return null;

        var preparation = build_spillover_scatter_sample(selected_group.Channels, samples.SkipLast(1).ToArray(), samples[^1], null, "");
        spillover_scatter_sample_cache = preparation.Sample;
        spillover_scatter_sample_cache_key = key;
        return spillover_scatter_sample_cache;
    }

    private void spillover_row_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, selected_spillover_row))
        {
            OnPropertyChanged(nameof(SelectedSpilloverParameterChoice));
            OnPropertyChanged(nameof(SpilloverSelection));
            refresh_spillover_histogram();
        }
        if (e.PropertyName is nameof(SpilloverControlRowViewModel.ParameterName) or nameof(SpilloverControlRowViewModel.PositiveSelection))
            mark_spillover_preview_outdated();
        refresh_spillover_configured_choices();
        refresh_spillover_population_text();
        raise_command_states();
    }

    private void refresh_spillover_configured_choices()
    {
        var current = SelectedSpilloverParameterChoice;
        SpilloverConfiguredParameterChoices.Clear();
        foreach (string parameter in SpilloverRows
                     .Select(row => row.ParameterName)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.Ordinal))
            SpilloverConfiguredParameterChoices.Add(parameter);
        OnPropertyChanged(nameof(SelectedSpilloverParameterChoice));
        if (current is not null && !SpilloverConfiguredParameterChoices.Contains(current))
            OnPropertyChanged(nameof(SelectedSpilloverParameterChoice));
    }

    private void refresh_spillover_population_text()
    {
        foreach (var row in SpilloverRows)
        {
            if (row.IsBlank || row.PositiveSelection is null)
            {
                row.SetPositiveFraction(null);
                continue;
            }

            int primary_count = primary_indices(row).Length;
            int selected_count = positive_count(row);
            row.SetPositiveFraction(primary_count > 0 ? selected_count / (double)primary_count : null);
        }
    }

    private void spillover_gate_committed()
    {
        if (SelectedSpilloverGatePreset is { } preset)
            invalidate_spillover_gate_cache(preset.Id);
        else
            invalidate_spillover_gate_cache(null);

        mark_spillover_preview_outdated();
        refresh_spillover_histogram();
        refresh_spillover_population_text();
        raise_command_states();
    }

    private static string distinct_channel(FlowGroup group, string preferred, string other)
    {
        if (!string.Equals(preferred, other, StringComparison.Ordinal))
            return preferred;
        return group.Channels.FirstOrDefault(channel => !string.Equals(channel.Name, other, StringComparison.Ordinal))?.Name ?? preferred;
    }

    private bool can_drop_spillover_control(object? parameter) =>
        selected_group is not null &&
        !is_spillover_preparing_row_caches &&
        parameter is ProjectNode { Kind: ProjectNodeKind.ControlSample, ControlSample: not null, Group: not null } node &&
        node.Group.Id == selected_group.Id &&
        selected_group.ControlSamples.Any(sample => sample.Id == node.ControlSample.Id);

    private async Task drop_spillover_control_async(ProjectNode? node)
    {
        if (!can_drop_spillover_control(node) || selected_group is null || node?.ControlSample is not { } sample)
            return;

        if (selected_group.SpilloverCompensation.Rows.Any(row => row.ControlSampleId == sample.Id))
            return;

        var group = selected_group;
        var gate = group.SpilloverCompensation.DefaultGatePreset;
        var gate_snapshot = new SpilloverGateSnapshot(gate.XChannel, gate.YChannel, gate.Vertices.ToArray());
        var channels = group.Channels.ToArray();
        var compensable = compensable_channels(group).ToArray();
        var occupied = group.SpilloverCompensation.Rows
            .Select(row => row.ParameterName)
            .Where(name => !string.Equals(name, SpilloverControlRowViewModel.BlankParameterName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var existing_scatter = spillover_scatter_sample_cache;
        string existing_scatter_key = spillover_scatter_sample_cache_key;
        var existing_samples = SpilloverRows.Select(row => row.Sample).DistinctBy(item => item.Id).ToArray();

        IsSpilloverPreparingRowCaches = true;
        raise_command_states();
        SpilloverAppendPreparation preparation;
        try
        {
            preparation = await Task.Run(() =>
            {
                string parameter = guess_spillover_parameter(sample, compensable, occupied);
                var primary = compute_primary_indices(sample, gate_snapshot);
                var channel_cache = build_gated_channel_cache(sample, primary, parameter);
                var scatter = build_spillover_scatter_sample(channels, existing_samples, sample, existing_scatter, existing_scatter_key);
                return new SpilloverAppendPreparation(parameter, primary, channel_cache, scatter);
            });
        }
        finally
        {
            IsSpilloverPreparingRowCaches = false;
            raise_command_states();
        }

        if (!ReferenceEquals(selected_group, group) || group.SpilloverCompensation.Rows.Any(row => row.ControlSampleId == sample.Id))
            return;

        var row = new SpilloverControlRow
        {
            ControlSampleId = sample.Id,
            ParameterName = preparation.ParameterName,
            GatePresetId = gate.Id
        };
        group.SpilloverCompensation.Rows.Add(row);
        var view_row = new SpilloverControlRowViewModel(sample, row, SpilloverParameterChoices, RemoveSpilloverControlCommand);
        view_row.PropertyChanged += spillover_row_changed;
        SpilloverRows.Add(view_row);
        OnPropertyChanged(nameof(HasSpilloverControls));

        spillover_primary_index_cache[spillover_primary_cache_key(sample, gate)] = preparation.PrimaryIndices;
        spillover_gated_channel_cache[spillover_channel_cache_key(sample, gate, preparation.ParameterName)] = preparation.ChannelCache;
        spillover_scatter_sample_cache = preparation.Scatter.Sample;
        spillover_scatter_sample_cache_key = preparation.Scatter.Key;

        mark_spillover_preview_outdated();
        SelectedSpilloverRow = view_row;
        OnPropertyChanged(nameof(SpilloverScatterSample));
        refresh_spillover_configured_choices();
        refresh_spillover_population_text();
        refresh_spillover_histogram();
        raise_command_states();
        refresh_project_tree();
    }

    private void remove_spillover_control(SpilloverControlRowViewModel? row)
    {
        if (selected_group is null || row is null)
            return;

        for (int index = selected_group.SpilloverCompensation.Rows.Count - 1; index >= 0; index--)
            if (selected_group.SpilloverCompensation.Rows[index].ControlSampleId == row.Sample.Id)
                selected_group.SpilloverCompensation.Rows.RemoveAt(index);

        mark_spillover_preview_outdated();
        invalidate_spillover_scatter_cache();
        refresh_spillover_workspace();
        refresh_project_tree();
    }

    private string guess_spillover_parameter(ControlSample sample)
    {
        if (selected_group is null)
            return SpilloverControlRowViewModel.BlankParameterName;

        var occupied = selected_group.SpilloverCompensation.Rows
            .Select(row => row.ParameterName)
            .Where(name => !string.Equals(name, SpilloverControlRowViewModel.BlankParameterName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        return guess_spillover_parameter(sample, compensable_channels(selected_group), occupied);
    }

    private static string guess_spillover_parameter(ControlSample sample, IEnumerable<ChannelDefinition> channels, ISet<string> occupied)
    {
        var candidates = channels
            .Where(channel => !occupied.Contains(channel.Name))
            .Select(channel => (channel.Name, Score: stratified_mean(sample.GetChannelValues(channel.Name), 5000)))
            .Where(item => double.IsFinite(item.Score))
            .OrderByDescending(item => item.Score)
            .ToArray();

        return candidates.FirstOrDefault().Name ?? SpilloverControlRowViewModel.BlankParameterName;
    }

    private void refresh_spillover_histogram()
    {
        SpilloverHistogramSeries.Clear();
        if (selected_spillover_row is null ||
            selected_group is null ||
            selected_spillover_row.IsBlank ||
            string.IsNullOrWhiteSpace(selected_spillover_row.ParameterName))
        {
            notify_spillover_histogram_properties();
            return;
        }

        string channel_name = selected_spillover_row.ParameterName;
        var channel = selected_spillover_row.Sample.Channels.FirstOrDefault(item => item.Name == channel_name);
        if (channel is null)
        {
            notify_spillover_histogram_properties();
            return;
        }

        var histogram_cache = spillover_histogram_cache(selected_spillover_row, channel);
        SpilloverHistogramMinimum = 0;
        SpilloverHistogramMaximum = new LogicleParameters().T;
        SpilloverHistogramLogicleT = new LogicleParameters().T;
        SpilloverHistogramAxisScale = HistogramAxisScaleKind.Logicle;
        SpilloverHistogramSeries.Add(new HistogramSeries
        {
            Name = channel_name,
            Values = histogram_cache.Values,
            SortedValues = histogram_cache.SortedValues,
            BinCount = 256,
            Color = Color.FromRgb(120, 160, 255)
        });
        notify_spillover_histogram_properties();
    }

    private static (double Minimum, double Maximum) spillover_channel_range(ChannelDefinition channel, IReadOnlyList<double> values)
    {
        double maximum = Math.Max(1, channel.Maximum);
        for (int index = values.Count - 1; index >= 0; index--)
        {
            if (!double.IsFinite(values[index]))
                continue;
            maximum = Math.Max(maximum, values[index]);
            break;
        }
        return Configuration.DefaultCoordinateScaleForChannel(channel.Name) == CoordinateScaleKind.Linear
            ? (-0.1 * maximum, 1.1 * maximum)
            : (-0.01 * maximum, 1.1 * maximum);
    }

    private SpilloverHistogramCache spillover_histogram_cache(SpilloverControlRowViewModel row, ChannelDefinition channel)
    {
        var channel_cache = spillover_gated_channel_values(row, channel.Name);
        return new SpilloverHistogramCache(channel.Name, channel_cache.Values, channel_cache.SortedValues);
    }

    private (double Minimum, double Maximum) spillover_axis_range(string channel_name, bool x_axis)
    {
        var axis = spillover_axis_settings(channel_name, x_axis);
        return (axis.Minimum, axis.Maximum);
    }

    private AxisSettings spillover_axis_settings(string channel_name, bool x_axis)
    {
        if (selected_group is null)
            return new AxisSettings
            {
                ChannelName = channel_name,
                Minimum = 0,
                Maximum = 262144,
                ScaleKind = CoordinateScaleKind.Linear
            };

        var view = selected_group.RootViewOptions;
        if (x_axis && string.Equals(view.XChannel, channel_name, StringComparison.Ordinal) && view.XMaximum > view.XMinimum)
            return new AxisSettings
            {
                ChannelName = channel_name,
                Minimum = view.XMinimum,
                Maximum = view.XMaximum,
                Scale = view.XScale.Clone()
            };
        if (!x_axis && string.Equals(view.YChannel, channel_name, StringComparison.Ordinal) && view.YMaximum > view.YMinimum)
            return new AxisSettings
            {
                ChannelName = channel_name,
                Minimum = view.YMinimum,
                Maximum = view.YMaximum,
                Scale = view.YScale.Clone()
            };

        var channel = selected_group.Channels.FirstOrDefault(item => item.Name == channel_name);
        var range = Configuration.DefaultChannelRange(channel?.Maximum ?? 262144);
        return new AxisSettings
        {
            ChannelName = channel_name,
            Minimum = range.Minimum,
            Maximum = range.Maximum,
            ScaleKind = Configuration.DefaultCoordinateScaleForChannel(channel_name)
        };
    }

    private void notify_spillover_histogram_properties()
    {
        OnPropertyChanged(nameof(SpilloverHistogramMinimum));
        OnPropertyChanged(nameof(SpilloverHistogramMaximum));
        OnPropertyChanged(nameof(SpilloverHistogramAxisScale));
        OnPropertyChanged(nameof(SpilloverHistogramLogicleT));
        OnPropertyChanged(nameof(SpilloverHistogramLogicleW));
        OnPropertyChanged(nameof(SpilloverHistogramLogicleM));
        OnPropertyChanged(nameof(SpilloverHistogramLogicleA));
    }

    private bool can_select_relative_spillover_parameter() =>
        SpilloverConfiguredParameterChoices.Count > 1;

    private void select_relative_spillover_parameter(int direction)
    {
        if (SpilloverConfiguredParameterChoices.Count == 0)
            return;

        int index = SpilloverConfiguredParameterChoices.IndexOf(selected_spillover_row?.ParameterName ?? "");
        if (index < 0)
            index = 0;
        index = (index + direction + SpilloverConfiguredParameterChoices.Count) % SpilloverConfiguredParameterChoices.Count;
        SelectedSpilloverParameterChoice = SpilloverConfiguredParameterChoices[index];
    }

    private bool can_calculate_spillover_compensation()
    {
        if (selected_group is null || is_spillover_calculating)
            return false;
        bool has_blank = SpilloverRows.Any(row => row.IsBlank);
        bool has_positive = SpilloverRows.Any(row => !row.IsBlank && row.PositiveSelection is not null);
        return has_blank && has_positive;
    }

    private SpilloverCalculationRow? spillover_calculation_row(SpilloverControlRowViewModel row)
    {
        if (selected_group is null)
            return null;
        var preset = selected_group.SpilloverCompensation.GatePresets.FirstOrDefault(item => item.Id == row.State.GatePresetId) ??
            selected_group.SpilloverCompensation.DefaultGatePreset;
        return new SpilloverCalculationRow(
            row.Sample,
            row.SampleName,
            row.ParameterName,
            row.PositiveSelection,
            preset.XChannel,
            preset.YChannel,
            preset.Vertices.ToArray());
    }

    private async Task calculate_spillover_compensation_async()
    {
        if (selected_group is null)
            return;

        var blank_row = SpilloverRows.FirstOrDefault(row => row.IsBlank);
        if (blank_row is null)
        {
            StatusText = "Blank control is required before calculating compensation.";
            return;
        }

        var channel_names = SpilloverRows
            .Where(row => !row.IsBlank && row.PositiveSelection is not null)
            .Select(row => row.ParameterName)
            .Where(name => selected_group.Channels.Any(channel => channel.Name == name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (channel_names.Length == 0)
        {
            StatusText = "At least one positive detector is required before calculating compensation.";
            return;
        }

        var rows = SpilloverRows
            .Where(row => !row.IsBlank && row.PositiveSelection is not null && channel_names.Contains(row.ParameterName, StringComparer.Ordinal))
            .Select(spillover_calculation_row)
            .Where(row => row is not null)
            .Cast<SpilloverCalculationRow>()
            .ToArray();
        string matrix_name = selected_group.SpilloverCompensation.MatrixName;

        StatusText = "Calculating auto-compensation...";
        is_spillover_calculating = true;
        raise_command_states();

        SpilloverCalculationResult result;
        try
        {
            result = await Task.Run(() => calculate_regression_spillover_matrix(matrix_name, channel_names, rows));
        }
        finally
        {
            is_spillover_calculating = false;
            raise_command_states();
        }
        if (result.Error is { } error)
        {
            StatusText = error;
            raise_command_states();
            return;
        }

        if (result.Matrix is not { } matrix)
            return;

        spillover_preview_matrix = matrix;
        refresh_spillover_preview(matrix);
        StatusText = "Calculated auto-compensation. Review or edit the matrix, then apply it.";
        raise_command_states();
    }

    private SpilloverCalculationResult calculate_regression_spillover_matrix(string matrix_name, IReadOnlyList<string> channel_names, IReadOnlyList<SpilloverCalculationRow> rows)
    {
        var values = new float[channel_names.Count, channel_names.Count];
        for (int index = 0; index < channel_names.Count; index++)
            values[index, index] = 1.0f;

        foreach (var primary_row in rows.Where(row => channel_names.Contains(row.ParameterName, StringComparer.Ordinal)))
        {
            if (primary_row.PositiveSelection is null)
                return SpilloverCalculationResult.Failed($"Positive range is missing for {primary_row.SampleName}.");

            int primary_index = index_of_channel(channel_names, primary_row.ParameterName);
            if (primary_index < 0)
                continue;

            foreach (string detector_name in channel_names)
            {
                int detector_index = index_of_channel(channel_names, detector_name);
                if (detector_index < 0 || detector_index == primary_index)
                    continue;

                var points = regression_points(primary_row, primary_row.ParameterName, detector_name);
                var fit = fit_line(points);
                if (fit is null)
                    return SpilloverCalculationResult.Failed($"Unable to fit {primary_row.ParameterName} leakage into {detector_name}.");

                values[detector_index, primary_index] = Convert.ToSingle(fit.Slope);
            }
        }

        return SpilloverCalculationResult.Succeeded(CompensationMatrix.Create(matrix_name, channel_names, values));
    }

    private bool can_apply_spillover_compensation() =>
        selected_group is not null && spillover_preview_matrix is not null && SpilloverPreviewMatrixRows.Count > 0;

    private void apply_spillover_compensation()
    {
        if (selected_group is null || spillover_preview_matrix is null)
            return;

        var channel_names = SpilloverPreviewChannels.ToArray();
        var values = new float[channel_names.Length, channel_names.Length];
        for (int display_row = 0; display_row < channel_names.Length; display_row++)
        for (int display_column = 0; display_column < channel_names.Length; display_column++)
        {
            if (display_row == display_column)
            {
                values[display_column, display_row] = 1.0f;
                continue;
            }

            if (display_row >= SpilloverPreviewMatrixRows.Count || display_column >= SpilloverPreviewMatrixRows[display_row].Values.Count)
            {
                StatusText = "Preview matrix dimensions are invalid.";
                return;
            }

            var cell = SpilloverPreviewMatrixRows[display_row].Values[display_column];
            string text = cell.Text;
            if (!double.TryParse(text, out double percent))
            {
                cell.IsInvalid = true;
                StatusText = $"Invalid compensation value for {channel_names[display_row]} / {channel_names[display_column]}.";
                return;
            }
            values[display_column, display_row] = Convert.ToSingle(percent / 100.0);
        }

        var matrix = CompensationMatrix.Create(SpilloverMatrixName, channel_names, values);
        selected_group.RegisterCompensation(matrix, make_applied_if_first: false);
        spillover_preview_matrix = matrix;
        IsSpilloverPreviewOutdated = false;
        refresh_project_tree();
        StatusText = $"Created compensation matrix: {matrix.Name}";
        raise_command_states();
    }

    private void clear_spillover_preview()
    {
        spillover_preview_matrix = null;
        IsSpilloverPreviewOutdated = false;
        SpilloverPreviewChannels.Clear();
        SpilloverPreviewMatrixRows.Clear();
        SpilloverPreviewCells.Clear();
        SpilloverPreviewPlotRows.Clear();
        OnPropertyChanged(nameof(HasSpilloverPreviewMatrix));
    }

    private void mark_spillover_preview_outdated()
    {
        if (spillover_preview_matrix is not null)
            IsSpilloverPreviewOutdated = true;
    }

    private void refresh_spillover_preview(CompensationMatrix matrix)
    {
        IsSpilloverPreviewOutdated = false;
        SpilloverPreviewChannels.Clear();
        foreach (string channel in matrix.ChannelNames)
            SpilloverPreviewChannels.Add(channel);

        SpilloverPreviewMatrixRows.Clear();
        for (int detector = 0; detector < matrix.ChannelNames.Count; detector++)
        {
            var values = new ObservableCollection<SpilloverPreviewMatrixCell>();
            for (int primary = 0; primary < matrix.ChannelNames.Count; primary++)
            {
                var cell = new SpilloverPreviewMatrixCell(
                    primary == detector ? "1" : (matrix.Values[detector, primary] * 100.0f).ToString("0.0"),
                    primary == detector);
                cell.PropertyChanged += spillover_preview_matrix_cell_changed;
                values.Add(cell);
            }
            SpilloverPreviewMatrixRows.Add(new SpilloverPreviewMatrixRow(matrix.ChannelNames[detector], values));
        }

        SpilloverPreviewCells.Clear();
        foreach (var cell in build_spillover_preview_cells(matrix))
            SpilloverPreviewCells.Add(cell);

        SpilloverPreviewPlotRows.Clear();
        for (int detector = 0; detector < matrix.ChannelNames.Count; detector++)
        {
            var row_cells = new ObservableCollection<SpilloverPreviewCell?>();
            for (int primary = 0; primary < matrix.ChannelNames.Count; primary++)
                row_cells.Add(detector == primary
                    ? null
                    : SpilloverPreviewCells.FirstOrDefault(cell =>
                        string.Equals(cell.XChannel, matrix.ChannelNames[primary], StringComparison.Ordinal) &&
                        string.Equals(cell.YChannel, matrix.ChannelNames[detector], StringComparison.Ordinal)));
            SpilloverPreviewPlotRows.Add(new SpilloverPreviewPlotRow(matrix.ChannelNames[detector], row_cells));
        }
        OnPropertyChanged(nameof(HasSpilloverPreviewMatrix));
    }

    private void spillover_preview_matrix_cell_changed(object? sender, PropertyChangedEventArgs e)
    {
        // Text edits validate the table only. Preview plots use the static auto-calculation data.
    }

    private void refresh_spillover_preview_plots_from_table()
    {
        if (spillover_preview_matrix is null)
            return;

        SpilloverPreviewCells.Clear();
        foreach (var cell in build_spillover_preview_cells(spillover_preview_matrix))
            SpilloverPreviewCells.Add(cell);

        SpilloverPreviewPlotRows.Clear();
        for (int detector = 0; detector < spillover_preview_matrix.ChannelNames.Count; detector++)
        {
            var row_cells = new ObservableCollection<SpilloverPreviewCell?>();
            for (int primary = 0; primary < spillover_preview_matrix.ChannelNames.Count; primary++)
                row_cells.Add(detector == primary
                    ? null
                    : SpilloverPreviewCells.FirstOrDefault(cell =>
                        string.Equals(cell.XChannel, spillover_preview_matrix.ChannelNames[primary], StringComparison.Ordinal) &&
                        string.Equals(cell.YChannel, spillover_preview_matrix.ChannelNames[detector], StringComparison.Ordinal)));
            SpilloverPreviewPlotRows.Add(new SpilloverPreviewPlotRow(spillover_preview_matrix.ChannelNames[detector], row_cells));
        }
    }

    private IEnumerable<SpilloverPreviewCell> build_spillover_preview_cells(CompensationMatrix matrix)
    {
        if (selected_group is null)
            yield break;

        for (int detector = 0; detector < matrix.ChannelNames.Count; detector++)
        for (int primary = 0; primary < matrix.ChannelNames.Count; primary++)
        {
            if (detector == primary)
                continue;

            string primary_channel = matrix.ChannelNames[primary];
            string detector_channel = matrix.ChannelNames[detector];
            var source_row = SpilloverRows.FirstOrDefault(row =>
                !row.IsBlank &&
                row.PositiveSelection is not null &&
                string.Equals(row.ParameterName, primary_channel, StringComparison.Ordinal));
            var points = source_row is null ? [] : regression_points(source_row, primary_channel, detector_channel);
            var regression_fit = fit_line(points);
            var display_fit = fit_line_from_table(detector, primary, regression_fit);

            var x_axis = preview_axis_settings(primary_channel);
            var y_axis = preview_axis_settings(detector_channel);
            yield return new SpilloverPreviewCell(
                primary_channel,
                detector_channel,
                points,
                x_axis.Minimum,
                x_axis.Maximum,
                x_axis.Scale.Clone(),
                y_axis.Minimum,
                y_axis.Maximum,
                y_axis.Scale.Clone(),
                display_fit);
        }
    }

    private SpilloverFitLine? fit_line_from_table(int detector_index, int primary_index, SpilloverFitLine? fallback)
    {
        if (detector_index >= SpilloverPreviewMatrixRows.Count ||
            primary_index >= SpilloverPreviewMatrixRows[detector_index].Values.Count)
            return fallback;

        var cell = SpilloverPreviewMatrixRows[detector_index].Values[primary_index];
        if (!cell.TryGetFraction(out double fraction))
            return fallback;

        return new SpilloverFitLine(fraction, fallback?.Intercept ?? 0.0);
    }

    private static int index_of_channel(IReadOnlyList<string> channels, string channel_name)
    {
        for (int index = 0; index < channels.Count; index++)
            if (string.Equals(channels[index], channel_name, StringComparison.Ordinal))
                return index;
        return -1;
    }

    private IReadOnlyList<Point> regression_points(SpilloverControlRowViewModel row, string primary_channel, string detector_channel)
    {
        var indices = positive_indices(row);
        var x_values = row.Sample.GetChannelValues(primary_channel, indices);
        var y_values = row.Sample.GetChannelValues(detector_channel, indices);
        int step = Math.Max(1, Math.Min(x_values.Length, y_values.Length) / 2500);
        var points = new List<Point>();
        for (int index = 0; index < x_values.Length && index < y_values.Length; index += step)
            if (double.IsFinite(x_values[index]) && double.IsFinite(y_values[index]))
                points.Add(new Point(x_values[index], y_values[index]));
        return points;
    }

    private static IReadOnlyList<Point> regression_points(SpilloverCalculationRow row, string primary_channel, string detector_channel)
    {
        var indices = positive_indices(row);
        var x_values = row.Sample.GetChannelValues(primary_channel, indices);
        var y_values = row.Sample.GetChannelValues(detector_channel, indices);
        int step = Math.Max(1, Math.Min(x_values.Length, y_values.Length) / 2500);
        var points = new List<Point>();
        for (int index = 0; index < x_values.Length && index < y_values.Length; index += step)
            if (double.IsFinite(x_values[index]) && double.IsFinite(y_values[index]))
                points.Add(new Point(x_values[index], y_values[index]));
        return points;
    }

    private AxisSettings preview_axis_settings(string channel_name)
    {
        var channel = selected_group?.Channels.FirstOrDefault(item => item.Name == channel_name);
        double maximum = Math.Max(1, channel?.Maximum ?? new LogicleParameters().T);
        var axis = new AxisSettings
        {
            ChannelName = channel_name,
            Minimum = Configuration.DefaultCoordinateScaleForChannel(channel_name) == CoordinateScaleKind.Linear ? -0.1 * maximum : -0.01 * maximum,
            Maximum = 1.1 * maximum,
            ScaleKind = Configuration.DefaultCoordinateScaleForChannel(channel_name)
        };
        return axis;
    }

    private static SpilloverFitLine? fit_line(IReadOnlyList<Point> points)
    {
        if (points.Count < 2)
            return null;

        double mean_x = points.Average(point => point.X);
        double mean_y = points.Average(point => point.Y);
        double numerator = 0;
        double denominator = 0;
        foreach (var point in points)
        {
            double dx = point.X - mean_x;
            numerator += dx * (point.Y - mean_y);
            denominator += dx * dx;
        }
        if (!double.IsFinite(denominator) || denominator <= 1e-12)
            return null;

        double slope = numerator / denominator;
        double intercept = mean_y - slope * mean_x;
        return double.IsFinite(slope) && double.IsFinite(intercept)
            ? new SpilloverFitLine(slope, intercept)
            : null;
    }

    private int[] primary_indices(SpilloverControlRowViewModel row)
    {
        var sample = row.Sample;
        if (selected_group is null)
            return Enumerable.Range(0, sample.EventCount).ToArray();

        var preset = selected_group.SpilloverCompensation.GatePresets.FirstOrDefault(item => item.Id == row.State.GatePresetId) ??
            selected_group.SpilloverCompensation.DefaultGatePreset;
        string key = spillover_primary_cache_key(sample, preset);
        if (spillover_primary_index_cache.TryGetValue(key, out var cached))
            return cached;

        if (preset.Vertices.Count < 3)
            return spillover_primary_index_cache[key] = Enumerable.Range(0, sample.EventCount).ToArray();

        string x_channel = preset.XChannel;
        string y_channel = preset.YChannel;
        var x_values = sample.GetChannelValues(x_channel);
        var y_values = sample.GetChannelValues(y_channel);
        var selected = new List<int>();
        for (int index = 0; index < sample.EventCount && index < x_values.Length && index < y_values.Length; index++)
            if (contains_polygon(preset.Vertices, x_values[index], y_values[index]))
                selected.Add(index);
        return spillover_primary_index_cache[key] = selected.ToArray();
    }

    private static int[] primary_indices(SpilloverCalculationRow row)
    {
        var sample = row.Sample;
        if (row.GateVertices.Length < 3)
            return Enumerable.Range(0, sample.EventCount).ToArray();

        var x_values = sample.GetChannelValues(row.GateXChannel);
        var y_values = sample.GetChannelValues(row.GateYChannel);
        var selected = new List<int>();
        for (int index = 0; index < sample.EventCount && index < x_values.Length && index < y_values.Length; index++)
            if (contains_polygon(row.GateVertices, x_values[index], y_values[index]))
                selected.Add(index);
        return selected.ToArray();
    }

    private int[] positive_indices(SpilloverControlRowViewModel row)
    {
        if (row.PositiveSelection is not { } selection)
            return [];

        var normalized = selection.Minimum <= selection.Maximum
            ? selection
            : new SpilloverRangeSelection(selection.Maximum, selection.Minimum);
        var cache = spillover_gated_channel_values(row, row.ParameterName);
        int lower = lower_bound(cache.SortedValues, normalized.Minimum);
        int upper = upper_bound(cache.SortedValues, normalized.Maximum);
        if (upper <= lower)
            return [];

        var indices = new int[upper - lower];
        Array.Copy(cache.SortedIndices, lower, indices, 0, indices.Length);
        return indices;
    }

    private int positive_count(SpilloverControlRowViewModel row)
    {
        if (row.PositiveSelection is not { } selection)
            return 0;

        var normalized = selection.Minimum <= selection.Maximum
            ? selection
            : new SpilloverRangeSelection(selection.Maximum, selection.Minimum);
        var cache = spillover_gated_channel_values(row, row.ParameterName);
        int lower = lower_bound(cache.SortedValues, normalized.Minimum);
        int upper = upper_bound(cache.SortedValues, normalized.Maximum);
        return Math.Max(0, upper - lower);
    }

    private static int[] positive_indices(SpilloverCalculationRow row)
    {
        if (row.PositiveSelection is not { } selection)
            return [];

        var normalized = selection.Minimum <= selection.Maximum
            ? selection
            : new SpilloverRangeSelection(selection.Maximum, selection.Minimum);
        var primary = primary_indices(row);
        var values = row.Sample.GetChannelValues(row.ParameterName);
        var selected = new List<int>();
        foreach (int index in primary)
            if (index >= 0 && index < values.Length && values[index] >= normalized.Minimum && values[index] <= normalized.Maximum)
                selected.Add(index);
        return selected.ToArray();
    }

    private SpilloverGatedChannelCache spillover_gated_channel_values(SpilloverControlRowViewModel row, string channel_name)
    {
        var sample = row.Sample;
        var preset = selected_group?.SpilloverCompensation.GatePresets.FirstOrDefault(item => item.Id == row.State.GatePresetId) ??
            selected_group?.SpilloverCompensation.DefaultGatePreset;
        string key = preset is null
            ? $"00000000000000000000000000000000\u001f{sample.Id:N}\u001f{channel_name}"
            : spillover_channel_cache_key(sample, preset, channel_name);
        if (spillover_gated_channel_cache.TryGetValue(key, out var cached))
            return cached;

        var primary = primary_indices(row);
        var source = sample.GetChannelValues(channel_name);
        var pairs = new List<(double Value, int Index)>(Math.Min(primary.Length, source.Length));
        foreach (int index in primary)
        {
            if (index < 0 || index >= source.Length)
                continue;
            double value = source[index];
            if (double.IsFinite(value))
                pairs.Add((value, index));
        }
        pairs.Sort(static (left, right) => left.Value.CompareTo(right.Value));

        var sorted_values = new double[pairs.Count];
        var sorted_indices = new int[pairs.Count];
        for (int index = 0; index < pairs.Count; index++)
        {
            sorted_values[index] = pairs[index].Value;
            sorted_indices[index] = pairs[index].Index;
        }

        var values = new double[sorted_values.Length];
        Array.Copy(sorted_values, values, sorted_values.Length);
        return spillover_gated_channel_cache[key] = new SpilloverGatedChannelCache(values, sorted_values, sorted_indices);
    }

    private static int lower_bound(IReadOnlyList<double> values, double target)
    {
        int low = 0;
        int high = values.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (values[mid] < target)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private static int upper_bound(IReadOnlyList<double> values, double target)
    {
        int low = 0;
        int high = values.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (values[mid] <= target)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private static double[] medians_for_indices(ControlSample sample, int[] indices, IReadOnlyList<string> channel_names)
    {
        var result = new double[channel_names.Count];
        for (int channel = 0; channel < channel_names.Count; channel++)
        {
            var values = sample.GetChannelValues(channel_names[channel], indices)
                .Select(value => (double)value)
                .Where(double.IsFinite)
                .OrderBy(value => value)
                .ToArray();
            result[channel] = values.Length == 0 ? double.NaN : gated.Reduction.MatrixUtilities.PercentileInSorted(values, 0.5);
        }
        return result;
    }

    private static double stratified_mean(IReadOnlyList<float> values, int maximum_count)
    {
        if (values.Count == 0 || maximum_count <= 0)
            return double.NaN;

        int count = Math.Min(maximum_count, values.Count);
        double sum = 0;
        int finite_count = 0;
        for (int index = 0; index < count; index++)
        {
            int source_index = count == values.Count
                ? index
                : count <= 1
                    ? 0
                    : (int)Math.Round(index * (values.Count - 1) / (double)(count - 1));
            double value = values[source_index];
            if (!double.IsFinite(value))
                continue;
            sum += value;
            finite_count++;
        }

        return finite_count == 0 ? double.NaN : sum / finite_count;
    }

    private static double median(IReadOnlyList<float> values)
    {
        var sorted = values.Select(value => (double)value).Where(double.IsFinite).OrderBy(value => value).ToArray();
        return sorted.Length == 0 ? double.NaN : gated.Reduction.MatrixUtilities.PercentileInSorted(sorted, 0.5);
    }

    private static bool contains_polygon(IReadOnlyList<Point> vertices, double x_value, double y_value)
    {
        bool inside = false;
        int previous = vertices.Count - 1;
        for (int current = 0; current < vertices.Count; current++)
        {
            var current_point = vertices[current];
            var previous_point = vertices[previous];
            bool crosses = current_point.Y > y_value != previous_point.Y > y_value;
            if (crosses)
            {
                double intersection = (previous_point.X - current_point.X) * (y_value - current_point.Y) /
                    (previous_point.Y - current_point.Y) + current_point.X;
                if (x_value < intersection)
                    inside = !inside;
            }
            previous = current;
        }
        return inside;
    }

    private static IEnumerable<ChannelDefinition> compensable_channels(FlowGroup group) =>
        group.Channels.Where(channel =>
            !Configuration.IsFscChannel(channel.Name) &&
            !Configuration.IsSscChannel(channel.Name) &&
            !Configuration.IsTimeChannel(channel.Name));
}

public sealed record PageDropRequest(ProjectNode Node, Point PagePoint);

public sealed record ProjectNodeDropRequest(ProjectNode Source, ProjectNode Target);

public sealed class SpilloverPreviewMatrixCell : NotifyBase
{
    private string text;
    private bool is_invalid;

    public SpilloverPreviewMatrixCell(string text, bool is_diagonal)
    {
        this.text = text;
        IsDiagonal = is_diagonal;
    }

    public string Text
    {
        get => text;
        set
        {
            if (!SetField(ref text, value ?? ""))
                return;
            validate();
        }
    }

    public bool IsDiagonal { get; }
    public bool IsEditable => !IsDiagonal;
    public bool IsInvalid
    {
        get => is_invalid;
        set => SetField(ref is_invalid, value);
    }

    public bool TryGetFraction(out double fraction)
    {
        fraction = 0;
        if (IsDiagonal)
        {
            fraction = 1.0;
            return true;
        }

        if (!double.TryParse(Text, out double percent))
        {
            IsInvalid = true;
            return false;
        }

        fraction = percent / 100.0;
        IsInvalid = false;
        return true;
    }

    private void validate()
    {
        if (IsDiagonal)
        {
            IsInvalid = false;
            return;
        }

        IsInvalid = !double.TryParse(Text, out _);
    }
}

public sealed record SpilloverPreviewMatrixRow(string ChannelName, ObservableCollection<SpilloverPreviewMatrixCell> Values);

public sealed record SpilloverPreviewPlotRow(string YChannel, ObservableCollection<SpilloverPreviewCell?> Cells);

public sealed record SpilloverFitLine(double Slope, double Intercept);

public sealed record SpilloverPreviewCell(
    string XChannel,
    string YChannel,
    IReadOnlyList<Point> Points,
    double XMinimum,
    double XMaximum,
    AxisScale XScale,
    double YMinimum,
    double YMaximum,
    AxisScale YScale,
    SpilloverFitLine? FitLine);

public sealed class PythonLogTask : NotifyBase
{
    private string display_name;

    public PythonLogTask(string key, string display_name)
    {
        Key = key;
        this.display_name = display_name;
    }

    public string Key { get; }

    public string DisplayName
    {
        get => display_name;
        set => SetField(ref display_name, value ?? "");
    }

    public ObservableCollection<PythonLogRun> Runs { get; } = new();

    public PythonLogRun AddRun(Guid run_id, DateTimeOffset started_at)
    {
        var existing = Runs.FirstOrDefault(run => run.Id == run_id);
        if (existing is not null)
            return existing;

        var run = new PythonLogRun(run_id, Runs.Count + 1, started_at);
        Runs.Add(run);
        return run;
    }

    public void AddMessage(Python.PythonLogMessage message)
    {
        var run = Runs.FirstOrDefault(item => item.Id == message.RunId)
            ?? AddRun(message.RunId, message.Timestamp);
        run.Messages.Add(new PythonLogEntry(message.Timestamp, message.Level, message.Text));
    }

    public override string ToString() => DisplayName;
}

public sealed class PythonLogRun
{
    public PythonLogRun(Guid id, int index, DateTimeOffset started_at)
    {
        Id = id;
        Index = index;
        StartedAt = started_at;
    }

    public Guid Id { get; }
    public int Index { get; }
    public DateTimeOffset StartedAt { get; }
    public ObservableCollection<PythonLogEntry> Messages { get; } = new();
}

public sealed record PythonLogEntry(DateTimeOffset Timestamp, Python.PythonLogLevel Level, string Text);

public enum ScriptSaveChoice
{
    Save,
    Discard,
    Cancel
}

public sealed record BooleanPopulationChoice(Guid GateId, PopulationRegion Region, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record BooleanGateSelection(BooleanPopulationChoice First, BooleanPopulationChoice Second);

public sealed record EquivalentSampleChoice(FlowSample Sample, PopulationResult? Population)
{
    public string SampleName => Sample.Name;
    public string CountText => Population is null ? "" : Population.EventCount.ToString("N0");
    public bool HasCount => Population is not null;
    public string DisplayName => HasCount ? $"{SampleName} {CountText}" : SampleName;

    public override string ToString() => DisplayName;
}

public sealed record PlatformParameterRow(string Parameter, string Value);

internal sealed record SpilloverHistogramCache(string ChannelName, double[] Values, double[] SortedValues);

internal sealed record SpilloverGatedChannelCache(double[] Values, double[] SortedValues, int[] SortedIndices);

internal sealed record SpilloverGateSnapshot(string XChannel, string YChannel, Point[] Vertices);

internal sealed record SpilloverScatterPreparation(ControlSample? Sample, string Key);

internal sealed record SpilloverAppendPreparation(
    string ParameterName,
    int[] PrimaryIndices,
    SpilloverGatedChannelCache ChannelCache,
    SpilloverScatterPreparation Scatter);

internal sealed record SpilloverCalculationRow(
    ControlSample Sample,
    string SampleName,
    string ParameterName,
    SpilloverRangeSelection? PositiveSelection,
    string GateXChannel,
    string GateYChannel,
    Point[] GateVertices);

internal sealed record SpilloverCalculationResult(CompensationMatrix? Matrix, string? Error)
{
    public static SpilloverCalculationResult Succeeded(CompensationMatrix matrix) => new(matrix, null);
    public static SpilloverCalculationResult Failed(string error) => new(null, error);
}

public sealed class SpilloverControlRowViewModel : NotifyBase
{
    public const string BlankParameterName = "Blank";
    private static readonly SvgImage SampleSvg = load_svg("avares://gated/Resources/tube.svg");
    private static readonly SvgImage OkSvg = load_svg("avares://gated/Resources/ok.svg");
    private static readonly SvgImage WarningSvg = load_svg("avares://gated/Resources/warning.svg");
    private static readonly SvgImage DeleteSvg = load_svg("avares://gated/Resources/delete.svg");
    private readonly SpilloverControlRow state;
    private readonly ObservableCollection<string> parameter_choices;
    private double? positive_fraction;

    public SpilloverControlRowViewModel(
        ControlSample sample,
        SpilloverControlRow state,
        ObservableCollection<string> parameter_choices,
        ICommand remove_command)
    {
        Sample = sample;
        this.state = state;
        this.parameter_choices = parameter_choices;
        RemoveCommand = remove_command;
        if (string.IsNullOrWhiteSpace(state.ParameterName))
            state.ParameterName = BlankParameterName;
    }

    public ControlSample Sample { get; }
    internal SpilloverControlRow State => state;
    public string SampleName => Sample.Name;
    public int EventCount => Sample.EventCount;
    public ObservableCollection<string> ParameterChoices => parameter_choices;
    public SvgImage SampleIcon => SampleSvg;
    public SvgImage PopulationIcon => IsBlank || PositiveSelection is not null ? OkSvg : WarningSvg;
    public SvgImage RemoveIcon => DeleteSvg;
    public ICommand RemoveCommand { get; }

    public string ParameterName
    {
        get => state.ParameterName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                OnPropertyChanged();
                return;
            }
            if (state.ParameterName == value)
                return;
            state.ParameterName = value;
            if (IsBlank)
                state.PositiveSelection = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBlank));
            OnPropertyChanged(nameof(PositivePercentText));
            OnPropertyChanged(nameof(PopulationIcon));
        }
    }

    public bool IsBlank => string.Equals(ParameterName, BlankParameterName, StringComparison.Ordinal);

    public SpilloverRangeSelection? PositiveSelection
    {
        get => state.PositiveSelection;
        set
        {
            if (Equals(state.PositiveSelection, value))
                return;
            state.PositiveSelection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositivePercentText));
            OnPropertyChanged(nameof(PopulationIcon));
        }
    }

    public string PositivePercentText => IsBlank
        ? "Blank control"
        : PositiveSelection is null ? "Define positive population" : positive_fraction.HasValue ? $"Positive {positive_fraction.Value:P1} total" : "Define positive population";

    public void SetPositiveFraction(double? value)
    {
        if (positive_fraction == value)
            return;
        positive_fraction = value;
        OnPropertyChanged(nameof(PositivePercentText));
        OnPropertyChanged(nameof(PopulationIcon));
    }

    private static SvgImage load_svg(string uri) =>
        new() { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri(uri))) };
}

public sealed class AxisChoice : NotifyBase
{
    private bool is_editor_x_selected;
    private bool is_editor_y_selected;
    private bool is_editor_color_selected;
    private bool is_layout_x_selected;
    private bool is_layout_y_selected;
    private bool is_layout_color_selected;

    public AxisChoice(string name, string label)
    {
        Name = name;
        Label = label;
    }

    public string Name { get; }
    public string Label { get; }
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
    public string DisplayLabel => HasLabel ? Label : Name;
    public bool IsEditorXSelected { get => is_editor_x_selected; set => SetField(ref is_editor_x_selected, value); }
    public bool IsEditorYSelected { get => is_editor_y_selected; set => SetField(ref is_editor_y_selected, value); }
    public bool IsEditorColorSelected { get => is_editor_color_selected; set => SetField(ref is_editor_color_selected, value); }
    public bool IsLayoutXSelected { get => is_layout_x_selected; set => SetField(ref is_layout_x_selected, value); }
    public bool IsLayoutYSelected { get => is_layout_y_selected; set => SetField(ref is_layout_y_selected, value); }
    public bool IsLayoutColorSelected { get => is_layout_color_selected; set => SetField(ref is_layout_color_selected, value); }
    public override string ToString() => DisplayLabel;
}

public sealed class ChannelRow : NotifyBase
{
    private readonly Func<ChannelRow, string, string> name_changed;
    private readonly Action<ChannelRow, string> label_changed;
    private string name;
    private string label;

    public ChannelRow(
        ChannelDefinition channel,
        ICommand remove_command,
        bool can_remove,
        string remove_tooltip,
        Func<ChannelRow, string, string> name_changed,
        Action<ChannelRow, string> label_changed)
    {
        RemoveCommand = remove_command;
        CanRemove = can_remove;
        RemoveTooltip = remove_tooltip;
        this.name_changed = name_changed;
        this.label_changed = label_changed;
        name = channel.Name;
        label = channel.Label;
        Maximum = channel.Maximum;
    }

    public string Name
    {
        get => name;
        set
        {
            string requested = value ?? "";
            string accepted = name_changed(this, requested);
            if (SetField(ref name, accepted))
                return;

            if (!string.Equals(requested, accepted, StringComparison.Ordinal))
                OnPropertyChanged(nameof(Name));
        }
    }

    public string Label
    {
        get => label;
        set
        {
            if (!SetField(ref label, value ?? ""))
                return;

            label_changed(this, label);
        }
    }

    public float Maximum { get; }
    public string MaximumDisplay => Maximum.ToString("N0");
    public ICommand RemoveCommand { get; }
    public bool CanRemove { get; }
    public string RemoveTooltip { get; }
}

public enum ProjectNodeKind
{
    Workspace,
    Metadata,
    LayoutFolder,
    Layout,
    IntegrationJobFolder,
    Platform,
    Group,
    GateFolder,
    Gate,
    GatePopulationSlot,
    StatisticDefinition,
    ControlFolder,
    SpilloverCompensation,
    SpectralUnmixing,
    ControlSample,
    CompensationFolder,
    Compensation,
    Sample,
    Population,
    Embedding,
    StatisticValue
}

public sealed class ProjectNode : NotifyBase
{
    private bool is_expanded;
    private bool is_selected;

    public string Key { get; }
    public ProjectNodeKind Kind { get; }
    public string Name { get; }
    public FlowGroup? Group { get; }
    public FlowSample? Sample { get; }
    public GateDefinition? Gate { get; }
    public PopulationResult? Population { get; }
    public StatisticDefinition? StatisticDefinition { get; }
    public StatisticResult? StatisticResult { get; }
    public CompensationMatrix? Compensation { get; }
    public ControlSample? ControlSample { get; }
    public PageLayout? Layout { get; }
    public Platform? Platform { get; }
    public string? EmbeddingName { get; }
    public PopulationRegion PopulationRegion { get; }
    public int? Count { get; }
    public bool IsAppliedCompensation { get; }
    public int Depth { get; }
    public ObservableCollection<ProjectNode> Children { get; } = new();

    public ProjectNode(
        ProjectNodeKind kind,
        string name,
        string key,
        FlowGroup? group = null,
        FlowSample? sample = null,
        GateDefinition? gate = null,
        PopulationResult? population = null,
        StatisticDefinition? statistic_definition = null,
        StatisticResult? statistic_result = null,
        CompensationMatrix? compensation = null,
        ControlSample? control_sample = null,
        PageLayout? layout = null,
        Platform? integration_job = null,
        string? embedding_name = null,
        PopulationRegion population_region = PopulationRegion.Primary,
        int? count = null,
        bool is_applied_compensation = false,
        int depth = 0)
    {
        Key = key;
        Kind = kind;
        Name = name;
        Group = group;
        Sample = sample;
        Gate = gate;
        Population = population;
        StatisticDefinition = statistic_definition;
        StatisticResult = statistic_result;
        Compensation = compensation;
        ControlSample = control_sample;
        Layout = layout;
        Platform = integration_job;
        EmbeddingName = embedding_name;
        PopulationRegion = population_region;
        Count = count;
        IsAppliedCompensation = is_applied_compensation;
        Depth = depth;
    }

    public bool IsExpanded
    {
        get => is_expanded;
        set
        {
            if (!SetField(ref is_expanded, value))
                return;
            OnPropertyChanged(nameof(ChevronText));
            OnPropertyChanged(nameof(ChevronIconPath));
        }
    }

    public bool IsSelected
    {
        get => is_selected;
        set => SetField(ref is_selected, value);
    }

    public bool HasChildren => Children.Count > 0;
    public bool HasNoChildren => Children.Count == 0;
    public string CountText => StatisticResult is not null
        ? StatisticResult.DisplayValue
        : Count.HasValue ? Count.Value.ToString("N0") : "";
    public double IndentWidth => Depth * 18.0;
    public Thickness IndentMargin => new(Depth * 14.0, 0, 0, 0);

    public string IconPath => Kind switch
    {
        ProjectNodeKind.Workspace => "avares://gated/Resources/workspace.svg",
        ProjectNodeKind.Metadata => "avares://gated/Resources/table-edit.svg",
        ProjectNodeKind.LayoutFolder => "avares://gated/Resources/table-edit.svg",
        ProjectNodeKind.Layout => "avares://gated/Resources/table-edit.svg",
        ProjectNodeKind.IntegrationJobFolder => "avares://gated/Resources/embedding.svg",
        ProjectNodeKind.Platform => "avares://gated/Resources/embedding.svg",
        ProjectNodeKind.Group => "avares://gated/Resources/group.svg",
        ProjectNodeKind.GateFolder => "avares://gated/Resources/gates.svg",
        ProjectNodeKind.Gate => "avares://gated/Resources/gate.svg",
        ProjectNodeKind.GatePopulationSlot => "avares://gated/Resources/subset.svg",
        ProjectNodeKind.StatisticDefinition => "avares://gated/Resources/statistics.svg",
        ProjectNodeKind.ControlFolder => "avares://gated/Resources/controls.svg",
        ProjectNodeKind.SpilloverCompensation => "avares://gated/Resources/matrix.svg",
        ProjectNodeKind.SpectralUnmixing => "avares://gated/Resources/embedding.svg",
        ProjectNodeKind.ControlSample => "avares://gated/Resources/tube.svg",
        ProjectNodeKind.CompensationFolder => "avares://gated/Resources/matrix.svg",
        ProjectNodeKind.Compensation => IsAppliedCompensation ? "avares://gated/Resources/ok.svg" : "avares://gated/Resources/matrix.svg",
        ProjectNodeKind.Sample => "avares://gated/Resources/tube.svg",
        ProjectNodeKind.Population => "avares://gated/Resources/subset.svg",
        ProjectNodeKind.Embedding => "avares://gated/Resources/embedding.svg",
        ProjectNodeKind.StatisticValue => "avares://gated/Resources/stats.svg",
        _ => "avares://gated/Resources/channel.svg"
    };

    public string ChevronIconPath => IsExpanded
        ? "avares://gated/Resources/chevron-down.svg"
        : "avares://gated/Resources/chevron-right.svg";

    public string ChevronText => HasChildren
        ? IsExpanded ? "v" : ">"
        : "";

    public string NodeIconText => Kind switch
    {
        ProjectNodeKind.Workspace => "W",
        ProjectNodeKind.Metadata => "M",
        ProjectNodeKind.LayoutFolder => "L",
        ProjectNodeKind.Layout => "P",
        ProjectNodeKind.IntegrationJobFolder => "J",
        ProjectNodeKind.Platform => "J",
        ProjectNodeKind.Group => "G",
        ProjectNodeKind.GateFolder => "F",
        ProjectNodeKind.Gate => "g",
        ProjectNodeKind.GatePopulationSlot => "P",
        ProjectNodeKind.StatisticDefinition => "D",
        ProjectNodeKind.ControlFolder => "C",
        ProjectNodeKind.SpilloverCompensation => "M",
        ProjectNodeKind.SpectralUnmixing => "U",
        ProjectNodeKind.ControlSample => "S",
        ProjectNodeKind.CompensationFolder => "C",
        ProjectNodeKind.Compensation => IsAppliedCompensation ? "*" : "M",
        ProjectNodeKind.Sample => "S",
        ProjectNodeKind.Population => "P",
        ProjectNodeKind.Embedding => "E",
        ProjectNodeKind.StatisticValue => "V",
        _ => "?"
    };
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> execute;
    private readonly Func<object?, bool>? can_execute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? can_execute = null)
    {
        this.execute = execute;
        this.can_execute = can_execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => can_execute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
