using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using gated.Models;

namespace gated.Controls;

public enum HistogramAxisScaleKind
{
    Linear,
    Log,
    Logicle,
    Arcsinh
}

public sealed record HistogramRangeSelection(double Minimum, double Maximum);

public sealed record HistogramPoint(double X, double Y);

public abstract record HistogramModel
{
    public abstract double Evaluate(double x);
}

public sealed record HistogramLinearModel(double K, double B) : HistogramModel
{
    public override double Evaluate(double x) => K * x + B;
}

public sealed record HistogramGaussianModel(double Sigma, double Mu) : HistogramModel
{
    public override double Evaluate(double x) => Sigma > 0
        ? Math.Exp(-0.5 * Math.Pow((x - Mu) / Sigma, 2)) / (Sigma * Math.Sqrt(2 * Math.PI))
        : double.NaN;
}

public sealed record HistogramPolynomialModel(int Order, IReadOnlyList<double> Coefficients) : HistogramModel
{
    public override double Evaluate(double x)
    {
        if (Order < 0 || Coefficients.Count != Order + 1) return double.NaN;
        double value = 0;
        foreach (double coefficient in Coefficients) value = value * x + coefficient;
        return value;
    }
}

public sealed record HistogramGammaModel(double Shape, double Scale) : HistogramModel
{
    public override double Evaluate(double x)
    {
        if (x < 0 || Shape <= 0 || Scale <= 0) return double.NaN;
        if (x == 0) return Shape == 1 ? 1 / Scale : Shape < 1 ? double.PositiveInfinity : 0;
        double log = (Shape - 1) * Math.Log(x) - x / Scale - log_gamma(Shape) - Shape * Math.Log(Scale);
        return Math.Exp(log);
    }

    private static double log_gamma(double value)
    {
        double[] coefficients = [676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7];
        if (value < 0.5) return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * value)) - log_gamma(1 - value);
        value -= 1;
        double x = 0.99999999999980993;
        for (int index = 0; index < coefficients.Length; index++) x += coefficients[index] / (value + index + 1);
        double t = value + coefficients.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (value + 0.5) * Math.Log(t) - t + Math.Log(x);
    }
}

public sealed record HistogramHypergeometricModel(int PopulationSize, int SuccessStates, int DrawCount) : HistogramModel
{
    public override double Evaluate(double x)
    {
        int successes = (int)Math.Round(x);
        if (Math.Abs(x - successes) > 1e-6 || PopulationSize <= 0 || SuccessStates < 0 || DrawCount < 0 ||
            SuccessStates > PopulationSize || DrawCount > PopulationSize || successes < 0 ||
            successes > SuccessStates || DrawCount - successes > PopulationSize - SuccessStates) return double.NaN;
        return Math.Exp(log_combination(SuccessStates, successes) +
                        log_combination(PopulationSize - SuccessStates, DrawCount - successes) -
                        log_combination(PopulationSize, DrawCount));
    }

    private static double log_combination(int n, int k)
    {
        if (k < 0 || k > n) return double.NegativeInfinity;
        k = Math.Min(k, n - k);
        double result = 0;
        for (int index = 1; index <= k; index++) result += Math.Log(n - k + index) - Math.Log(index);
        return result;
    }
}

public sealed record HistogramLogLogisticModel(double Slope, double Upper, double Midpoint) : HistogramModel
{
    public override double Evaluate(double x)
    {
        if (x <= 0 || Slope <= 0 || Upper <= 0 || Midpoint <= 0) return double.NaN;
        double exponent = Math.Clamp(Slope * (Math.Log(x) - Math.Log(Midpoint)), -60, 60);
        return Upper / (1 + Math.Exp(exponent));
    }
}

public sealed record HistogramCalculationTerm(double Coefficient, HistogramModel Model);

public sealed record HistogramCalculationModel(IReadOnlyList<HistogramCalculationTerm> Terms) : HistogramModel
{
    public override double Evaluate(double x) => evaluate(x, new HashSet<HistogramModel>(ReferenceEqualityComparer.Instance));

    public IReadOnlyList<HistogramCalculationTerm> ResolveElementary()
    {
        var result = new List<HistogramCalculationTerm>();
        flatten(1, this, result, new HashSet<HistogramModel>(ReferenceEqualityComparer.Instance));
        return result;
    }

    private static void flatten(double coefficient, HistogramModel model, ICollection<HistogramCalculationTerm> result, HashSet<HistogramModel> path)
    {
        if (!path.Add(model)) throw new InvalidOperationException("Histogram calculation models cannot contain recursive dependencies.");
        if (model is HistogramCalculationModel calculation)
            foreach (var term in calculation.Terms) flatten(coefficient * term.Coefficient, term.Model, result, path);
        else
            result.Add(new HistogramCalculationTerm(coefficient, model));
        path.Remove(model);
    }

    private double evaluate(double x, HashSet<HistogramModel> path)
    {
        if (!path.Add(this)) return double.NaN;
        double value = 0;
        foreach (var term in Terms)
        {
            double item = term.Model is HistogramCalculationModel calculation
                ? calculation.evaluate(x, path)
                : term.Model.Evaluate(x);
            if (!double.IsFinite(item) || !double.IsFinite(term.Coefficient)) { path.Remove(this); return double.NaN; }
            value += term.Coefficient * item;
        }
        path.Remove(this);
        return value;
    }
}

public sealed class HistogramModelLayer : NotifyBase
{
    private HistogramModel model = new HistogramLinearModel(0, 0);
    private Color color = gated.Shared.ThemeResources.AppColor("Theme5");
    private string name = "";
    private double thickness = 1.8;
    private HistogramAxisScaleKind x_input_scale = HistogramAxisScaleKind.Linear;
    private double x_arcsinh_cofactor = 5.0;
    public HistogramModel Model { get => model; set => SetField(ref model, value ?? new HistogramLinearModel(0, 0)); }
    public Color Color { get => color; set => SetField(ref color, value); }
    public string Name { get => name; set => SetField(ref name, value ?? ""); }
    public double Thickness { get => thickness; set => SetField(ref thickness, Math.Max(0.5, value)); }
    public HistogramAxisScaleKind XInputScale { get => x_input_scale; set => SetField(ref x_input_scale, value); }
    public double XArcsinhCofactor { get => x_arcsinh_cofactor; set => SetField(ref x_arcsinh_cofactor, value > 0 && double.IsFinite(value) ? value : 5.0); }

    public double TransformInput(double raw_value) => XInputScale switch
    {
        HistogramAxisScaleKind.Log => Math.Sign(raw_value) * Math.Log10(1 + Math.Abs(raw_value)),
        HistogramAxisScaleKind.Arcsinh => Math.Asinh(raw_value / XArcsinhCofactor),
        _ => raw_value
    };
}

public sealed class HistogramCurveSeries : NotifyBase
{
    private IReadOnlyList<HistogramPoint> points = [];
    private Color color = gated.Shared.ThemeResources.AppColor("Theme6");
    private string name = "";
    private double thickness = 1.6;
    private bool is_dashed;
    private double fill_opacity;
    public IReadOnlyList<HistogramPoint> Points { get => points; set => SetField(ref points, value ?? []); }
    public Color Color { get => color; set => SetField(ref color, value); }
    public string Name { get => name; set => SetField(ref name, value ?? ""); }
    public double Thickness { get => thickness; set => SetField(ref thickness, Math.Max(0.5, value)); }
    public bool IsDashed { get => is_dashed; set => SetField(ref is_dashed, value); }
    public double FillOpacity { get => fill_opacity; set => SetField(ref fill_opacity, Math.Clamp(value, 0, 1)); }
}

public enum HistogramAnnotationOrientation { Vertical, Horizontal }

public sealed class HistogramLineAnnotation : NotifyBase
{
    private double value;
    private string text = "";
    private Color color = gated.Shared.ThemeResources.AppColor("Theme6");
    private bool is_editable;
    public HistogramAnnotationOrientation Orientation { get; init; }
    public double Value { get => value; set => SetField(ref this.value, value); }
    public string Text { get => text; set => SetField(ref text, value ?? ""); }
    public Color Color { get => color; set => SetField(ref color, value); }
    public bool IsEditable { get => is_editable; set => SetField(ref is_editable, value); }
    public bool IsDashed { get; set; } = true;
}

public sealed class HistogramSeries : NotifyBase
{
    private IReadOnlyList<double> values = [];
    private IReadOnlyList<double>? sorted_values;
    private int bin_count = 256;
    private Color color = gated.Shared.ThemeResources.AppColor("Theme4");
    private string name = "";

    public IReadOnlyList<double> Values
    {
        get => values;
        set
        {
            if (SetField(ref values, value ?? []))
                SortedValues = null;
        }
    }

    public IReadOnlyList<double>? SortedValues
    {
        get => sorted_values;
        set => SetField(ref sorted_values, value);
    }

    public int BinCount
    {
        get => bin_count;
        set => SetField(ref bin_count, Math.Max(1, value));
    }

    public Color Color
    {
        get => color;
        set => SetField(ref color, value);
    }

    public string Name
    {
        get => name;
        set => SetField(ref name, value ?? "");
    }
}

public sealed class HistogramView : Control
{
    public static readonly StyledProperty<IReadOnlyList<HistogramSeries>?> SeriesProperty =
        AvaloniaProperty.Register<HistogramView, IReadOnlyList<HistogramSeries>?>(nameof(Series));

    public static readonly StyledProperty<double?> MinimumProperty =
        AvaloniaProperty.Register<HistogramView, double?>(nameof(Minimum));

    public static readonly StyledProperty<double?> MaximumProperty =
        AvaloniaProperty.Register<HistogramView, double?>(nameof(Maximum));

    public static readonly StyledProperty<HistogramAxisScaleKind> AxisScaleProperty =
        AvaloniaProperty.Register<HistogramView, HistogramAxisScaleKind>(nameof(AxisScale), HistogramAxisScaleKind.Linear);

    public static readonly StyledProperty<double> LogicleTopOfScaleProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleTopOfScale), new LogicleParameters().T);

    public static readonly StyledProperty<double> LogicleLinearizationWidthProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleLinearizationWidth), new LogicleParameters().W);

    public static readonly StyledProperty<double> LogicleDecadesProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleDecades), new LogicleParameters().M);

    public static readonly StyledProperty<double> LogicleNegativeDecadesProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(LogicleNegativeDecades), new LogicleParameters().A);

    public static readonly StyledProperty<double> ArcsinhCofactorProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(ArcsinhCofactor), 5.0);

    public static readonly StyledProperty<int> BinSmoothingProperty =
        AvaloniaProperty.Register<HistogramView, int>(nameof(BinSmoothing), 2);

    public static readonly StyledProperty<double> YMaximumFactorProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(YMaximumFactor), 1.1);

    public static readonly StyledProperty<bool> IsGatingEnabledProperty =
        AvaloniaProperty.Register<HistogramView, bool>(nameof(IsGatingEnabled));

    public static readonly StyledProperty<HistogramRangeSelection?> SelectionProperty =
        AvaloniaProperty.Register<HistogramView, HistogramRangeSelection?>(nameof(Selection), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyList<HistogramCurveSeries>?> CurvesProperty =
        AvaloniaProperty.Register<HistogramView, IReadOnlyList<HistogramCurveSeries>?>(nameof(Curves));
    public static readonly StyledProperty<IReadOnlyList<HistogramModelLayer>?> ModelsProperty =
        AvaloniaProperty.Register<HistogramView, IReadOnlyList<HistogramModelLayer>?>(nameof(Models));
    public static readonly StyledProperty<IReadOnlyList<HistogramLineAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<HistogramView, IReadOnlyList<HistogramLineAnnotation>?>(nameof(Annotations));
    public static readonly StyledProperty<double> YMinimumProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(YMinimum), 0);
    public static readonly StyledProperty<double> YMaximumProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(YMaximum), 1.1);
    public static readonly StyledProperty<HistogramAxisScaleKind> YAxisScaleProperty =
        AvaloniaProperty.Register<HistogramView, HistogramAxisScaleKind>(nameof(YAxisScale), HistogramAxisScaleKind.Linear);
    public static readonly StyledProperty<string> XTitleProperty =
        AvaloniaProperty.Register<HistogramView, string>(nameof(XTitle), "");
    public static readonly StyledProperty<string> YTitleProperty =
        AvaloniaProperty.Register<HistogramView, string>(nameof(YTitle), "Normalized Frequency");
    public static readonly StyledProperty<ICommand?> AnnotationCommittedCommandProperty =
        AvaloniaProperty.Register<HistogramView, ICommand?>(nameof(AnnotationCommittedCommand));

    private Rect plot_rect;
    private List<PreparedSeries>? prepared_series;
    private PreparedAxis? prepared_axis;
    private INotifyCollectionChanged? subscribed_series_collection;
    private readonly List<HistogramSeries> subscribed_series = new();
    private DragTarget drag_target = DragTarget.None;
    private HistogramRangeSelection? draft_selection;
    private HistogramLineAnnotation? dragged_annotation;
    private readonly List<INotifyPropertyChanged> subscribed_layers = new();
    private readonly List<INotifyCollectionChanged> subscribed_layer_collections = new();

    static HistogramView()
    {
        AffectsRender<HistogramView>(
            SeriesProperty,
            MinimumProperty,
            MaximumProperty,
            AxisScaleProperty,
            LogicleTopOfScaleProperty,
            LogicleLinearizationWidthProperty,
            LogicleDecadesProperty,
            LogicleNegativeDecadesProperty,
            ArcsinhCofactorProperty,
            BinSmoothingProperty,
            YMaximumFactorProperty,
            IsGatingEnabledProperty,
            SelectionProperty,
            CurvesProperty,
            ModelsProperty,
            AnnotationsProperty,
            YMinimumProperty,
            YMaximumProperty,
            YAxisScaleProperty,
            XTitleProperty,
            YTitleProperty);
    }

    public IReadOnlyList<HistogramSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double? Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double? Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public HistogramAxisScaleKind AxisScale
    {
        get => GetValue(AxisScaleProperty);
        set => SetValue(AxisScaleProperty, value);
    }

    public double LogicleTopOfScale
    {
        get => GetValue(LogicleTopOfScaleProperty);
        set => SetValue(LogicleTopOfScaleProperty, value);
    }

    public double LogicleLinearizationWidth
    {
        get => GetValue(LogicleLinearizationWidthProperty);
        set => SetValue(LogicleLinearizationWidthProperty, value);
    }

    public double LogicleDecades
    {
        get => GetValue(LogicleDecadesProperty);
        set => SetValue(LogicleDecadesProperty, value);
    }

    public double LogicleNegativeDecades
    {
        get => GetValue(LogicleNegativeDecadesProperty);
        set => SetValue(LogicleNegativeDecadesProperty, value);
    }

    public double ArcsinhCofactor
    {
        get => GetValue(ArcsinhCofactorProperty);
        set => SetValue(ArcsinhCofactorProperty, value);
    }

    public int BinSmoothing
    {
        get => GetValue(BinSmoothingProperty);
        set => SetValue(BinSmoothingProperty, value);
    }

    public double YMaximumFactor
    {
        get => GetValue(YMaximumFactorProperty);
        set => SetValue(YMaximumFactorProperty, value);
    }

    public bool IsGatingEnabled
    {
        get => GetValue(IsGatingEnabledProperty);
        set => SetValue(IsGatingEnabledProperty, value);
    }

    public HistogramRangeSelection? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    public IReadOnlyList<HistogramCurveSeries>? Curves { get => GetValue(CurvesProperty); set => SetValue(CurvesProperty, value); }
    public IReadOnlyList<HistogramModelLayer>? Models { get => GetValue(ModelsProperty); set => SetValue(ModelsProperty, value); }
    public IReadOnlyList<HistogramLineAnnotation>? Annotations { get => GetValue(AnnotationsProperty); set => SetValue(AnnotationsProperty, value); }
    public double YMinimum { get => GetValue(YMinimumProperty); set => SetValue(YMinimumProperty, value); }
    public double YMaximum { get => GetValue(YMaximumProperty); set => SetValue(YMaximumProperty, value); }
    public HistogramAxisScaleKind YAxisScale { get => GetValue(YAxisScaleProperty); set => SetValue(YAxisScaleProperty, value); }
    public string XTitle { get => GetValue(XTitleProperty); set => SetValue(XTitleProperty, value); }
    public string YTitle { get => GetValue(YTitleProperty); set => SetValue(YTitleProperty, value); }
    public ICommand? AnnotationCommittedCommand { get => GetValue(AnnotationCommittedCommandProperty); set => SetValue(AnnotationCommittedCommandProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 140 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeriesProperty)
            resubscribe_series();
        if (change.Property == CurvesProperty || change.Property == ModelsProperty || change.Property == AnnotationsProperty)
            resubscribe_layers();
        if (change.Property == SeriesProperty
            || change.Property == MinimumProperty
            || change.Property == MaximumProperty
            || change.Property == AxisScaleProperty
            || change.Property == LogicleTopOfScaleProperty
            || change.Property == LogicleLinearizationWidthProperty
            || change.Property == LogicleDecadesProperty
            || change.Property == LogicleNegativeDecadesProperty
            || change.Property == ArcsinhCofactorProperty
            || change.Property == BinSmoothingProperty
            || change.Property == YMaximumFactorProperty
            || change.Property == YMinimumProperty
            || change.Property == YMaximumProperty
            || change.Property == YAxisScaleProperty)
            invalidate_bins();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!plot_rect.Contains(e.GetPosition(this)))
            return;

        var point = e.GetPosition(this);
        dragged_annotation = nearest_editable_annotation(point.X);
        if (dragged_annotation is not null)
        {
            dragged_annotation.Value = screen_to_data(point.X);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!IsGatingEnabled)
            return;
        double value = screen_to_data(point.X);
        drag_target = nearest_selection_edge(point.X);
        if (drag_target == DragTarget.None)
        {
            draft_selection = new HistogramRangeSelection(value, value);
            drag_target = DragTarget.Maximum;
        }
        else
        {
            draft_selection = Selection;
        }
        update_selection(value);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (dragged_annotation is not null)
        {
            dragged_annotation.Value = screen_to_data(point.X);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (drag_target != DragTarget.None)
        {
            update_selection(screen_to_data(point.X));
            e.Handled = true;
            return;
        }

        Cursor = plot_rect.Contains(point) && (nearest_editable_annotation(point.X) is not null ||
                 IsGatingEnabled && nearest_selection_edge(point.X) != DragTarget.None)
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragged_annotation is { } annotation)
        {
            dragged_annotation = null;
            if (AnnotationCommittedCommand?.CanExecute(annotation) == true)
                AnnotationCommittedCommand.Execute(annotation);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (draft_selection is { } selection)
            Selection = normalized_selection(selection);
        draft_selection = null;
        drag_target = DragTarget.None;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;

        const double left_axis_space = 72;
        const double right_space = 18;
        const double top_space = 10;
        const double bottom_axis_space = 42;
        plot_rect = new Rect(
            bounds.Left + left_axis_space,
            bounds.Top + top_space,
            Math.Max(1, bounds.Width - left_axis_space - right_space),
            Math.Max(1, bounds.Height - top_space - bottom_axis_space));

        context.FillRectangle(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background4")), plot_rect);
        draw_grid(context);
        draw_series(context);
        draw_curves(context);
        draw_models(context);
        draw_selection(context);
        draw_annotations(context);
        draw_axes(context);
    }

    private void draw_series(DrawingContext context)
    {
        var series = prepared_series ??= prepare_series();
        foreach (var item in series)
        {
            if (item.Points.Count == 0)
                continue;

            var points = item.Points.Select(point => new Point(data_to_screen(point.X), y_data_to_screen(point.Y))).ToArray();

            var fill = new SolidColorBrush(Color.FromArgb(42, item.Color.R, item.Color.G, item.Color.B));
            var stroke = new Pen(new SolidColorBrush(item.Color), 1.8);
            var fill_geometry = new StreamGeometry();
            using (var geometry = fill_geometry.Open())
            {
                geometry.BeginFigure(new Point(points[0].X, y_data_to_screen(0)), true);
                foreach (var point in points)
                    geometry.LineTo(point);
                geometry.LineTo(new Point(points[^1].X, y_data_to_screen(0)));
                geometry.EndFigure(true);
            }
            context.DrawGeometry(fill, null, fill_geometry);

            var line_geometry = new StreamGeometry();
            using (var geometry = line_geometry.Open())
            {
                geometry.BeginFigure(points[0], false);
                foreach (var point in points.Skip(1))
                    geometry.LineTo(point);
            }
            context.DrawGeometry(null, stroke, line_geometry);
        }
    }

    private List<PreparedSeries> prepare_series()
    {
        var result = new List<PreparedSeries>();
        if (Series is null || prepare_axis() is not { } axis)
            return result;

        double transformed_span = axis.TransformedMaximum - axis.TransformedMinimum;
        if (transformed_span <= 0)
            return result;

        foreach (var source in Series)
        {
            int bins = Math.Max(1, source.BinCount);
            var counts = new double[bins];
            foreach (double value in source.Values)
            {
                if (!double.IsFinite(value) || !try_transform(value, out double transformed))
                    continue;

                double normalized = (transformed - axis.TransformedMinimum) / transformed_span;
                if (normalized < 0 || normalized > 1)
                    continue;

                int bin = Math.Clamp((int)(normalized * bins), 0, bins - 1);
                counts[bin]++;
            }

            counts = smooth(counts, BinSmoothing);
            double maximum = counts.DefaultIfEmpty(0).Max();
            var points = new List<Point>(bins);
            if (maximum > 0)
            {
                for (int bin = 0; bin < bins; bin++)
                {
                    double normalized_x = (bin + 0.5) / bins;
                    double normalized_y = counts[bin] / maximum;
                    double transformed = axis.TransformedMinimum + normalized_x * transformed_span;
                    double raw_x = axis.Scale.InverseTransform(transformed);
                    points.Add(new Point(raw_x, normalized_y));
                }
            }

            result.Add(new PreparedSeries(source.Color, points));
        }

        return result;
    }

    private void draw_curves(DrawingContext context)
    {
        if (Curves is null) return;
        foreach (var curve in Curves)
            draw_xy_line(context, curve.Points, curve.Color, curve.Thickness, curve.IsDashed, curve.FillOpacity);
    }

    private void draw_models(DrawingContext context)
    {
        if (Models is null || prepare_axis() is not { } axis) return;
        foreach (var layer in Models)
        {
            var points = new List<HistogramPoint>();
            for (int index = 0; index <= 320; index++)
            {
                double transformed = axis.TransformedMinimum + (axis.TransformedMaximum - axis.TransformedMinimum) * index / 320.0;
                double x = axis.Scale.InverseTransform(transformed);
                double y;
                try { y = layer.Model.Evaluate(layer.TransformInput(x)); }
                catch { y = double.NaN; }
                if (double.IsFinite(x) && double.IsFinite(y)) points.Add(new HistogramPoint(x, y));
                else if (points.Count > 1) { draw_xy_line(context, points, layer.Color, layer.Thickness); points.Clear(); }
                else points.Clear();
            }
            if (points.Count > 1) draw_xy_line(context, points, layer.Color, layer.Thickness);
        }
    }

    private void draw_xy_line(DrawingContext context, IReadOnlyList<HistogramPoint> source, Color color, double thickness, bool dashed = false, double fill_opacity = 0)
    {
        var finite = source.Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).ToArray();
        if (finite.Length < 2) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(data_to_screen(finite[0].X), y_data_to_screen(finite[0].Y)), fill_opacity > 0);
            foreach (var point in finite.Skip(1)) path.LineTo(new Point(data_to_screen(point.X), y_data_to_screen(point.Y)));
            if (fill_opacity > 0)
            {
                path.LineTo(new Point(data_to_screen(finite[^1].X), y_data_to_screen(YMinimum)));
                path.LineTo(new Point(data_to_screen(finite[0].X), y_data_to_screen(YMinimum)));
                path.EndFigure(true);
            }
        }
        var fill = fill_opacity > 0
            ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * fill_opacity), color.R, color.G, color.B))
            : null;
        context.DrawGeometry(fill, new Pen(new SolidColorBrush(color), thickness, dashed ? DashStyle.Dash : null), geometry);
    }

    private void draw_annotations(DrawingContext context)
    {
        if (Annotations is null) return;
        foreach (var annotation in Annotations)
        {
            var pen = new Pen(new SolidColorBrush(annotation.Color), annotation.IsEditable ? 2 : 1.4,
                annotation.IsDashed ? DashStyle.Dash : null);
            if (annotation.Orientation == HistogramAnnotationOrientation.Vertical)
            {
                double x = data_to_screen(annotation.Value);
                context.DrawLine(pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
                if (!string.IsNullOrWhiteSpace(annotation.Text))
                    draw_centered_text(context, annotation.Text, new Point(x, plot_rect.Top + 3), 11, annotation.Color);
            }
            else
            {
                double y = y_data_to_screen(annotation.Value);
                context.DrawLine(pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
                if (!string.IsNullOrWhiteSpace(annotation.Text))
                    context.DrawText(create_text(annotation.Text, 11, annotation.Color), new Point(plot_rect.Left + 4, y - 16));
            }
        }
    }

    private void draw_selection(DrawingContext context)
    {
        if ((draft_selection ?? Selection) is not { } selection)
            return;

        var normalized = normalized_selection(selection);
        double left = data_to_screen(normalized.Minimum);
        double right = data_to_screen(normalized.Maximum);
        var fill = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayThemeSelection"));
        var pen = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme6")), 1.4, DashStyle.Dash);
        context.FillRectangle(fill, new Rect(left, plot_rect.Top, Math.Max(0, right - left), plot_rect.Height));
        context.DrawLine(pen, new Point(left, plot_rect.Top), new Point(left, plot_rect.Bottom));
        context.DrawLine(pen, new Point(right, plot_rect.Top), new Point(right, plot_rect.Bottom));

        if (!IsGatingEnabled)
            return;

        var center_y = plot_rect.Top + plot_rect.Height / 2;
        var handle_pen = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Theme6")), 1.6);
        context.DrawLine(handle_pen, new Point(left, center_y), new Point(right, center_y));
        draw_selection_handle(context, left, center_y, handle_pen);
        draw_selection_handle(context, right, center_y, handle_pen);
    }

    private static void draw_selection_handle(DrawingContext context, double x, double y, Pen pen)
    {
        var rect = new Rect(x - 5, y - 5, 10, 10);
        context.FillRectangle(Brushes.White, rect);
        context.DrawRectangle(null, pen, rect);
    }

    private void draw_grid(DrawingContext context)
    {
        var major_grid_pen = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMajor")), 1);
        var minor_grid_pen = new Pen(new SolidColorBrush(gated.Shared.ThemeResources.AppColor("OverlayGridMinor")), 1);
        for (int index = 1; index < 5; index++)
        {
            double y = plot_rect.Bottom - plot_rect.Height * index / 5.0;
            context.DrawLine(index == 5 ? major_grid_pen : minor_grid_pen, new Point(plot_rect.Left, y), new Point(plot_rect.Right, y));
        }

        foreach (double tick in minor_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(minor_grid_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }

        foreach (double tick in major_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(major_grid_pen, new Point(x, plot_rect.Top), new Point(x, plot_rect.Bottom));
        }
    }

    private void draw_axes(DrawingContext context)
    {
        var axis_color = gated.Shared.ThemeResources.AppColor("Text5");
        var text_color = gated.Shared.ThemeResources.AppColor("Text1");
        var tick_text = gated.Shared.ThemeResources.AppColor("Text5");
        var axis_pen = new Pen(new SolidColorBrush(axis_color), 1);
        var tick_pen = new Pen(new SolidColorBrush(axis_color), 1);
        context.DrawLine(axis_pen, new Point(plot_rect.Left, plot_rect.Bottom), new Point(plot_rect.Right, plot_rect.Bottom));
        context.DrawLine(axis_pen, new Point(plot_rect.Left, plot_rect.Top), new Point(plot_rect.Left, plot_rect.Bottom));

        foreach (double tick in minor_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(tick_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 4));
        }

        foreach (double tick in major_axis_ticks())
        {
            double x = data_to_screen(tick);
            context.DrawLine(tick_pen, new Point(x, plot_rect.Bottom), new Point(x, plot_rect.Bottom + 6));
            draw_centered_text(context, format_axis_value(tick), new Point(x, plot_rect.Bottom + 8), 11, tick_text);
        }

        for (int index = 0; index <= 5; index++)
        {
            double value = YMinimum + (YMaximum - YMinimum) * index / 5.0;
            double y = y_data_to_screen(value);
            context.DrawLine(tick_pen, new Point(plot_rect.Left - 6, y), new Point(plot_rect.Left, y));
            draw_right_aligned_text(context, format_axis_value(value), new Point(plot_rect.Left - 8, y - 7), 11, tick_text);
        }

        string x_title = string.IsNullOrWhiteSpace(XTitle) ? Series?.FirstOrDefault()?.Name ?? "" : XTitle;
        if (!string.IsNullOrWhiteSpace(x_title))
            draw_centered_text(context, x_title, new Point(plot_rect.Left + plot_rect.Width / 2, Bounds.Bottom - 15), 12, text_color);
        if (!string.IsNullOrWhiteSpace(YTitle))
            draw_vertical_centered_text(context, YTitle, new Point(Bounds.Left + 18, plot_rect.Top + plot_rect.Height / 2), 12, text_color);
    }

    private IEnumerable<double> major_axis_ticks()
    {
        if (prepare_axis() is not { } prepared || prepared.Maximum <= prepared.Minimum)
            yield break;

        var axis = new AxisSettings
        {
            Minimum = prepared.Minimum,
            Maximum = prepared.Maximum,
            ScaleKind = coordinate_scale_kind()
        };

        axis.Scale.Logicle = new LogicleParameters(
            LogicleTopOfScale,
            LogicleLinearizationWidth,
            LogicleDecades,
            LogicleNegativeDecades);
        axis.ArcsinhCofactor = ArcsinhCofactor;

        foreach (double value in Configuration.MajorAxisTicks(axis))
            if (value >= prepared.Minimum && value <= prepared.Maximum)
                yield return value;
    }

    private IEnumerable<double> minor_axis_ticks()
    {
        if (prepare_axis() is not { } prepared || prepared.Maximum <= prepared.Minimum)
            yield break;

        var axis = new AxisSettings
        {
            Minimum = prepared.Minimum,
            Maximum = prepared.Maximum,
            ScaleKind = coordinate_scale_kind()
        };
        axis.Scale.Logicle = new LogicleParameters(
            LogicleTopOfScale,
            LogicleLinearizationWidth,
            LogicleDecades,
            LogicleNegativeDecades);
        axis.ArcsinhCofactor = ArcsinhCofactor;

        foreach (double value in Configuration.MinorAxisTicks(axis))
            if (value >= prepared.Minimum && value <= prepared.Maximum)
                yield return value;
    }

    private double data_to_screen(double value)
    {
        if (prepare_axis() is not { } axis || !try_transform(value, out double transformed))
            return plot_rect.Left;

        double span = axis.TransformedMaximum - axis.TransformedMinimum;
        if (span <= 0)
            return plot_rect.Left;
        return plot_rect.Left + Math.Clamp((transformed - axis.TransformedMinimum) / span, 0, 1) * plot_rect.Width;
    }

    private double y_data_to_screen(double value)
    {
        var scale = y_scale();
        double minimum = scale.Transform(YMinimum);
        double maximum = scale.Transform(YMaximum);
        double transformed = scale.Transform(value);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || !double.IsFinite(transformed) || maximum <= minimum)
            return plot_rect.Bottom;
        return plot_rect.Bottom - Math.Clamp((transformed - minimum) / (maximum - minimum), 0, 1) * plot_rect.Height;
    }

    private AxisScale y_scale() => new()
    {
        Kind = YAxisScale switch
        {
            HistogramAxisScaleKind.Log => CoordinateScaleKind.Logarithmic,
            HistogramAxisScaleKind.Logicle => CoordinateScaleKind.Logicle,
            HistogramAxisScaleKind.Arcsinh => CoordinateScaleKind.Arcsinh,
            _ => CoordinateScaleKind.Linear
        },
        Logicle = new LogicleParameters(LogicleTopOfScale, LogicleLinearizationWidth, LogicleDecades, LogicleNegativeDecades),
        ArcsinhCofactor = ArcsinhCofactor
    };

    private double screen_to_data(double x)
    {
        if (prepare_axis() is not { } axis)
            return effective_minimum();

        double normalized = Math.Clamp((x - plot_rect.Left) / plot_rect.Width, 0, 1);
        double transformed = axis.TransformedMinimum + normalized * (axis.TransformedMaximum - axis.TransformedMinimum);
        return try_inverse_transform(transformed, out double value) ? value : effective_minimum();
    }

    private PreparedAxis? prepare_axis()
    {
        if (prepared_axis is not null)
            return prepared_axis;

        var (minimum, maximum) = effective_bounds();
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return null;
        var scale = shared_scale();
        double transformed_minimum;
        double transformed_maximum;
        if (maximum <= minimum)
        {
            transformed_minimum = scale.Transform(minimum - 0.5);
            transformed_maximum = scale.Transform(maximum + 0.5);
            if (transformed_maximum <= transformed_minimum)
                return null;
            return prepared_axis = new PreparedAxis(minimum, maximum, transformed_minimum, transformed_maximum, scale);
        }
        transformed_minimum = scale.Transform(minimum);
        transformed_maximum = scale.Transform(maximum);
        if (!double.IsFinite(transformed_minimum) || !double.IsFinite(transformed_maximum) || transformed_maximum <= transformed_minimum)
            return null;
        return prepared_axis = new PreparedAxis(minimum, maximum, transformed_minimum, transformed_maximum, scale);
    }

    private bool try_transform(double value, out double transformed)
    {
        transformed = value;
        if (!double.IsFinite(value))
            return false;

        transformed = (prepared_axis?.Scale ?? shared_scale()).Transform(value);
        return double.IsFinite(transformed);
    }

    private bool try_inverse_transform(double transformed, out double value)
    {
        value = transformed;
        if (!double.IsFinite(transformed))
            return false;
        value = (prepared_axis?.Scale ?? shared_scale()).InverseTransform(transformed);
        return double.IsFinite(value);
    }

    private AxisScale shared_scale() => new()
    {
        Kind = coordinate_scale_kind(),
        Logicle = new LogicleParameters(LogicleTopOfScale, LogicleLinearizationWidth, LogicleDecades, LogicleNegativeDecades),
        ArcsinhCofactor = ArcsinhCofactor
    };

    private CoordinateScaleKind coordinate_scale_kind() => AxisScale switch
    {
        HistogramAxisScaleKind.Log => CoordinateScaleKind.Logarithmic,
        HistogramAxisScaleKind.Logicle => CoordinateScaleKind.Logicle,
        HistogramAxisScaleKind.Arcsinh => CoordinateScaleKind.Arcsinh,
        _ => CoordinateScaleKind.Linear
    };

    private void update_selection(double value)
    {
        var current = draft_selection ?? Selection;
        if (current is not { } selection)
            return;

        draft_selection = drag_target switch
        {
            DragTarget.Minimum => new HistogramRangeSelection(value, selection.Maximum),
            DragTarget.Maximum => new HistogramRangeSelection(selection.Minimum, value),
            _ => selection
        };
        InvalidateVisual();
    }

    private DragTarget nearest_selection_edge(double x)
    {
        if (Selection is not { } selection)
            return DragTarget.None;

        var normalized = normalized_selection(selection);
        double left = data_to_screen(normalized.Minimum);
        double right = data_to_screen(normalized.Maximum);
        double left_distance = Math.Abs(x - left);
        double right_distance = Math.Abs(x - right);
        double best = Math.Min(left_distance, right_distance);
        if (best > 12)
            return DragTarget.None;
        return left_distance <= right_distance ? DragTarget.Minimum : DragTarget.Maximum;
    }

    private HistogramLineAnnotation? nearest_editable_annotation(double x) =>
        Annotations?
            .Where(annotation => annotation.IsEditable && annotation.Orientation == HistogramAnnotationOrientation.Vertical)
            .Select(annotation => (Annotation: annotation, Distance: Math.Abs(data_to_screen(annotation.Value) - x)))
            .Where(item => item.Distance <= 12)
            .OrderBy(item => item.Distance)
            .Select(item => item.Annotation)
            .FirstOrDefault();

    private static HistogramRangeSelection normalized_selection(HistogramRangeSelection selection) =>
        selection.Minimum <= selection.Maximum
            ? selection
            : new HistogramRangeSelection(selection.Maximum, selection.Minimum);

    private double effective_minimum() => prepare_axis()?.Minimum ?? effective_bounds().Minimum;

    private (double Minimum, double Maximum) effective_bounds()
    {
        var finite = finite_sorted_values();
        if (finite.Length == 0)
            return (Minimum ?? 0, Maximum ?? 1);
        double observed_minimum = percentile_in_sorted(finite, 0.0001);
        double observed_maximum = percentile_in_sorted(finite, 0.999);
        double minimum = Minimum is { } requested_minimum && double.IsFinite(requested_minimum)
            ? requested_minimum
            : observed_minimum;
        double maximum = Maximum is { } requested_maximum && double.IsFinite(requested_maximum)
            ? requested_maximum
            : observed_maximum;
        if (maximum < minimum)
            (minimum, maximum) = (maximum, minimum);
        return (minimum, maximum);
    }

    private double[] finite_sorted_values()
    {
        if (Series is null || Series.Count == 0)
            return [];

        if (Series.Count == 1 && Series[0].SortedValues is { } sorted)
        {
            if (sorted is double[] array)
                return array;
            return sorted.ToArray();
        }

        var finite = Series.SelectMany(item => item.SortedValues ?? item.Values)
            .Where(double.IsFinite)
            .ToArray();
        Array.Sort(finite);
        return finite;
    }

    private static double percentile_in_sorted(double[] values, double quantile)
    {
        if (values.Length == 1)
            return values[0];
        double position = Math.Clamp(quantile, 0, 1) * (values.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(values.Length - 1, lower + 1);
        double fraction = position - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }

    private static double[] smooth(double[] source, int passes)
    {
        passes = Math.Clamp(passes, 0, 16);
        var result = source;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = new double[result.Length];
            for (int index = 0; index < result.Length; index++)
            {
                double total = result[index] * 2;
                double weight = 2;
                if (index > 0)
                {
                    total += result[index - 1];
                    weight += 1;
                }
                if (index + 1 < result.Length)
                {
                    total += result[index + 1];
                    weight += 1;
                }
                next[index] = total / weight;
            }
            result = next;
        }

        return result;
    }

    private void invalidate_bins()
    {
        prepared_series = null;
        prepared_axis = null;
        InvalidateVisual();
    }

    private void resubscribe_series()
    {
        if (subscribed_series_collection is not null)
            subscribed_series_collection.CollectionChanged -= on_series_collection_changed;
        foreach (var series in subscribed_series)
            series.PropertyChanged -= on_series_property_changed;

        subscribed_series.Clear();
        subscribed_series_collection = Series as INotifyCollectionChanged;
        if (subscribed_series_collection is not null)
            subscribed_series_collection.CollectionChanged += on_series_collection_changed;

        if (Series is null)
            return;
        foreach (var series in Series)
        {
            subscribed_series.Add(series);
            series.PropertyChanged += on_series_property_changed;
        }
    }

    private void resubscribe_layers()
    {
        foreach (var collection in subscribed_layer_collections) collection.CollectionChanged -= on_layer_collection_changed;
        foreach (var layer in subscribed_layers) layer.PropertyChanged -= on_layer_property_changed;
        subscribed_layer_collections.Clear();
        subscribed_layers.Clear();
        subscribe_layer_collection(Curves);
        subscribe_layer_collection(Models);
        subscribe_layer_collection(Annotations);
    }

    private void subscribe_layer_collection<T>(IReadOnlyList<T>? values) where T : class, INotifyPropertyChanged
    {
        if (values is INotifyCollectionChanged collection)
        {
            subscribed_layer_collections.Add(collection);
            collection.CollectionChanged += on_layer_collection_changed;
        }
        if (values is null) return;
        foreach (var value in values)
        {
            subscribed_layers.Add(value);
            value.PropertyChanged += on_layer_property_changed;
        }
    }

    private void on_layer_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        resubscribe_layers();
        InvalidateVisual();
    }

    private void on_layer_property_changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void on_series_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        resubscribe_series();
        invalidate_bins();
    }

    private void on_series_property_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HistogramSeries.Values) or nameof(HistogramSeries.SortedValues) or nameof(HistogramSeries.BinCount))
            invalidate_bins();
        else
            InvalidateVisual();
    }

    private void draw_centered_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width / 2, origin.Y));
    }

    private void draw_right_aligned_text(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        context.DrawText(formatted, new Point(origin.X - formatted.Width, origin.Y));
    }

    private void draw_vertical_centered_text(DrawingContext context, string text, Point center, double size, Color color)
    {
        var formatted = create_text(text, size, color);
        using (context.PushTransform(Matrix.CreateTranslation(-formatted.Width / 2, -formatted.Height / 2) *
                                     Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
            context.DrawText(formatted, new Point());
    }

    private FormattedText create_text(string text, double size, Color color) =>
        new(
            text ?? "",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this)),
            size,
            new SolidColorBrush(color));

    private static string format_axis_value(double value)
    {
        double absolute = Math.Abs(value);
        if (absolute >= 10000 || absolute is > 0 and < 0.01)
            return value.ToString("0.##E0", CultureInfo.InvariantCulture);
        if (absolute >= 100)
            return value.ToString("0", CultureInfo.InvariantCulture);
        if (absolute >= 10)
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private sealed record PreparedSeries(Color Color, List<Point> Points);
    private sealed record PreparedAxis(double Minimum, double Maximum, double TransformedMinimum, double TransformedMaximum, AxisScale Scale);

    private enum DragTarget
    {
        None,
        Minimum,
        Maximum
    }
}
