using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;

namespace gated.Models;

public enum CoordinateScaleKind
{
    Linear,
    Logicle
}

public enum GateKind
{
    Polygon,
    Rectangle,
    Quadrant,
    CurlyQuadrant,
    Threshold,
    Range,
    Merge,
    Exclude,
    Overlap
}

public enum PlotMode
{
    Density,
    Contour,
    Zebra,
    Histogram
}

public enum GatingTool
{
    View,
    Polygon,
    Rectangle,
    Quadrant,
    CurlyQuadrant,
    Threshold,
    Range
}

public enum StatisticKind
{
    Mean,
    Median,
    GeometricMean,
    CoefficientOfVariation,
    StandardDeviation,
    FrequencyOfParent,
    FrequencyOfAll,
    NumberOfEvents
}

public sealed record ChannelDefinition(int Index, string Name, string Label, float Maximum, float Gain);

public sealed record LogicleParameters(double T = 262144.0, double W = 0.3, double M = 3.0, double A = 0.0);

public sealed class AxisScale
{
    public CoordinateScaleKind Kind { get; set; } = CoordinateScaleKind.Linear;
    public LogicleParameters Logicle { get; set; } = new();

    public AxisScale Clone() =>
        new()
        {
            Kind = Kind,
            Logicle = Logicle
        };

    public double Transform(double value)
    {
        if (Kind == CoordinateScaleKind.Linear)
            return value;

        return new LogicleTransform(Logicle).Transform(value);
    }

    public double InverseTransform(double value)
    {
        if (Kind == CoordinateScaleKind.Linear)
            return value;

        return new LogicleTransform(Logicle).InverseTransform(value);
    }

    public bool IsEquivalent(AxisScale other)
    {
        if (Kind != other.Kind)
            return false;

        if (Kind == CoordinateScaleKind.Linear)
            return true;

        return Logicle.Equals(other.Logicle);
    }
}

public sealed class AxisSettings : NotifyBase
{
    private string channel_name = "";
    private double minimum;
    private double maximum = 262144.0;
    private AxisScale scale = new();

    public string ChannelName
    {
        get => channel_name;
        set => SetField(ref channel_name, value ?? "");
    }

    public double Minimum
    {
        get => minimum;
        set => SetField(ref minimum, value);
    }

    public double Maximum
    {
        get => maximum;
        set => set_maximum(value, sync_logicle_top: true);
    }

    public AxisScale Scale
    {
        get => scale;
        set
        {
            value.Logicle = value.Logicle with { T = maximum };
            if (!SetField(ref scale, value))
                return;

            OnPropertyChanged(nameof(ScaleKind));
            OnPropertyChanged(nameof(IsLogicle));
            OnPropertyChanged(nameof(LogicleTopOfScale));
            OnPropertyChanged(nameof(LogicleDecades));
            OnPropertyChanged(nameof(LogicleLinearizationWidth));
            OnPropertyChanged(nameof(LogicleNegativeDecades));
        }
    }

    public CoordinateScaleKind ScaleKind
    {
        get => scale.Kind;
        set
        {
            if (scale.Kind == value)
                return;

            scale.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scale));
            OnPropertyChanged(nameof(IsLogicle));
        }
    }

    public bool IsLogicle => ScaleKind == CoordinateScaleKind.Logicle;

    public double LogicleTopOfScale
    {
        get => scale.Logicle.T;
        set
        {
            if (Math.Abs(scale.Logicle.T - value) < double.Epsilon)
                return;

            scale.Logicle = scale.Logicle with { T = value };
            set_maximum(value, sync_logicle_top: false);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scale));
        }
    }

    public double LogicleDecades
    {
        get => scale.Logicle.M;
        set
        {
            if (Math.Abs(scale.Logicle.M - value) < double.Epsilon)
                return;

            scale.Logicle = scale.Logicle with { M = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scale));
        }
    }

    public double LogicleLinearizationWidth
    {
        get => scale.Logicle.W;
        set
        {
            if (Math.Abs(scale.Logicle.W - value) < double.Epsilon)
                return;

            scale.Logicle = scale.Logicle with { W = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scale));
        }
    }

    public double LogicleNegativeDecades
    {
        get => scale.Logicle.A;
        set
        {
            if (Math.Abs(scale.Logicle.A - value) < double.Epsilon)
                return;

            scale.Logicle = scale.Logicle with { A = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scale));
        }
    }

    private void set_maximum(double value, bool sync_logicle_top)
    {
        if (!SetField(ref maximum, value))
            return;

        if (!sync_logicle_top)
            return;

        scale.Logicle = scale.Logicle with { T = value };
        OnPropertyChanged(nameof(LogicleTopOfScale));
        OnPropertyChanged(nameof(Scale));
    }
}

public sealed class GateDefinition : NotifyBase
{
    private string name = "Gate";
    private GateKind kind;
    private string x_channel = "";
    private string? y_channel;
    private bool is_selected;

    public Guid Id { get; } = Guid.NewGuid();
    public ObservableCollection<Point> Vertices { get; } = new();
    public ObservableCollection<GateDefinition> Children { get; } = new();
    public ObservableCollection<StatisticDefinition> Statistics { get; } = new();
    public double XMinimum { get; set; }
    public double XMaximum { get; set; } = 262144.0;
    public double YMinimum { get; set; }
    public double YMaximum { get; set; } = 262144.0;
    public AxisScale XScale { get; set; } = new();
    public AxisScale YScale { get; set; } = new();
    public GateDefinition? Parent { get; set; }

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public GateKind Kind
    {
        get => kind;
        set => SetField(ref kind, value);
    }

    public string XChannel
    {
        get => x_channel;
        set => SetField(ref x_channel, value);
    }

    public string? YChannel
    {
        get => y_channel;
        set => SetField(ref y_channel, value);
    }

    public bool IsSelected
    {
        get => is_selected;
        set => SetField(ref is_selected, value);
    }

    public bool IsOneDimensional => Kind is GateKind.Threshold or GateKind.Range || string.IsNullOrWhiteSpace(YChannel);
}

public sealed class StatisticDefinition
{
    public StatisticKind Kind { get; init; }
    public string ChannelName { get; init; } = "";
}

public sealed class StatisticResult
{
    public StatisticKind Kind { get; init; }
    public string ChannelName { get; init; } = "";
    public double Value { get; init; }

    public string DisplayName
    {
        get
        {
            switch (Kind)
            {
                case StatisticKind.Mean:
                    return $"Mean of {ChannelName}";
                case StatisticKind.Median:
                    return $"Median of {ChannelName}";
                case StatisticKind.GeometricMean:
                    return $"Geometric Mean of {ChannelName}";
                case StatisticKind.StandardDeviation:
                    return $"Standard Deviation of {ChannelName}";
                case StatisticKind.CoefficientOfVariation:
                    return $"Coefficient of Variation of {ChannelName}";

                case StatisticKind.NumberOfEvents:
                    return $"Number of Events";
                
                case StatisticKind.FrequencyOfParent:
                    return $"Frequency of Parent (%)";
                case StatisticKind.FrequencyOfAll:
                    return $"Frequency of All (%)";

                default: return $"{Kind}";
            }  
        }
    }

    public string DisplayValue
    {
        get
        {
            switch (Kind)
            {
                case StatisticKind.Mean:
                case StatisticKind.Median:
                case StatisticKind.GeometricMean:
                case StatisticKind.StandardDeviation:
                case StatisticKind.CoefficientOfVariation:
                    return Value.ToString("N2");

                case StatisticKind.NumberOfEvents:
                    return Value.ToString("N0");
                
                case StatisticKind.FrequencyOfParent:
                case StatisticKind.FrequencyOfAll:
                    return $"{Value:0.##}%";

                default:
                    return Value.ToString("N2");
            }  
        }
    }
}

public sealed class PopulationResult : NotifyBase
{
    private int event_count;

    public GateDefinition Gate { get; init; } = new();
    public int[] EventIndices { get; set; } = Array.Empty<int>();
    public ObservableCollection<PopulationResult> Children { get; } = new();
    public ObservableCollection<StatisticResult> Statistics { get; } = new();

    public int EventCount
    {
        get => event_count;
        set => SetField(ref event_count, value);
    }
}

public sealed class CompensationMatrix : NotifyBase
{
    private float[,] values = new float[0, 0];

    public IReadOnlyList<string> ChannelNames { get; private set; } = Array.Empty<string>();

    public float[,] Values
    {
        get => values;
        private set => SetField(ref values, value);
    }

    public void ResetIdentity(IReadOnlyList<string> channel_names)
    {
        ChannelNames = channel_names.ToArray();
        Values = new float[channel_names.Count, channel_names.Count];
        for (int index = 0; index < channel_names.Count; index++)
            Values[index, index] = 1.0f;
        OnPropertyChanged(nameof(ChannelNames));
    }
}

public sealed class FlowSample : NotifyBase
{
    private string name = "";
    private float[,] compensated_events = new float[0, 0];

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<ChannelDefinition> Channels { get; } = new();
    public float[,] RawEvents { get; private init; } = new float[0, 0];
    public ObservableCollection<PopulationResult> Populations { get; } = new();
    public Dictionary<string, float[]> Embeddings { get; } = new();

    public float[,] CompensatedEvents
    {
        get => compensated_events;
        private set => SetField(ref compensated_events, value);
    }

    public int EventCount => RawEvents.GetLength(0);
    public int ChannelCount => RawEvents.GetLength(1);

    public string ChannelProfile => string.Join("|", Channels.Select(channel => channel.Name));

    public FlowSample(string name, IEnumerable<ChannelDefinition> channels, float[,] raw_events)
    {
        Name = name;
        RawEvents = raw_events;
        foreach (var channel in channels)
            Channels.Add(channel);
        CompensatedEvents = (float[,])raw_events.Clone();
    }

    public float[] GetChannelValues(string channel_name, int[]? event_indices = null)
    {
        int channel_index = GetChannelIndex(channel_name);
        if (channel_index < 0)
            return Array.Empty<float>();

        if (event_indices is null)
        {
            var values = new float[EventCount];
            for (int row = 0; row < EventCount; row++)
                values[row] = CompensatedEvents[row, channel_index];
            return values;
        }

        var selected = new float[event_indices.Length];
        for (int index = 0; index < event_indices.Length; index++)
            selected[index] = CompensatedEvents[event_indices[index], channel_index];
        return selected;
    }

    public int GetChannelIndex(string channel_name)
    {
        for (int index = 0; index < Channels.Count; index++)
        {
            if (Channels[index].Name == channel_name)
                return index;
        }

        return -1;
    }

    public void ApplyCompensation(CompensationMatrix matrix)
    {
        if (matrix.Values.GetLength(0) != ChannelCount)
        {
            CompensatedEvents = (float[,])RawEvents.Clone();
            return;
        }

        var compensated = new float[EventCount, ChannelCount];
        for (int row = 0; row < EventCount; row++)
        {
            for (int target = 0; target < ChannelCount; target++)
            {
                double value = 0;
                for (int source = 0; source < ChannelCount; source++)
                    value += RawEvents[row, source] * matrix.Values[source, target];
                compensated[row, target] = Convert.ToSingle(value);
            }
        }

        CompensatedEvents = compensated;
    }

    public void Recalculate(FlowGroup group)
    {
        ApplyCompensation(group.Compensation);
        Populations.Clear();
        var all_indices = Enumerable.Range(0, EventCount).ToArray();
        foreach (var gate in group.Gates)
            Populations.Add(build_population(group, gate, all_indices, all_indices.Length));
    }

    private PopulationResult build_population(FlowGroup group, GateDefinition gate, int[] parent_indices, int all_count)
    {
        var event_indices = GateEvaluator.Apply(this, gate, parent_indices);
        var result = new PopulationResult { Gate = gate, EventIndices = event_indices, EventCount = event_indices.Length };
        foreach (var definition in gate.Statistics)
            result.Statistics.Add(StatisticsCalculator.Calculate(this, definition, event_indices, parent_indices.Length, all_count));
        foreach (var child in gate.Children)
            result.Children.Add(build_population(group, child, event_indices, all_count));
        return result;
    }
}

public sealed class FlowGroup : NotifyBase
{
    private string name = "Samples";

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<FlowSample> Samples { get; } = new();
    public ObservableCollection<GateDefinition> Gates { get; } = new();
    public CompensationMatrix Compensation { get; } = new();
    public string ChannelProfile { get; private set; } = "";

    public IReadOnlyList<ChannelDefinition> Channels => Samples.FirstOrDefault() is { } sample
        ? sample.Channels.ToList()
        : Array.Empty<ChannelDefinition>();

    public bool CanAccept(FlowSample sample) => Samples.Count == 0 || ChannelProfile == sample.ChannelProfile;

    public void AddSample(FlowSample sample)
    {
        if (Samples.Count == 0)
        {
            ChannelProfile = sample.ChannelProfile;
            Compensation.ResetIdentity(sample.Channels.Select(channel => channel.Name).ToArray());
        }

        Samples.Add(sample);
        sample.Recalculate(this);
        OnPropertyChanged(nameof(Channels));
    }

    public void RecalculateSamples()
    {
        foreach (var sample in Samples)
            sample.Recalculate(this);
    }
}

public sealed class FlowWorkspace : NotifyBase
{
    private string name = "Untitled Workspace";

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<FlowGroup> Groups { get; } = new();
}

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, string? property_name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(property_name);
        return true;
    }

    protected void OnPropertyChanged(string? property_name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property_name));
}
