using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using gated.Reduction;

namespace gated.Models;

public enum PlatformStatus
{
    Draft,
    Ready,
    Running,
    Cancelled,
    Complete,
    Warning,
    Failed
}

public enum PlatformKind
{
    Integration,
    CellCycle,
    Proliferation,
    IntensityComparison
}

public enum PlatformTransformationKind
{
    Linear,
    Logarithm,
    Logicle,
    Arcsinh
}

public enum CellCycleModelKind
{
    WatsonPragmatic,
    DeanJettFox
}

public enum PlatformFitCurveKind
{
    Gaussian,
    GaussianSum,
    CellCycleSum,
    Linear,
    Exponential,
    Gamma,
    Addition
}

public static class PlatformParameterKeys
{
    public const string BatchColumn = "batch_column";
    public const string Model = "model";
    public const string DrawModelSum = "draw_model_sum";
    public const string DrawComponents = "draw_components";
    public const string FillComponents = "fill_components";
    public const string MaxGenerations = "max_generations";
    public const string PeakProminence = "peak_prominence";
    public const string ReferenceSample = "reference_sample";
}

public sealed class PlatformRunState : NotifyBase
{
    private PlatformStatus status = PlatformStatus.Draft;
    private string warning_text = "";
    private int current_step;
    private bool is_running;
    private double progress_fraction;
    private string progress_text = "";
    private bool cancellation_requested;

    public PlatformStatus Status
    {
        get => status;
        set
        {
            if (!SetField(ref status, value))
                return;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string WarningText
    {
        get => warning_text;
        set
        {
            if (!SetField(ref warning_text, value ?? ""))
                return;
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public bool IsRunning
    {
        get => is_running;
        set
        {
            if (!SetField(ref is_running, value))
                return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsRunning;

    public double ProgressFraction
    {
        get => progress_fraction;
        set => SetField(ref progress_fraction, Math.Clamp(value, 0, 1));
    }

    public string ProgressText
    {
        get => progress_text;
        set => SetField(ref progress_text, value ?? "");
    }

    public bool CancellationRequested
    {
        get => cancellation_requested;
        set => SetField(ref cancellation_requested, value);
    }

    public int CurrentStep
    {
        get => current_step;
        set => SetField(ref current_step, Math.Clamp(value, 0, 7));
    }

    public string StatusText => HasWarning ? WarningText : Status.ToString();
}

public sealed class PlatformAxisOptions : NotifyBase
{
    private PlatformTransformationKind transform = PlatformTransformationKind.Linear;
    private double minimum = -0.1 * new LogicleParameters().T;
    private double maximum = new LogicleParameters().T;
    private LogicleParameters logicle = new();

    public PlatformTransformationKind Transform
    {
        get => transform;
        set => SetField(ref transform, value);
    }

    public double Minimum
    {
        get => minimum;
        set => SetField(ref minimum, double.IsFinite(value) ? value : -0.1 * Maximum);
    }

    public double Maximum
    {
        get => maximum;
        set => SetField(ref maximum, double.IsFinite(value) && value > 0 ? value : new LogicleParameters().T);
    }

    public LogicleParameters Logicle
    {
        get => logicle;
        set => SetField(ref logicle, value);
    }
}

public sealed class PlatformSmoothingOptions : NotifyBase
{
    private int half_window = 4;
    private bool enabled = true;

    public int HalfWindow
    {
        get => half_window;
        set => SetField(ref half_window, Math.Clamp(value, 0, 50));
    }

    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }
}

public sealed class PlatformDataSet
{
    public float[,]? Matrix { get; set; }
    public float[,]? Compensated { get; set; }
    public float[,]? Transformed { get; set; }

    public void Clear()
    {
        Matrix = null;
        Compensated = null;
        Transformed = null;
    }
}

public abstract class Platform : NotifyBase
{
    private string name = "Platform";
    private readonly Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlatformChannelTransformation> transformations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlatformPlotSeries> series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlatformFitCurve> models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PlatformFitCurve>> components = new(StringComparer.Ordinal);

    public Guid Id { get; set; } = Guid.NewGuid();
    public abstract PlatformKind Kind { get; }
    public PlatformRunState RunState { get; } = new();
    public PlatformAxisOptions Axis { get; } = new();
    public PlatformDataSet Data { get; } = new();
    public IDictionary<string, object?> Parameters => parameters;
    public IDictionary<string, PlatformChannelTransformation> Transformations => transformations;
    public IDictionary<string, PlatformPlotSeries> Series => series;
    public IDictionary<string, PlatformFitCurve> Models => models;
    public IDictionary<string, List<PlatformFitCurve>> Components => components;
    public virtual bool HasGraphics => Kind != PlatformKind.Integration;
    public virtual bool HasDataTable => ResultTables.Count > 0 || PlatformStatistics.Count > 0;

    public string Name
    {
        get => name;
        set => SetField(ref name, string.IsNullOrWhiteSpace(value) ? "Platform" : value.Trim(), nameof(Name));
    }

    public PlatformStatus Status
    {
        get => RunState.Status;
        set
        {
            if (RunState.Status == value)
                return;
            RunState.Status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string WarningText
    {
        get => RunState.WarningText;
        set
        {
            if (RunState.WarningText == (value ?? ""))
                return;
            RunState.WarningText = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool HasWarning => RunState.HasWarning;

    public bool IsRunning
    {
        get => RunState.IsRunning;
        set
        {
            if (RunState.IsRunning == value)
                return;
            RunState.IsRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => RunState.IsIdle;

    public double ProgressFraction
    {
        get => RunState.ProgressFraction;
        set
        {
            RunState.ProgressFraction = value;
            OnPropertyChanged();
        }
    }

    public string ProgressText
    {
        get => RunState.ProgressText;
        set
        {
            RunState.ProgressText = value ?? "";
            OnPropertyChanged();
        }
    }

    public bool CancellationRequested
    {
        get => RunState.CancellationRequested;
        set
        {
            RunState.CancellationRequested = value;
            OnPropertyChanged();
        }
    }

    public int CurrentStep
    {
        get => RunState.CurrentStep;
        set
        {
            RunState.CurrentStep = value;
            OnPropertyChanged();
        }
    }

    public string StatusText => RunState.StatusText;

    public ObservableCollection<PlatformPopulationInput> Populations { get; } = new();
    public ObservableCollection<PlatformFeatureSelection> Features { get; } = new();
    public ObservableCollection<PlatformResultTable> ResultTables { get; } = new();
    public ObservableCollection<PlatformPlotSeries> PlotSeries { get; } = new();
    public ObservableCollection<PlatformFitCurve> FitCurves { get; } = new();
    public ObservableCollection<PlatformStatisticResult> PlatformStatistics { get; } = new();

    public string ResourcePath => Kind switch
    {
        PlatformKind.CellCycle => "avares://gated/Python/cell-cycle.py",
        PlatformKind.Proliferation => "avares://gated/Python/proliferation.py",
        PlatformKind.IntensityComparison => "avares://gated/Python/intensity-comparison.py",
        _ => ""
    };

    public PlatformRowMap RowMap { get; } = new();
    public float[,]? Matrix
    {
        get => Data.Matrix;
        set => Data.Matrix = value;
    }
    public float[,]? Compensated
    {
        get => Data.Compensated;
        set => Data.Compensated = value;
    }
    public float[,]? Transformed
    {
        get => Data.Transformed;
        set => Data.Transformed = value;
    }

    public virtual bool HasIntegrated => false;
    public bool HasResults => HasIntegrated || ResultTables.Count > 0 || PlotSeries.Count > 0 || FitCurves.Count > 0 || PlatformStatistics.Count > 0;
    public virtual bool IsConfigurationLocked => false;

    public void NotifyIntegrationDataChanged()
    {
        OnPropertyChanged(nameof(HasIntegrated));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsConfigurationLocked));
    }

    public void InvalidateFromConfiguration()
    {
        RowMap.Clear();
        Data.Clear();
        OnConfigurationInvalidated();
        NotifyIntegrationDataChanged();
        ClearFitResults();
        CurrentStep = Math.Min(CurrentStep, 3);
        WarningText = "Configuration changed. Rerun this platform before downstream steps.";
        Status = PlatformStatus.Warning;
    }

    protected virtual void OnConfigurationInvalidated()
    {
    }

    public void ClearFitResults()
    {
        ResultTables.Clear();
        PlotSeries.Clear();
        FitCurves.Clear();
        PlatformStatistics.Clear();
        series.Clear();
        models.Clear();
        components.Clear();
    }

    public void InvalidateFitResults(string warning_text)
    {
        ClearFitResults();
        CurrentStep = Math.Min(CurrentStep, 3);
        WarningText = warning_text;
        Status = PlatformStatus.Warning;
    }

    public string[] SelectedFeatureNames => Features
        .Where(feature => feature.IsSelected && !feature.IsIndeterminate && feature.IsChannel && !string.IsNullOrWhiteSpace(feature.ChannelName))
        .Select(feature => feature.ChannelName)
        .ToArray();

    public void SetParameter(string key, object? value, string? property_name = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (parameters.TryGetValue(key, out object? current) && parameter_equals(current, value))
            return;
        parameters[key] = value;
        OnPropertyChanged(nameof(Parameters));
        if (!string.IsNullOrWhiteSpace(property_name))
            OnPropertyChanged(property_name);
    }

    public string GetParameterString(string key, string default_value) => get_string_parameter(key, default_value);

    protected string get_string_parameter(string key, string default_value) =>
        parameters.TryGetValue(key, out object? value) ? parameter_to_string(value, default_value) : default_value;

    protected bool get_bool_parameter(string key, bool default_value) =>
        parameters.TryGetValue(key, out object? value) && bool.TryParse(parameter_to_string(value, ""), out bool parsed) ? parsed : default_value;

    protected int get_int_parameter(string key, int default_value, int minimum, int maximum) =>
        parameters.TryGetValue(key, out object? value) && int.TryParse(parameter_to_string(value, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : default_value;

    protected double get_double_parameter(string key, double default_value, double minimum, double maximum) =>
        parameters.TryGetValue(key, out object? value) && double.TryParse(parameter_to_string(value, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : default_value;

    protected T get_enum_parameter<T>(string key, T default_value) where T : struct, Enum =>
        parameters.TryGetValue(key, out object? value) && Enum.TryParse<T>(parameter_to_string(value, ""), ignoreCase: true, out var parsed)
            ? parsed
            : default_value;

    protected void set_parameter(string key, bool value, string property_name) =>
        set_parameter(key, value.ToString(), property_name);

    protected void set_parameter(string key, int value, string property_name) =>
        set_parameter(key, value.ToString(CultureInfo.InvariantCulture), property_name);

    protected void set_parameter(string key, double value, string property_name) =>
        set_parameter(key, value.ToString("G17", CultureInfo.InvariantCulture), property_name);

    protected void set_parameter(string key, string value, string property_name)
    {
        SetParameter(key, value, property_name);
    }

    private static bool parameter_equals(object? current, object? next) =>
        string.Equals(parameter_to_string(current, ""), parameter_to_string(next, ""), StringComparison.Ordinal);

    internal static string ParameterToJson(object? value) =>
        value switch
        {
            null => "null",
            JsonElement element => element.GetRawText(),
            string text => JsonSerializer.Serialize(text),
            bool boolean => JsonSerializer.Serialize(boolean),
            int integer => JsonSerializer.Serialize(integer),
            long integer => JsonSerializer.Serialize(integer),
            float number => JsonSerializer.Serialize(number),
            double number => JsonSerializer.Serialize(number),
            decimal number => JsonSerializer.Serialize(number),
            _ => JsonSerializer.Serialize(value)
        };

    internal static object? ParameterFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            return parameter_from_element(document.RootElement);
        }
        catch
        {
            return json;
        }
    }

    private static object? parameter_from_element(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out double number) => number,
            JsonValueKind.String => element.GetString() ?? "",
            _ => element.Clone()
        };

    private static string parameter_to_string(object? value, string default_value)
    {
        if (value is null)
            return default_value;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? default_value : element.GetRawText();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? default_value;
    }
}

public abstract class UnivariatePlatform : Platform
{
    private string major = "";
    private double[] histogram = [];
    private double[] smoothed = [];
    private readonly PlatformSmoothingOptions smoothing = new();

    public PlatformSmoothingOptions Smoothing => smoothing;

    public string Major
    {
        get => major;
        set => SetField(ref major, value ?? "");
    }

    public double[] Histogram
    {
        get => histogram;
        set => SetField(ref histogram, value ?? []);
    }

    public double[] Smoothed
    {
        get => smoothed;
        set => SetField(ref smoothed, value ?? []);
    }

    public int SmoothingWindow
    {
        get => smoothing.HalfWindow;
        set => smoothing.HalfWindow = value;
    }

    public bool EnableSmoothing
    {
        get => smoothing.Enabled;
        set => smoothing.Enabled = value;
    }

    protected override void OnConfigurationInvalidated()
    {
        Histogram = [];
        Smoothed = [];
    }
}

public abstract class MultivariatePlatform : Platform
{
    public float[,]? Normalized { get; set; }

    public override bool HasIntegrated => Normalized is not null;

    public override bool IsConfigurationLocked => HasIntegrated;

    protected override void OnConfigurationInvalidated()
    {
        Normalized = null;
    }
}

public sealed class IntegrationPlatform : MultivariatePlatform
{
    public int[] BatchIds { get; set; } = [];
    public override PlatformKind Kind => PlatformKind.Integration;
    public override bool HasGraphics => false;
    public CytoNormOptions CytoNormOptions { get; set; } = new();

    public string BatchColumnName
    {
        get => get_string_parameter(PlatformParameterKeys.BatchColumn, "");
        set => set_parameter(PlatformParameterKeys.BatchColumn, value ?? "", nameof(BatchColumnName));
    }

    protected override void OnConfigurationInvalidated()
    {
        base.OnConfigurationInvalidated();
        BatchIds = [];
    }
}

public sealed class CellCyclePlatform : UnivariatePlatform
{
    public override PlatformKind Kind => PlatformKind.CellCycle;

    public CellCycleModelKind Model
    {
        get => get_enum_parameter(PlatformParameterKeys.Model, CellCycleModelKind.WatsonPragmatic);
        set => set_parameter(PlatformParameterKeys.Model, value.ToString(), nameof(Model));
    }

    public bool DrawModelSum
    {
        get => get_bool_parameter(PlatformParameterKeys.DrawModelSum, true);
        set => set_parameter(PlatformParameterKeys.DrawModelSum, value, nameof(DrawModelSum));
    }

    public bool DrawComponents
    {
        get => get_bool_parameter(PlatformParameterKeys.DrawComponents, true);
        set => set_parameter(PlatformParameterKeys.DrawComponents, value, nameof(DrawComponents));
    }

    public bool FillComponents
    {
        get => get_bool_parameter(PlatformParameterKeys.FillComponents, true);
        set => set_parameter(PlatformParameterKeys.FillComponents, value, nameof(FillComponents));
    }
}

public sealed class ProliferationPlatform : UnivariatePlatform
{
    public override PlatformKind Kind => PlatformKind.Proliferation;

    public bool DrawModelSum
    {
        get => get_bool_parameter(PlatformParameterKeys.DrawModelSum, true);
        set => set_parameter(PlatformParameterKeys.DrawModelSum, value, nameof(DrawModelSum));
    }

    public bool DrawComponents
    {
        get => get_bool_parameter(PlatformParameterKeys.DrawComponents, true);
        set => set_parameter(PlatformParameterKeys.DrawComponents, value, nameof(DrawComponents));
    }

    public int MaxGenerations
    {
        get => get_int_parameter(PlatformParameterKeys.MaxGenerations, 8, 1, 32);
        set => set_parameter(PlatformParameterKeys.MaxGenerations, Math.Clamp(value, 1, 32), nameof(MaxGenerations));
    }

    public double PeakProminence
    {
        get => get_double_parameter(PlatformParameterKeys.PeakProminence, 0.03, 0.001, 1.0);
        set => set_parameter(PlatformParameterKeys.PeakProminence, Math.Clamp(value, 0.001, 1.0), nameof(PeakProminence));
    }
}

public sealed class IntensityComparisonPlatform : UnivariatePlatform
{
    public override PlatformKind Kind => PlatformKind.IntensityComparison;

    public string ReferenceSample
    {
        get => get_string_parameter(PlatformParameterKeys.ReferenceSample, "");
        set => set_parameter(PlatformParameterKeys.ReferenceSample, value ?? "", nameof(ReferenceSample));
    }
}

public sealed class PlatformChannelTransformation : NotifyBase
{
    private PlatformTransformationKind kind = PlatformTransformationKind.Linear;
    private double minimum;
    private double maximum = new LogicleParameters().T;
    private LogicleParameters logicle = new();

    public PlatformTransformationKind Kind
    {
        get => kind;
        set => SetField(ref kind, value);
    }

    public double Minimum
    {
        get => minimum;
        set => SetField(ref minimum, value);
    }

    public double Maximum
    {
        get => maximum;
        set => SetField(ref maximum, value);
    }

    public LogicleParameters Logicle
    {
        get => logicle;
        set => SetField(ref logicle, value);
    }
}

public sealed class PlatformResultTable
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string[] Columns { get; init; } = [];
    public List<string[]> Rows { get; } = new();
}

public sealed class PlatformPlotSeries
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string XLabel { get; init; } = "";
    public string YLabel { get; init; } = "";
    public int SourceId { get; init; } = -1;
    public PlatformSeriesRole Role { get; init; } = PlatformSeriesRole.Observed;
    public double[] X { get; init; } = [];
    public double[] Y { get; init; } = [];
}

public enum PlatformSeriesRole
{
    Observed,
    Fit,
    Component
}

public sealed class PlatformFitCurve
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string XLabel { get; init; } = "";
    public string YLabel { get; init; } = "";
    public int SourceId { get; init; } = -1;
    public PlatformSeriesRole Role { get; init; } = PlatformSeriesRole.Fit;
    public PlatformFitCurveKind Kind { get; init; }
    public PlatformTransformationKind FitTransformation { get; init; } = PlatformTransformationKind.Linear;
    public LogicleParameters FitLogicle { get; init; } = new();
    public double Normalizer { get; init; } = 1.0;
    public double[] Parameters { get; init; } = [];
    public string[] ModelKeys { get; init; } = [];
    public double[] Weights { get; init; } = [];
    public double Intercept { get; init; }
}

public sealed class PlatformStatisticResult
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed class PlatformPopulationInput : NotifyBase
{
    private bool is_selected = true;
    private bool is_expanded = true;
    private bool is_enabled = true;
    private bool is_indeterminate;
    private bool is_platform_dropped;
    private Guid? parent_key;
    private Guid row_key = Guid.NewGuid();

    public Guid RowKey
    {
        get => row_key;
        init => row_key = value;
    }
    public Guid? ParentKey
    {
        get => parent_key;
        init => parent_key = value;
    }
    public Guid GroupId { get; init; }
    public Guid SampleId { get; init; }
    public Guid GateId { get; init; }
    public PopulationRegion Region { get; init; } = PopulationRegion.Primary;
    public string GroupName { get; init; } = "";
    public string SampleName { get; init; } = "";
    public string PopulationName { get; init; } = "";
    public int EventCount { get; set; }
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
    public bool IsPopulation { get; init; } = true;

    public bool IsSelected
    {
        get => is_selected;
        set => SetField(ref is_selected, value);
    }

    public bool IsEnabled
    {
        get => is_enabled;
        set => SetField(ref is_enabled, value);
    }

    public bool IsIndeterminate
    {
        get => is_indeterminate;
        set => SetField(ref is_indeterminate, value);
    }

    public bool IsExpanded
    {
        get => is_expanded;
        set => SetField(ref is_expanded, value);
    }

    public bool IsPlatformDropped
    {
        get => is_platform_dropped;
        set => SetField(ref is_platform_dropped, value);
    }

    public string DisplayName => IsPopulation ? PopulationName : $"{GroupName} / {SampleName}";
}

public sealed class PlatformFeatureSelection : NotifyBase
{
    private bool is_selected = true;
    private bool is_enabled = true;
    private bool is_indeterminate;
    private bool is_expanded = true;
    private Guid row_key = Guid.NewGuid();

    public Guid RowKey
    {
        get => row_key;
        init => row_key = value;
    }
    public Guid? ParentKey { get; init; }
    public string ChannelName { get; init; } = "";
    public string Label { get; init; } = "";
    public string GroupName { get; init; } = "";
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
    public bool IsChannel { get; init; } = true;

    public bool IsSelected
    {
        get => is_selected;
        set => SetField(ref is_selected, value);
    }

    public bool IsEnabled
    {
        get => is_enabled;
        set => SetField(ref is_enabled, value);
    }

    public bool IsIndeterminate
    {
        get => is_indeterminate;
        set => SetField(ref is_indeterminate, value);
    }

    public bool IsExpanded
    {
        get => is_expanded;
        set => SetField(ref is_expanded, value);
    }

    public string DisplayName => IsChannel ? ChannelName : GroupName;
}

public sealed class PlatformRowMap
{
    public List<PlatformRowMapSource> Sources { get; } = new();
    public int[] SourceIds { get; private set; } = [];
    public int[] EventIndices { get; private set; } = [];
    public int Count => EventIndices.Length;

    public void Set(IEnumerable<PlatformRowMapSource> sources, int[] source_ids, int[] event_indices)
    {
        if (source_ids.Length != event_indices.Length)
            throw new ArgumentException("Row map source ids and event indices must have the same length.");

        Sources.Clear();
        Sources.AddRange(sources);
        SourceIds = source_ids;
        EventIndices = event_indices;
    }

    public void Clear() => Set([], [], []);
}

public sealed class PlatformRowMapSource
{
    public Guid GroupId { get; init; }
    public Guid SampleId { get; init; }
    public Guid GateId { get; init; }
    public PopulationRegion Region { get; init; } = PopulationRegion.Primary;
}
