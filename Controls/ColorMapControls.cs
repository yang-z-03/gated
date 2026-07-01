using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using gated.Models;

namespace gated.Controls;

public sealed class ColorMapPreview : Control
{
    public static readonly StyledProperty<PlotColorMap?> ColorMapProperty =
        AvaloniaProperty.Register<ColorMapPreview, PlotColorMap?>(nameof(ColorMap));

    static ColorMapPreview()
    {
        AffectsRender<ColorMapPreview>(ColorMapProperty);
    }

    public PlotColorMap? ColorMap
    {
        get => GetValue(ColorMapProperty);
        set => SetValue(ColorMapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(72, 12);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var map = ColorMap ?? PlotColorMaps.Get(PlotColorPalette.Viridis);
        var rect = new Rect(0, Math.Max(0, Bounds.Height / 2 - 3), Bounds.Width, 6);
        draw_color_map(context, map, rect, 3);
    }

    internal static void draw_color_map(DrawingContext context, PlotColorMap map, Rect rect, double radius)
    {
        var fill_rect = rect.Deflate(1);
        double fill_radius = Math.Max(0, radius - 1);
        int steps = Math.Max(1, (int)Math.Ceiling(fill_rect.Width));
        for (int index = 0; index < steps; index++)
        {
            double left = fill_rect.Left + fill_rect.Width * index / steps;
            double right = fill_rect.Left + fill_rect.Width * (index + 1) / steps;
            double value = steps == 1 ? 0.5 : index / (double)(steps - 1);
            var strip = clipped_vertical_strip(fill_rect, left, right, fill_radius);
            if (strip.Height <= 0)
                continue;

            context.FillRectangle(
                new SolidColorBrush(map.ColorAt(value)),
                strip);
        }
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)), 1), rect, radius, radius);
    }

    internal static Rect clipped_vertical_strip(Rect rect, double left, double right, double radius)
    {
        radius = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        if (radius <= 0)
            return new Rect(left, rect.Top, Math.Ceiling(right - left) + 1, rect.Height);

        double sample_x = Math.Clamp((left + right) / 2, rect.Left, rect.Right);
        double center_y = rect.Top + rect.Height / 2;
        double half_height = rect.Height / 2;
        if (sample_x < rect.Left + radius)
        {
            double dx = rect.Left + radius - sample_x;
            half_height = Math.Sqrt(Math.Max(0, radius * radius - dx * dx));
        }
        else if (sample_x > rect.Right - radius)
        {
            double dx = sample_x - (rect.Right - radius);
            half_height = Math.Sqrt(Math.Max(0, radius * radius - dx * dx));
        }

        double top = center_y - half_height;
        double height = half_height * 2;
        return new Rect(left, top, Math.Ceiling(right - left) + 1, height);
    }
}

public sealed class ColorMapRangeSlider : Control
{
    public static readonly StyledProperty<DotColorSettings?> SettingsProperty =
        AvaloniaProperty.Register<ColorMapRangeSlider, DotColorSettings?>(nameof(Settings));

    private const double track_top = 12;
    private const double track_height = 9;
    private const double handle_radius = 6;
    private const double tick_top = 29;
    private DotColorSettings? subscribed_settings;
    private DragHandle drag_handle = DragHandle.None;

    static ColorMapRangeSlider()
    {
        AffectsRender<ColorMapRangeSlider>(SettingsProperty);
    }

    public ColorMapRangeSlider()
    {
        ClipToBounds = false;
    }

    public DotColorSettings? Settings
    {
        get => GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SettingsProperty)
            resubscribe_settings();
    }

    protected override Size MeasureOverride(Size availableSize) => new(260, 58);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Settings is not { HasAvailableRange: true } settings)
            return;

        var point = e.GetPosition(this);
        double min_x = value_to_x(settings.RangeMinimum);
        double max_x = value_to_x(settings.RangeMaximum);
        drag_handle = Math.Abs(point.X - min_x) <= Math.Abs(point.X - max_x)
            ? DragHandle.Minimum
            : DragHandle.Maximum;
        update_drag(point.X);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (drag_handle == DragHandle.None)
            return;

        update_drag(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        drag_handle = DragHandle.None;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Settings is not { HasAvailableRange: true } settings)
        {
            draw_empty(context);
            return;
        }

        var map = PlotColorMaps.Get(settings.Palette);
        var track = track_rect();
        double min_x = value_to_x(settings.RangeMinimum);
        double max_x = value_to_x(settings.RangeMaximum);
        draw_mapped_color_track(context, map, track, min_x, max_x, 4);

        draw_ticks(context, settings);
        draw_handle(context, min_x, settings.RangeMinimum);
        draw_handle(context, max_x, settings.RangeMaximum);
    }

    private void draw_empty(DrawingContext context)
    {
        var track = track_rect();
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(72, 72, 72)), track, 4);
        draw_handle(context, track.Left, 0);
        draw_handle(context, track.Right, 0);
        context.DrawText(create_text("Select a dot color", 12, Color.FromRgb(142, 142, 142)), new Point(Math.Max(0, track.Left - handle_radius), tick_top));
    }

    private void draw_ticks(DrawingContext context, DotColorSettings settings)
    {
        var minor_pen = new Pen(new SolidColorBrush(Color.FromRgb(105, 105, 105)), 1);
        var major_pen = new Pen(new SolidColorBrush(Color.FromRgb(170, 170, 170)), 1);
        foreach (double tick in minor_ticks(settings))
        {
            double x = value_to_x(tick);
            context.DrawLine(minor_pen, new Point(x, tick_top), new Point(x, tick_top + 4));
        }

        foreach (double tick in major_ticks(settings))
        {
            double x = value_to_x(tick);
            context.DrawLine(major_pen, new Point(x, tick_top - 1), new Point(x, tick_top + 8));
            var text = create_text(format_tick(tick), 12, Color.FromRgb(150, 150, 150));
            context.DrawText(text, new Point(Math.Clamp(x - handle_radius, 0, Math.Max(0, Bounds.Width - text.Width)), tick_top + 10));
        }
    }

    private void draw_handle(DrawingContext context, double x, double value)
    {
        var center = new Point(x, track_top + track_height / 2);
        context.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(178, 178, 178)),
            new Pen(new SolidColorBrush(Color.FromRgb(92, 92, 92)), 1.2),
            center,
            handle_radius,
            handle_radius);
    }

    private static void fill_rounded_segment(DrawingContext context, IBrush brush, Rect rect, double left, double right, double radius)
    {
        if (right <= left)
            return;

        int steps = Math.Max(1, (int)Math.Ceiling(right - left));
        for (int index = 0; index < steps; index++)
        {
            double strip_left = left + (right - left) * index / steps;
            double strip_right = left + (right - left) * (index + 1) / steps;
            var strip = ColorMapPreview.clipped_vertical_strip(rect, strip_left, strip_right, radius);
            if (strip.Height > 0)
                context.FillRectangle(brush, strip);
        }
    }

    private static void draw_mapped_color_track(DrawingContext context, PlotColorMap map, Rect rect, double minimum_x, double maximum_x, double radius)
    {
        minimum_x = Math.Clamp(minimum_x, rect.Left, rect.Right);
        maximum_x = Math.Clamp(maximum_x, rect.Left, rect.Right);
        if (maximum_x < minimum_x)
            (minimum_x, maximum_x) = (maximum_x, minimum_x);

        fill_rounded_segment(context, new SolidColorBrush(map.ColorAt(0)), rect, rect.Left, minimum_x, radius);
        if (maximum_x > minimum_x)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(maximum_x - minimum_x));
            for (int index = 0; index < steps; index++)
            {
                double left = minimum_x + (maximum_x - minimum_x) * index / steps;
                double right = minimum_x + (maximum_x - minimum_x) * (index + 1) / steps;
                double value = steps == 1 ? 0.5 : index / (double)(steps - 1);
                var strip = ColorMapPreview.clipped_vertical_strip(rect, left, right, radius);
                if (strip.Height > 0)
                    context.FillRectangle(new SolidColorBrush(map.ColorAt(value)), strip);
            }
        }
        else
        {
            fill_rounded_segment(context, new SolidColorBrush(map.ColorAt(0.5)), rect, minimum_x, maximum_x + 1, radius);
        }

        fill_rounded_segment(context, new SolidColorBrush(map.ColorAt(1)), rect, maximum_x, rect.Right, radius);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)), 1), rect, radius, radius);
    }

    private void update_drag(double x)
    {
        if (Settings is not { HasAvailableRange: true } settings)
            return;

        double value = x_to_value(x);
        if (drag_handle == DragHandle.Minimum)
            settings.RangeMinimum = Math.Min(value, settings.RangeMaximum);
        else if (drag_handle == DragHandle.Maximum)
            settings.RangeMaximum = Math.Max(value, settings.RangeMinimum);
    }

    private double value_to_x(double value)
    {
        if (Settings is not { HasAvailableRange: true } settings)
            return track_rect().Left;

        var track = track_rect();
        double minimum = transform(settings.AvailableMinimum, settings);
        double maximum = transform(settings.AvailableMaximum, settings);
        double transformed = transform(value, settings);
        if (maximum <= minimum || !double.IsFinite(transformed))
            return track.Left;

        double normalized = Math.Clamp((transformed - minimum) / (maximum - minimum), 0, 1);
        return track.Left + normalized * track.Width;
    }

    private double x_to_value(double x)
    {
        if (Settings is not { HasAvailableRange: true } settings)
            return 0;

        var track = track_rect();
        double normalized = Math.Clamp((x - track.Left) / Math.Max(1, track.Width), 0, 1);
        double minimum = transform(settings.AvailableMinimum, settings);
        double maximum = transform(settings.AvailableMaximum, settings);
        return inverse_transform(minimum + normalized * (maximum - minimum), settings);
    }

    private Rect track_rect() => new(8, track_top, Math.Max(1, Bounds.Width - 16), track_height);

    private static double transform(double value, DotColorSettings settings) =>
        settings.UseLogScale && settings.CanUseLogScale ? Math.Log10(1 + Math.Max(0, value)) : value;

    private static double inverse_transform(double value, DotColorSettings settings) =>
        settings.UseLogScale && settings.CanUseLogScale ? Math.Pow(10, value) - 1 : value;

    private static double[] major_ticks(DotColorSettings settings) =>
        settings.UseLogScale && settings.CanUseLogScale
            ? log_ticks(settings.AvailableMinimum, settings.AvailableMaximum, major: true)
            : linear_ticks(settings.AvailableMinimum, settings.AvailableMaximum, 5);

    private static double[] minor_ticks(DotColorSettings settings) =>
        settings.UseLogScale && settings.CanUseLogScale
            ? log_ticks(settings.AvailableMinimum, settings.AvailableMaximum, major: false)
            : linear_ticks(settings.AvailableMinimum, settings.AvailableMaximum, 17);

    private static double[] linear_ticks(double minimum, double maximum, int target_count)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return [];

        double raw_step = (maximum - minimum) / Math.Max(1, target_count - 1);
        double exponent = Math.Pow(10, Math.Floor(Math.Log10(raw_step)));
        double scaled = raw_step / exponent;
        double step = (scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * exponent;
        double first = Math.Ceiling(minimum / step) * step;
        return Enumerable.Range(0, 128)
            .Select(index => first + index * step)
            .TakeWhile(value => value <= maximum)
            .Where(value => value >= minimum)
            .ToArray();
    }

    private static double[] log_ticks(double minimum, double maximum, bool major)
    {
        minimum = Math.Max(0, minimum);
        if (!double.IsFinite(maximum) || maximum <= minimum)
            return [];

        int first_power = 0;
        if (minimum > 0)
            first_power = (int)Math.Floor(Math.Log10(minimum));
        if (major && maximum >= 1000)
            first_power = Math.Max(first_power, 3);
        int last_power = (int)Math.Ceiling(Math.Log10(maximum));
        int[] multipliers = major ? [1] : [2, 3, 4, 5, 6, 7, 8, 9];
        var ticks = Enumerable.Range(first_power, Math.Max(0, last_power - first_power + 1))
            .SelectMany(power => multipliers.Select(multiplier => multiplier * Math.Pow(10, power)))
            .Where(value => value >= minimum && value <= maximum);
        if (major && minimum <= 0 && maximum >= 0)
            ticks = ticks.Prepend(0);

        return ticks
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }

    private static string format_tick(double value)
    {
        double abs = Math.Abs(value);
        return abs switch
        {
            >= 1000 => Configuration.FormatAxisValue(value),
            > 0 and < 0.01 => value.ToString("0.##E+0", CultureInfo.InvariantCulture),
            >= 100 => value.ToString("0", CultureInfo.InvariantCulture),
            >= 10 => value.ToString("0.#", CultureInfo.InvariantCulture),
            _ => value.ToString("0.##", CultureInfo.InvariantCulture)
        };
    }

    private FormattedText create_text(string text, double size, Color color) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                TextElement.GetFontFamily(this),
                TextElement.GetFontStyle(this),
                TextElement.GetFontWeight(this),
                TextElement.GetFontStretch(this)),
            size,
            new SolidColorBrush(color));

    private void resubscribe_settings()
    {
        if (subscribed_settings is not null)
            subscribed_settings.PropertyChanged -= settings_property_changed;

        subscribed_settings = Settings;
        if (subscribed_settings is not null)
            subscribed_settings.PropertyChanged += settings_property_changed;
        InvalidateVisual();
    }

    private void settings_property_changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private enum DragHandle
    {
        None,
        Minimum,
        Maximum
    }
}
