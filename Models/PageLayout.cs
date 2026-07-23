using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

public enum PageElementKind
{
    FlowPlot,
    PlatformPlot,
    StatisticTable,
    PlatformStatisticTable
}

public class PagePlotElement : NotifyBase
{
    private double x;
    private double y;
    private double size = 260;
    private double width = 260;
    private double height = 260;
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
    private PlotColorPalette density_palette = PlotColorPalette.Turbo;

    public Guid Id { get; init; } = Guid.NewGuid();
    public virtual PageElementKind ElementKind => PageElementKind.FlowPlot;
    public virtual double MinimumWidth => 120;
    public virtual double MinimumHeight => 80;
    public virtual bool HasFixedHeight => false;
    public Guid? ParentElementId { get; init; }
    public FlowGroup? Group { get; init; }
    public FlowSample? Sample { get; init; }
    public GateDefinition? Gate { get; init; }
    public PopulationResult? Population { get; init; }
    public bool UsesPopulation { get; init; }
    public PopulationRegion PopulationRegion { get; init; } = PopulationRegion.Primary;
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
        set
        {
            double value_clamped = Math.Round(Math.Clamp(value, 120, 640), 1);
            if (!SetField(ref size, value_clamped, nameof(Size)))
                return;
            width = clamp_width(value_clamped);
            height = clamp_height(value_clamped);
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
            OnPropertyChanged(nameof(Bounds));
        }
    }

    public double Width
    {
        get => width;
        set
        {
            if (SetField(ref width, clamp_width(value), nameof(Width)))
                OnPropertyChanged(nameof(Bounds));
        }
    }

    public double Height
    {
        get => height;
        set
        {
            if (SetField(ref height, clamp_height(value), nameof(Height)))
                OnPropertyChanged(nameof(Bounds));
        }
    }

    private double clamp_width(double value) => Math.Round(Math.Clamp(value, MinimumWidth, 1600), 1);

    private double clamp_height(double value)
    {
        double minimum = MinimumHeight;
        return Math.Round(HasFixedHeight ? minimum : Math.Clamp(value, minimum, 1200), 1);
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

    public PlotColorPalette DensityPalette
    {
        get => density_palette;
        set => SetField(ref density_palette, value, nameof(DensityPalette));
    }

    public bool IsHistogram => PlotMode == PlotMode.Histogram || Gate?.IsOneDimensional == true;

    public Rect Bounds => new(X, Y, Width, Height);
}

public sealed class StatisticTableElement : PagePlotElement
{
    public override PageElementKind ElementKind => PageElementKind.StatisticTable;
    public ObservableCollection<StatisticTableColumn> Columns { get; } = new();
    public override bool HasFixedHeight => true;
    public override double MinimumWidth => table_width(new[] { "Sample" }.Concat(Columns.Select(column => column.Title)));
    public override double MinimumHeight => table_height(Math.Max(1, Group?.Samples.Count ?? 0));

    private static double table_width(IEnumerable<string> columns) =>
        Math.Clamp(20 + columns.Sum(column => Math.Clamp((column?.Length ?? 0) * 7.0 + 22, 54, 190)), 160, 1600);

    private static double table_height(int rows) => 48 + (rows + 1) * 20;
}

public sealed class PlatformPlotElement : PagePlotElement
{
    public override PageElementKind ElementKind => PageElementKind.PlatformPlot;
    public override double MinimumWidth => 160;
    public override double MinimumHeight => 120;
    public Platform? Platform { get; init; }
    public string OutputKey { get; init; } = "";
}

public sealed class PlatformStatisticTableElement : PagePlotElement
{
    public override PageElementKind ElementKind => PageElementKind.PlatformStatisticTable;
    public override bool HasFixedHeight => true;
    public override double MinimumWidth
    {
        get
        {
            var table = Platform is null ? null : gated.ViewModels.Platforms.PlatformCatalog.Get(Platform.Kind).CreatePresentation(Platform).Table(OutputKey);
            if (table is null)
                return 160;
            return Math.Clamp(20 + table.Columns.Sum(column => Math.Clamp((column?.Length ?? 0) * 7.0 + 22, 54, 190)), 160, 1600);
        }
    }
    public override double MinimumHeight
    {
        get
        {
            var table = Platform is null ? null : gated.ViewModels.Platforms.PlatformCatalog.Get(Platform.Kind).CreatePresentation(Platform).Table(OutputKey);
            int rows = table?.Rows.Count ?? Math.Max(1, Platform?.PlatformStatistics.Count ?? 0);
            return 48 + (rows + 1) * 20;
        }
    }
    public Platform? Platform { get; init; }
    public string OutputKey { get; init; } = "";
}

public sealed class StatisticTableColumn
{
    public FlowGroup? Group { get; init; }
    public GateDefinition? Gate { get; init; }
    public StatisticDefinition? Statistic { get; init; }
    public string Title { get; init; } = "";
}
