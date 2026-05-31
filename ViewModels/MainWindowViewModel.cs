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
    private PlotMode selected_plot_mode = PlotMode.Density;
    private GatingTool active_tool = GatingTool.View;
    private bool show_outlier_points = true;
    private bool show_gridlines = true;
    private int contour_level_count = 10;
    private int density_smoothing = 9;
    private string status_text = "Append samples to grouping to begin analysis";
    private AxisSettings x_axis = new();
    private AxisSettings y_axis = new();
    private int next_gate_number = 1;

    public FlowWorkspace Workspace { get; } = new();
    public ObservableCollection<ProjectNode> ProjectNodes { get; } = new();
    public ObservableCollection<ChannelRow> ChannelRows { get; } = new();
    public ObservableCollection<AxisChoice> AxisChoices { get; } = new();
    private DataTable statistic_table = new();
    public ObservableCollection<GateDefinition> PlotGates { get; } = new();
    public ObservableCollection<CoordinateScaleKind> CoordinateScaleChoices { get; } = new(Enum.GetValues<CoordinateScaleKind>());
    public DataView StatisticTableView => statistic_table.DefaultView;
    public DataTable StatisticTable => statistic_table;

    public ICommand CreateGroupCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand RenameGateCommand { get; }
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
    public Func<string, string, Task<string?>>? RequestTextInputAsync { get; set; }
    public Func<string, IReadOnlyList<AxisChoice>, Task<string?>>? RequestChoiceInputAsync { get; set; }
    public Func<CompensationMatrix, Task<bool>>? RequestCompensationEditorAsync { get; set; }

    public MainWindowViewModel()
    {
        CreateGroupCommand = new RelayCommand(_ => create_group());
        RenameGroupCommand = new RelayCommand(_ => _ = rename_selected_group_async(), _ => selected_group is not null);
        RenameGateCommand = new RelayCommand(_ => _ = rename_selected_gate_async(), _ => selected_gate is not null);
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
        private set => SetField(ref selected_sample, value);
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

    public bool ShowGridlines
    {
        get => show_gridlines;
        set => SetField(ref show_gridlines, value);
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
        }
    }

    public AxisSettings YAxis
    {
        get => y_axis;
        private set
        {
            set_axis(ref y_axis, value, y_axis_property_changed);
            OnPropertyChanged(nameof(SelectedYAxisChoice));
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
        }
    }

    public string StatusText
    {
        get => status_text;
        set => SetField(ref status_text, value);
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
        foreach (var group in loaded.Groups)
        {
            group.RecalculateSamples();
            Workspace.Groups.Add(group);
        }

        project_expansion_state.Clear();
        SelectedNode = null;
        SelectedGroup = Workspace.Groups.FirstOrDefault();
        SelectedSample = selected_group?.Samples.FirstOrDefault();
        SelectedGate = null;
        SelectedPopulation = null;
        SelectedCompensation = null;
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
            selected_group.Samples.Remove(selected_sample);
        else if (selected_node?.Kind == ProjectNodeKind.Gate && selected_group is not null && selected_gate is not null)
            remove_gate(selected_group, selected_gate);
        else if (selected_node?.Kind == ProjectNodeKind.Group && selected_group is not null)
            Workspace.Groups.Remove(selected_group);

        selected_group = Workspace.Groups.FirstOrDefault();
        selected_sample = selected_group?.Samples.FirstOrDefault();
        selected_gate = selected_group?.Gates.FirstOrDefault();
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

        selected_group.RecalculateSamples();
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

        switch (node.Kind)
        {
            case ProjectNodeKind.Workspace:
                SelectedGroup = Workspace.Groups.FirstOrDefault();
                SelectedSample = selected_group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                refresh_plot_gates();
                break;
            case ProjectNodeKind.GateFolder:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                refresh_plot_gates();
                break;
            case ProjectNodeKind.CompensationFolder:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                refresh_plot_gates();
                break;
            case ProjectNodeKind.Compensation:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = node.Compensation;
                apply_root_axis_context();
                refresh_plot_gates();
                break;
            case ProjectNodeKind.Sample:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = node.Sample;
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                refresh_plot_gates();
                break;
            case ProjectNodeKind.Gate:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = node.Gate;
                SelectedCompensation = null;
                if (node.Gate is not null)
                    apply_axis_from_gate_context(node.Gate);
                refresh_plot_gates();
                break;
            case ProjectNodeKind.Population:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = node.Sample;
                SelectedPopulation = node.Population;
                SelectedGate = node.Population?.Gate;
                SelectedCompensation = null;
                if (node.Population is not null)
                    apply_axis_from_gate_context(node.Population.Gate);
                refresh_plot_gates();
                break;
            case ProjectNodeKind.Group:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = node.Group?.Samples.FirstOrDefault();
                SelectedPopulation = null;
                SelectedGate = null;
                SelectedCompensation = null;
                apply_root_axis_context();
                refresh_plot_gates();
                break;
        }

        refresh_selection_sidebars();
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

        var x_channel = selected_group.Channels.FirstOrDefault(channel => channel.Name == gate.XChannel);
        bool has_preferred_view = !string.IsNullOrWhiteSpace(gate.PreferredXChannel);
        string preferred_x_channel_name = has_preferred_view ? gate.PreferredXChannel : gate.XChannel;
        var preferred_x_channel = selected_group.Channels.FirstOrDefault(channel => channel.Name == preferred_x_channel_name);
        if (x_channel is null || preferred_x_channel is null)
            return;

        string? preferred_y_channel_name = has_preferred_view ? gate.PreferredYChannel : gate.YChannel;
        var preferred_y_channel = string.IsNullOrWhiteSpace(preferred_y_channel_name)
            ? null
            : selected_group.Channels.FirstOrDefault(channel => channel.Name == preferred_y_channel_name);

        XAxis = new AxisSettings
        {
            ChannelName = preferred_x_channel.Name,
            Minimum = has_preferred_view ? gate.PreferredXMinimum : gate.XMinimum,
            Maximum = has_preferred_view ? gate.PreferredXMaximum : gate.XMaximum,
            Scale = (has_preferred_view ? gate.PreferredXScale : gate.XScale).Clone()
        };
        if (preferred_y_channel is not null)
        {
            YAxis = new AxisSettings
            {
                ChannelName = preferred_y_channel.Name,
                Minimum = has_preferred_view ? gate.PreferredYMinimum : gate.YMinimum,
                Maximum = has_preferred_view ? gate.PreferredYMaximum : gate.YMaximum,
                Scale = (has_preferred_view ? gate.PreferredYScale : gate.YScale).Clone()
            };
        }
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
        }
        sync_selected_gate_preferred_view();
        refresh_plot_gates();
    }

    private void y_axis_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AxisSettings.ChannelName))
        {
            apply_axis_channel_defaults(YAxis);
            OnPropertyChanged(nameof(SelectedYAxisChoice));
        }
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
        var channel = selected_group?.Channels.FirstOrDefault(item => item.Name == axis.ChannelName);
        if (channel is null)
            return;

        axis.Minimum = 0;
        axis.Maximum = channel.Maximum;
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
        if (selected_group is not null)
        {
            foreach (var channel in selected_group.Channels)
                AxisChoices.Add(new AxisChoice(channel.Name, channel.Label));
        }

        OnPropertyChanged(nameof(SelectedXAxisChoice));
        OnPropertyChanged(nameof(SelectedYAxisChoice));
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
        int? count = null,
        bool is_applied_compensation = false,
        int depth = 0)
    {
        return new ProjectNode(kind, name, key, group, sample, gate, population, statistic_definition, statistic_result, compensation, count, is_applied_compensation, depth)
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
            ConcatenateSamplesCommand,
            ApplyCompensationCommand,
            EditCompensationCommand,
            DeleteSelectedCommand,
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
}

public sealed record AxisChoice(string Name, string Label)
{
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
    public string DisplayLabel => HasLabel ? Label : Name;
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
    Group,
    GateFolder,
    Gate,
    StatisticDefinition,
    CompensationFolder,
    Compensation,
    Sample,
    Population,
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
        ProjectNodeKind.Group => "avares://gated/Resources/group.svg",
        ProjectNodeKind.GateFolder => "avares://gated/Resources/gates.svg",
        ProjectNodeKind.Gate => "avares://gated/Resources/gate.svg",
        ProjectNodeKind.StatisticDefinition => "avares://gated/Resources/statistics.svg",
        ProjectNodeKind.CompensationFolder => "avares://gated/Resources/matrix.svg",
        ProjectNodeKind.Compensation => IsAppliedCompensation ? "avares://gated/Resources/ok.svg" : "avares://gated/Resources/matrix.svg",
        ProjectNodeKind.Sample => "avares://gated/Resources/tube.svg",
        ProjectNodeKind.Population => "avares://gated/Resources/subset.svg",
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
        ProjectNodeKind.Group => "G",
        ProjectNodeKind.GateFolder => "F",
        ProjectNodeKind.Gate => "g",
        ProjectNodeKind.StatisticDefinition => "D",
        ProjectNodeKind.CompensationFolder => "C",
        ProjectNodeKind.Compensation => IsAppliedCompensation ? "*" : "M",
        ProjectNodeKind.Sample => "S",
        ProjectNodeKind.Population => "P",
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
