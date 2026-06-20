using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Avalonia;
using Python.Runtime;

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
    OffsetQuadrant,
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

public enum MetadataColumnKind
{
    String,
    Integer,
    Float
}

public enum EmbeddingValueKind
{
    Float,
    Integer
}

public enum GatingTool
{
    View,
    Polygon,
    Rectangle,
    Quadrant,
    CurlyQuadrant,
    OffsetQuadrant,
    Threshold,
    Range
}

public enum PopulationRegion
{
    Primary,
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
    More,
    Less,
    InRange,
    BelowRange,
    AboveRange
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
    NumberOfEvents,
    Python
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
    private bool use_log_scale;

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

    public bool UseLogScale
    {
        get => use_log_scale;
        set => SetField(ref use_log_scale, value);
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
    public PlotMode PlotMode { get; set; } = PlotMode.Density;
    public bool ShowOutlierPoints { get; set; } = true;
    public bool DrawLargeDots { get; set; }
    public bool ShowGridlines { get; set; } = true;
    public bool ShowGateAnnotations { get; set; } = true;
    public bool ShowGateAnnotationNames { get; set; }
    public int ContourLevelCount { get; set; } = 10;
    public int DensitySmoothing { get; set; } = 9;

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
    public PlotMode PreferredPlotMode { get; set; } = PlotMode.Density;
    public bool PreferredShowOutlierPoints { get; set; } = true;
    public bool PreferredDrawLargeDots { get; set; }
    public bool PreferredShowGridlines { get; set; } = true;
    public bool PreferredShowGateAnnotations { get; set; } = true;
    public bool PreferredShowGateAnnotationNames { get; set; }
    public int PreferredContourLevelCount { get; set; } = 10;
    public int PreferredDensitySmoothing { get; set; } = 9;
    public Dictionary<PopulationRegion, string> PopulationNames { get; } = new();
    public Dictionary<PopulationRegion, GateViewOptions> PopulationPreferredViews { get; } = new();
    public Dictionary<string, GateViewOptions> SamplePreferredViews { get; } = new(StringComparer.Ordinal);
    public PopulationRegion ParentPopulationRegion { get; set; } = PopulationRegion.Primary;
    public GateDefinition? Parent { get; set; }
    public Guid? BooleanFirstGateId { get; set; }
    public PopulationRegion BooleanFirstRegion { get; set; } = PopulationRegion.Primary;
    public Guid? BooleanSecondGateId { get; set; }
    public PopulationRegion BooleanSecondRegion { get; set; } = PopulationRegion.Primary;

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

    public bool IsOneDimensional => !IsBooleanCombination && (Kind is GateKind.Threshold or GateKind.Range || string.IsNullOrWhiteSpace(YChannel));
    public bool HasLinkedPopulations => Kind is GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant or GateKind.Threshold or GateKind.Range;
    public bool IsBooleanCombination => Kind is GateKind.Merge or GateKind.Exclude or GateKind.Overlap;

    public IReadOnlyList<PopulationRegion> PopulationRegions => Kind switch
    {
        GateKind.Quadrant or GateKind.CurlyQuadrant or GateKind.OffsetQuadrant =>
        [
            PopulationRegion.TopRight,
            PopulationRegion.TopLeft,
            PopulationRegion.BottomRight,
            PopulationRegion.BottomLeft
        ],
        GateKind.Threshold =>
        [
            PopulationRegion.More,
            PopulationRegion.Less
        ],
        GateKind.Range =>
        [
            PopulationRegion.InRange,
            PopulationRegion.BelowRange,
            PopulationRegion.AboveRange
        ],
        _ => [PopulationRegion.Primary]
    };

    public string PopulationName(PopulationRegion region)
    {
        if (region == PopulationRegion.Primary)
            return Name;
        if (PopulationNames.TryGetValue(region, out string? name) && !string.IsNullOrWhiteSpace(name))
            return name;

        return region switch
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
    }
}

public sealed class StatisticDefinition
{
    public StatisticKind Kind { get; set; }
    public string ChannelName { get; set; } = "";
    public string PythonSource { get; set; } = "";
    public string PythonCallableName { get; set; } = "entry";
    public int PythonApiVersion { get; set; } = 1;
    public string PythonDisplayName { get; set; } = "";
    public string PythonParametersJson { get; set; } = "[]";

    public void SetPythonMethod(
        string source,
        string callable_name = "entry",
        string? display_name = null,
        string? parameters_json = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Python statistic source cannot be empty.", nameof(source));
        if (string.IsNullOrWhiteSpace(callable_name))
            throw new ArgumentException("Python callable name cannot be empty.", nameof(callable_name));

        Kind = StatisticKind.Python;
        ChannelName = "";
        PythonSource = source;
        PythonCallableName = callable_name;
        PythonDisplayName = string.IsNullOrWhiteSpace(display_name) ? callable_name : display_name.Trim();
        PythonApiVersion = 1;
        PythonParametersJson = string.IsNullOrWhiteSpace(parameters_json) ? "[]" : parameters_json;
    }
}

public sealed class StatisticResult
{
    public StatisticKind Kind { get; init; }
    public string ChannelName { get; init; } = "";
    public double Value { get; init; }
    public PyObject? PythonValue { get; init; }
    public string PythonFormattedValue { get; init; } = "";
    public string PythonSource { get; init; } = "";
    public string PythonCallableName { get; init; } = "";
    public string PythonParametersJson { get; init; } = "";

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
                case StatisticKind.Python:
                    return string.IsNullOrWhiteSpace(PythonDisplayName) ? "Python statistic" : PythonDisplayName;

                default: return $"{Kind}";
            }  
        }
    }

    public string PythonDisplayName { get; init; } = "";

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
                case StatisticKind.Python:
                    return PythonFormattedValue;

                default:
                    return Value.ToString("N2");
            }  
        }
    }
}

public sealed class PopulationResult : NotifyBase
{
    private int event_count;
    private int[] event_indices = Array.Empty<int>();
    private int[]? plot_event_indices_cache;
    private readonly object normalized_channel_cache_lock = new();
    private readonly Dictionary<NormalizedChannelCacheKey, float[]> normalized_channel_cache = new();

    public GateDefinition Gate { get; init; } = new();
    public PopulationRegion Region { get; init; } = PopulationRegion.Primary;
    public ObservableCollection<PopulationResult> Children { get; } = new();
    public ObservableCollection<StatisticResult> Statistics { get; } = new();

    public int[] EventIndices
    {
        get => event_indices;
        set
        {
            event_indices = value ?? Array.Empty<int>();
            plot_event_indices_cache = null;
            lock (normalized_channel_cache_lock)
                normalized_channel_cache.Clear();
        }
    }

    public string DisplayName => Gate.PopulationName(Region);

    public int EventCount
    {
        get => event_count;
        set => SetField(ref event_count, value);
    }

    public int[] GetPlotEventIndices()
    {
        if (event_indices.Length <= PlotEventSampler.MaximumCandidateCount)
            return event_indices;

        return plot_event_indices_cache ??= PlotEventSampler.Sample(
            event_indices,
            HashCode.Combine(Gate.Id, Region, event_indices.Length));
    }

    public float[] GetNormalizedChannelValues(
        FlowSample sample,
        string channel_name,
        double minimum,
        double maximum,
        AxisScale scale,
        CancellationToken cancellation_token = default)
    {
        var key = NormalizedChannelCacheKey.Create(channel_name, minimum, maximum, scale);
        lock (normalized_channel_cache_lock)
        {
            if (normalized_channel_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var normalized = sample.CreateNormalizedChannelValues(channel_name, event_indices, minimum, maximum, scale, cancellation_token);
        lock (normalized_channel_cache_lock)
        {
            if (normalized_channel_cache.TryGetValue(key, out var cached))
                return cached;
            normalized_channel_cache[key] = normalized;
            return normalized;
        }
    }
}

internal static class PlotEventSampler
{
    public const int MaximumCandidateCount = 100_000;

    public static int[] Sample(int[] source, int seed)
    {
        if (source.Length <= MaximumCandidateCount)
            return source;

        var result = new int[MaximumCandidateCount];
        Array.Copy(source, result, MaximumCandidateCount);

        var random = new Random(seed);
        for (int index = MaximumCandidateCount; index < source.Length; index++)
        {
            int replacement = random.Next(index + 1);
            if (replacement < MaximumCandidateCount)
                result[replacement] = source[index];
        }

        return result;
    }

    public static int[] SampleRange(int count, int seed)
    {
        if (count <= 0)
            return Array.Empty<int>();

        int sample_count = Math.Min(count, MaximumCandidateCount);
        var result = new int[sample_count];
        for (int index = 0; index < sample_count; index++)
            result[index] = index;

        if (count <= MaximumCandidateCount)
            return result;

        var random = new Random(seed);
        for (int index = MaximumCandidateCount; index < count; index++)
        {
            int replacement = random.Next(index + 1);
            if (replacement < MaximumCandidateCount)
                result[replacement] = index;
        }

        return result;
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

public sealed class EmbeddingData
{
    public EmbeddingValueKind Kind { get; set; }
    public float[] Values { get; set; } = Array.Empty<float>();
    public Dictionary<int, string> Categories { get; } = new();

    public bool IsCategorical => Kind == EmbeddingValueKind.Integer;
}

public sealed class FlowSample : NotifyBase
{
    private string name = "";
    private float[,] compensated_events = new float[0, 0];
    private CompensationMatrix? applied_compensation_cache;
    private bool has_applied_compensation_cache;
    private readonly object normalized_channel_cache_lock = new();
    private readonly Dictionary<NormalizedChannelCacheKey, float[]> normalized_channel_cache = new();
    private int[]? plot_event_indices_cache;
    private int plot_event_indices_cache_count = -1;
    private int[]? all_event_indices_cache;
    private int all_event_indices_cache_count = -1;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public ObservableCollection<ChannelDefinition> Channels { get; } = new();
    public float[,] RawEvents { get; private init; } = new float[0, 0];
    public ObservableCollection<PopulationResult> Populations { get; } = new();
    public Dictionary<string, EmbeddingData> Embeddings { get; } = new();
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
            return select_values(embedding_values.Values, event_indices);

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

    public float[] GetNormalizedChannelValues(
        string channel_name,
        double minimum,
        double maximum,
        AxisScale scale,
        CancellationToken cancellation_token = default)
    {
        var key = NormalizedChannelCacheKey.Create(channel_name, minimum, maximum, scale);
        lock (normalized_channel_cache_lock)
        {
            if (normalized_channel_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var normalized = CreateNormalizedChannelValues(channel_name, null, minimum, maximum, scale, cancellation_token);
        lock (normalized_channel_cache_lock)
        {
            if (normalized_channel_cache.TryGetValue(key, out var cached))
                return cached;
            normalized_channel_cache[key] = normalized;
            return normalized;
        }
    }

    public void PrepareNormalizedChannelValues(
        string channel_name,
        int[]? event_indices,
        double minimum,
        double maximum,
        AxisScale scale,
        CancellationToken cancellation_token = default)
    {
        if (event_indices is null)
        {
            _ = GetNormalizedChannelValues(channel_name, minimum, maximum, scale, cancellation_token);
            return;
        }

        _ = CreateNormalizedChannelValues(channel_name, event_indices, minimum, maximum, scale, cancellation_token);
    }

    internal float[] CreateNormalizedChannelValues(
        string channel_name,
        int[]? event_indices,
        double minimum,
        double maximum,
        AxisScale scale,
        CancellationToken cancellation_token = default)
    {
        var source = GetChannelValues(channel_name, event_indices);
        if (source.Length == 0)
            return Array.Empty<float>();

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
            return new float[source.Length];

        var normalized = new float[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();
            double transformed = transform is null ? source[index] : transform.Transform(source[index]);
            normalized[index] = Convert.ToSingle((transformed - transformed_minimum) / transformed_span);
        }

        return normalized;
    }

    public void InvalidateNormalizedChannelCache()
    {
        lock (normalized_channel_cache_lock)
            normalized_channel_cache.Clear();
    }

    public int[] GetPlotEventIndices()
    {
        if (plot_event_indices_cache is not null && plot_event_indices_cache_count == EventCount)
            return plot_event_indices_cache;

        plot_event_indices_cache_count = EventCount;
        plot_event_indices_cache = PlotEventSampler.SampleRange(EventCount, HashCode.Combine(Id, EventCount));
        return plot_event_indices_cache;
    }

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
        InvalidateNormalizedChannelCache();

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

    public void Recalculate(FlowGroup group, bool force_compensation = false, CancellationToken cancellation_token = default)
    {
        cancellation_token.ThrowIfCancellationRequested();
        ApplyCompensation(group.AppliedCompensation, force_compensation);
        Populations.Clear();
        var all_indices = GetAllEventIndices();
        foreach (var gate in group.Gates)
        foreach (var population in build_population_results(group, gate, all_indices, all_indices.Length, parent_population: null, cancellation_token))
            Populations.Add(population);
        RefreshPopulationEmbedding();
        OnPropertyChanged(nameof(Populations));
    }

    public bool RecalculateGateSubtree(FlowGroup group, GateDefinition gate, CancellationToken cancellation_token = default)
    {
        cancellation_token.ThrowIfCancellationRequested();
        ApplyCompensation(group.AppliedCompensation);

        int[] parent_indices;
        IList<PopulationResult> siblings;
        PopulationResult? parent_population;
        if (gate.Parent is null)
        {
            parent_indices = GetAllEventIndices();
            siblings = Populations;
            parent_population = null;
        }
        else
        {
            parent_population = find_population(Populations, gate.Parent, gate.ParentPopulationRegion);
            if (parent_population is null)
                return false;

            parent_indices = parent_population.EventIndices;
            siblings = parent_population.Children;
        }

        var replacements = build_population_results(group, gate, parent_indices, EventCount, parent_population, cancellation_token).ToArray();
        replace_population_results(siblings, gate, replacements);
        RefreshPopulationEmbedding();
        OnPropertyChanged(nameof(Populations));
        return true;
    }

    private PopulationResult build_population(FlowGroup group, GateDefinition gate, int[] parent_indices, int all_count, PopulationResult? parent_population)
    {
        return build_population(group, gate, PopulationRegion.Primary, parent_indices, all_count, parent_population, CancellationToken.None);
    }

    private PopulationResult build_population(
        FlowGroup group,
        GateDefinition gate,
        PopulationRegion region,
        int[] parent_indices,
        int all_count,
        PopulationResult? parent_population,
        CancellationToken cancellation_token)
    {
        cancellation_token.ThrowIfCancellationRequested();
        var event_indices = GateEvaluator.Apply(this, group, gate, region, parent_indices, parent_population, cancellation_token);
        var result = new PopulationResult { Gate = gate, Region = region, EventIndices = event_indices, EventCount = event_indices.Length };
        cancellation_token.ThrowIfCancellationRequested();
        foreach (var definition in gate.Statistics)
        {
            cancellation_token.ThrowIfCancellationRequested();
            result.Statistics.Add(StatisticsCalculator.Calculate(this, definition, event_indices, parent_indices.Length, all_count));
        }
        foreach (var child in gate.Children)
        {
            cancellation_token.ThrowIfCancellationRequested();
            if (child.ParentPopulationRegion != region)
                continue;

            foreach (var child_result in build_population_results(group, child, event_indices, all_count, result, cancellation_token))
                result.Children.Add(child_result);
        }
        return result;
    }

    public void RefreshPopulationEmbedding()
    {
        var values = Enumerable.Repeat(float.NaN, EventCount).ToArray();
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var population in Populations)
            assign_population_embedding(population, values, categories);

        var embedding = new EmbeddingData { Kind = EmbeddingValueKind.Integer, Values = values };
        foreach (var item in categories.OrderBy(item => item.Value))
            embedding.Categories[item.Value] = item.Key;
        Embeddings["Populations"] = embedding;
        InvalidateNormalizedChannelCache();
    }

    private static void assign_population_embedding(PopulationResult population, float[] values, Dictionary<string, int> categories)
    {
        if (!categories.TryGetValue(population.DisplayName, out int category))
        {
            category = categories.Count + 1;
            categories[population.DisplayName] = category;
        }

        foreach (int index in population.EventIndices)
            if (index >= 0 && index < values.Length)
                values[index] = category;

        foreach (var child in population.Children)
            assign_population_embedding(child, values, categories);
    }

    private IEnumerable<PopulationResult> build_population_results(
        FlowGroup group,
        GateDefinition gate,
        int[] parent_indices,
        int all_count,
        PopulationResult? parent_population,
        CancellationToken cancellation_token)
    {
        foreach (var region in gate.PopulationRegions)
            yield return build_population(group, gate, region, parent_indices, all_count, parent_population, cancellation_token);
    }

    private int[] GetAllEventIndices()
    {
        if (all_event_indices_cache is not null && all_event_indices_cache_count == EventCount)
            return all_event_indices_cache;

        all_event_indices_cache_count = EventCount;
        all_event_indices_cache = Enumerable.Range(0, EventCount).ToArray();
        return all_event_indices_cache;
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, GateDefinition gate, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate && population.Region == region)
                return population;

            var child = find_population(population.Children, gate, region);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static void replace_population_results(
        IList<PopulationResult> siblings,
        GateDefinition gate,
        IReadOnlyList<PopulationResult> replacements)
    {
        int insert_index = siblings.Count;
        for (int index = siblings.Count - 1; index >= 0; index--)
        {
            if (siblings[index].Gate != gate)
                continue;

            insert_index = index;
            siblings.RemoveAt(index);
        }

        for (int index = 0; index < replacements.Count; index++)
            siblings.Insert(insert_index + index, replacements[index]);
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
    public GateViewOptions RootViewOptions { get; set; } = new();
    public Dictionary<string, GateViewOptions> SampleRootViewOptions { get; } = new(StringComparer.Ordinal);
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

    public void AddSample(FlowSample sample, bool recalculate = true)
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
        if (recalculate)
        {
            if (ReferenceEquals(previous_applied_compensation, AppliedCompensation))
                sample.Recalculate(this);
            else
                RecalculateSamples();
        }
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

    public void SetAppliedCompensation(CompensationMatrix compensation, bool manual, bool recalculate = true)
    {
        if (!CompensationCandidates.Contains(compensation))
            RegisterCompensation(compensation, make_applied_if_first: false);

        AppliedCompensation = compensation;
        applied_compensation_is_manual |= manual;
        if (recalculate)
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

    public void RecalculateSamples(bool force_compensation, CancellationToken cancellation_token = default)
    {
        foreach (var sample in Samples)
        {
            cancellation_token.ThrowIfCancellationRequested();
            sample.Recalculate(this, force_compensation, cancellation_token);
        }
    }

    public void ApplyCompensationToSamples(bool force_compensation, CancellationToken cancellation_token = default)
    {
        foreach (var sample in Samples)
        {
            cancellation_token.ThrowIfCancellationRequested();
            sample.ApplyCompensation(AppliedCompensation, force_compensation);
        }
    }

    public bool RecalculateGateSubtree(GateDefinition gate, CancellationToken cancellation_token = default)
    {
        bool recalculated_all = true;
        foreach (var sample in Samples)
        {
            cancellation_token.ThrowIfCancellationRequested();
            recalculated_all &= sample.RecalculateGateSubtree(this, gate, cancellation_token);
        }

        return recalculated_all;
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
    double LogicleNegativeDecades)
{
    public static NormalizedChannelCacheKey Create(string channel_name, double minimum, double maximum, AxisScale scale) =>
        new(
            channel_name,
            minimum,
            maximum,
            scale.Kind,
            scale.Logicle.T,
            scale.Logicle.W,
            scale.Logicle.M,
            scale.Logicle.A);
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
    public ObservableCollection<PageLayout> PageLayouts { get; } = new();
    public ObservableCollection<Platform> IntegrationJobs { get; } = new();
    public ObservableCollection<string> RecentFilePaths { get; } = new();
    public Dictionary<string, MetadataColumnKind> MetadataColumns { get; } = new(StringComparer.Ordinal);
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
