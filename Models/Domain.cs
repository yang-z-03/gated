using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Python.Runtime;

namespace gated.Models;

public enum CoordinateScaleKind
{
    Linear,
    Logicle,
    Logarithmic,
    Arcsinh
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
    Gray,
    YellowGreen,
    YellowGreenBlue,
    GreenBlue,
    BlueGreen,
    PurpleBlueGreen,
    PurpleBlue,
    BluePurple,
    RedPurple,
    PurpleRed,
    OrangeRed,
    YellowOrangeRed,
    Purples,
    Blues,
    Greens,
    Oranges,
    Reds,
    Greys,
    Spectral,
    Inferno,
    Magma,
    Cividis,
    Colorblind,
    Sunset,
    Iridescent,
    Rainbow
}

public sealed class PlotColorMap
{
    public PlotColorMap(PlotColorPalette palette, string name, IReadOnlyList<Color> colors)
    {
        Palette = palette;
        Name = name;
        Colors = colors;
    }

    public PlotColorPalette Palette { get; }
    public string Name { get; }
    public IReadOnlyList<Color> Colors { get; }

    public Color ColorAt(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (Colors.Count == 0)
            return Avalonia.Media.Colors.Black;
        if (Colors.Count == 1)
            return Colors[0];

        double scaled = value * (Colors.Count - 1);
        int index = Math.Clamp((int)Math.Floor(scaled), 0, Colors.Count - 2);
        double t = scaled - index;
        var first = Colors[index];
        var second = Colors[index + 1];
        return Color.FromRgb(
            to_byte(first.R + (second.R - first.R) * t),
            to_byte(first.G + (second.G - first.G) * t),
            to_byte(first.B + (second.B - first.B) * t));
    }

    public override string ToString() => Name;

    private static byte to_byte(double value) =>
        Convert.ToByte(Math.Clamp((int)Math.Round(value), 0, 255));
}

public static class PlotColorMaps
{
    public static IReadOnlyList<PlotColorMap> All { get; } =
    [
        map(PlotColorPalette.Viridis, "Viridis", "#440154", "#46317E", "#365A8C", "#277E8E", "#1EA087", "#49C16D", "#9DD93A", "#FDE724"),
        map(PlotColorPalette.Plasma, "Plasma", "#0C0786", "#5201A3", "#8908A5", "#B83289", "#DA5A68", "#F38748", "#FDBB2B", "#EFF821"),
        map(PlotColorPalette.Turbo, "Turbo", "#30123b", "#4560d6", "#36a8f9", "#1ae4b6", "#71fd5f", "#c5ef33", "#f9ba38", "#f66b18", "#cb2b03", "#7a0402"),
        map(PlotColorPalette.Gray, "Gray", "#000000", "#252525", "#525252", "#737373", "#969696", "#bdbdbd", "#d9d9d9", "#f0f0f0", "#ffffff"),
        map(PlotColorPalette.YellowGreen, "Yellow-Green", "#004529", "#006837", "#238443", "#41ab5d", "#78c679", "#addd8e", "#d9f0a3", "#f7fcb9", "#ffffe5"),
        map(PlotColorPalette.YellowGreenBlue, "Yellow-Green-Blue", "#081d58", "#253494", "#225ea8", "#1d91c0", "#41b6c4", "#7fcdbb", "#c7e9b4", "#edf8b1", "#ffffd9"),
        map(PlotColorPalette.GreenBlue, "Green-Blue", "#084081", "#0868ac", "#2b8cbe", "#4eb3d3", "#7bccc4", "#a8ddb5", "#ccebc5", "#e0f3db", "#f7fcf0"),
        map(PlotColorPalette.BlueGreen, "Blue-Green", "#00441b", "#006d2c", "#238b45", "#41ae76", "#66c2a4", "#99d8c9", "#ccece6", "#e5f5f9", "#f7fcfd"),
        map(PlotColorPalette.PurpleBlueGreen, "Purple-Blue-Green", "#014636", "#016c59", "#02818a", "#3690c0", "#67a9cf", "#a6bddb", "#d0d1e6", "#ece2f0", "#fff7fb"),
        map(PlotColorPalette.PurpleBlue, "Purple-Blue", "#023858", "#045a8d", "#0570b0", "#3690c0", "#74a9cf", "#a6bddb", "#d0d1e6", "#ece7f2", "#fff7fb"),
        map(PlotColorPalette.BluePurple, "Blue-Purple", "#4d004b", "#810f7c", "#88419d", "#8c6bb1", "#8c96c6", "#9ebcda", "#bfd3e6", "#e0ecf4", "#f7fcfd"),
        map(PlotColorPalette.RedPurple, "Red-Purple", "#49006a", "#7a0177", "#ae017e", "#dd3497", "#f768a1", "#fa9fb5", "#fcc5c0", "#fde0dd", "#fff7f3"),
        map(PlotColorPalette.PurpleRed, "Purple-Red", "#67001f", "#980043", "#ce1256", "#e7298a", "#df65b0", "#c994c7", "#d4b9da", "#e7e1ef", "#f7f4f9"),
        map(PlotColorPalette.OrangeRed, "Orange-Red", "#7f0000", "#b30000", "#d7301f", "#ef6548", "#fc8d59", "#fdbb84", "#fdd49e", "#fee8c8", "#fff7ec"),
        map(PlotColorPalette.YellowOrangeRed, "Yellow-Orange-Red", "#800026", "#bd0026", "#e31a1c", "#fc4e2a", "#fd8d3c", "#feb24c", "#fed976", "#ffeda0", "#ffffcc"),
        map(PlotColorPalette.Purples, "Purples", "#3f007d", "#54278f", "#6a51a3", "#807dba", "#9e9ac8", "#bcbddc", "#dadaeb", "#efedf5", "#fcfbfd"),
        map(PlotColorPalette.Blues, "Blues", "#08306b", "#08519c", "#2171b5", "#4292c6", "#6baed6", "#9ecae1", "#c6dbef", "#deebf7", "#f7fbff"),
        map(PlotColorPalette.Greens, "Greens", "#00441b", "#006d2c", "#238b45", "#41ab5d", "#74c476", "#a1d99b", "#c7e9c0", "#e5f5e0", "#f7fcf5"),
        map(PlotColorPalette.Oranges, "Oranges", "#7f2704", "#a63603", "#d94801", "#f16913", "#fd8d3c", "#fdae6b", "#fdd0a2", "#fee6ce", "#fff5eb"),
        map(PlotColorPalette.Reds, "Reds", "#67000d", "#a50f15", "#cb181d", "#ef3b2c", "#fb6a4a", "#fc9272", "#fcbba1", "#fee0d2", "#fff5f0"),
        map(PlotColorPalette.Greys, "Greys", "#000000", "#252525", "#525252", "#737373", "#969696", "#bdbdbd", "#d9d9d9", "#f0f0f0", "#ffffff"),
        map(PlotColorPalette.Spectral, "Spectral", "#3288bd", "#66c2a5", "#abdda4", "#e6f598", "#fee08b", "#fdae61", "#f46d43", "#d53e4f"),
        map(PlotColorPalette.Inferno, "Inferno", "#000003", "#270B52", "#63146E", "#9E2963", "#D24742", "#F57C15", "#FABF25", "#FCFEA4"),
        map(PlotColorPalette.Magma, "Magma", "#000003", "#221150", "#5D177E", "#972C7F", "#D1426E", "#F8755C", "#FEB97F", "#FBFCBF"),
        map(PlotColorPalette.Cividis, "Cividis", "#00204C", "#15396D", "#49536B", "#6C6D72", "#8D8878", "#B2A672", "#D9C661", "#FFE945"),
        map(PlotColorPalette.Colorblind, "Colorblind", "#0072B2", "#E69F00", "#F0E442", "#009E73", "#56B4E9", "#D55E00", "#CC79A7", "#000000"),
        map(PlotColorPalette.Sunset, "Sunset", "#364B9A", "#4A7BB7", "#6EA6CD", "#98CAE1", "#C2E4EF", "#EAECCC", "#FEDA8B", "#FDB366", "#F67E4B", "#DD3D2D", "#A50026"),
        map(PlotColorPalette.Iridescent, "Iridescent", "#FEFBE9", "#FCF7D5", "#F5F3C1", "#EAF0B5", "#DDECBF", "#D0E7CA", "#C2E3D2", "#B5DDD8", "#A8D8DC", "#9BD2E1", "#8DCBE4", "#81C4E7", "#7BBCE7", "#7EB2E4", "#88A5DD", "#9398D2", "#9B8AC4", "#9D7DB2", "#9A709E", "#906388", "#805770", "#684957", "#46353A"),
        map(PlotColorPalette.Rainbow, "Rainbow", "#E8ECFB", "#D9CCE3", "#CAACCB", "#BA8DB4", "#AA6F9E", "#994F88", "#882E72", "#1965B0", "#437DBF", "#6195CF", "#7BAFDE", "#4EB265", "#90C987", "#CAE0AB", "#F7F056", "#F7CB45", "#F4A736", "#EE8026", "#E65518", "#DC050C", "#A5170E", "#72190E", "#42150A")
    ];

    public static PlotColorMap Get(PlotColorPalette palette) =>
        All.FirstOrDefault(item => item.Palette == palette) ?? All[0];

    public static Color ColorAt(PlotColorPalette palette, double value) =>
        Get(palette).ColorAt(value);

    private static PlotColorMap map(PlotColorPalette palette, string name, params string[] colors) =>
        new(palette, name, colors.Select(parse_hex_color).ToArray());

    private static Color parse_hex_color(string value)
    {
        string hex = value.TrimStart('#');
        if (hex.Length != 6)
            return Colors.Black;
        return Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }
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
    private string name;
    private string label;

    public ChannelDefinition(int index, string name, string label, float maximum, float gain)
    {
        Index = index;
        this.name = name;
        this.label = label;
        Maximum = maximum;
        Gain = gain;
    }

    public int Index { get; }

    public string Name
    {
        get => name;
        set => SetField(ref name, value ?? "");
    }

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
    public double ArcsinhCofactor { get; set; } = 5.0;

    public AxisScale Clone() =>
        new()
        {
            Kind = Kind,
            Logicle = Logicle,
            ArcsinhCofactor = ArcsinhCofactor
        };

    public double Transform(double value)
    {
        return Kind switch
        {
            CoordinateScaleKind.Linear => value,
            CoordinateScaleKind.Logarithmic => Math.Sign(value) * Math.Log10(1.0 + Math.Abs(value)),
            CoordinateScaleKind.Arcsinh => Math.Asinh(value / valid_arcsinh_cofactor()),
            _ => new LogicleTransform(Logicle).Transform(value)
        };
    }

    public double InverseTransform(double value)
    {
        return Kind switch
        {
            CoordinateScaleKind.Linear => value,
            CoordinateScaleKind.Logarithmic => Math.Sign(value) * (Math.Pow(10.0, Math.Abs(value)) - 1.0),
            CoordinateScaleKind.Arcsinh => valid_arcsinh_cofactor() * Math.Sinh(value),
            _ => new LogicleTransform(Logicle).InverseTransform(value)
        };
    }

    public bool IsEquivalent(AxisScale other)
    {
        if (Kind != other.Kind)
            return false;

        return Kind switch
        {
            CoordinateScaleKind.Linear or CoordinateScaleKind.Logarithmic => true,
            CoordinateScaleKind.Arcsinh => Math.Abs(valid_arcsinh_cofactor() - other.valid_arcsinh_cofactor()) < 1e-12,
            _ => Logicle.Equals(other.Logicle)
        };
    }

    private double valid_arcsinh_cofactor() =>
        double.IsFinite(ArcsinhCofactor) && ArcsinhCofactor > 0 ? ArcsinhCofactor : 5.0;
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
            OnPropertyChanged(nameof(IsArcsinh));
            OnPropertyChanged(nameof(LogicleTopOfScale));
            OnPropertyChanged(nameof(LogicleDecades));
            OnPropertyChanged(nameof(LogicleLinearizationWidth));
            OnPropertyChanged(nameof(LogicleNegativeDecades));
            OnPropertyChanged(nameof(ArcsinhCofactor));
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
            OnPropertyChanged(nameof(IsArcsinh));
        }
    }

    public bool IsLogicle => ScaleKind == CoordinateScaleKind.Logicle;
    public bool IsArcsinh => ScaleKind == CoordinateScaleKind.Arcsinh;

    public double ArcsinhCofactor
    {
        get => scale.ArcsinhCofactor;
        set
        {
            value = double.IsFinite(value) && value > 0 ? value : 5.0;
            if (Math.Abs(scale.ArcsinhCofactor - value) < double.Epsilon)
                return;
            scale.ArcsinhCofactor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scale));
        }
    }

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
    private double available_minimum;
    private double available_maximum = 1;
    private double range_minimum;
    private double range_maximum = 1;
    private bool has_available_range;
    private string range_channel_name = "";
    private bool clamp_negative_values_to_zero;

    public string ChannelName
    {
        get => channel_name;
        set
        {
            if (!SetField(ref channel_name, value ?? ""))
                return;

            OnPropertyChanged(nameof(HasChannel));
        }
    }

    public PlotColorPalette Palette
    {
        get => palette;
        set => SetField(ref palette, value);
    }

    public bool UseLogScale
    {
        get => use_log_scale;
        set => SetField(ref use_log_scale, value && CanUseLogScale);
    }

    public bool HasChannel => !string.IsNullOrWhiteSpace(channel_name);

    public double AvailableMinimum
    {
        get => available_minimum;
        private set => SetField(ref available_minimum, value);
    }

    public double AvailableMaximum
    {
        get => available_maximum;
        private set => SetField(ref available_maximum, value);
    }

    public double RangeMinimum
    {
        get => range_minimum;
        set
        {
            double coerced = coerce_range_value(value);
            if (coerced > range_maximum)
                coerced = range_maximum;
            SetField(ref range_minimum, coerced);
        }
    }

    public double RangeMaximum
    {
        get => range_maximum;
        set
        {
            double coerced = coerce_range_value(value);
            if (coerced < range_minimum)
                coerced = range_minimum;
            SetField(ref range_maximum, coerced);
        }
    }

    public bool HasAvailableRange
    {
        get => has_available_range;
        private set => SetField(ref has_available_range, value);
    }

    public bool CanUseLogScale => HasAvailableRange && available_minimum >= 0;

    public bool ClampNegativeValuesToZero
    {
        get => clamp_negative_values_to_zero;
        private set => SetField(ref clamp_negative_values_to_zero, value);
    }

    public void SetAvailableRange(double minimum, double maximum, bool reset_selection = true)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            range_channel_name = "";
            SetField(ref has_available_range, false, nameof(HasAvailableRange));
            SetField(ref available_minimum, 0d, nameof(AvailableMinimum));
            SetField(ref available_maximum, 1d, nameof(AvailableMaximum));
            SetField(ref range_minimum, 0d, nameof(RangeMinimum));
            SetField(ref range_maximum, 1d, nameof(RangeMaximum));
            UseLogScale = false;
            OnPropertyChanged(nameof(CanUseLogScale));
            return;
        }

        if (maximum <= minimum)
            maximum = minimum + 1;

        SetField(ref has_available_range, true, nameof(HasAvailableRange));
        SetField(ref available_minimum, minimum, nameof(AvailableMinimum));
        SetField(ref available_maximum, maximum, nameof(AvailableMaximum));
        if (reset_selection)
        {
            SetField(ref range_minimum, minimum, nameof(RangeMinimum));
            SetField(ref range_maximum, maximum, nameof(RangeMaximum));
        }
        else
        {
            SetRange(range_minimum, range_maximum);
        }
        if (!CanUseLogScale)
            UseLogScale = false;
        OnPropertyChanged(nameof(CanUseLogScale));
    }

    public void SetAvailableRangeForChannel(string channel_name, double minimum, double maximum, bool clamp_negative_values_to_zero, bool force_reset_selection = false)
    {
        bool reset_selection = force_reset_selection || !string.Equals(range_channel_name, channel_name, StringComparison.Ordinal);
        range_channel_name = channel_name;
        ClampNegativeValuesToZero = clamp_negative_values_to_zero;
        SetAvailableRange(minimum, maximum, reset_selection);
    }

    public void SetRange(double minimum, double maximum)
    {
        if (!HasAvailableRange)
            return;

        minimum = coerce_range_value(minimum);
        maximum = coerce_range_value(maximum);
        if (maximum < minimum)
            (minimum, maximum) = (maximum, minimum);

        SetField(ref range_minimum, minimum, nameof(RangeMinimum));
        SetField(ref range_maximum, maximum, nameof(RangeMaximum));
    }

    private double coerce_range_value(double value)
    {
        if (!HasAvailableRange || !double.IsFinite(value))
            return value;
        return Math.Clamp(value, available_minimum, available_maximum);
    }
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
    public PlotColorPalette DensityPalette { get; set; } = PlotColorPalette.Turbo;
    public DotColorSettings DotColor { get; set; } = new();

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
    public PlotColorPalette PreferredDensityPalette { get; set; } = PlotColorPalette.Turbo;
    public DotColorSettings PreferredDotColor { get; set; } = new();
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
    public string DisplayName { get; set; } = "";
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

    public void RenameChannel(string old_name, string new_name)
    {
        if (!ChannelNames.Contains(old_name, StringComparer.Ordinal))
            return;

        ChannelNames = ChannelNames.Select(name => string.Equals(name, old_name, StringComparison.Ordinal) ? new_name : name).ToArray();
        OnPropertyChanged(nameof(ChannelNames));
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

public sealed record SpilloverRangeSelection(double Minimum, double Maximum);

public sealed class SpilloverControlRow : NotifyBase
{
    private Guid control_sample_id;
    private string parameter_name = "";
    private SpilloverRangeSelection? positive_selection;
    private Guid gate_preset_id;

    public Guid ControlSampleId
    {
        get => control_sample_id;
        set => SetField(ref control_sample_id, value);
    }

    public string ParameterName
    {
        get => parameter_name;
        set => SetField(ref parameter_name, value ?? "");
    }

    public SpilloverRangeSelection? PositiveSelection
    {
        get => positive_selection;
        set => SetField(ref positive_selection, value);
    }

    public Guid GatePresetId { get => gate_preset_id; set => SetField(ref gate_preset_id, value); }
}

public sealed class ControlGatePreset : NotifyBase
{
    private string name = "Default";
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => name; set => SetField(ref name, string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim()); }
    public string XChannel { get; set; } = "FSC-A";
    public string YChannel { get; set; } = "SSC-A";
    public AxisSettings XAxis { get; set; } = new() { ChannelName = "FSC-A", ScaleKind = CoordinateScaleKind.Linear };
    public AxisSettings YAxis { get; set; } = new() { ChannelName = "SSC-A", ScaleKind = CoordinateScaleKind.Linear };
    public ObservableCollection<Point> Vertices { get; } = new();

    public override string ToString() => Name;
}

public sealed class SpilloverCompensationState : NotifyBase
{
    private string matrix_name = "Auto Comp";

    public string MatrixName
    {
        get => matrix_name;
        set => SetField(ref matrix_name, string.IsNullOrWhiteSpace(value) ? "Auto Comp" : value.Trim());
    }

    public ObservableCollection<ControlGatePreset> GatePresets { get; } = new();
    public ControlGatePreset DefaultGatePreset
    {
        get
        {
            if (GatePresets.Count == 0)
                GatePresets.Add(new ControlGatePreset());
            return GatePresets[0];
        }
    }
    public ObservableCollection<Point> PrimaryVertices => DefaultGatePreset.Vertices;
    public ObservableCollection<SpilloverControlRow> Rows { get; } = new();
}

public enum SpectralControlRole
{
    Molecule,
    UnstainedAf
}

public sealed class SpectralControlRow : NotifyBase
{
    private Guid control_sample_id;
    private SpectralControlRole role;
    private string molecule_name = "";
    private bool use_automatic_peak = true;
    private string peak_channel = "";
    private SpilloverRangeSelection? positive_selection;
    private Guid gate_preset_id;
    public Guid ControlSampleId { get => control_sample_id; set => SetField(ref control_sample_id, value); }
    public SpectralControlRole Role { get => role; set => SetField(ref role, value); }
    public string MoleculeName { get => molecule_name; set => SetField(ref molecule_name, value ?? ""); }
    public bool UseAutomaticPeak { get => use_automatic_peak; set => SetField(ref use_automatic_peak, value); }
    public string PeakChannel { get => peak_channel; set => SetField(ref peak_channel, value ?? ""); }
    public SpilloverRangeSelection? PositiveSelection { get => positive_selection; set => SetField(ref positive_selection, value); }
    public Guid GatePresetId { get => gate_preset_id; set => SetField(ref gate_preset_id, value); }
    public SpectralPlotData? PlotCache { get; set; }
    public int[] GatedEventCache { get; set; } = [];
    public int[] PositiveEventCache { get; set; } = [];
    public string CachedPeakChannel { get; set; } = "";
    public float[] CachedFingerprint { get; set; } = [];
    public int CachedPositiveCount { get; set; }
    public int CachedPopulationCount { get; set; }
}

public sealed record SpectralPlotData(
    IReadOnlyList<string> DetectorNames,
    IReadOnlyList<ExcitationLightKind> ExcitationLights,
    int[,] Density,
    double RawMaximum,
    string PeakChannel,
    SpilloverRangeSelection? PositiveSelection);

public sealed class SpectralUnmixingState : NotifyBase
{
    private bool is_stale = true;
    private bool is_user_modified;
    private Guid? linked_output_group_id;
    public ObservableCollection<ControlGatePreset> GatePresets { get; } = new();
    public ObservableCollection<SpectralControlRow> Rows { get; } = new();
    public List<string> DetectorNames { get; } = new();
    public List<string> SignatureNames { get; } = new();
    public float[,] Signatures { get; private set; } = new float[0, 0];
    public float[,] Similarity { get; private set; } = new float[0, 0];
    public float[,] Coefficients { get; private set; } = new float[0, 0];
    public Dictionary<Guid, Guid> GeneratedSampleIds { get; } = new();
    public bool IsStale { get => is_stale; set => SetField(ref is_stale, value); }
    public bool IsUserModified { get => is_user_modified; set => SetField(ref is_user_modified, value); }
    public Guid? LinkedOutputGroupId { get => linked_output_group_id; set => SetField(ref linked_output_group_id, value); }
    public ControlGatePreset DefaultGatePreset
    {
        get
        {
            if (GatePresets.Count == 0) GatePresets.Add(new ControlGatePreset());
            return GatePresets[0];
        }
    }
    public void SetFit(IReadOnlyList<string> detectors, IReadOnlyList<string> signatures, float[,] spectra, float[,] similarity, float[,] coefficients)
    {
        DetectorNames.Clear(); DetectorNames.AddRange(detectors);
        SignatureNames.Clear(); SignatureNames.AddRange(signatures);
        Signatures = (float[,])spectra.Clone();
        Similarity = (float[,])similarity.Clone();
        Coefficients = (float[,])coefficients.Clone();
        IsStale = false;
        IsUserModified = false;
        OnPropertyChanged(nameof(Signatures)); OnPropertyChanged(nameof(Similarity)); OnPropertyChanged(nameof(Coefficients));
    }
    public void ReplaceCoefficients(float[,] coefficients)
    {
        Coefficients = (float[,])coefficients.Clone();
        IsUserModified = true;
        OnPropertyChanged(nameof(Coefficients));
    }
}

public sealed class ControlSample : NotifyBase
{
    private string name = "";
    private readonly object normalized_channel_cache_lock = new();
    private readonly Dictionary<NormalizedChannelCacheKey, float[]> normalized_channel_cache = new();

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set => SetField(ref name, value ?? "");
    }

    public ObservableCollection<ChannelDefinition> Channels { get; } = new();
    public float[,] RawEvents { get; private set; } = new float[0, 0];
    public Dictionary<string, string> Metadata { get; } = new();
    public int EventCount => RawEvents.GetLength(0);
    public int ChannelCount => RawEvents.GetLength(1);
    public string ChannelProfile => string.Join("|", Channels.Select(channel => channel.Name));

    public ControlSample(string name, IEnumerable<ChannelDefinition> channels, float[,] raw_events)
    {
        Name = name;
        RawEvents = raw_events;
        foreach (var channel in channels)
            Channels.Add(channel);
    }

    public int GetChannelIndex(string channel_name)
    {
        for (int index = 0; index < Channels.Count; index++)
            if (Channels[index].Name == channel_name)
                return index;
        return -1;
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
                values[row] = RawEvents[row, channel_index];
            return values;
        }

        var selected = new float[event_indices.Length];
        for (int index = 0; index < event_indices.Length; index++)
        {
            int row = event_indices[index];
            selected[index] = row >= 0 && row < EventCount ? RawEvents[row, channel_index] : float.NaN;
        }
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

        var normalized = create_normalized_channel_values(channel_name, null, minimum, maximum, scale, cancellation_token);
        lock (normalized_channel_cache_lock)
        {
            if (normalized_channel_cache.TryGetValue(key, out var cached))
                return cached;
            normalized_channel_cache[key] = normalized;
            return normalized;
        }
    }

    public void ProjectChannels(IReadOnlyList<ChannelDefinition> required_channels)
    {
        var indices = required_channels.Select(channel => GetChannelIndex(channel.Name)).ToArray();
        if (indices.Any(index => index < 0))
            throw new InvalidOperationException("Control sample does not contain every required channel.");

        RawEvents = project_matrix(RawEvents, indices);
        Channels.Clear();
        for (int index = 0; index < required_channels.Count; index++)
        {
            var source = required_channels[index];
            Channels.Add(new ChannelDefinition(index, source.Name, source.Label, source.Maximum, source.Gain));
        }
        InvalidateNormalizedChannelCache();
    }

    public void InvalidateNormalizedChannelCache()
    {
        lock (normalized_channel_cache_lock)
            normalized_channel_cache.Clear();
    }

    private float[] create_normalized_channel_values(
        string channel_name,
        int[]? event_indices,
        double minimum,
        double maximum,
        AxisScale scale,
        CancellationToken cancellation_token)
    {
        var source = GetChannelValues(channel_name, event_indices);
        if (source.Length == 0)
            return Array.Empty<float>();

        double transformed_minimum = scale.Transform(minimum);
        double transformed_maximum = scale.Transform(maximum);

        double transformed_span = transformed_maximum - transformed_minimum;
        if (transformed_span <= 0)
            return new float[source.Length];

        var normalized = new float[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();
            double transformed = scale.Transform(source[index]);
            normalized[index] = Convert.ToSingle((transformed - transformed_minimum) / transformed_span);
        }

        return normalized;
    }

    private static float[,] project_matrix(float[,] source, IReadOnlyList<int> indices)
    {
        var projected = new float[source.GetLength(0), indices.Count];
        for (int row = 0; row < source.GetLength(0); row++)
        for (int column = 0; column < indices.Count; column++)
            projected[row, column] = source[row, indices[column]];
        return projected;
    }
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
    public float[,] RawEvents { get; private set; } = new float[0, 0];
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

    public void ProjectChannels(IReadOnlyList<ChannelDefinition> required_channels)
    {
        var indices = required_channels.Select(channel => GetChannelIndex(channel.Name)).ToArray();
        if (indices.Any(index => index < 0))
            throw new InvalidOperationException("Sample does not contain every required channel.");

        RawEvents = project_matrix(RawEvents, indices);
        CompensatedEvents = project_matrix(CompensatedEvents, indices);
        Channels.Clear();
        for (int index = 0; index < required_channels.Count; index++)
        {
            var source = required_channels[index];
            Channels.Add(new ChannelDefinition(index, source.Name, source.Label, source.Maximum, source.Gain));
        }
        DefaultCompensation = null;
        InvalidateNormalizedChannelCache();
    }

    private static float[,] project_matrix(float[,] source, IReadOnlyList<int> indices)
    {
        var projected = new float[source.GetLength(0), indices.Count];
        for (int row = 0; row < source.GetLength(0); row++)
        for (int column = 0; column < indices.Count; column++)
            projected[row, column] = source[row, indices[column]];
        return projected;
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

        double transformed_minimum = scale.Transform(minimum);
        double transformed_maximum = scale.Transform(maximum);

        double transformed_span = transformed_maximum - transformed_minimum;
        if (transformed_span <= 0)
            return new float[source.Length];

        var normalized = new float[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();
            double transformed = scale.Transform(source[index]);
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

        if (is_identity_matrix(matrix.Values))
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

    private static bool is_identity_matrix(float[,] matrix)
    {
        int rows = matrix.GetLength(0);
        int columns = matrix.GetLength(1);
        if (rows != columns)
            return false;

        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            float expected = row == column ? 1.0f : 0.0f;
            if (Math.Abs(matrix[row, column] - expected) > 0.000001f)
                return false;
        }

        return true;
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
    public ObservableCollection<ControlSample> ControlSamples { get; } = new();
    public ObservableCollection<GateDefinition> Gates { get; } = new();
    public ObservableCollection<StatisticDefinition> Statistics { get; } = new();
    public ObservableCollection<CompensationMatrix> CompensationCandidates { get; } = new();
    public SpilloverCompensationState SpilloverCompensation { get; } = new();
    public SpectralUnmixingState SpectralUnmixing { get; } = new();
    public Guid? SpectralSourceGroupId { get; set; }
    public GateViewOptions RootViewOptions { get; set; } = new();
    public Dictionary<string, GateViewOptions> SampleRootViewOptions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AxisSettings> DataImpliedViewOptions { get; } = new(StringComparer.Ordinal);
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

    public void RefreshChannelProfile()
    {
        ChannelProfile = Samples.FirstOrDefault()?.ChannelProfile ?? "";
        OnPropertyChanged(nameof(Channels));
    }

    public void ResetIdentityCompensation()
    {
        CompensationCandidates.Clear();
        AppliedCompensation = null;
        applied_compensation_is_manual = false;
        if (Samples.FirstOrDefault() is not { } sample)
            return;

        var identity = new CompensationMatrix { Name = "Identity" };
        identity.ResetIdentity(sample.Channels.Select(channel => channel.Name).ToArray());
        RegisterCompensation(identity, make_applied_if_first: true);
    }

    public void RenameChannelInProfile(string old_name, string new_name)
    {
        if (string.IsNullOrWhiteSpace(ChannelProfile))
            return;

        var names = ChannelProfile.Split('|');
        bool changed = false;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] != old_name)
                continue;

            names[i] = new_name;
            changed = true;
        }

        if (changed)
            ChannelProfile = string.Join("|", names);
    }

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
            {
                sample.Recalculate(this);
                RecalculateDataImpliedViewOptions();
            }
            else
            {
                RecalculateSamples();
            }
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

        RecalculateDataImpliedViewOptions(cancellation_token);
    }

    public void ApplyCompensationToSamples(bool force_compensation, CancellationToken cancellation_token = default)
    {
        foreach (var sample in Samples)
        {
            cancellation_token.ThrowIfCancellationRequested();
            sample.ApplyCompensation(AppliedCompensation, force_compensation);
        }

        RecalculateDataImpliedViewOptions(cancellation_token);
    }

    public void RecalculateDataImpliedViewOptions(CancellationToken cancellation_token = default)
    {
        DataImpliedViewOptions.Clear();
        if (Samples.Count == 0)
            return;

        foreach (var channel in Channels)
        {
            cancellation_token.ThrowIfCancellationRequested();
            if (create_data_implied_axis(channel.Name, is_embedding: false, cancellation_token) is { } axis)
                DataImpliedViewOptions[channel.Name] = axis;
        }

        foreach (string embedding_name in Samples
                     .SelectMany(sample => sample.Embeddings.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            cancellation_token.ThrowIfCancellationRequested();
            if (create_data_implied_axis(embedding_name, is_embedding: true, cancellation_token) is { } axis)
                DataImpliedViewOptions[embedding_name] = axis;
        }
    }

    private AxisSettings? create_data_implied_axis(string channel_name, bool is_embedding, CancellationToken cancellation_token)
    {
        var range = finite_value_range(channel_name, is_embedding, cancellation_token);
        if (!range.HasValues)
            return null;

        double? channel_maximum = null;
        if (!is_embedding)
        {
            var channel = Channels.FirstOrDefault(item => item.Name == channel_name);
            if (double.IsFinite(channel?.Maximum ?? double.NaN) && channel!.Maximum > 0)
                channel_maximum = channel.Maximum;
        }

        bool is_time = !is_embedding && Configuration.IsTimeChannel(channel_name);
        double maximum = range.Maximum;
        if (!double.IsFinite(maximum))
            return null;

        if (is_time)
        {
            double minimum = apply_channel_minimum_floor(hundred_floor(range.Minimum), channel_maximum);
            double upper = range.Maximum > range.Minimum ? hundred_ceiling(range.Maximum) : minimum + 1e-6;
            upper = apply_channel_maximum_cap(upper, channel_maximum);
            return new AxisSettings
            {
                ChannelName = channel_name,
                Minimum = minimum,
                Maximum = upper,
                ScaleKind = CoordinateScaleKind.Linear
            };
        }

        bool use_linear = is_embedding || Configuration.DefaultCoordinateScaleForChannel(channel_name) == CoordinateScaleKind.Linear;
        double implied_maximum = Math.Max(maximum, 1e-6);
        if (use_linear)
        {
            double minimum = apply_channel_minimum_floor(hundred_floor(-0.1 * implied_maximum), channel_maximum);
            double upper = hundred_ceiling(1.1 * implied_maximum);
            upper = apply_channel_maximum_cap(upper, channel_maximum);
            return new AxisSettings
            {
                ChannelName = channel_name,
                Minimum = Math.Max(minimum, hundred_floor(range.Minimum)),
                Maximum = upper,
                ScaleKind = CoordinateScaleKind.Linear
            };
        }

        double logicle_minimum = apply_channel_minimum_floor(hundred_floor(-0.01 * implied_maximum), channel_maximum);
        double logicle_maximum = apply_channel_maximum_cap(hundred_ceiling(3 * implied_maximum), channel_maximum);
        return new AxisSettings
        {
            ChannelName = channel_name,
            Minimum = Math.Max(logicle_minimum, hundred_floor(range.Minimum)),
            Maximum = logicle_maximum,
            ScaleKind = CoordinateScaleKind.Logicle
        };
    }

    private FiniteValueRange finite_value_range(string channel_name, bool is_embedding, CancellationToken cancellation_token)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        bool has_values = false;
        foreach (var sample in Samples)
        {
            cancellation_token.ThrowIfCancellationRequested();
            if (is_embedding)
            {
                if (sample.Embeddings.TryGetValue(channel_name, out var embedding))
                    add_finite_values(embedding.Values, ref minimum, ref maximum, ref has_values, cancellation_token);
                continue;
            }

            int channel_index = sample.GetChannelIndex(channel_name);
            if (channel_index < 0)
                continue;

            add_finite_channel_values(sample.CompensatedEvents, channel_index, ref minimum, ref maximum, ref has_values, cancellation_token);
        }

        return has_values
            ? new FiniteValueRange(true, minimum, maximum)
            : default;
    }

    public override string ToString() => Name;

    private static void add_finite_channel_values(
        float[,] values,
        int channel_index,
        ref double minimum,
        ref double maximum,
        ref bool has_values,
        CancellationToken cancellation_token)
    {
        int row_count = values.GetLength(0);
        for (int row = 0; row < row_count; row++)
        {
            if ((row & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();

            add_finite_value(values[row, channel_index], ref minimum, ref maximum, ref has_values);
        }
    }

    private static void add_finite_values(
        IReadOnlyList<float> values,
        ref double minimum,
        ref double maximum,
        ref bool has_values,
        CancellationToken cancellation_token)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if ((index & 4095) == 0)
                cancellation_token.ThrowIfCancellationRequested();

            add_finite_value(values[index], ref minimum, ref maximum, ref has_values);
        }
    }

    private static void add_finite_value(float value, ref double minimum, ref double maximum, ref bool has_values)
    {
        if (!float.IsFinite(value))
            return;

        if (value < minimum)
            minimum = value;
        if (value > maximum)
            maximum = value;
        has_values = true;
    }

    private readonly record struct FiniteValueRange(bool HasValues, double Minimum, double Maximum);

    private static double hundred_floor(double value)
    {
        if (!double.IsFinite(value))
            return 0;
        return Math.Floor(value / 100.0) * 100.0;
    }

    private static double hundred_ceiling(double value)
    {
        if (!double.IsFinite(value))
            return 0;
        return Math.Ceiling(value / 100.0) * 100.0;
    }

    private static double apply_channel_maximum_cap(double maximum, double? channel_maximum) =>
        channel_maximum is { } cap && maximum > cap ? cap : maximum;

    private static double apply_channel_minimum_floor(double minimum, double? channel_maximum) =>
        channel_maximum is { } cap && minimum < -0.01 * cap ? -0.01 * cap : minimum;

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
    double LogicleNegativeDecades,
    double ArcsinhCofactor)
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
            scale.Logicle.A,
            scale.ArcsinhCofactor);
}

public sealed class FlowWorkspace : NotifyBase
{
    private string name = "Untitled Workspace";
    private PyObject? python_storage;

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

    internal bool HasPythonStorage => python_storage is not null;

    internal PyObject GetPythonStorage()
    {
        python_storage ??= new PyDict();
        return python_storage;
    }

    internal PyObject? DetachPythonStorage()
    {
        var storage = python_storage;
        python_storage = null;
        return storage;
    }
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
