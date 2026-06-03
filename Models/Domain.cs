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
    Dotplot,
    Contour,
    Zebra,
    Histogram
}

public enum PlotColorPalette
{
    Viridis,
    Plasma,
    Turbo,
    Gray
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

public enum PopulationRegion
{
    Primary,
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft
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

public sealed class ChannelDefinition : NotifyBase
{
    private string label;

    public ChannelDefinition(int index, string name, string label, float maximum, float gain)
    {
        Index = index;
        Name = name;
        this.label = label;
        Maximum = maximum;
        Gain = gain;
    }

    public int Index { get; }
    public string Name { get; }

    public string Label
    {
        get => label;
        set => SetField(ref label, value ?? "");
    }

    public float Maximum { get; }
    public float Gain { get; }
}

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

public sealed class DotColorSettings : NotifyBase
{
    private string channel_name = "";
    private PlotColorPalette palette = PlotColorPalette.Viridis;

    public string ChannelName
    {
        get => channel_name;
        set => SetField(ref channel_name, value ?? "");
    }

    public PlotColorPalette Palette
    {
        get => palette;
        set => SetField(ref palette, value);
    }

    public bool HasChannel => !string.IsNullOrWhiteSpace(channel_name);
}

public sealed class GateViewOptions
{
    public string XChannel { get; set; } = "";
    public string? YChannel { get; set; }
    public double XMinimum { get; set; }
    public double XMaximum { get; set; } = 262144.0;
    public AxisScale XScale { get; set; } = new();
    public double YMinimum { get; set; }
    public double YMaximum { get; set; } = 262144.0;
    public AxisScale YScale { get; set; } = new();

    public bool HasView => !string.IsNullOrWhiteSpace(XChannel);
}

public sealed class GateDefinition : NotifyBase
{
    private string name = "Gate";
    private GateKind kind;
    private string x_channel = "";
    private string? y_channel;
    private bool is_selected;
    private bool is_tree_expanded = true;

    public Guid Id { get; init; } = Guid.NewGuid();
    public ObservableCollection<Point> Vertices { get; } = new();
    public ObservableCollection<GateDefinition> Children { get; } = new();
    public ObservableCollection<StatisticDefinition> Statistics { get; } = new();
    public double XMinimum { get; set; }
    public double XMaximum { get; set; } = 262144.0;
    public double YMinimum { get; set; }
    public double YMaximum { get; set; } = 262144.0;
    public AxisScale XScale { get; set; } = new();
    public AxisScale YScale { get; set; } = new();
    public string PreferredXChannel { get; set; } = "";
    public string? PreferredYChannel { get; set; }
    public double PreferredXMinimum { get; set; }
    public double PreferredXMaximum { get; set; } = 262144.0;
    public double PreferredYMinimum { get; set; }
    public double PreferredYMaximum { get; set; } = 262144.0;
    public AxisScale PreferredXScale { get; set; } = new();
    public AxisScale PreferredYScale { get; set; } = new();
    public Dictionary<string, GateViewOptions> SamplePreferredViews { get; } = new(StringComparer.Ordinal);
    public PopulationRegion ParentPopulationRegion { get; set; } = PopulationRegion.Primary;
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

    public bool IsTreeExpanded
    {
        get => is_tree_expanded;
        set => SetField(ref is_tree_expanded, value);
    }

    public bool IsOneDimensional => Kind is GateKind.Threshold or GateKind.Range || string.IsNullOrWhiteSpace(YChannel);
    public bool HasLinkedPopulations => Kind is GateKind.Quadrant or GateKind.CurlyQuadrant;
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
    public PopulationRegion Region { get; init; } = PopulationRegion.Primary;
    public int[] EventIndices { get; set; } = Array.Empty<int>();
    public ObservableCollection<PopulationResult> Children { get; } = new();
    public ObservableCollection<StatisticResult> Statistics { get; } = new();

    public string DisplayName => Region switch
    {
        PopulationRegion.TopRight => $"{Gate.Name}: Top right",
        PopulationRegion.TopLeft => $"{Gate.Name}: Top left",
        PopulationRegion.BottomRight => $"{Gate.Name}: Bottom right",
        PopulationRegion.BottomLeft => $"{Gate.Name}: Bottom left",
        _ => Gate.Name
    };

    public int EventCount
    {
        get => event_count;
        set => SetField(ref event_count, value);
    }
}

public sealed class CompensationMatrix : NotifyBase
{
    private string name = "Compensation";
    private float[,] values = new float[0, 0];

    public Guid Id { get; init; } = Guid.NewGuid();
    public IReadOnlyList<string> ChannelNames { get; private set; } = Array.Empty<string>();

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public float[,] Values
    {
        get => values;
        private set => SetField(ref values, value);
    }

    public static CompensationMatrix Create(string name, IReadOnlyList<string> channel_names, float[,] values)
    {
        var matrix = new CompensationMatrix
        {
            Name = name,
            ChannelNames = channel_names.ToArray(),
            Values = (float[,])values.Clone()
        };
        matrix.OnPropertyChanged(nameof(ChannelNames));
        return matrix;
    }

    public void ResetIdentity(IReadOnlyList<string> channel_names)
    {
        ChannelNames = channel_names.ToArray();
        Values = new float[channel_names.Count, channel_names.Count];
        for (int index = 0; index < channel_names.Count; index++)
            Values[index, index] = 1.0f;
        OnPropertyChanged(nameof(ChannelNames));
    }

    public void ReplaceValues(float[,] new_values)
    {
        if (new_values.GetLength(0) != ChannelNames.Count || new_values.GetLength(1) != ChannelNames.Count)
            throw new ArgumentException("Compensation dimensions must match the channel list.", nameof(new_values));

        Values = (float[,])new_values.Clone();
    }

    public bool IsEquivalentTo(CompensationMatrix other)
    {
        if (!ChannelNames.SequenceEqual(other.ChannelNames, StringComparer.Ordinal))
            return false;
        if (Values.GetLength(0) != other.Values.GetLength(0) || Values.GetLength(1) != other.Values.GetLength(1))
            return false;

        for (int row = 0; row < Values.GetLength(0); row++)
        for (int column = 0; column < Values.GetLength(1); column++)
        {
            if (Math.Abs(Values[row, column] - other.Values[row, column]) > 0.000001f)
                return false;
        }

        return true;
    }
}

public sealed class FlowSample : NotifyBase
{
    private string name = "";
    private float[,] compensated_events = new float[0, 0];
    private CompensationMatrix? applied_compensation_cache;
    private bool has_applied_compensation_cache;
    private readonly Dictionary<NormalizedChannelCacheKey, float[]> normalized_channel_cache = new();

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<ChannelDefinition> Channels { get; } = new();
    public float[,] RawEvents { get; private init; } = new float[0, 0];
    public ObservableCollection<PopulationResult> Populations { get; } = new();
    public Dictionary<string, float[]> Embeddings { get; } = new();
    public Dictionary<string, string> Metadata { get; } = new();
    public CompensationMatrix? DefaultCompensation { get; set; }

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
        if (Embeddings.TryGetValue(channel_name, out var embedding_values))
            return select_values(embedding_values, event_indices);

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

    public float[] GetNormalizedChannelValues(string channel_name, double minimum, double maximum, AxisScale scale)
    {
        var key = new NormalizedChannelCacheKey(
            channel_name,
            minimum,
            maximum,
            scale.Kind,
            scale.Logicle.T,
            scale.Logicle.W,
            scale.Logicle.M,
            scale.Logicle.A);
        if (normalized_channel_cache.TryGetValue(key, out var cached))
            return cached;

        var source = GetChannelValues(channel_name);
        if (source.Length == 0)
        {
            normalized_channel_cache[key] = Array.Empty<float>();
            return Array.Empty<float>();
        }

        double transformed_minimum;
        double transformed_maximum;
        LogicleTransform? transform = null;
        if (scale.Kind == CoordinateScaleKind.Linear)
        {
            transformed_minimum = minimum;
            transformed_maximum = maximum;
        }
        else
        {
            transform = new LogicleTransform(scale.Logicle);
            transformed_minimum = transform.Transform(minimum);
            transformed_maximum = transform.Transform(maximum);
        }

        double transformed_span = transformed_maximum - transformed_minimum;
        if (transformed_span <= 0)
        {
            var empty_span_values = new float[source.Length];
            normalized_channel_cache[key] = empty_span_values;
            return empty_span_values;
        }

        var normalized = new float[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            double transformed = transform is null ? source[index] : transform.Transform(source[index]);
            normalized[index] = Convert.ToSingle((transformed - transformed_minimum) / transformed_span);
        }

        normalized_channel_cache[key] = normalized;
        return normalized;
    }

    public void InvalidateNormalizedChannelCache() => normalized_channel_cache.Clear();

    private static float[] select_values(float[] values, int[]? event_indices)
    {
        if (event_indices is null)
            return values.ToArray();

        var selected = new float[event_indices.Length];
        for (int index = 0; index < event_indices.Length; index++)
            selected[index] = values[event_indices[index]];
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

    public void ApplyCompensation(CompensationMatrix? matrix)
    {
        ApplyCompensation(matrix, force: false);
    }

    public void ApplyCompensation(CompensationMatrix? matrix, bool force)
    {
        if (!force && has_applied_compensation_cache && ReferenceEquals(applied_compensation_cache, matrix))
            return;

        applied_compensation_cache = matrix;
        has_applied_compensation_cache = true;
        normalized_channel_cache.Clear();

        if (matrix is null || matrix.Values.GetLength(0) == 0)
        {
            CompensatedEvents = (float[,])RawEvents.Clone();
            return;
        }

        var mapped_indices = matrix.ChannelNames.Select(GetChannelIndex).ToArray();
        if (mapped_indices.Any(index => index < 0) || matrix.Values.GetLength(0) != mapped_indices.Length || matrix.Values.GetLength(1) != mapped_indices.Length)
        {
            CompensatedEvents = (float[,])RawEvents.Clone();
            return;
        }

        if (!try_invert(matrix.Values, out var inverse))
        {
            CompensatedEvents = (float[,])RawEvents.Clone();
            return;
        }

        var compensated = new float[EventCount, ChannelCount];
        for (int row = 0; row < EventCount; row++)
        {
            for (int column = 0; column < ChannelCount; column++)
                compensated[row, column] = RawEvents[row, column];

            for (int target = 0; target < mapped_indices.Length; target++)
            {
                double value = 0;
                for (int source = 0; source < mapped_indices.Length; source++)
                    value += RawEvents[row, mapped_indices[source]] * inverse[source, target];
                compensated[row, mapped_indices[target]] = Convert.ToSingle(value);
            }
        }

        CompensatedEvents = compensated;
    }

    private static bool try_invert(float[,] matrix, out double[,] inverse)
    {
        int size = matrix.GetLength(0);
        inverse = new double[size, size];
        if (matrix.GetLength(1) != size)
            return false;

        var augmented = new double[size, size * 2];
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
                augmented[row, column] = matrix[row, column];
            augmented[row, size + row] = 1.0;
        }

        for (int pivot = 0; pivot < size; pivot++)
        {
            int best_row = pivot;
            double best_value = Math.Abs(augmented[pivot, pivot]);
            for (int row = pivot + 1; row < size; row++)
            {
                double value = Math.Abs(augmented[row, pivot]);
                if (value <= best_value)
                    continue;
                best_value = value;
                best_row = row;
            }

            if (best_value < 1e-12)
                return false;

            if (best_row != pivot)
            {
                for (int column = 0; column < size * 2; column++)
                    (augmented[pivot, column], augmented[best_row, column]) = (augmented[best_row, column], augmented[pivot, column]);
            }

            double divisor = augmented[pivot, pivot];
            for (int column = 0; column < size * 2; column++)
                augmented[pivot, column] /= divisor;

            for (int row = 0; row < size; row++)
            {
                if (row == pivot)
                    continue;
                double factor = augmented[row, pivot];
                for (int column = 0; column < size * 2; column++)
                    augmented[row, column] -= factor * augmented[pivot, column];
            }
        }

        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
            inverse[row, column] = augmented[row, size + column];

        return true;
    }

    public void Recalculate(FlowGroup group, bool force_compensation = false)
    {
        ApplyCompensation(group.AppliedCompensation, force_compensation);
        Populations.Clear();
        var all_indices = Enumerable.Range(0, EventCount).ToArray();
        foreach (var gate in group.Gates)
        foreach (var population in build_population_results(group, gate, all_indices, all_indices.Length))
            Populations.Add(population);
    }

    private PopulationResult build_population(FlowGroup group, GateDefinition gate, int[] parent_indices, int all_count)
    {
        return build_population(group, gate, PopulationRegion.Primary, parent_indices, all_count);
    }

    private PopulationResult build_population(FlowGroup group, GateDefinition gate, PopulationRegion region, int[] parent_indices, int all_count)
    {
        var event_indices = GateEvaluator.Apply(this, gate, region, parent_indices);
        var result = new PopulationResult { Gate = gate, Region = region, EventIndices = event_indices, EventCount = event_indices.Length };
        foreach (var definition in gate.Statistics)
            result.Statistics.Add(StatisticsCalculator.Calculate(this, definition, event_indices, parent_indices.Length, all_count));
        foreach (var child in gate.Children)
        {
            if (child.ParentPopulationRegion != region)
                continue;

            foreach (var child_result in build_population_results(group, child, event_indices, all_count))
                result.Children.Add(child_result);
        }
        return result;
    }

    private IEnumerable<PopulationResult> build_population_results(FlowGroup group, GateDefinition gate, int[] parent_indices, int all_count)
    {
        if (!gate.HasLinkedPopulations)
        {
            yield return build_population(group, gate, PopulationRegion.Primary, parent_indices, all_count);
            yield break;
        }

        yield return build_population(group, gate, PopulationRegion.TopRight, parent_indices, all_count);
        yield return build_population(group, gate, PopulationRegion.TopLeft, parent_indices, all_count);
        yield return build_population(group, gate, PopulationRegion.BottomRight, parent_indices, all_count);
        yield return build_population(group, gate, PopulationRegion.BottomLeft, parent_indices, all_count);
    }
}

public sealed class FlowGroup : NotifyBase
{
    private string name = "Samples";
    private CompensationMatrix? applied_compensation;
    private bool applied_compensation_is_manual;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<FlowSample> Samples { get; } = new();
    public ObservableCollection<GateDefinition> Gates { get; } = new();
    public ObservableCollection<StatisticDefinition> Statistics { get; } = new();
    public ObservableCollection<CompensationMatrix> CompensationCandidates { get; } = new();
    public string ChannelProfile { get; private set; } = "";

    public CompensationMatrix? AppliedCompensation
    {
        get => applied_compensation;
        private set => SetField(ref applied_compensation, value);
    }

    public IReadOnlyList<ChannelDefinition> Channels => Samples.FirstOrDefault() is { } sample
        ? sample.Channels.ToList()
        : Array.Empty<ChannelDefinition>();

    public bool CanAccept(FlowSample sample) => Samples.Count == 0 || ChannelProfile == sample.ChannelProfile;

    public void AddSample(FlowSample sample)
    {
        if (Samples.Count == 0)
        {
            ChannelProfile = sample.ChannelProfile;
        }

        var previous_applied_compensation = AppliedCompensation;
        if (sample.DefaultCompensation is not null)
        {
            var registered = RegisterCompensation(sample.DefaultCompensation, make_applied_if_first: true);
            if (!applied_compensation_is_manual && AppliedCompensation?.Name == "Identity" && !ReferenceEquals(AppliedCompensation, registered))
                AppliedCompensation = registered;
        }
        else if (CompensationCandidates.Count == 0)
        {
            var identity = new CompensationMatrix { Name = "Identity" };
            identity.ResetIdentity(sample.Channels.Select(channel => channel.Name).ToArray());
            RegisterCompensation(identity, make_applied_if_first: true);
        }

        Samples.Add(sample);
        if (ReferenceEquals(previous_applied_compensation, AppliedCompensation))
            sample.Recalculate(this);
        else
            RecalculateSamples();
        OnPropertyChanged(nameof(Channels));
    }

    public CompensationMatrix RegisterCompensation(CompensationMatrix compensation, bool make_applied_if_first)
    {
        var existing = CompensationCandidates.FirstOrDefault(item => item.IsEquivalentTo(compensation));
        if (existing is not null)
        {
            if (AppliedCompensation is null && make_applied_if_first && !applied_compensation_is_manual)
                AppliedCompensation = existing;
            return existing;
        }

        compensation.Name = unique_compensation_name(compensation.Name);
        CompensationCandidates.Add(compensation);
        if (AppliedCompensation is null && make_applied_if_first && !applied_compensation_is_manual)
            AppliedCompensation = compensation;
        return compensation;
    }

    public void SetAppliedCompensation(CompensationMatrix compensation, bool manual)
    {
        if (!CompensationCandidates.Contains(compensation))
            RegisterCompensation(compensation, make_applied_if_first: false);

        AppliedCompensation = compensation;
        applied_compensation_is_manual |= manual;
        RecalculateSamples();
    }

    public bool IsAppliedCompensation(CompensationMatrix compensation) =>
        ReferenceEquals(AppliedCompensation, compensation);

    private string unique_compensation_name(string preferred_name)
    {
        string base_name = string.IsNullOrWhiteSpace(preferred_name) ? "Compensation" : preferred_name.Trim();
        if (CompensationCandidates.All(item => item.Name != base_name))
            return base_name;

        int index = 2;
        while (CompensationCandidates.Any(item => item.Name == $"{base_name} {index}"))
            index++;

        return $"{base_name} {index}";
    }

    public void RecalculateSamples()
    {
        RecalculateSamples(force_compensation: false);
    }

    public void RecalculateSamples(bool force_compensation)
    {
        foreach (var sample in Samples)
            sample.Recalculate(this, force_compensation);
    }
}

internal readonly record struct NormalizedChannelCacheKey(
    string ChannelName,
    double Minimum,
    double Maximum,
    CoordinateScaleKind ScaleKind,
    double LogicleTopOfScale,
    double LogicleLinearizationWidth,
    double LogicleDecades,
    double LogicleNegativeDecades);

public sealed class FlowWorkspace : NotifyBase
{
    private string name = "Untitled Workspace";

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<FlowGroup> Groups { get; } = new();
    public ObservableCollection<PageLayout> PageLayouts { get; } = new();
    public ObservableCollection<IntegrationJob> IntegrationJobs { get; } = new();
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
