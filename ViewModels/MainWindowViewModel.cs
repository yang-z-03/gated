using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using gated.Models;
using gated.Reduction;
using gated.Services;
using PythonWorkspaceContext = gated.Python.PythonWorkspaceContext;

namespace gated.ViewModels;

public sealed class MainWindowViewModel : NotifyBase
{
    private readonly ObservableCollection<ProjectNode> project_roots = new();
    private readonly Dictionary<string, bool> project_expansion_state = new();
    private ProjectNode? selected_node;
    private FlowGroup? selected_group;
    private FlowSample? selected_sample;
    private GateDefinition? selected_gate;
    private PopulationResult? selected_population;
    private CompensationMatrix? selected_compensation;
    private PageLayout? selected_page_layout;
    private IntegrationJob? selected_integration_job;
    private bool is_workspace_metadata_mode;
    private bool syncing_metadata;
    private PlotMode selected_plot_mode = PlotMode.Density;
    private GatingTool active_tool = GatingTool.View;
    private bool show_outlier_points = true;
    private bool draw_large_dots;
    private bool show_gridlines = true;
    private bool show_gate_annotations = true;
    private int contour_level_count = 10;
    private int density_smoothing = 9;
    private string status_text = "Append samples to grouping to begin analysis";
    private string python_script_text = "";
    private string python_script_output = "";
    private string python_script_name = "";
    private string python_script_log = "";
    private PythonScriptDefinition? editing_python_script;
    private PythonScriptDefinition? selected_integration_macro;
    private bool is_python_script_dirty;
    private bool syncing_python_script;
    private bool is_python_script_editor_mode;
    private bool is_python_script_running;
    private AxisSettings x_axis = new();
    private AxisSettings y_axis = new();
    private DotColorSettings dot_color = new();
    private int next_gate_number = 1;
    private bool is_page_editor_mode;
    private PagePlotElement? selected_page_element;
    private PagePlotElement? subscribed_page_menu_element;
    private EquivalentSampleChoice? selected_equivalent_sample_choice;
    private bool syncing_equivalent_sample_choices;
    private bool is_plot_transform_preparing;
    private CancellationTokenSource? plot_transform_preparation_cancellation;
    private bool is_gate_recalculating;
    private CancellationTokenSource? gate_recalculation_cancellation;

    public FlowWorkspace Workspace { get; } = new();
    public ObservableCollection<ProjectNode> ProjectNodes { get; } = new();
    public ObservableCollection<ChannelRow> ChannelRows { get; } = new();
    public ObservableCollection<AxisChoice> AxisChoices { get; } = new();
    public ObservableCollection<AxisChoice> ColorChoices { get; } = new();
    public ObservableCollection<AxisChoice> SelectedPageAxisChoices { get; } = new();
    public ObservableCollection<AxisChoice> SelectedPageColorChoices { get; } = new();
    public ObservableCollection<EquivalentSampleChoice> EquivalentSampleChoices { get; } = new();
    public ObservableCollection<string> BatchColumnChoices { get; } = new();
    private DataTable statistic_table = new();
    private DataTable workspace_metadata_table = new();
    public ObservableCollection<GateDefinition> PlotGates { get; } = new();
    public ObservableCollection<PagePlotElement> PageElements => selected_page_layout?.Elements ?? empty_page_elements;
    private readonly ObservableCollection<PagePlotElement> empty_page_elements = new();
    public ObservableCollection<CoordinateScaleKind> CoordinateScaleChoices { get; } = new(Enum.GetValues<CoordinateScaleKind>());
    public ObservableCollection<PlotMode> PlotModeChoices { get; } = new(Enum.GetValues<PlotMode>());
    public ObservableCollection<PlotColorPalette> PlotColorPaletteChoices { get; } = new(Enum.GetValues<PlotColorPalette>());
    public ObservableCollection<CytoNormGoal> CytoNormGoalChoices { get; } = new(Enum.GetValues<CytoNormGoal>());
    public ObservableCollection<PythonScriptDefinition> MacroScripts { get; } = new();
    public ObservableCollection<PythonScriptDefinition> StatisticScripts { get; } = new();
    public ObservableCollection<string> RecentFilePaths => Workspace.RecentFilePaths;
    public DataView StatisticTableView => statistic_table.DefaultView;
    public DataTable StatisticTable => statistic_table;
    public DataView WorkspaceMetadataTableView => workspace_metadata_table.DefaultView;
    public DataTable WorkspaceMetadataTable => workspace_metadata_table;

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
    public ICommand ApplyCompensationCommand { get; }
    public ICommand EditCompensationCommand { get; }
    public ICommand ExpandProjectTreeCommand { get; }
    public ICommand CollapseProjectTreeCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
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
    public ICommand AddPageElementCommand { get; }
    public ICommand DeletePageElementCommand { get; }
    public ICommand RefreshIntegrationJobFeaturesCommand { get; }
    public ICommand RunIntegrationJobCommand { get; }
    public ICommand RunIntegrationMacroCommand { get; }
    public ICommand CancelIntegrationJobCommand { get; }
    public ICommand ApplyWorkspaceMetadataCommand { get; }
    public ICommand AddStringMetadataColumnCommand { get; }
    public ICommand AddIntegerMetadataColumnCommand { get; }
    public ICommand AddFloatMetadataColumnCommand { get; }
    public ICommand OpenPythonScriptEditorCommand { get; }
    public ICommand ClosePythonScriptEditorCommand { get; }
    public ICommand RunPythonScriptCommand { get; }
    public ICommand IntegrationPopulationSelectionChangedCommand { get; }
    public ICommand IntegrationFeatureSelectionChangedCommand { get; }
    public ICommand SelectPreviousEquivalentSampleCommand { get; }
    public ICommand SelectNextEquivalentSampleCommand { get; }
    public Func<string, string, Task<string?>>? RequestTextInputAsync { get; set; }
    public Func<string, string, Task<ScriptSaveChoice>>? RequestScriptSaveChoiceAsync { get; set; }
    public Func<string, IReadOnlyList<AxisChoice>, Task<string?>>? RequestChoiceInputAsync { get; set; }
    public Func<string, IReadOnlyList<BooleanPopulationChoice>, Task<BooleanGateSelection?>>? RequestBooleanGateInputAsync { get; set; }
    public Func<CompensationMatrix, Task<bool>>? RequestCompensationEditorAsync { get; set; }

    public MainWindowViewModel()
    {
        foreach (string path in RecentFileStore.Load())
            Workspace.RecentFilePaths.Add(path);
        ReloadScriptRepositories();
        CreateGroupCommand = new RelayCommand(_ => create_group());
        CreateLayoutCommand = new RelayCommand(_ => create_layout());
        CreateIntegrationJobCommand = new RelayCommand(_ => create_integration_job(), _ => Workspace.Groups.Any(group => group.Samples.Count > 0));
        RenameWorkspaceCommand = new RelayCommand(_ => _ = rename_workspace_async());
        RenameIntegrationJobCommand = new RelayCommand(_ => _ = rename_selected_integration_job_async(), _ => selected_integration_job is not null);
        RenameGroupCommand = new RelayCommand(_ => _ = rename_selected_group_async(), _ => selected_group is not null);
        RenameGateCommand = new RelayCommand(_ => _ = rename_selected_gate_async(), _ => selected_gate is not null);
        RenameLayoutCommand = new RelayCommand(_ => _ = rename_selected_layout_async(), _ => selected_page_layout is not null);
        RenameSelectedNodeCommand = new RelayCommand(_ => _ = rename_selected_node_async(), _ => can_rename_selected_node());
        ConcatenateSamplesCommand = new RelayCommand(_ => concatenate_selected_group(), _ => selected_group?.Samples.Count > 1);
        ApplyCompensationCommand = new RelayCommand(_ => apply_selected_compensation(), _ => selected_group is not null && selected_compensation is not null);
        EditCompensationCommand = new RelayCommand(_ => _ = edit_selected_compensation_async(), _ => selected_group is not null && selected_compensation is not null);
        ExpandProjectTreeCommand = new RelayCommand(_ => set_project_tree_expanded(true));
        CollapseProjectTreeCommand = new RelayCommand(_ => set_project_tree_expanded(false));
        DeleteSelectedCommand = new RelayCommand(_ => delete_selected());
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
        SelectProjectNodeCommand = new RelayCommand(parameter => select_project_node(parameter as ProjectNode));
        AddPageElementCommand = new RelayCommand(parameter => add_page_element(parameter as PageDropRequest));
        DeletePageElementCommand = new RelayCommand(_ => delete_selected_page_element(), _ => selected_page_element is not null);
        RefreshIntegrationJobFeaturesCommand = new RelayCommand(_ => refresh_selected_integration_job_features(), _ => selected_integration_job is not null && IsIntegrationJobIdle);
        RunIntegrationJobCommand = new RelayCommand(_ => _ = run_integration_job_async(), _ => selected_integration_job is { HasIntegrated: false } && IsIntegrationJobIdle);
        RunIntegrationMacroCommand = new RelayCommand(_ => _ = run_selected_integration_macro_async(), _ => selected_integration_job is { HasIntegrated: true } && selected_integration_macro is not null && IsIntegrationJobIdle && !IsPythonScriptRunning);
        CancelIntegrationJobCommand = new RelayCommand(_ => cancel_selected_integration_job(), _ => selected_integration_job is not null);
        ApplyWorkspaceMetadataCommand = new RelayCommand(_ => CommitWorkspaceSampleMetadata(), _ => IsWorkspaceMetadataMode);
        AddStringMetadataColumnCommand = new RelayCommand(_ => _ = add_metadata_column_async(MetadataColumnKind.String));
        AddIntegerMetadataColumnCommand = new RelayCommand(_ => _ = add_metadata_column_async(MetadataColumnKind.Integer));
        AddFloatMetadataColumnCommand = new RelayCommand(_ => _ = add_metadata_column_async(MetadataColumnKind.Float));
        OpenPythonScriptEditorCommand = new RelayCommand(_ => OpenPythonScriptEditor());
        ClosePythonScriptEditorCommand = new RelayCommand(_ => _ = ClosePythonScriptEditorAsync());
        RunPythonScriptCommand = new RelayCommand(_ => _ = run_python_script_async(), _ => !is_python_script_running);
        IntegrationPopulationSelectionChangedCommand = new RelayCommand(_ => UpdateIntegrationJobPopulationSelectionStates());
        IntegrationFeatureSelectionChangedCommand = new RelayCommand(_ => UpdateIntegrationJobFeatureSelectionStates());
        SelectPreviousEquivalentSampleCommand = new RelayCommand(_ => select_relative_equivalent_sample(-1), _ => EquivalentSampleChoices.Count > 1);
        SelectNextEquivalentSampleCommand = new RelayCommand(_ => select_relative_equivalent_sample(1), _ => EquivalentSampleChoices.Count > 1);
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
            apply_node_selection(value);
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

    public bool IsEditorPlotCalculating => IsPlotTransformPreparing || IsGateRecalculating;

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
            OnPropertyChanged(nameof(CanCreateOneDimensionalGate));
            OnPropertyChanged(nameof(CanCreateTwoDimensionalGate));
            enforce_active_tool_allowed();
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
    public bool IsEditorXAxisLinearScale { get => XAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value) XAxis.ScaleKind = CoordinateScaleKind.Linear; } }
    public bool IsEditorXAxisLogicleScale { get => XAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value) XAxis.ScaleKind = CoordinateScaleKind.Logicle; } }
    public bool IsEditorYAxisLinearScale { get => YAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value) YAxis.ScaleKind = CoordinateScaleKind.Linear; } }
    public bool IsEditorYAxisLogicleScale { get => YAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value) YAxis.ScaleKind = CoordinateScaleKind.Logicle; } }

    public bool IsLayoutDensityPlotMode { get => selected_page_element?.PlotMode == PlotMode.Density; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Density; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutDotplotPlotMode { get => selected_page_element?.PlotMode == PlotMode.Dotplot; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Dotplot; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutContourPlotMode { get => selected_page_element?.PlotMode == PlotMode.Contour; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Contour; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutZebraPlotMode { get => selected_page_element?.PlotMode == PlotMode.Zebra; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Zebra; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutHistogramPlotMode { get => selected_page_element?.PlotMode == PlotMode.Histogram; set { if (value && selected_page_element is not null) { selected_page_element.PlotMode = PlotMode.Histogram; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutXAxisLinearScale { get => selected_page_element?.XAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value && selected_page_element is not null) { selected_page_element.XAxis.ScaleKind = CoordinateScaleKind.Linear; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutXAxisLogicleScale { get => selected_page_element?.XAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value && selected_page_element is not null) { selected_page_element.XAxis.ScaleKind = CoordinateScaleKind.Logicle; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutYAxisLinearScale { get => selected_page_element?.YAxis.ScaleKind == CoordinateScaleKind.Linear; set { if (value && selected_page_element is not null) { selected_page_element.YAxis.ScaleKind = CoordinateScaleKind.Linear; refresh_selected_page_menu_state(); } } }
    public bool IsLayoutYAxisLogicleScale { get => selected_page_element?.YAxis.ScaleKind == CoordinateScaleKind.Logicle; set { if (value && selected_page_element is not null) { selected_page_element.YAxis.ScaleKind = CoordinateScaleKind.Logicle; refresh_selected_page_menu_state(); } } }

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
        set => SetField(ref show_outlier_points, value);
    }

    public bool DrawLargeDots
    {
        get => draw_large_dots;
        set => SetField(ref draw_large_dots, value);
    }

    public bool ShowGridlines
    {
        get => show_gridlines;
        set => SetField(ref show_gridlines, value);
    }

    public bool ShowGateAnnotations
    {
        get => show_gate_annotations;
        set => SetField(ref show_gate_annotations, value);
    }

    public int ContourLevelCount
    {
        get => contour_level_count;
        set => SetField(ref contour_level_count, Math.Clamp(value, 2, 80));
    }

    public int DensitySmoothing
    {
        get => density_smoothing;
        set => SetField(ref density_smoothing, Math.Clamp(value, 0, 12));
    }

    public AxisSettings XAxis
    {
        get => x_axis;
        private set
        {
            set_axis(ref x_axis, value, x_axis_property_changed);
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
            OnPropertyChanged(nameof(SelectedYAxisChoice));
            OnPropertyChanged(nameof(IsEditorYAxisLinearScale));
            OnPropertyChanged(nameof(IsEditorYAxisLogicleScale));
        }
    }

    public DotColorSettings DotColor
    {
        get => dot_color;
        private set => SetField(ref dot_color, value);
    }

    public AxisChoice? SelectedDotColorChoice
    {
        get => ColorChoices.FirstOrDefault(choice => choice.Name == DotColor.ChannelName);
        set
        {
            if (value is null || DotColor.ChannelName == value.Name)
                return;

            DotColor.ChannelName = value.Name;
            OnPropertyChanged();
            refresh_axis_menu_state();
        }
    }

    public AxisChoice? SelectedXAxisChoice
    {
        get => AxisChoices.FirstOrDefault(choice => choice.Name == XAxis.ChannelName);
        set
        {
            if (value is null || XAxis.ChannelName == value.Name)
                return;

            XAxis.ChannelName = value.Name;
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

            YAxis.ChannelName = value.Name;
            OnPropertyChanged();
            refresh_axis_menu_state();
        }
    }

    public string StatusText
    {
        get => status_text;
        set => SetField(ref status_text, value);
    }

    public bool IsPageEditorMode
    {
        get => is_page_editor_mode;
        set
        {
            if (!SetField(ref is_page_editor_mode, value))
                return;
            if (value)
            {
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                IsPythonScriptEditorMode = false;
            }
            OnPropertyChanged(nameof(IsDefaultAnalysisMode));
            StatusText = value
                ? "Page editor: drag gate definitions or sample populations onto the canvas"
                : "Analysis view";
        }
    }

    public bool IsIntegrationJobMode => SelectedIntegrationJob is not null;
    public bool IsWorkspaceMetadataMode
    {
        get => is_workspace_metadata_mode;
        private set
        {
            if (!SetField(ref is_workspace_metadata_mode, value))
                return;
            if (value)
                IsPythonScriptEditorMode = false;
            OnPropertyChanged(nameof(IsDefaultAnalysisMode));
        }
    }
    public bool IsPythonScriptEditorMode
    {
        get => is_python_script_editor_mode;
        private set
        {
            if (!SetField(ref is_python_script_editor_mode, value))
                return;
            if (value)
            {
                is_page_editor_mode = false;
                OnPropertyChanged(nameof(IsPageEditorMode));
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                StatusText = "Python script editor";
            }
            OnPropertyChanged(nameof(IsDefaultAnalysisMode));
        }
    }

    public bool IsDefaultAnalysisMode => !IsPageEditorMode && !IsIntegrationJobMode && !IsWorkspaceMetadataMode && !IsPythonScriptEditorMode;
    public bool IsIntegrationJobIdle => SelectedIntegrationJob is not { IsRunning: true };
    public bool IsSelectedIntegrationJobConfigEditable => SelectedIntegrationJob is { IsConfigurationLocked: false } && IsIntegrationJobIdle;
    public bool IsSelectedIntegrationJobConfigReadOnly => !IsSelectedIntegrationJobConfigEditable;

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

    public string PythonScriptLog
    {
        get => python_script_log;
        private set => SetField(ref python_script_log, value, nameof(PythonScriptLog));
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
            OnPropertyChanged(nameof(SelectedPageXAxisChoice));
            OnPropertyChanged(nameof(SelectedPageYAxisChoice));
            OnPropertyChanged(nameof(SelectedPageDotColorChoice));
            refresh_selected_page_menu_state();
            if (DeletePageElementCommand is RelayCommand relay)
                relay.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedPageElement => SelectedPageElement is not null;

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
        }
    }

    public IntegrationJob? SelectedIntegrationJob
    {
        get => selected_integration_job;
        private set
        {
            if (!SetField(ref selected_integration_job, value))
                return;
            if (value is not null)
            {
                IsPythonScriptEditorMode = false;
                IsWorkspaceMetadataMode = false;
            }
            refresh_batch_column_choices();
            OnPropertyChanged(nameof(IsIntegrationJobMode));
            OnPropertyChanged(nameof(IsDefaultAnalysisMode));
            OnPropertyChanged(nameof(IsIntegrationJobIdle));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigEditable));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigReadOnly));
            OnPropertyChanged(nameof(SelectedIntegrationJobName));
            OnPropertyChanged(nameof(SelectedIntegrationJobBatchColumnName));
            raise_command_states();
        }
    }

    public string SelectedIntegrationJobName
    {
        get => selected_integration_job?.Name ?? "";
        set
        {
            if (selected_integration_job is null)
                return;
            selected_integration_job.Name = value;
            OnPropertyChanged();
            refresh_project_tree();
        }
    }

    public string SelectedIntegrationJobBatchColumnName
    {
        get => selected_integration_job?.BatchColumnName ?? "";
        set
        {
            if (selected_integration_job is null)
                return;
            string next = value ?? "";
            if (selected_integration_job.BatchColumnName == next)
                return;
            selected_integration_job.BatchColumnName = next;
            OnPropertyChanged();
            if (selected_integration_job.SourceData is not null)
                selected_integration_job.InvalidateFromConfiguration();
            raise_command_states();
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageElement));
            refresh_axis_menu_state();
        }
    }

    public void AddFiles(IEnumerable<string> file_paths)
    {
        var reader = new FcsReader();
        int loaded_count = 0;
        foreach (string file_path in file_paths)
        {
            var sample = reader.Read(file_path);
            add_sample_to_compatible_group(sample);
            loaded_count++;
        }

        refresh_project_tree();
        if (loaded_count > 0 && selected_group is not null)
            reset_axes_from_group(selected_group);
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = loaded_count == 0 ? StatusText : $"Loaded {loaded_count} FCS sample(s)";
        raise_command_states();
    }

    public void AddFilesToGroup(IEnumerable<string> file_paths, FlowGroup target_group)
    {
        var reader = new FcsReader();
        var fallback_groups = new List<FlowGroup>();
        int loaded_count = 0;
        foreach (string file_path in file_paths)
        {
            var sample = reader.Read(file_path);
            var group = target_group.CanAccept(sample)
                ? target_group
                : fallback_groups.FirstOrDefault(item => item.CanAccept(sample));
            if (group is null)
            {
                group = new FlowGroup { Name = $"Group {Workspace.Groups.Count + 1}" };
                Workspace.Groups.Add(group);
                fallback_groups.Add(group);
            }

            group.AddSample(sample);
            SelectedGroup = group;
            SelectedSample = sample;
            loaded_count++;
        }

        refresh_project_tree();
        if (loaded_count > 0 && selected_group is not null)
            reset_axes_from_group(selected_group);
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = loaded_count == 0 ? StatusText : $"Loaded {loaded_count} FCS sample(s)";
        raise_command_states();
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
            reset_axes_from_group(selected_group);
        refresh_workspace_sample_metadata();
        seed_loaded_workspace_expansion_state();
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Loaded workspace: {System.IO.Path.GetFileName(file_path)}";
        raise_command_states();
    }

    public void RecalculateSelectedGroup()
    {
        selected_group?.RecalculateSamples();
        refresh_selected_population_reference();
        refresh_project_tree();
        OnPropertyChanged(nameof(PlotGate));
        OnPropertyChanged(nameof(PlotPopulation));
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
        try
        {
            await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (has_external_boolean_dependency(group, gate) || !group.RecalculateGateSubtree(gate, cancellation.Token))
                    group.RecalculateSamples(force_compensation: false, cancellation.Token);
            }, cancellation.Token);
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
                refresh_plot_gates();
                refresh_selected_statistics();
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

    public void RunPythonExtension(string code)
    {
        void capture_log(string text) => 
            append_python_log(text);
        Python.PythonExtensionRuntime.LogReceived += capture_log;
        try
        {
            var context = new PythonWorkspaceContext(Workspace);
            context.execute(code);
            void refresh()
            {
                foreach (var group in Workspace.Groups)
                    group.RecalculateSamples();
                SelectedGroup ??= Workspace.Groups.FirstOrDefault();
                SelectedSample ??= selected_group?.Samples.FirstOrDefault();
                refresh_project_tree();
                refresh_selection_sidebars();
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
        catch (Exception exception)
        {
            append_python_log(exception.ToString());
            throw;
        }
        finally
        {
            Python.PythonExtensionRuntime.LogReceived -= capture_log;
        }
    }

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
        if (!await confirm_python_script_transition_async())
            return;
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
        if (!await confirm_python_script_transition_async())
            return;
        string? name = RequestTextInputAsync is null
            ? $"Python statistic {StatisticScripts.Count + 1}"
            : await RequestTextInputAsync("Create Python statistic", "Statistic name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var statistic = PythonScriptRepository.NewStatistic(name.Trim());
        OpenPythonScriptEditor(statistic, dirty: true);
        StatusText = $"Created Python statistic script: {statistic.Name}";
    }

    public async Task OpenPythonScriptEditorAsync(PythonScriptDefinition? script = null)
    {
        if (!await confirm_python_script_transition_async())
            return;
        OpenPythonScriptEditor(script);
    }

    public void OpenPythonScriptEditor(PythonScriptDefinition? script = null, bool dirty = false)
    {
        editing_python_script = script;
        PythonScriptLog = "";
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
        }
        syncing_python_script = false;
        IsPythonScriptDirty = dirty;
        IsPythonScriptEditorMode = true;
    }

    public async Task ClosePythonScriptEditorAsync()
    {
        if (!await confirm_python_script_transition_async())
            return;
        IsPythonScriptEditorMode = false;
        editing_python_script = null;
        OnPropertyChanged(nameof(HasEditingPythonScript));
        OnPropertyChanged(nameof(CanSavePythonScript));
        OnPropertyChanged(nameof(PythonScriptFileName));
        IsPythonScriptDirty = false;
        PythonScriptLog = "";
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
            append_python_log(validation_error);
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
        PythonScriptLog = "";
        await Task.Run(() => RunPythonExtension(macro.Source));
        StatusText = $"Ran macro: {macro.Name}";
    }

    public void ApplyStatisticScript(PythonScriptDefinition script)
    {
        if (selected_group is null || script.Kind != PythonScriptRepositoryKind.Statistic)
            return;

        Python.PythonExtensionRuntime.ValidateStatisticSource(script.Source, "entry");
        var definition = new StatisticDefinition();
        definition.SetPythonMethod(script.Source, "entry", script.Name);
        var definitions = selected_gate?.Statistics ?? selected_group.Statistics;
        definitions.Add(definition);
        selected_group.RecalculateSamples();
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
        PythonScriptLog = "";
        try
        {
            await Task.Run(() => RunPythonExtension(PythonScriptText));
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

    private async Task<bool> confirm_python_script_transition_async()
    {
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

    private void append_python_log(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => append_python_log(text));
            return;
        }

        PythonScriptLog = string.IsNullOrEmpty(PythonScriptLog)
            ? text
            : PythonScriptLog + Environment.NewLine + text;
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

    private void add_sample_to_compatible_group(FlowSample sample)
    {
        var group = Workspace.Groups.FirstOrDefault(item => item.CanAccept(sample));
        if (group is null)
        {
            group = new FlowGroup { Name = $"Group {Workspace.Groups.Count + 1}" };
            Workspace.Groups.Add(group);
        }

        group.AddSample(sample);
        SelectedGroup = group;
        SelectedSample = sample;
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

    private void create_layout()
    {
        var layout = new PageLayout { Name = next_layout_name() };
        Workspace.PageLayouts.Add(layout);
        SelectedPageLayout = layout;
        IsPageEditorMode = true;
        refresh_project_tree();
        raise_command_states();
    }

    private void create_integration_job()
    {
        var job = new IntegrationJob { Name = next_integration_job_name() };
        populate_integration_job_choices(job);
        Workspace.IntegrationJobs.Add(job);
        SelectedIntegrationJob = job;
        IsPageEditorMode = false;
        refresh_project_tree();
        select_project_node(find_project_node($"workspace:integration-job:{job.Id}"));
        StatusText = $"Created integration job: {job.Name}";
        raise_command_states();
    }

    private string next_integration_job_name()
    {
        int index = Workspace.IntegrationJobs.Count + 1;
        while (Workspace.IntegrationJobs.Any(job => job.Name == $"Integration job {index}"))
            index++;
        return $"Integration job {index}";
    }

    private void populate_integration_job_choices(IntegrationJob job)
    {
        job.Populations.Clear();
        ensure_metadata_schema();
        job.BatchColumnName = default_batch_column_name();
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
        {
            var sample_row_key = Guid.NewGuid();
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
                IsSelected = selected_group is null || ReferenceEquals(group, selected_group)
            });

            foreach (var population in sample.Populations)
                append_integration_population_selection(job, group, sample, population, sample_row_key, 1);
        }

        update_population_selection_states(job);
        refresh_integration_job_features(job);
        refresh_batch_column_choices();
    }

    private void append_integration_population_selection(IntegrationJob job, FlowGroup group, FlowSample sample, PopulationResult population, Guid parent_key, int depth)
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
            IsSelected = selected_group is null || ReferenceEquals(group, selected_group)
        });

        foreach (var child in population.Children)
            append_integration_population_selection(job, group, sample, child, row_key, depth + 1);
    }

    private void refresh_selected_integration_job_features()
    {
        if (selected_integration_job is null)
            return;
        update_population_selection_states(selected_integration_job);
        refresh_integration_job_features(selected_integration_job);
        selected_integration_job.InvalidateFromConfiguration();
    }

    private void refresh_integration_job_features(IntegrationJob job)
    {
        update_population_selection_states(job);
        var selected_sample_ids = job.Populations
            .Where(population => population.IsSelected && population.IsEnabled)
            .Select(population => population.SampleId)
            .Distinct()
            .ToArray();
        var samples = Workspace.Groups.SelectMany(group => group.Samples)
            .Where(sample => selected_sample_ids.Contains(sample.Id))
            .ToArray();

        var previous = job.Features.ToDictionary(feature => feature.ChannelName, feature => feature.IsSelected, StringComparer.Ordinal);
        bool previous_root_selected = job.Features.FirstOrDefault(feature => !feature.IsChannel)?.IsSelected ?? true;
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
            IsSelected = previous_root_selected
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
                IsSelected = !previous.TryGetValue(channel.Name, out bool was_selected) || was_selected
            });
        }
        update_feature_selection_states(job);
    }

    public void UpdateIntegrationJobPopulationSelectionStates()
    {
        if (selected_integration_job is null)
            return;
        update_population_selection_states(selected_integration_job);
        refresh_integration_job_features(selected_integration_job);
    }

    public void UpdateIntegrationJobFeatureSelectionStates()
    {
        if (selected_integration_job is null)
            return;
        update_feature_selection_states(selected_integration_job);
    }

    private static void update_population_selection_states(IntegrationJob job)
    {
        var rows = job.Populations.ToArray();
        apply_hierarchy_states(rows, row => row.RowKey, row => row.ParentKey, row => row.IsSelected, (row, value) => row.IsSelected = value,
            (row, value) => row.IsEnabled = value, (row, value) => row.IsIndeterminate = value);
    }

    private static void update_feature_selection_states(IntegrationJob job)
    {
        var rows = job.Features.ToArray();
        apply_hierarchy_states(rows, row => row.RowKey, row => row.ParentKey, row => row.IsSelected, (row, value) => row.IsSelected = value,
            (row, value) => row.IsEnabled = value, (row, value) => row.IsIndeterminate = value);
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

    private async Task run_integration_job_async()
    {
        if (selected_integration_job is null)
            return;
        var job = selected_integration_job;
        await run_integration_job_stage_async(job, runner => runner.RunIntegration(job));
        finish_integration_job_step(job, "Integration complete");
    }

    private async Task run_integration_job_stage_async(IntegrationJob job, Func<IntegrationJobRunner, bool> action)
    {
        try
        {
            job.CancellationRequested = false;
            job.IsRunning = true;
            job.ProgressFraction = 0;
            job.ProgressText = "Starting";
            OnPropertyChanged(nameof(IsIntegrationJobIdle));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigEditable));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigReadOnly));
            raise_command_states();
            await Task.Run(() => action(new IntegrationJobRunner(Workspace)));
        }
        catch (OperationCanceledException exception)
        {
            job.WarningText = exception.Message;
            job.Status = IntegrationJobStatus.Cancelled;
        }
        catch (Exception exception)
        {
            job.WarningText = exception.Message;
            job.Status = IntegrationJobStatus.Failed;
        }
        finally
        {
            job.IsRunning = false;
            job.CancellationRequested = false;
            OnPropertyChanged(nameof(IsIntegrationJobIdle));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigEditable));
            raise_command_states();
        }
    }

    private void cancel_selected_integration_job()
    {
        if (selected_integration_job is null)
            return;
        selected_integration_job.CancellationRequested = true;
        if (!selected_integration_job.IsRunning)
            selected_integration_job.Status = IntegrationJobStatus.Cancelled;
        selected_integration_job.WarningText = "Cancellation requested. Completed intermediate results remain available.";
        StatusText = selected_integration_job.WarningText;
        refresh_project_tree();
    }

    private async Task run_selected_integration_macro_async()
    {
        if (selected_integration_macro is null || selected_integration_job is not { HasIntegrated: true })
            return;

        IsPythonScriptRunning = true;
        PythonScriptOutput = "Running ...";
        PythonScriptLog = "";
        try
        {
            await RunMacroAsync(selected_integration_macro);
            PythonScriptOutput = "Completed.";
            StatusText = $"Ran macro: {selected_integration_macro.Name}";
        }
        catch (Exception exception)
        {
            PythonScriptOutput = exception.Message;
            StatusText = $"Python macro failed: {exception.Message}";
        }
        finally
        {
            IsPythonScriptRunning = false;
            raise_command_states();
        }
    }

    private void finish_integration_job_step(IntegrationJob job, string success_status)
    {
        StatusText = job.HasWarning ? job.WarningText : success_status;
        refresh_project_tree();
        raise_command_states();
    }

    private void refresh_workspace_sample_metadata()
    {
        rebuild_workspace_metadata_table();
        refresh_batch_column_choices();
    }

    private void rebuild_workspace_metadata_table()
    {
        workspace_metadata_table = build_metadata_table(workspace_sample_rows());
        workspace_metadata_table.ColumnChanged += metadata_table_column_changed;
        OnPropertyChanged(nameof(WorkspaceMetadataTable));
        OnPropertyChanged(nameof(WorkspaceMetadataTableView));
    }

    private void metadata_table_column_changed(object sender, DataColumnChangeEventArgs e)
    {
        if (syncing_metadata || e.Column is null || e.Column.ColumnName.StartsWith("__", StringComparison.Ordinal) || e.Column.ColumnName is "Group" or "Sample")
            return;
        syncing_metadata = true;
        try
        {
            commit_metadata_row(e.Row);
            refresh_batch_column_choices();
        }
        finally
        {
            syncing_metadata = false;
        }
    }

    private IEnumerable<(FlowGroup Group, FlowSample Sample)> workspace_sample_rows()
    {
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
            yield return (group, sample);
    }

    private DataTable build_metadata_table(IEnumerable<(FlowGroup Group, FlowSample Sample)> rows)
    {
        ensure_metadata_schema();
        var table = new DataTable();
        table.Columns.Add("__GroupId", typeof(Guid));
        table.Columns.Add("__SampleId", typeof(Guid));
        table.Columns.Add("Group", typeof(string));
        table.Columns.Add("Sample", typeof(string));
        foreach (var column in Workspace.MetadataColumns.OrderBy(item => item.Key, StringComparer.Ordinal))
            table.Columns.Add(column.Key, type_for_metadata_kind(column.Value));

        foreach (var (group, sample) in rows)
        {
            var row = table.NewRow();
            row["__GroupId"] = group.Id;
            row["__SampleId"] = sample.Id;
            row["Group"] = group.Name;
            row["Sample"] = sample.Name;
            foreach (var column in Workspace.MetadataColumns)
            {
                if (!sample.Metadata.TryGetValue(column.Key, out string? value) || string.IsNullOrWhiteSpace(value))
                    row[column.Key] = DBNull.Value;
                else
                    row[column.Key] = parse_metadata_value(value, column.Value) ?? DBNull.Value;
            }
            table.Rows.Add(row);
        }

        return table;
    }

    private void ensure_metadata_schema()
    {
        foreach (var key in Workspace.Groups
                     .SelectMany(group => group.Samples)
                     .SelectMany(sample => sample.Metadata.Keys)
                     .Where(key => key is not ("Group" or "Sample"))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!Workspace.MetadataColumns.ContainsKey(key))
                Workspace.MetadataColumns[key] = infer_metadata_kind(key);
        }
    }

    private void refresh_batch_column_choices()
    {
        ensure_metadata_schema();
        var choices = Workspace.MetadataColumns
            .Where(column => column.Value == MetadataColumnKind.String)
            .Select(column => column.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        BatchColumnChoices.Clear();
        foreach (string choice in choices)
            BatchColumnChoices.Add(choice);

        if (selected_integration_job is not null)
        {
            if (!string.IsNullOrWhiteSpace(selected_integration_job.BatchColumnName) && !choices.Contains(selected_integration_job.BatchColumnName, StringComparer.Ordinal))
                selected_integration_job.BatchColumnName = "";
            if (string.IsNullOrWhiteSpace(selected_integration_job.BatchColumnName))
                selected_integration_job.BatchColumnName = default_batch_column_name(choices);
        }

        OnPropertyChanged(nameof(SelectedIntegrationJobBatchColumnName));
    }

    private string default_batch_column_name(string[]? choices = null)
    {
        choices ??= Workspace.MetadataColumns
            .Where(column => column.Value == MetadataColumnKind.String)
            .Select(column => column.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return choices.FirstOrDefault(name => string.Equals(name, "Batch", StringComparison.Ordinal)) ??
               choices.FirstOrDefault() ??
               "";
    }

    public void CommitWorkspaceSampleMetadata()
    {
        commit_metadata_table(workspace_metadata_table);
        StatusText = "Workspace sample metadata updated";
        refresh_batch_column_choices();
    }

    private void commit_metadata_table(DataTable table)
    {
        if (syncing_metadata)
            return;
        syncing_metadata = true;
        try
        {
            foreach (DataRow row in table.Rows)
                commit_metadata_row(row);
            refresh_batch_column_choices();
        }
        finally
        {
            syncing_metadata = false;
        }
    }

    private void commit_metadata_row(DataRow row)
    {
        if (row["__SampleId"] is not Guid sample_id)
            return;
        var sample = Workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == sample_id);
        if (sample is null)
            return;

        foreach (var column in Workspace.MetadataColumns)
        {
            object value = row.Table.Columns.Contains(column.Key) ? row[column.Key] : DBNull.Value;
            if (value == DBNull.Value || value is null || string.IsNullOrWhiteSpace(Convert.ToString(value)))
                sample.Metadata.Remove(column.Key);
            else
                sample.Metadata[column.Key] = format_metadata_value(value, column.Value);
        }
    }

    private async Task add_metadata_column_async(MetadataColumnKind kind)
    {
        if (RequestTextInputAsync is null)
            return;
        string? name = await RequestTextInputAsync($"Add {kind.ToString().ToLowerInvariant()} metadata column", "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();
        if (name is "Group" or "Sample" || Workspace.MetadataColumns.ContainsKey(name))
            return;
        Workspace.MetadataColumns[name] = kind;
        rebuild_workspace_metadata_table();
        refresh_batch_column_choices();
    }

    private MetadataColumnKind infer_metadata_kind(string key)
    {
        var values = Workspace.Groups.SelectMany(group => group.Samples)
            .Select(sample => sample.Metadata.TryGetValue(key, out string? value) ? value : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (values.Length == 0)
            return MetadataColumnKind.String;
        if (values.All(value => int.TryParse(value, out _)))
            return MetadataColumnKind.Integer;
        if (values.All(value => double.TryParse(value, out _)))
            return MetadataColumnKind.Float;
        return MetadataColumnKind.String;
    }

    private static Type type_for_metadata_kind(MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => typeof(int),
            MetadataColumnKind.Float => typeof(double),
            _ => typeof(string)
        };

    private static object? parse_metadata_value(string value, MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => int.TryParse(value, out int int_value) ? int_value : null,
            MetadataColumnKind.Float => double.TryParse(value, out double double_value) ? double_value : null,
            _ => value
        };

    private static string format_metadata_value(object value, MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => Convert.ToInt32(value).ToString(),
            MetadataColumnKind.Float => Convert.ToDouble(value).ToString("G17"),
            _ => Convert.ToString(value) ?? ""
        };

    private async Task rename_selected_group_async()
    {
        if (selected_group is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename group", selected_group.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_group.Name = name.Trim();
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
        refresh_project_tree();
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

    private async Task rename_selected_integration_job_async()
    {
        if (selected_integration_job is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename integration job", selected_integration_job.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_integration_job.Name = name.Trim();
        OnPropertyChanged(nameof(SelectedIntegrationJobName));
        refresh_project_tree();
    }

    private bool can_rename_selected_node() =>
        selected_node?.Kind is ProjectNodeKind.Group
            or ProjectNodeKind.Workspace
            or ProjectNodeKind.Compensation
            or ProjectNodeKind.Layout
            or ProjectNodeKind.IntegrationJob
            or ProjectNodeKind.Gate
            or ProjectNodeKind.GatePopulationSlot
            or ProjectNodeKind.Population
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
            case ProjectNodeKind.IntegrationJob:
                await rename_selected_integration_job_async();
                break;
            case ProjectNodeKind.Gate:
            case ProjectNodeKind.GatePopulationSlot:
            case ProjectNodeKind.Population:
                await rename_selected_gate_async();
                break;
            case ProjectNodeKind.Sample:
                await rename_selected_sample_async();
                break;
        }
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
        refresh_project_tree();
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

    private void concatenate_selected_group()
    {
        if (selected_group is null || selected_group.Samples.Count < 2)
            return;

        var samples = selected_group.Samples.ToArray();
        int row_count = samples.Sum(sample => sample.EventCount);
        int column_count = samples[0].ChannelCount;
        var raw_events = new float[row_count, column_count];
        int offset = 0;
        foreach (var sample in samples)
        {
            for (int row = 0; row < sample.EventCount; row++)
            for (int column = 0; column < column_count; column++)
                raw_events[offset + row, column] = sample.RawEvents[row, column];
            offset += sample.EventCount;
        }

        var concatenated = new FlowSample($"{selected_group.Name} concatenated", samples[0].Channels, raw_events);
        selected_group.AddSample(concatenated);
        selected_group.RecalculateSamples();
        SelectedSample = concatenated;
        SelectedPopulation = null;
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Concatenated {samples.Length} sample(s)";
        raise_command_states();
    }

    private void delete_selected()
    {
        var group_to_recalculate = selected_group;
        if (selected_node?.Kind == ProjectNodeKind.Sample && selected_group is not null && selected_sample is not null)
        {
            remove_sample_preferred_views(selected_group, selected_sample.Name);
            selected_group.Samples.Remove(selected_sample);
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
        else if (selected_node?.Kind == ProjectNodeKind.IntegrationJob && selected_integration_job is not null)
            Workspace.IntegrationJobs.Remove(selected_integration_job);

        group_to_recalculate?.RecalculateSamples();
        selected_group = Workspace.Groups.FirstOrDefault();
        selected_sample = selected_group?.Samples.FirstOrDefault();
        selected_gate = selected_group?.Gates.FirstOrDefault();
        selected_population = null;
        selected_integration_job = null;
        refresh_project_tree();
        refresh_selection_sidebars();
        refresh_plot_gates();
        refresh_selected_statistics();
    }

    private void apply_selected_compensation()
    {
        if (selected_group is null || selected_compensation is null)
            return;

        selected_group.SetAppliedCompensation(selected_compensation, manual: true);
        refresh_project_tree();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Applied compensation: {selected_compensation.Name}";
    }

    private async Task edit_selected_compensation_async()
    {
        if (selected_group is null || selected_compensation is null || RequestCompensationEditorAsync is null)
            return;

        bool updated = await RequestCompensationEditorAsync(selected_compensation);
        if (!updated)
            return;

        selected_group.RecalculateSamples(force_compensation: true);
        refresh_project_tree();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"Edited compensation: {selected_compensation.Name}";
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
        foreach (var gate in all_gates(group.Gates))
            gate.SamplePreferredViews.Remove(sample_name);
    }

    private static void rename_sample_preferred_views(FlowGroup group, string old_name, string new_name)
    {
        if (old_name == new_name)
            return;

        foreach (var gate in all_gates(group.Gates))
        {
            if (!gate.SamplePreferredViews.Remove(old_name, out var view))
                continue;

            gate.SamplePreferredViews[new_name] = view;
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

        foreach (var item in source_gate.SamplePreferredViews)
            gate.SamplePreferredViews[item.Key] = new GateViewOptions
            {
                XChannel = item.Value.XChannel,
                YChannel = item.Value.YChannel,
                XMinimum = item.Value.XMinimum,
                XMaximum = item.Value.XMaximum,
                XScale = item.Value.XScale.Clone(),
                YMinimum = item.Value.YMinimum,
                YMaximum = item.Value.YMaximum,
                YScale = item.Value.YScale.Clone()
            };
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
            choices.Add(new BooleanPopulationChoice(gate.Id, region, $"{gate.Name}: {population_region_name(region)}"));

        return choices;
    }

    private static string population_region_name(PopulationRegion region) =>
        region switch
        {
            PopulationRegion.TopRight => "Top right",
            PopulationRegion.TopLeft => "Top left",
            PopulationRegion.BottomRight => "Bottom right",
            PopulationRegion.BottomLeft => "Bottom left",
            PopulationRegion.More => "More",
            PopulationRegion.Less => "Less",
            PopulationRegion.InRange => "In range",
            PopulationRegion.BelowRange => "Below range",
            PopulationRegion.AboveRange => "Above range",
            _ => "Population"
        };

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
            return;

        IsPythonScriptEditorMode = false;
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
                apply_root_axis_context();
                needs_plot_refresh = true;
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
                IsPageEditorMode = true;
                SelectedPageLayout = Workspace.PageLayouts.FirstOrDefault();
                break;
            case ProjectNodeKind.Layout:
                SelectedIntegrationJob = null;
                IsPageEditorMode = true;
                SelectedPageLayout = node.Layout;
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
            case ProjectNodeKind.IntegrationJob:
                IsPageEditorMode = false;
                SelectedIntegrationJob = node.IntegrationJob;
                SelectedGroup = Workspace.Groups.FirstOrDefault();
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                StatusText = node.IntegrationJob?.StatusText ?? "Integration job";
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
                break;
            case ProjectNodeKind.Compensation:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedCompensation = node.Compensation;
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
                StatusText = string.IsNullOrWhiteSpace(node.EmbeddingName)
                    ? StatusText
                    : $"Embedding: {node.EmbeddingName}";
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
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = node.Group?.Samples.FirstOrDefault();
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

        var first_gate = selected_group.Gates.FirstOrDefault();
        reset_axes_from_group(selected_group);
    }

    private void apply_axis_from_gate_context(GateDefinition gate)
    {
        set_axes_from_gate(gate);
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
        if (selected_sample is not null &&
            selected_population is not null &&
            gate.SamplePreferredViews.TryGetValue(selected_sample.Name, out var sample_view))
            return sample_view;

        return string.IsNullOrWhiteSpace(gate.PreferredXChannel)
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
                YScale = gate.PreferredYScale
            };
    }

    private void reset_axes_from_group(FlowGroup group)
    {
        var first = get_default_x_channel(group);
        if (first is null)
            return;

        var second = get_default_y_channel(group, first);
        XAxis = new AxisSettings { ChannelName = first.Name, Minimum = 0, Maximum = first.Maximum };
        YAxis = new AxisSettings { ChannelName = second.Name, Minimum = 0, Maximum = second.Maximum };
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
        if (selected_sample is not null && selected_population is not null)
        {
            var sample_view = new GateViewOptions
            {
                XChannel = XAxis.ChannelName,
                XMinimum = XAxis.Minimum,
                XMaximum = XAxis.Maximum,
                XScale = XAxis.Scale.Clone()
            };
            if (IsYAxisEnabled)
            {
                sample_view.YChannel = YAxis.ChannelName;
                sample_view.YMinimum = YAxis.Minimum;
                sample_view.YMaximum = YAxis.Maximum;
                sample_view.YScale = YAxis.Scale.Clone();
            }
            gate.SamplePreferredViews[selected_sample.Name] = sample_view;
            return;
        }

        gate.PreferredXChannel = XAxis.ChannelName;
        gate.PreferredXMinimum = XAxis.Minimum;
        gate.PreferredXMaximum = XAxis.Maximum;
        gate.PreferredXScale = XAxis.Scale.Clone();
        if (IsYAxisEnabled)
        {
            gate.PreferredYChannel = YAxis.ChannelName;
            gate.PreferredYMinimum = YAxis.Minimum;
            gate.PreferredYMaximum = YAxis.Maximum;
            gate.PreferredYScale = YAxis.Scale.Clone();
            return;
        }

        gate.PreferredYChannel = null;
    }

    private void sync_selected_gate_preferred_view()
    {
        if (selected_gate is null)
            return;

        copy_current_view_to_preferred_view(selected_gate);
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

    private void apply_axis_channel_defaults(AxisSettings axis)
    {
        apply_channel_range_defaults(axis, selected_group, selected_sample, selected_population);
    }

    private static ChannelDefinition? get_default_x_channel(FlowGroup group) =>
        find_channel(group, "SSC-A") ?? group.Channels.FirstOrDefault();

    private static ChannelDefinition get_default_y_channel(FlowGroup group, ChannelDefinition x_channel) =>
        find_channel(group, "FSC-A")
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

        foreach (var channel in selected_group.Channels)
            ChannelRows.Add(new ChannelRow(channel, update_channel_label));

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

    private void update_channel_label(ChannelRow row, string label)
    {
        if (selected_group is null)
            return;

        foreach (var channel in selected_group.Samples.SelectMany(sample => sample.Channels).Where(channel => channel.Name == row.Name))
            channel.Label = label;

        refresh_axis_choices();
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
        refresh_axis_menu_state();
    }

    private IEnumerable<string> available_embedding_axis_names(FlowSample? sample, PopulationResult? population)
    {
        if (sample is null || population is null)
            return Array.Empty<string>();

        return population.AvailableEmbeddingNames
            .Where(embedding_name => sample.Embeddings.ContainsKey(embedding_name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private void refresh_plot_gates()
    {
        PlotGates.Clear();
        var source = selected_gate?.Children ?? selected_group?.Gates;
        if (source is null)
            return;

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
        var jobs_node = create_project_node(ProjectNodeKind.IntegrationJobFolder, "Integration jobs", "workspace:integration-jobs", depth: 1);
        foreach (var job in Workspace.IntegrationJobs)
            jobs_node.Children.Add(create_project_node(ProjectNodeKind.IntegrationJob, job.Name, $"workspace:integration-job:{job.Id}", integration_job: job, count: job.RowMap.Count, depth: 2));
        workspace_node.Children.Add(jobs_node);
        foreach (var group in Workspace.Groups)
        {
            string group_key = $"group:{group.Id}";
            var group_node = create_project_node(ProjectNodeKind.Group, group.Name, group_key, group: group, depth: 1);
            var gates_node = create_project_node(ProjectNodeKind.GateFolder, "Gating strategies", $"{group_key}:gates", group: group, depth: 2);
            foreach (var gate in group.Gates)
                append_gate_node(gates_node, gate, group, $"{group_key}:gate:{gate.Id}", 3);

            group_node.Children.Add(gates_node);
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
        PageLayout? layout = null,
        IntegrationJob? integration_job = null,
        string? embedding_name = null,
        PopulationRegion population_region = PopulationRegion.Primary,
        int? count = null,
        bool is_applied_compensation = false,
        int depth = 0)
    {
        return new ProjectNode(kind, name, key, group, sample, gate, population, statistic_definition, statistic_result, compensation, layout, integration_job, embedding_name, population_region, count, is_applied_compensation, depth)
        {
            IsExpanded = project_expansion_state.TryGetValue(key, out bool is_expanded) ? is_expanded : true
        };
    }

    private void append_gate_node(ProjectNode parent, GateDefinition gate, FlowGroup group, string key, int depth)
    {
        var gate_node = create_project_node(ProjectNodeKind.Gate, gate.Name, key, gate: gate, group: group, count: count_gate_events(group, gate), depth: depth);
        foreach (var region in gate.PopulationRegions.Where(region => region != PopulationRegion.Primary))
        {
            gate_node.Children.Add(create_project_node(
                ProjectNodeKind.GatePopulationSlot,
                $"{gate.Name}: {population_region_name(region)}",
                $"{key}:slot:{region}",
                gate: gate,
                group: group,
                population_region: region,
                count: count_gate_region_events(group, gate, region),
                depth: depth + 1));
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

        foreach (var child in gate.Children)
            append_gate_node(gate_node, child, group, $"{key}:gate:{child.Id}", depth + 1);
        parent.Children.Add(gate_node);
    }

    private static string statistic_name(StatisticDefinition statistic)
    {
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

            default: return $"{statistic.Kind}";
        }  
    }

    private void append_population_node(ProjectNode parent, FlowSample sample, PopulationResult population, FlowGroup group, string key, int depth)
    {
        var population_node = create_project_node(ProjectNodeKind.Population, population.DisplayName, key, group: group, sample: sample, gate: population.Gate, population: population, count: population.EventCount, depth: depth);
        foreach (string embedding_name in available_embedding_axis_names(sample, population))
        {
            population_node.Children.Add(create_project_node(
                ProjectNodeKind.Embedding,
                embedding_name,
                $"{key}:embedding:{embedding_name}",
                group: group,
                sample: sample,
                gate: population.Gate,
                population: population,
                embedding_name: embedding_name,
                count: population.EventCount,
                depth: depth + 1));
        }
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
        if (node.Kind is ProjectNodeKind.Gate or ProjectNodeKind.GatePopulationSlot && node.Gate is not null)
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

    private void seed_gate_expansion_state(GateDefinition gate, string key)
    {
        project_expansion_state[key] = gate.IsTreeExpanded;
        foreach (var child in gate.Children)
            seed_gate_expansion_state(child, $"{key}:gate:{child.Id}");
    }

    private void select_project_node(ProjectNode? node)
    {
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
            ApplyCompensationCommand,
            EditCompensationCommand,
            DeleteSelectedCommand,
            CreateIntegrationJobCommand,
            RenameIntegrationJobCommand,
            RefreshIntegrationJobFeaturesCommand,
            RunIntegrationJobCommand,
            RunIntegrationMacroCommand,
            CancelIntegrationJobCommand,
            ApplyWorkspaceMetadataCommand,
            SelectPreviousEquivalentSampleCommand,
            SelectNextEquivalentSampleCommand,
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
        result.Kind == definition.Kind && result.ChannelName == definition.ChannelName;

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
        if (node.Kind is not (ProjectNodeKind.Gate or ProjectNodeKind.Population) || node.Gate is null || node.Group is null)
            return;

        var gate = node.Gate;
        var (x_axis, y_axis) = axes_for_page_node(gate, node);

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
            XAxis = x_axis,
            YAxis = y_axis,
            X = Math.Max(0, request.PagePoint.X - 130),
            Y = Math.Max(0, request.PagePoint.Y - 130),
            Size = 260,
            Title = node.Kind == ProjectNodeKind.Population && node.Sample is not null
                ? $"{node.Sample.Name} - {node.Name}"
                : $"{node.Group.Name} - {gate.Name}",
            PlotMode = gate.IsOneDimensional ? PlotMode.Histogram : EffectivePlotMode,
            ShowGridlines = ShowGridlines,
            ShowOutlierPoints = ShowOutlierPoints,
            DrawLargeDots = DrawLargeDots,
            ShowTickLabels = false,
            UsePseudocolor = true,
            DotColor = DotColorClone(),
            ContourLevelCount = ContourLevelCount,
            DensitySmoothing = DensitySmoothing
        };
        PageElements.Add(element);
        SelectedPageElement = element;
        refresh_project_tree();
        StatusText = $"Added page plot: {element.Title}";
    }

    private GateViewOptions? preferred_view_for_node(GateDefinition gate, ProjectNode node)
    {
        if (node.Kind == ProjectNodeKind.Population &&
            node.Sample is not null &&
            gate.SamplePreferredViews.TryGetValue(node.Sample.Name, out var sample_view))
            return sample_view;

        return string.IsNullOrWhiteSpace(gate.PreferredXChannel)
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
                YScale = gate.PreferredYScale
            };
    }

    private (AxisSettings XAxis, AxisSettings YAxis) axes_for_page_node(GateDefinition gate, ProjectNode node)
    {
        if (node_matches_current_plot_context(node))
            return (XAxisClone(), YAxisClone());

        var preferred_view = preferred_view_for_node(gate, node);
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
        new() { ChannelName = DotColor.ChannelName, Palette = DotColor.Palette, UseLogScale = DotColor.UseLogScale };

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
        PageElements.Remove(selected_page_element);
        SelectedPageElement = PageElements.LastOrDefault();
        refresh_project_tree();
    }

    private void apply_page_axis_channel_defaults(AxisSettings axis)
    {
        apply_channel_range_defaults(axis, selected_page_element?.Group ?? selected_group, selected_page_element?.Sample, selected_page_element?.Population);
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

        axis.Minimum = 0;
        axis.Maximum = channel.Maximum;
    }

    private static IEnumerable<float> embedding_values(FlowSample? sample, PopulationResult? population, string embedding_name)
    {
        if (sample is null ||
            population is null ||
            string.IsNullOrWhiteSpace(embedding_name) ||
            !sample.Embeddings.TryGetValue(embedding_name, out var values))
            return Array.Empty<float>();

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
    }

    private void selected_page_menu_axis_changed(object? sender, PropertyChangedEventArgs e)
    {
        refresh_selected_page_menu_state();
        if (ReferenceEquals(sender, selected_page_element?.XAxis))
        {
            OnPropertyChanged(nameof(SelectedPageXAxisChoice));
            refresh_axis_menu_state();
        }
        if (ReferenceEquals(sender, selected_page_element?.YAxis))
        {
            OnPropertyChanged(nameof(SelectedPageYAxisChoice));
            refresh_axis_menu_state();
        }
        if (ReferenceEquals(sender, selected_page_element?.DotColor))
        {
            OnPropertyChanged(nameof(SelectedPageDotColorChoice));
            refresh_axis_menu_state();
        }
    }

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
}

public sealed record PageDropRequest(ProjectNode Node, Point PagePoint);

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
    private readonly Action<ChannelRow, string> label_changed;
    private string label;

    public ChannelRow(ChannelDefinition channel, Action<ChannelRow, string> label_changed)
    {
        this.label_changed = label_changed;
        Name = channel.Name;
        label = channel.Label;
        Maximum = channel.Maximum;
    }

    public string Name { get; }

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
}

public enum ProjectNodeKind
{
    Workspace,
    Metadata,
    LayoutFolder,
    Layout,
    IntegrationJobFolder,
    IntegrationJob,
    Group,
    GateFolder,
    Gate,
    GatePopulationSlot,
    StatisticDefinition,
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
    public PageLayout? Layout { get; }
    public IntegrationJob? IntegrationJob { get; }
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
        PageLayout? layout = null,
        IntegrationJob? integration_job = null,
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
        Layout = layout;
        IntegrationJob = integration_job;
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
        ProjectNodeKind.IntegrationJob => "avares://gated/Resources/embedding.svg",
        ProjectNodeKind.Group => "avares://gated/Resources/group.svg",
        ProjectNodeKind.GateFolder => "avares://gated/Resources/gates.svg",
        ProjectNodeKind.Gate => "avares://gated/Resources/gate.svg",
        ProjectNodeKind.GatePopulationSlot => "avares://gated/Resources/subset.svg",
        ProjectNodeKind.StatisticDefinition => "avares://gated/Resources/statistics.svg",
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
        ProjectNodeKind.IntegrationJob => "J",
        ProjectNodeKind.Group => "G",
        ProjectNodeKind.GateFolder => "F",
        ProjectNodeKind.Gate => "g",
        ProjectNodeKind.GatePopulationSlot => "P",
        ProjectNodeKind.StatisticDefinition => "D",
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
