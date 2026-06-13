using System;
using System.Collections.ObjectModel;
using Avalonia;

namespace gated.Models;

public sealed class PageLayout : NotifyBase
{
    private string name = "Layout";

    public Guid Id { get; } = Guid.NewGuid();
    public ObservableCollection<PagePlotElement> Elements { get; } = new();

    public string Name
    {
        get => name;
        set => SetField(ref name, string.IsNullOrWhiteSpace(value) ? name : value.Trim(), nameof(Name));
    }
}

public sealed class PagePlotElement : NotifyBase
{
    private double x;
    private double y;
    private double size = 260;
    private string title = "Plot";
    private PlotMode plot_mode = PlotMode.Dotplot;
    private bool show_gridlines = true;
    private bool show_outlier_points = true;
    private bool show_tick_labels;
    private bool use_pseudocolor = true;
    private bool draw_large_dots;
    private bool show_gates = true;
    private bool show_gate_annotations = true;
    private bool show_gate_annotation_names;
    private int contour_level_count = 10;
    private int density_smoothing = 6;

    public Guid Id { get; } = Guid.NewGuid();
    public FlowGroup? Group { get; init; }
    public FlowSample? Sample { get; init; }
    public GateDefinition? Gate { get; init; }
    public PopulationResult? Population { get; init; }
    public AxisSettings XAxis { get; init; } = new();
    public AxisSettings YAxis { get; init; } = new();
    public DotColorSettings DotColor { get; init; } = new();

    public double X
    {
        get => x;
        set => SetField(ref x, Math.Round(value, 1), nameof(X));
    }

    public double Y
    {
        get => y;
        set => SetField(ref y, Math.Round(value, 1), nameof(Y));
    }

    public double Size
    {
        get => size;
        set => SetField(ref size, Math.Round(Math.Clamp(value, 120, 640), 1), nameof(Size));
    }

    public string Title
    {
        get => title;
        set => SetField(ref title, value ?? "", nameof(Title));
    }

    public PlotMode PlotMode
    {
        get => plot_mode;
        set => SetField(ref plot_mode, value, nameof(PlotMode));
    }

    public bool ShowGridlines
    {
        get => show_gridlines;
        set => SetField(ref show_gridlines, value, nameof(ShowGridlines));
    }

    public bool ShowOutlierPoints
    {
        get => show_outlier_points;
        set => SetField(ref show_outlier_points, value, nameof(ShowOutlierPoints));
    }

    public bool ShowTickLabels
    {
        get => show_tick_labels;
        set => SetField(ref show_tick_labels, value, nameof(ShowTickLabels));
    }

    public bool UsePseudocolor
    {
        get => use_pseudocolor;
        set => SetField(ref use_pseudocolor, value, nameof(UsePseudocolor));
    }

    public bool DrawLargeDots
    {
        get => draw_large_dots;
        set => SetField(ref draw_large_dots, value, nameof(DrawLargeDots));
    }

    public bool ShowGates
    {
        get => show_gates;
        set => SetField(ref show_gates, value, nameof(ShowGates));
    }

    public bool ShowGateAnnotations
    {
        get => show_gate_annotations;
        set => SetField(ref show_gate_annotations, value, nameof(ShowGateAnnotations));
    }

    public bool ShowGateAnnotationNames
    {
        get => show_gate_annotation_names;
        set => SetField(ref show_gate_annotation_names, value, nameof(ShowGateAnnotationNames));
    }

    public int ContourLevelCount
    {
        get => contour_level_count;
        set => SetField(ref contour_level_count, Math.Clamp(value, 2, 80), nameof(ContourLevelCount));
    }

    public int DensitySmoothing
    {
        get => density_smoothing;
        set => SetField(ref density_smoothing, Math.Clamp(value, 0, 12), nameof(DensitySmoothing));
    }

    public bool IsHistogram => PlotMode == PlotMode.Histogram || Gate?.IsOneDimensional == true;

    public Rect Bounds => new(X, Y, Size, Size);
}
