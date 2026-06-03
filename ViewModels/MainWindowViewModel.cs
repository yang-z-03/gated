using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using gated.Models;
using gated.Reduction;
using gated.Services;

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
    private IntegrationJobSampleMetadata[] subscribed_workspace_metadata_rows = [];
    private IntegrationJob? subscribed_integration_job_metadata;
    private IntegrationJobSampleMetadata[] subscribed_job_metadata_rows = [];
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
    private AxisSettings x_axis = new();
    private AxisSettings y_axis = new();
    private DotColorSettings dot_color = new();
    private int next_gate_number = 1;
    private bool is_page_editor_mode;
    private PagePlotElement? selected_page_element;
    private PagePlotElement? subscribed_page_menu_element;

    public FlowWorkspace Workspace { get; } = new();
    public ObservableCollection<ProjectNode> ProjectNodes { get; } = new();
    public ObservableCollection<ChannelRow> ChannelRows { get; } = new();
    public ObservableCollection<AxisChoice> AxisChoices { get; } = new();
    public ObservableCollection<AxisChoice> ColorChoices { get; } = new();
    public ObservableCollection<AxisChoice> SelectedPageAxisChoices { get; } = new();
    public ObservableCollection<AxisChoice> SelectedPageColorChoices { get; } = new();
    private DataTable statistic_table = new();
    public ObservableCollection<GateDefinition> PlotGates { get; } = new();
    public ObservableCollection<PagePlotElement> PageElements => selected_page_layout?.Elements ?? empty_page_elements;
    private readonly ObservableCollection<PagePlotElement> empty_page_elements = new();
    public ObservableCollection<CoordinateScaleKind> CoordinateScaleChoices { get; } = new(Enum.GetValues<CoordinateScaleKind>());
    public ObservableCollection<PlotMode> PlotModeChoices { get; } = new(Enum.GetValues<PlotMode>());
    public ObservableCollection<PlotColorPalette> PlotColorPaletteChoices { get; } = new(Enum.GetValues<PlotColorPalette>());
    public ObservableCollection<KnnDistanceMetric> KnnDistanceChoices { get; } = new(Enum.GetValues<KnnDistanceMetric>());
    public ObservableCollection<KnnSearchMethod> KnnSearchChoices { get; } = new(Enum.GetValues<KnnSearchMethod>());
    public ObservableCollection<CytoNormGoal> CytoNormGoalChoices { get; } = new(Enum.GetValues<CytoNormGoal>());
    public ObservableCollection<FlowSomDistance> FlowSomDistanceChoices { get; } = new(Enum.GetValues<FlowSomDistance>());
    public ObservableCollection<IntegrationJobSampleMetadata> WorkspaceSampleMetadata { get; } = new();
    public DataView StatisticTableView => statistic_table.DefaultView;
    public DataTable StatisticTable => statistic_table;

    public ICommand CreateGroupCommand { get; }
    public ICommand CreateLayoutCommand { get; }
    public ICommand CreateIntegrationJobCommand { get; }
    public ICommand RenameIntegrationJobCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand RenameGateCommand { get; }
    public ICommand RenameLayoutCommand { get; }
    public ICommand ConcatenateSamplesCommand { get; }
    public ICommand ApplyCompensationCommand { get; }
    public ICommand EditCompensationCommand { get; }
    public ICommand ExpandProjectTreeCommand { get; }
    public ICommand CollapseProjectTreeCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand AddPolygonGateCommand { get; }
    public ICommand AddRectangleGateCommand { get; }
    public ICommand AddThresholdGateCommand { get; }
    public ICommand AddRangeGateCommand { get; }
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
    public ICommand RunIntegrationJobKnnCommand { get; }
    public ICommand RunIntegrationJobUmapCommand { get; }
    public ICommand RunIntegrationJobLeidenCommand { get; }
    public ICommand RunIntegrationJobFlowSomCommand { get; }
    public ICommand WriteIntegrationJobResultsCommand { get; }
    public ICommand CancelIntegrationJobCommand { get; }
    public ICommand ApplyWorkspaceMetadataCommand { get; }
    public ICommand IntegrationPopulationSelectionChangedCommand { get; }
    public ICommand IntegrationFeatureSelectionChangedCommand { get; }
    public Func<string, string, Task<string?>>? RequestTextInputAsync { get; set; }
    public Func<string, IReadOnlyList<AxisChoice>, Task<string?>>? RequestChoiceInputAsync { get; set; }
    public Func<CompensationMatrix, Task<bool>>? RequestCompensationEditorAsync { get; set; }

    public MainWindowViewModel()
    {
        CreateGroupCommand = new RelayCommand(_ => create_group());
        CreateLayoutCommand = new RelayCommand(_ => create_layout());
        CreateIntegrationJobCommand = new RelayCommand(_ => create_integration_job(), _ => Workspace.Groups.Any(group => group.Samples.Count > 0));
        RenameIntegrationJobCommand = new RelayCommand(_ => _ = rename_selected_integration_job_async(), _ => selected_integration_job is not null);
        RenameGroupCommand = new RelayCommand(_ => _ = rename_selected_group_async(), _ => selected_group is not null);
        RenameGateCommand = new RelayCommand(_ => _ = rename_selected_gate_async(), _ => selected_gate is not null);
        RenameLayoutCommand = new RelayCommand(_ => _ = rename_selected_layout_async(), _ => selected_page_layout is not null);
        ConcatenateSamplesCommand = new RelayCommand(_ => concatenate_selected_group(), _ => selected_group?.Samples.Count > 1);
        ApplyCompensationCommand = new RelayCommand(_ => apply_selected_compensation(), _ => selected_group is not null && selected_compensation is not null);
        EditCompensationCommand = new RelayCommand(_ => _ = edit_selected_compensation_async(), _ => selected_group is not null && selected_compensation is not null);
        ExpandProjectTreeCommand = new RelayCommand(_ => set_project_tree_expanded(true));
        CollapseProjectTreeCommand = new RelayCommand(_ => set_project_tree_expanded(false));
        DeleteSelectedCommand = new RelayCommand(_ => delete_selected());
        AddPolygonGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Polygon));
        AddRectangleGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Rectangle));
        AddThresholdGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Threshold));
        AddRangeGateCommand = new RelayCommand(_ => _ = add_gate_async(GateKind.Range));
        AddMeanStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.Mean), _ => selected_group is not null);
        AddMedianStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.Median), _ => selected_group is not null);
        AddGeometricMeanStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.GeometricMean), _ => selected_group is not null);
        AddCoefficientOfVariationStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.CoefficientOfVariation), _ => selected_group is not null);
        AddStandardDeviationStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.StandardDeviation), _ => selected_group is not null);
        AddFrequencyOfParentStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.FrequencyOfParent), _ => selected_group is not null);
        AddFrequencyOfAllStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.FrequencyOfAll), _ => selected_group is not null);
        AddCountStatisticCommand = new RelayCommand(_ => _ = add_statistic_async(StatisticKind.NumberOfEvents), _ => selected_group is not null);
        AddCanvasGateCommand = new RelayCommand(parameter => _ = add_canvas_gate_async(parameter as GateDefinition));
        GateEditedCommand = new RelayCommand(_ => RecalculateSelectedGroup());
        ToggleProjectNodeCommand = new RelayCommand(parameter => toggle_project_node(parameter as ProjectNode));
        SelectProjectNodeCommand = new RelayCommand(parameter => select_project_node(parameter as ProjectNode));
        AddPageElementCommand = new RelayCommand(parameter => add_page_element(parameter as PageDropRequest));
        DeletePageElementCommand = new RelayCommand(_ => delete_selected_page_element(), _ => selected_page_element is not null);
        RefreshIntegrationJobFeaturesCommand = new RelayCommand(_ => refresh_selected_integration_job_features(), _ => selected_integration_job is not null && IsIntegrationJobIdle);
        RunIntegrationJobCommand = new RelayCommand(_ => _ = run_integration_job_async(), _ => selected_integration_job is { HasIntegrated: false } && IsIntegrationJobIdle);
        RunIntegrationJobKnnCommand = new RelayCommand(_ => _ = run_integration_job_knn_async(), _ => selected_integration_job is { HasIntegrated: true, HasKnnGraph: false } && IsIntegrationJobIdle);
        RunIntegrationJobUmapCommand = new RelayCommand(_ => _ = run_integration_job_umap_async(), _ => selected_integration_job is { HasKnnGraph: true, HasUmap: false } && IsIntegrationJobIdle);
        RunIntegrationJobLeidenCommand = new RelayCommand(_ => _ = run_integration_job_leiden_async(), _ => selected_integration_job is { HasKnnGraph: true, HasLeiden: false } && IsIntegrationJobIdle);
        RunIntegrationJobFlowSomCommand = new RelayCommand(_ => _ = run_integration_job_flowsom_async(), _ => selected_integration_job is { HasIntegrated: true, HasFlowSom: false } && IsIntegrationJobIdle);
        WriteIntegrationJobResultsCommand = new RelayCommand(_ => _ = write_integration_job_results_async(), _ => selected_integration_job is not null && IsIntegrationJobIdle);
        CancelIntegrationJobCommand = new RelayCommand(_ => cancel_selected_integration_job(), _ => selected_integration_job is not null);
        ApplyWorkspaceMetadataCommand = new RelayCommand(_ => CommitWorkspaceSampleMetadata(), _ => IsWorkspaceMetadataMode);
        IntegrationPopulationSelectionChangedCommand = new RelayCommand(_ => UpdateIntegrationJobPopulationSelectionStates());
        IntegrationFeatureSelectionChangedCommand = new RelayCommand(_ => UpdateIntegrationJobFeatureSelectionStates());
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
            raise_command_states();
            refresh_selected_statistics();
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
            refresh_axis_choices();
            refresh_selected_statistics();
        }
    }

    public GateDefinition? PlotGate => selected_gate;
    public PopulationResult? PlotPopulation => selected_population;
    public string SelectedGateName => PlotGate?.Name ?? "No gate selected";

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
                return;

            OnPropertyChanged(nameof(IsViewTool));
            OnPropertyChanged(nameof(IsPolygonTool));
            OnPropertyChanged(nameof(IsRectangleTool));
            OnPropertyChanged(nameof(IsQuadrantTool));
            OnPropertyChanged(nameof(IsCurlyQuadrantTool));
            OnPropertyChanged(nameof(IsThresholdTool));
            OnPropertyChanged(nameof(IsRangeTool));
        }
    }

    public bool IsViewTool => ActiveTool == GatingTool.View;
    public bool IsPolygonTool => ActiveTool == GatingTool.Polygon;
    public bool IsRectangleTool => ActiveTool == GatingTool.Rectangle;
    public bool IsQuadrantTool => ActiveTool == GatingTool.Quadrant;
    public bool IsCurlyQuadrantTool => ActiveTool == GatingTool.CurlyQuadrant;
    public bool IsThresholdTool => ActiveTool == GatingTool.Threshold;
    public bool IsRangeTool => ActiveTool == GatingTool.Range;

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
            OnPropertyChanged(nameof(IsDefaultAnalysisMode));
        }
    }
    public bool IsDefaultAnalysisMode => !IsPageEditorMode && !IsIntegrationJobMode && !IsWorkspaceMetadataMode;
    public bool IsIntegrationJobIdle => SelectedIntegrationJob is not { IsRunning: true };
    public bool IsSelectedIntegrationJobConfigEditable => SelectedIntegrationJob is { IsConfigurationLocked: false } && IsIntegrationJobIdle;
    public bool IsSelectedIntegrationJobConfigReadOnly => !IsSelectedIntegrationJobConfigEditable;

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
            unsubscribe_job_metadata_rows();
            if (value is not null)
            {
                IsWorkspaceMetadataMode = false;
                refresh_job_metadata_from_workspace(value);
                subscribe_job_metadata_rows(value);
            }
            OnPropertyChanged(nameof(IsIntegrationJobMode));
            OnPropertyChanged(nameof(IsDefaultAnalysisMode));
            OnPropertyChanged(nameof(IsIntegrationJobIdle));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigEditable));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigReadOnly));
            OnPropertyChanged(nameof(IsSelectedIntegrationJobConfigReadOnly));
            OnPropertyChanged(nameof(SelectedIntegrationJobName));
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
        foreach (var group in loaded.Groups)
        {
            group.RecalculateSamples();
            Workspace.Groups.Add(group);
        }
        foreach (var layout in loaded.PageLayouts)
            Workspace.PageLayouts.Add(layout);
        foreach (var job in loaded.IntegrationJobs)
            Workspace.IntegrationJobs.Add(job);
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
        refresh_project_tree();
        OnPropertyChanged(nameof(PlotGate));
        OnPropertyChanged(nameof(PlotPopulation));
        refresh_plot_gates();
        refresh_selected_statistics();
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
        job.SampleMetadata.Clear();
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

            job.SampleMetadata.Add(new IntegrationJobSampleMetadata
            {
                GroupId = group.Id,
                SampleId = sample.Id,
                GroupName = group.Name,
                SampleName = sample.Name,
                Batch = sample.Metadata.TryGetValue("Batch", out string? batch) ? batch : group.Name,
                Condition = sample.Metadata.TryGetValue("Condition", out string? condition) ? condition : "",
                Notes = sample.Metadata.TryGetValue("Notes", out string? notes) ? notes : ""
            });

            foreach (var population in sample.Populations)
                append_integration_population_selection(job, group, sample, population, sample_row_key, 1);
        }

        update_population_selection_states(job);
        refresh_integration_job_features(job);
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
        sync_job_metadata_to_workspace(job);
        await run_integration_job_stage_async(job, runner => runner.RunIntegration(job));
        finish_integration_job_step(job, "Integration complete");
    }

    private async Task run_integration_job_knn_async()
    {
        if (selected_integration_job is null)
            return;
        var job = selected_integration_job;
        sync_job_metadata_to_workspace(job);
        await run_integration_job_stage_async(job, runner => runner.RunKnn(job));
        finish_integration_job_step(job, "kNN graph complete");
    }

    private async Task run_integration_job_umap_async()
    {
        if (selected_integration_job is null)
            return;
        var job = selected_integration_job;
        sync_job_metadata_to_workspace(job);
        await run_integration_job_stage_async(job, runner => runner.RunUmap(job));
        finish_integration_job_step(job, "UMAP complete");
        refresh_axis_choices();
    }

    private async Task run_integration_job_leiden_async()
    {
        if (selected_integration_job is null)
            return;
        var job = selected_integration_job;
        sync_job_metadata_to_workspace(job);
        await run_integration_job_stage_async(job, runner => runner.RunLeiden(job));
        finish_integration_job_step(job, "Leiden clustering complete");
        refresh_axis_choices();
    }

    private async Task run_integration_job_flowsom_async()
    {
        if (selected_integration_job is null)
            return;
        var job = selected_integration_job;
        sync_job_metadata_to_workspace(job);
        await run_integration_job_stage_async(job, runner => runner.RunFlowSom(job));
        finish_integration_job_step(job, "FlowSOM clustering complete");
        refresh_axis_choices();
    }

    private async Task write_integration_job_results_async()
    {
        if (selected_integration_job is null)
            return;
        var job = selected_integration_job;
        sync_job_metadata_to_workspace(job);
        await run_integration_job_stage_async(job, runner => runner.WriteBack(job));
        finish_integration_job_step(job, job.Status == IntegrationJobStatus.Complete ? "Integration job results written" : job.StatusText);
        refresh_axis_choices();
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

    private void finish_integration_job_step(IntegrationJob job, string success_status)
    {
        StatusText = job.HasWarning ? job.WarningText : success_status;
        refresh_project_tree();
        raise_command_states();
    }

    private void refresh_workspace_sample_metadata()
    {
        unsubscribe_workspace_metadata_rows();
        WorkspaceSampleMetadata.Clear();
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
        {
            WorkspaceSampleMetadata.Add(new IntegrationJobSampleMetadata
            {
                GroupId = group.Id,
                SampleId = sample.Id,
                GroupName = group.Name,
                SampleName = sample.Name,
                Batch = sample.Metadata.TryGetValue("Batch", out string? batch) ? batch : "",
                Condition = sample.Metadata.TryGetValue("Condition", out string? condition) ? condition : "",
                Notes = sample.Metadata.TryGetValue("Notes", out string? notes) ? notes : ""
            });
        }
        subscribe_workspace_metadata_rows();
    }

    public void CommitWorkspaceSampleMetadata()
    {
        foreach (var row in WorkspaceSampleMetadata)
            sync_workspace_metadata_row(row);
        StatusText = "Workspace sample metadata updated";
    }

    private void sync_workspace_metadata_row(IntegrationJobSampleMetadata row)
    {
        if (syncing_metadata)
            return;
        syncing_metadata = true;
        try
        {
            var sample = Workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == row.SampleId);
            if (sample is null)
                return;
            sample.Metadata["Batch"] = row.Batch;
            sample.Metadata["Condition"] = row.Condition;
            sample.Metadata["Notes"] = row.Notes;

            foreach (var job_row in Workspace.IntegrationJobs.SelectMany(job => job.SampleMetadata).Where(metadata => metadata.SampleId == row.SampleId))
            {
                job_row.Batch = row.Batch;
                job_row.Condition = row.Condition;
                job_row.Notes = row.Notes;
            }
        }
        finally
        {
            syncing_metadata = false;
        }
    }

    private void subscribe_workspace_metadata_rows()
    {
        subscribed_workspace_metadata_rows = WorkspaceSampleMetadata.ToArray();
        foreach (var row in subscribed_workspace_metadata_rows)
            row.PropertyChanged += workspace_metadata_row_changed;
    }

    private void unsubscribe_workspace_metadata_rows()
    {
        foreach (var row in subscribed_workspace_metadata_rows)
            row.PropertyChanged -= workspace_metadata_row_changed;
        subscribed_workspace_metadata_rows = [];
    }

    private void workspace_metadata_row_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is IntegrationJobSampleMetadata row)
            sync_workspace_metadata_row(row);
    }

    private void subscribe_job_metadata_rows(IntegrationJob job)
    {
        subscribed_integration_job_metadata = job;
        subscribed_job_metadata_rows = job.SampleMetadata.ToArray();
        foreach (var row in subscribed_job_metadata_rows)
            row.PropertyChanged += job_metadata_row_changed;
    }

    private void unsubscribe_job_metadata_rows()
    {
        foreach (var row in subscribed_job_metadata_rows)
            row.PropertyChanged -= job_metadata_row_changed;
        subscribed_job_metadata_rows = [];
        subscribed_integration_job_metadata = null;
    }

    private void job_metadata_row_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (syncing_metadata || sender is not IntegrationJobSampleMetadata row)
            return;
        syncing_metadata = true;
        try
        {
            var sample = Workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == row.SampleId);
            if (sample is not null)
            {
                sample.Metadata["Batch"] = row.Batch;
                sample.Metadata["Condition"] = row.Condition;
                sample.Metadata["Notes"] = row.Notes;
            }

            var workspace_row = WorkspaceSampleMetadata.FirstOrDefault(metadata => metadata.SampleId == row.SampleId);
            if (workspace_row is not null)
            {
                workspace_row.Batch = row.Batch;
                workspace_row.Condition = row.Condition;
                workspace_row.Notes = row.Notes;
            }

            foreach (var other_job_row in Workspace.IntegrationJobs
                         .Where(job => !ReferenceEquals(job, subscribed_integration_job_metadata))
                         .SelectMany(job => job.SampleMetadata)
                         .Where(metadata => metadata.SampleId == row.SampleId))
            {
                other_job_row.Batch = row.Batch;
                other_job_row.Condition = row.Condition;
                other_job_row.Notes = row.Notes;
            }
        }
        finally
        {
            syncing_metadata = false;
        }
    }

    private void sync_job_metadata_to_workspace(IntegrationJob job)
    {
        foreach (var row in job.SampleMetadata)
        {
            var sample = Workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == row.SampleId);
            if (sample is null)
                continue;
            sample.Metadata["Batch"] = row.Batch;
            sample.Metadata["Condition"] = row.Condition;
            sample.Metadata["Notes"] = row.Notes;

            var workspace_row = WorkspaceSampleMetadata.FirstOrDefault(metadata => metadata.SampleId == row.SampleId);
            if (workspace_row is not null)
            {
                workspace_row.Batch = row.Batch;
                workspace_row.Condition = row.Condition;
                workspace_row.Notes = row.Notes;
            }
        }
    }

    private void refresh_job_metadata_from_workspace(IntegrationJob job)
    {
        foreach (var row in job.SampleMetadata)
        {
            var sample = Workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == row.SampleId);
            if (sample is null)
                continue;
            row.Batch = sample.Metadata.TryGetValue("Batch", out string? batch) ? batch : row.Batch;
            row.Condition = sample.Metadata.TryGetValue("Condition", out string? condition) ? condition : row.Condition;
            row.Notes = sample.Metadata.TryGetValue("Notes", out string? notes) ? notes : row.Notes;
        }
    }

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
        if (selected_node?.Kind == ProjectNodeKind.Sample && selected_group is not null && selected_sample is not null)
        {
            remove_sample_preferred_views(selected_group, selected_sample.Name);
            selected_group.Samples.Remove(selected_sample);
        }
        else if (selected_node?.Kind == ProjectNodeKind.Gate && selected_group is not null && selected_gate is not null)
            remove_gate(selected_group, selected_gate);
        else if (selected_node?.Kind == ProjectNodeKind.Group && selected_group is not null)
            Workspace.Groups.Remove(selected_group);
        else if (selected_node?.Kind == ProjectNodeKind.IntegrationJob && selected_integration_job is not null)
            Workspace.IntegrationJobs.Remove(selected_integration_job);

        selected_group = Workspace.Groups.FirstOrDefault();
        selected_sample = selected_group?.Samples.FirstOrDefault();
        selected_gate = selected_group?.Gates.FirstOrDefault();
        selected_integration_job = null;
        selected_group?.RecalculateSamples();
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

    private static IEnumerable<GateDefinition> all_gates(IEnumerable<GateDefinition> gates)
    {
        foreach (var gate in gates)
        {
            yield return gate;
            foreach (var child in all_gates(gate.Children))
                yield return child;
        }
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

        var first = get_default_x_channel(selected_group)!;
        var second = get_default_y_channel(selected_group, first);
        var gate = new GateDefinition
        {
            Name = await request_gate_name_async(),
            Kind = kind,
            XChannel = XAxis.ChannelName,
            YChannel = kind is GateKind.Threshold or GateKind.Range ? null : YAxis.ChannelName,
            ParentPopulationRegion = selected_population?.Region ?? PopulationRegion.Primary
        };
        copy_current_view_to_gate(gate);

        if (kind is GateKind.Polygon)
        {
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.2, second.Maximum * 0.2));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.55, second.Maximum * 0.25));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.7, second.Maximum * 0.65));
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.25, second.Maximum * 0.75));
        }
        else
        {
            gate.Vertices.Add(new Avalonia.Point(first.Maximum * 0.25, second.Maximum * 0.25));
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
        selected_group.RecalculateSamples();
        refresh_project_tree();
        OnPropertyChanged(nameof(PlotGate));
        refresh_plot_gates();
        refresh_selected_statistics();
        raise_command_states();
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
        foreach (var sample in selected_group.Samples)
        foreach (string embedding_name in sample.Embeddings.Keys)
        {
            if (choices.All(choice => choice.Name != embedding_name))
                choices.Add(new AxisChoice(embedding_name, ""));
        }

        return choices;
    }

    private async Task add_canvas_gate_async(GateDefinition? gate)
    {
        if (gate is null || selected_group is null)
            return;

        var siblings = selected_gate is null ? selected_group.Gates : selected_gate.Children;
        gate.Name = await request_gate_name_async();
        if (string.IsNullOrWhiteSpace(gate.XChannel))
            gate.XChannel = XAxis.ChannelName;
        if (!gate.IsOneDimensional && string.IsNullOrWhiteSpace(gate.YChannel))
            gate.YChannel = YAxis.ChannelName;
        gate.ParentPopulationRegion = selected_population?.Region ?? PopulationRegion.Primary;
        copy_current_view_to_gate(gate);

        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = gate.XChannel });
        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.FrequencyOfParent, ChannelName = gate.XChannel });
        gate.Parent = selected_gate;
        siblings.Add(gate);
        selected_group.RecalculateSamples();
        refresh_project_tree();
        refresh_plot_gates();
        refresh_selected_statistics();
        StatusText = $"{gate.Kind} gate created from canvas";
        raise_command_states();
    }

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
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                needs_plot_refresh = true;
                break;
            case ProjectNodeKind.Compensation:
                SelectedIntegrationJob = null;
                IsWorkspaceMetadataMode = false;
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = node.Compensation;
                apply_root_axis_context();
                needs_plot_refresh = true;
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
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = node.Gate;
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
            return;
        }

        foreach (var channel in selected_group.Channels)
            ChannelRows.Add(new ChannelRow(channel, update_channel_label));

        refresh_axis_choices();
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

        return sample.Embeddings
            .Where(embedding => Workspace.IntegrationJobs.Any(job =>
                job_outputs_key(job, embedding.Key) &&
                job.RowMap.Any(row =>
                    row.SampleId == sample.Id &&
                    row.GateId == population.Gate.Id &&
                    row.Region == population.Region)) &&
                population.EventIndices.Any(index =>
                index >= 0 &&
                index < embedding.Value.Length &&
                !float.IsNaN(embedding.Value[index]) &&
                !float.IsInfinity(embedding.Value[index])))
            .Select(embedding => embedding.Key)
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
        int? count = null,
        bool is_applied_compensation = false,
        int depth = 0)
    {
        return new ProjectNode(kind, name, key, group, sample, gate, population, statistic_definition, statistic_result, compensation, layout, integration_job, embedding_name, count, is_applied_compensation, depth)
        {
            IsExpanded = project_expansion_state.TryGetValue(key, out bool is_expanded) ? is_expanded : true
        };
    }

    private void append_gate_node(ProjectNode parent, GateDefinition gate, FlowGroup group, string key, int depth)
    {
        var gate_node = create_project_node(ProjectNodeKind.Gate, gate.Name, key, gate: gate, group: group, count: count_gate_events(group, gate), depth: depth);
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
        foreach (var embedding in sample.Embeddings.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            bool is_job_population_output = Workspace.IntegrationJobs.Any(job =>
                job.RowMap.Any(row =>
                    row.SampleId == sample.Id &&
                    row.GateId == population.Gate.Id &&
                    row.Region == population.Region) &&
                job_outputs_key(job, embedding.Key));
            if (!is_job_population_output)
                continue;

            int finite_count = population.EventIndices.Count(index =>
                index >= 0 &&
                index < embedding.Value.Length &&
                !float.IsNaN(embedding.Value[index]) &&
                !float.IsInfinity(embedding.Value[index]));
            if (finite_count == 0)
                continue;

            population_node.Children.Add(create_project_node(
                ProjectNodeKind.Embedding,
                embedding.Key,
                $"{key}:embedding:{embedding.Key}",
                group: group,
                sample: sample,
                gate: population.Gate,
                population: population,
                embedding_name: embedding.Key,
                count: finite_count,
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

    private static bool job_outputs_key(IntegrationJob job, string key) =>
        key == job.LeidenKey ||
        key == job.FlowSomKey ||
        key == job.UmapXKey ||
        key == job.UmapYKey ||
        key == job.UmapZKey;

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
            RenameGateCommand,
            RenameLayoutCommand,
            ConcatenateSamplesCommand,
            ApplyCompensationCommand,
            EditCompensationCommand,
            DeleteSelectedCommand,
            CreateIntegrationJobCommand,
            RenameIntegrationJobCommand,
            RefreshIntegrationJobFeaturesCommand,
            RunIntegrationJobCommand,
            RunIntegrationJobKnnCommand,
            RunIntegrationJobUmapCommand,
            RunIntegrationJobLeidenCommand,
            RunIntegrationJobFlowSomCommand,
            WriteIntegrationJobResultsCommand,
            CancelIntegrationJobCommand,
            ApplyWorkspaceMetadataCommand,
            AddMeanStatisticCommand,
            AddMedianStatisticCommand,
            AddGeometricMeanStatisticCommand,
            AddCoefficientOfVariationStatisticCommand,
            AddStandardDeviationStatisticCommand,
            AddFrequencyOfParentStatisticCommand,
            AddFrequencyOfAllStatisticCommand,
            AddCountStatisticCommand
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

        var element = new PagePlotElement
        {
            Group = node.Group,
            Sample = node.Kind == ProjectNodeKind.Population ? node.Sample : null,
            Gate = gate,
            Population = node.Kind == ProjectNodeKind.Population ? node.Population : null,
            XAxis = x_axis,
            YAxis = y_axis,
            X = Math.Clamp(request.PagePoint.X - 130, 0, 740),
            Y = Math.Clamp(request.PagePoint.Y - 130, 0, 500),
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

    private AxisSettings XAxisClone() =>
        new() { ChannelName = XAxis.ChannelName, Minimum = XAxis.Minimum, Maximum = XAxis.Maximum, Scale = XAxis.Scale.Clone() };

    private AxisSettings YAxisClone() =>
        new() { ChannelName = YAxis.ChannelName, Minimum = YAxis.Minimum, Maximum = YAxis.Maximum, Scale = YAxis.Scale.Clone() };

    private DotColorSettings DotColorClone() =>
        new() { ChannelName = DotColor.ChannelName, Palette = DotColor.Palette };

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
