using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed class MainWindowViewModel : NotifyBase
{
    private readonly ObservableCollection<ProjectNode> project_roots = new();
    private ProjectNode? selected_node;
    private FlowGroup? selected_group;
    private FlowSample? selected_sample;
    private GateDefinition? selected_gate;
    private PopulationResult? selected_population;
    private FlowGroup? sidebars_group;
    private PlotMode selected_plot_mode = PlotMode.Density;
    private GatingTool active_tool = GatingTool.View;
    private string status_text = "Append samples to grouping to begin analysis";
    private AxisSettings x_axis = new();
    private AxisSettings y_axis = new();

    public FlowWorkspace Workspace { get; } = new();
    public ObservableCollection<ProjectNode> ProjectNodes { get; } = new();
    public ObservableCollection<ChannelDefinition> ChannelRows { get; } = new();
    public ObservableCollection<AxisChoice> AxisChoices { get; } = new();
    private DataTable statistic_table = new();
    public ObservableCollection<GateDefinition> PlotGates { get; } = new();
    public ObservableCollection<CoordinateScaleKind> CoordinateScaleChoices { get; } = new(Enum.GetValues<CoordinateScaleKind>());
    public DataView StatisticTableView => statistic_table.DefaultView;
    public DataTable StatisticTable => statistic_table;

    public ICommand CreateGroupCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand AddPolygonGateCommand { get; }
    public ICommand AddRectangleGateCommand { get; }
    public ICommand AddThresholdGateCommand { get; }
    public ICommand AddRangeGateCommand { get; }
    public ICommand AddMeanStatisticCommand { get; }
    public ICommand AddMedianStatisticCommand { get; }
    public ICommand AddCountStatisticCommand { get; }
    public ICommand AddCanvasGateCommand { get; }
    public ICommand GateEditedCommand { get; }
    public ICommand ToggleProjectNodeCommand { get; }
    public ICommand SelectProjectNodeCommand { get; }

    public MainWindowViewModel()
    {
        CreateGroupCommand = new RelayCommand(_ => create_group());
        DeleteSelectedCommand = new RelayCommand(_ => delete_selected());
        AddPolygonGateCommand = new RelayCommand(_ => add_gate(GateKind.Polygon));
        AddRectangleGateCommand = new RelayCommand(_ => add_gate(GateKind.Rectangle));
        AddThresholdGateCommand = new RelayCommand(_ => add_gate(GateKind.Threshold));
        AddRangeGateCommand = new RelayCommand(_ => add_gate(GateKind.Range));
        AddMeanStatisticCommand = new RelayCommand(_ => add_statistic(StatisticKind.Mean));
        AddMedianStatisticCommand = new RelayCommand(_ => add_statistic(StatisticKind.Median));
        AddCountStatisticCommand = new RelayCommand(_ => add_statistic(StatisticKind.NumberOfEvents));
        AddCanvasGateCommand = new RelayCommand(parameter => add_canvas_gate(parameter as GateDefinition));
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
        private set => SetField(ref selected_group, value);
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

    public PlotMode SelectedPlotMode
    {
        get => selected_plot_mode;
        set
        {
            if (!SetField(ref selected_plot_mode, value))
                return;
            OnPropertyChanged(nameof(EffectivePlotMode));
            OnPropertyChanged(nameof(IsYAxisEnabled));
            refresh_plot_gates();
        }
    }

    public PlotMode EffectivePlotMode => selected_gate?.IsOneDimensional == true ? PlotMode.Histogram : SelectedPlotMode;
    public bool IsYAxisEnabled => EffectivePlotMode != PlotMode.Histogram;

    public GatingTool ActiveTool
    {
        get => active_tool;
        set => SetField(ref active_tool, value);
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

    private static void remove_gate(FlowGroup group, GateDefinition gate)
    {
        if (gate.Parent is not null)
        {
            gate.Parent.Children.Remove(gate);
            return;
        }

        group.Gates.Remove(gate);
    }

    private void add_gate(GateKind kind)
    {
        if (selected_group is null || selected_group.Channels.Count == 0)
            return;

        var first = get_default_x_channel(selected_group)!;
        var second = get_default_y_channel(selected_group, first);
        var gate = new GateDefinition
        {
            Name = $"Gate {(selected_gate?.Children.Count ?? selected_group.Gates.Count) + 1}",
            Kind = kind,
            XChannel = XAxis.ChannelName,
            YChannel = kind is GateKind.Threshold or GateKind.Range ? null : YAxis.ChannelName
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
    }

    private void add_statistic(StatisticKind kind)
    {
        if (selected_gate is null || selected_group is null)
            return;

        string channel_name = selected_gate.XChannel;
        selected_gate.Statistics.Add(new StatisticDefinition { Kind = kind, ChannelName = channel_name });
        selected_group.RecalculateSamples();
        refresh_project_tree();
        refresh_selected_statistics();
    }

    private void add_canvas_gate(GateDefinition? gate)
    {
        if (gate is null || selected_group is null)
            return;

        var siblings = selected_gate is null ? selected_group.Gates : selected_gate.Children;
        gate.Name = $"Gate {siblings.Count + 1}";
        if (string.IsNullOrWhiteSpace(gate.XChannel))
            gate.XChannel = XAxis.ChannelName;
        if (!gate.IsOneDimensional && string.IsNullOrWhiteSpace(gate.YChannel))
            gate.YChannel = YAxis.ChannelName;
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
    }

    private void apply_node_selection(ProjectNode? node)
    {
        if (node is null)
            return;

        switch (node.Kind)
        {
            case ProjectNodeKind.GateFolder:
                if (node.Group is not null)
                    SelectedGroup = node.Group;
                refresh_selection_sidebars();
                SelectedSample = null;
                SelectedPopulation = null;
                SelectedGate = null;
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
                apply_root_axis_context();
                refresh_plot_gates();
                break;
        }

        refresh_selection_sidebars();
        refresh_selected_statistics();
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
        if (x_channel is null)
            return;

        var y_channel = string.IsNullOrWhiteSpace(gate.YChannel)
            ? null
            : selected_group.Channels.FirstOrDefault(channel => channel.Name == gate.YChannel);

        XAxis = new AxisSettings
        {
            ChannelName = x_channel.Name,
            Minimum = gate.XMinimum,
            Maximum = gate.XMaximum,
            Scale = gate.XScale.Clone()
        };
        if (y_channel is not null)
        {
            YAxis = new AxisSettings
            {
                ChannelName = y_channel.Name,
                Minimum = gate.YMinimum,
                Maximum = gate.YMaximum,
                Scale = gate.YScale.Clone()
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
        if (gate.IsOneDimensional)
        {
            gate.YChannel = null;
            return;
        }

        gate.YChannel = YAxis.ChannelName;
        gate.YMinimum = YAxis.Minimum;
        gate.YMaximum = YAxis.Maximum;
        gate.YScale = YAxis.Scale.Clone();
    }

    private void sync_selected_gate_preferred_view()
    {
        if (selected_gate is null)
            return;

        copy_current_view_to_gate(selected_gate);
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
        if (ReferenceEquals(sidebars_group, selected_group))
            return;

        sidebars_group = selected_group;
        ChannelRows.Clear();
        if (selected_group is null)
        {
            AxisChoices.Clear();
            return;
        }

        foreach (var channel in selected_group.Channels)
            ChannelRows.Add(channel);

        var existing_choices = AxisChoices.Select(choice => choice.Name).ToHashSet();
        var current_choices = selected_group.Channels.Select(channel => channel.Name).ToHashSet();
        if (existing_choices.SetEquals(current_choices))
            return;

        AxisChoices.Clear();
        foreach (var channel in selected_group.Channels)
            AxisChoices.Add(new AxisChoice(channel.Name, channel.Label));
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
        project_roots.Clear();
        var workspace_node = new ProjectNode(ProjectNodeKind.Workspace, Workspace.Name, depth: 0) { IsExpanded = true };
        foreach (var group in Workspace.Groups)
        {
            var group_node = new ProjectNode(ProjectNodeKind.Group, group.Name, group: group, depth: 1) { IsExpanded = true };
            var gates_node = new ProjectNode(ProjectNodeKind.GateFolder, "Gating strategies", group: group, depth: 2) { IsExpanded = true };
            foreach (var gate in group.Gates)
                append_gate_node(gates_node, gate, group, 3);

            group_node.Children.Add(gates_node);
            foreach (var sample in group.Samples)
            {
                var sample_node = new ProjectNode(ProjectNodeKind.Sample, sample.Name, sample: sample, group: group, count: sample.EventCount, depth: 2) { IsExpanded = true };
                foreach (var population in sample.Populations)
                    append_population_node(sample_node, sample, population, group, 3);
                group_node.Children.Add(sample_node);
            }
            workspace_node.Children.Add(group_node);
        }

        project_roots.Add(workspace_node);
        refresh_visible_project_nodes();
    }

    private void append_gate_node(ProjectNode parent, GateDefinition gate, FlowGroup group, int depth)
    {
        var gate_node = new ProjectNode(ProjectNodeKind.Gate, gate.Name, gate: gate, group: group, count: count_gate_events(group, gate), depth: depth) { IsExpanded = true };
        foreach (var statistic in gate.Statistics)
            gate_node.Children.Add(new ProjectNode(ProjectNodeKind.StatisticDefinition, statistic_name(statistic), gate: gate, group: group, statistic_definition: statistic, depth: depth + 1));
        foreach (var child in gate.Children)
            append_gate_node(gate_node, child, group, depth + 1);
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

    private void append_population_node(ProjectNode parent, FlowSample sample, PopulationResult population, FlowGroup group, int depth)
    {
        var population_node = new ProjectNode(ProjectNodeKind.Population, population.Gate.Name, group: group, sample: sample, gate: population.Gate, population: population, count: population.EventCount, depth: depth) { IsExpanded = true };
        foreach (var statistic in population.Statistics)
            population_node.Children.Add(new ProjectNode(ProjectNodeKind.StatisticValue, statistic.DisplayName, group: group, sample: sample, gate: population.Gate, population: population, statistic_result: statistic, depth: depth + 1));
        foreach (var child in population.Children)
            append_population_node(population_node, sample, child, group, depth + 1);
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
        refresh_visible_project_nodes();
    }

    private void select_project_node(ProjectNode? node)
    {
        SelectedNode = node;
    }

    private static int count_gate_events(FlowGroup group, GateDefinition gate)
    {
        int count = 0;
        foreach (var sample in group.Samples)
            count += find_population(sample.Populations, gate)?.EventCount ?? 0;
        return count;
    }

    private void refresh_selected_statistics()
    {
        statistic_table = new DataTable();
        statistic_table.Columns.Add("Sample", typeof(string));

        if (selected_group is null || selected_gate is null)
        {
            OnPropertyChanged(nameof(StatisticTable));
            OnPropertyChanged(nameof(StatisticTableView));
            return;
        }

        var definitions = selected_gate.Statistics.ToArray();
        var column_names = new List<string>();
        foreach (var definition in definitions)
        {
            string column_name = unique_column_name(statistic_name(definition), column_names);
            column_names.Add(column_name);
            statistic_table.Columns.Add(column_name, typeof(string));
        }

        foreach (var sample in selected_group.Samples)
        {
            var population = find_population(sample.Populations, selected_gate);
            var row = statistic_table.NewRow();
            row["Sample"] = sample.Name;
            for (int index = 0; index < definitions.Length; index++)
            {
                var statistic = population?.Statistics.FirstOrDefault(item => statistic_matches_definition(item, definitions[index]));
                row[column_names[index]] = statistic?.DisplayValue ?? "";
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

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, GateDefinition gate)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate)
                return population;
            var child = find_population(population.Children, gate);
            if (child is not null)
                return child;
        }

        return null;
    }
}

public sealed record AxisChoice(string Name, string Label)
{
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
    public string DisplayLabel => HasLabel ? Label : Name;
}

public enum ProjectNodeKind
{
    Workspace,
    Group,
    GateFolder,
    Gate,
    StatisticDefinition,
    Sample,
    Population,
    StatisticValue
}

public sealed class ProjectNode : NotifyBase
{
    private bool is_expanded;
    private bool is_selected;

    public ProjectNodeKind Kind { get; }
    public string Name { get; }
    public FlowGroup? Group { get; }
    public FlowSample? Sample { get; }
    public GateDefinition? Gate { get; }
    public PopulationResult? Population { get; }
    public StatisticDefinition? StatisticDefinition { get; }
    public StatisticResult? StatisticResult { get; }
    public int? Count { get; }
    public int Depth { get; }
    public ObservableCollection<ProjectNode> Children { get; } = new();

    public ProjectNode(
        ProjectNodeKind kind,
        string name,
        FlowGroup? group = null,
        FlowSample? sample = null,
        GateDefinition? gate = null,
        PopulationResult? population = null,
        StatisticDefinition? statistic_definition = null,
        StatisticResult? statistic_result = null,
        int? count = null,
        int depth = 0)
    {
        Kind = kind;
        Name = name;
        Group = group;
        Sample = sample;
        Gate = gate;
        Population = population;
        StatisticDefinition = statistic_definition;
        StatisticResult = statistic_result;
        Count = count;
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
