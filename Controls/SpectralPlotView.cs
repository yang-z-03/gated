using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using gated.Models;

namespace gated.Controls;

public sealed class SpectralPlotView : Control
{
    public static readonly StyledProperty<SpectralPlotData?> DataProperty = AvaloniaProperty.Register<SpectralPlotView, SpectralPlotData?>(nameof(Data));
    public static readonly StyledProperty<IReadOnlyList<float>?> SignatureProperty = AvaloniaProperty.Register<SpectralPlotView, IReadOnlyList<float>?>(nameof(Signature));
    public static readonly StyledProperty<double> SignatureAmplitudeProperty = AvaloniaProperty.Register<SpectralPlotView, double>(nameof(SignatureAmplitude), 1.0);
    public static readonly StyledProperty<IBrush?> PlotBackgroundProperty = AvaloniaProperty.Register<SpectralPlotView, IBrush?>(nameof(PlotBackground));
    public static readonly StyledProperty<IBrush?> PlotForegroundProperty = AvaloniaProperty.Register<SpectralPlotView, IBrush?>(nameof(PlotForeground));

    static SpectralPlotView() => AffectsRender<SpectralPlotView>(DataProperty, SignatureProperty, SignatureAmplitudeProperty, PlotBackgroundProperty, PlotForegroundProperty);

    public SpectralPlotData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IReadOnlyList<float>? Signature { get => GetValue(SignatureProperty); set => SetValue(SignatureProperty, value); }
    public double SignatureAmplitude { get => GetValue(SignatureAmplitudeProperty); set => SetValue(SignatureAmplitudeProperty, value); }
    public IBrush? PlotBackground { get => GetValue(PlotBackgroundProperty); set => SetValue(PlotBackgroundProperty, value); }
    public IBrush? PlotForeground { get => GetValue(PlotForegroundProperty); set => SetValue(PlotForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var axis_color = Color.FromRgb(142, 148, 160);
        var text_color = Color.FromRgb(230, 235, 245);
        var tick_text = Color.FromRgb(140, 148, 162);
        var foreground = new SolidColorBrush(tick_text);
        var stresstext = new SolidColorBrush(text_color);
        var axis_pen = new Pen(new SolidColorBrush(axis_color), 1);
        var splitter_pen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);

        var plot = new Rect(72, 12, Math.Max(1, Bounds.Width - 74), Math.Max(1, Bounds.Height - 96));
        if (Data is not { } data || data.DetectorNames.Count == 0 || data.Density.Length == 0)
        {
            draw_axes(context, plot, axis_pen, tick_text, raw_maximum: 100000, detector_count: 0, slot: 0);
            draw_vertical_centered_text(context, "Fluorescence Intensity", new Point(Bounds.Left + 18, plot.Top + plot.Height / 2), 12, stresstext);
            return;
        }

        int detector_count = data.DetectorNames.Count;
        int bins = data.Density.GetLength(1);
        double slot = plot.Width / detector_count;
        int maximum_count = data.Density.Cast<int>().DefaultIfEmpty(0).Max();
        double transformed_maximum = spectral_transform(Math.Max(10000, data.RawMaximum));
        if (transformed_maximum <= 0) transformed_maximum = 1;

        int peak_index = string.IsNullOrWhiteSpace(data.PeakChannel)
            ? -1
            : data.DetectorNames.ToList().FindIndex(name => string.Equals(name, data.PeakChannel, StringComparison.Ordinal));
        int label_stride = detector_label_stride(data.DetectorNames, slot, 11);
        for (int detector = 0; detector < detector_count; detector++)
        {
            for (int bin = 0; bin < bins; bin++)
            {
                int count = data.Density[detector, bin];
                if (count == 0) continue;
                double density = Math.Log(1.0 + count) / Math.Log(1.0 + Math.Max(1, maximum_count));
                Color color = turbo_density_color(density);
                double y = plot.Bottom - (bin + 1) * plot.Height / bins;
                context.FillRectangle(new SolidColorBrush(color), new Rect(plot.Left + detector * slot, y, Math.Max(1, slot + .5), plot.Height / bins + .5));
            }
            bool draw_label = detector % label_stride == 0 || detector == peak_index;
            if (draw_label)
                draw_vertical_text(context, data.DetectorNames[detector], new Point(plot.Left + (detector + .5) * slot, plot.Bottom + 10), 11, stresstext);
            if (detector > 0 && data.ExcitationLights[detector - 1] != data.ExcitationLights[detector])
                context.DrawLine(splitter_pen, new Point(plot.Left + detector * slot, plot.Top), new Point(plot.Left + detector * slot, plot.Bottom));
        }

        draw_peak_range(context, data, plot, slot, peak_index, transformed_maximum, foreground);
        if (Signature is { Count: > 0 })
            draw_signature(context, data, plot, slot, transformed_maximum);

        draw_axes(context, plot, axis_pen, tick_text, data.RawMaximum, detector_count, slot);
        draw_vertical_centered_text(context, "Fluorescence Intensity", new Point(Bounds.Left + 18, plot.Top + plot.Height / 2), 12, stresstext);
    }

    private void draw_signature(DrawingContext context, SpectralPlotData data, Rect plot, double slot, double transformed_maximum)
    {
        int detector_count = data.DetectorNames.Count;
        Point? previous = null;
        ExcitationLightKind? previous_light = null;
        for (int index = 0; index < Math.Min(Signature!.Count, detector_count); index++)
        {
            var light = data.ExcitationLights.ElementAtOrDefault(index);
            double raw = Math.Max(0, Signature[index] * SignatureAmplitude);
            double normalized = spectral_transform(raw) / transformed_maximum;
            var point = new Point(plot.Left + (index + .5) * slot, plot.Bottom - Math.Clamp(normalized, 0, 1) * plot.Height);
            if (previous is { } from && previous_light == light)
                context.DrawLine(new Pen(new SolidColorBrush(excitation_color(light)), 2.8, lineCap: PenLineCap.Round), from, point);
            previous = point;
            previous_light = light;
        }
    }

    private static void draw_peak_range(DrawingContext context, SpectralPlotData data, Rect plot, double slot, int peak_index, double transformed_maximum, IBrush foreground)
    {
        if (peak_index < 0 || data.PositiveSelection is not { } range)
            return;
        double minimum = Math.Min(range.Minimum, range.Maximum);
        double maximum = Math.Max(range.Minimum, range.Maximum);
        double top = plot.Bottom - Math.Clamp(spectral_transform(maximum) / transformed_maximum, 0, 1) * plot.Height;
        double bottom = plot.Bottom - Math.Clamp(spectral_transform(minimum) / transformed_maximum, 0, 1) * plot.Height;
        double center = plot.Left + (peak_index + 0.5) * slot;
        double cap = Math.Max(4, Math.Min(slot * 0.42, 12));
        var pen = new Pen(Brushes.White, 2.4, lineCap: PenLineCap.Square);
        context.DrawLine(pen, new Point(center, top), new Point(center, bottom));
        context.DrawLine(pen, new Point(center - cap, top), new Point(center + cap, top));
        context.DrawLine(pen, new Point(center - cap, bottom), new Point(center + cap, bottom));
    }

    private void draw_axes(DrawingContext context, Rect plot, Pen axis_pen, Color tick_text, double raw_maximum, int detector_count, double slot)
    {
        context.DrawLine(axis_pen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        context.DrawLine(axis_pen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        double transformed_maximum = spectral_transform(Math.Max(10000, raw_maximum));
        if (transformed_maximum <= 0) transformed_maximum = 1;
        foreach (double tick in spectral_ticks(raw_maximum))
        {
            double y = plot.Bottom - spectral_transform(tick) / transformed_maximum * plot.Height;
            context.DrawLine(axis_pen, new Point(plot.Left - 4, y), new Point(plot.Left, y));
            draw_right_text(context, Configuration.FormatAxisValue(tick), new Point(plot.Left - 10, y), 11, new SolidColorBrush(tick_text));
        }
        for (int detector = 0; detector < detector_count; detector++)
        {
            double x = plot.Left + (detector + 0.5) * slot;
            context.DrawLine(axis_pen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
        }
    }

    private static IEnumerable<double> spectral_ticks(double maximum)
    {
        yield return 0;
        yield return 1000;
        if (!double.IsFinite(maximum))
            yield break;
        for (double value = 10000; value <= Math.Max(maximum, 10000); value *= 10)
        {
            yield return value;
            if (value > maximum)
                yield break;
        }
    }

    private static double spectral_transform(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0;
        return value <= 1000.0 ? value / 1000.0 : 1.0 + Math.Log10(value / 1000.0);
    }

    private static Color turbo_density_color(double value)
    {
        var color = PlotColorMaps.ColorAt(PlotColorPalette.Turbo, value);
        byte alpha = (byte)Math.Clamp(36 + value * 210, 0, 246);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color excitation_color(ExcitationLightKind light)
    {
        return light switch
        {
            ExcitationLightKind.UV => Colors.MediumPurple,
            ExcitationLightKind.Violet => Colors.BlueViolet,
            ExcitationLightKind.Blue => Colors.DodgerBlue,
            ExcitationLightKind.Green => Colors.LimeGreen,
            ExcitationLightKind.Yellow => Colors.Gold,
            ExcitationLightKind.Red => Colors.OrangeRed,
            ExcitationLightKind.FarRed => Colors.Firebrick,
            _ => Colors.White
        };
    }

    private FormattedText text(string value, double size, IBrush brush) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this)), size, brush);
    private int detector_label_stride(IReadOnlyList<string> labels, double slot, double size)
    {
        if (slot <= 0)
            return Math.Max(1, labels.Count);
        double required = labels.Select(label => text(label, size, Brushes.White).Height + 4).DefaultIfEmpty(size + 4).Max();
        return Math.Max(1, (int)Math.Ceiling(required / slot));
    }
    private void draw_right_text(DrawingContext context, string value, Point point, double size, IBrush brush)
    {
        var formatted = text(value, size, brush);
        context.DrawText(formatted, new Point(point.X - formatted.Width, point.Y - formatted.Height / 2));
    }
    private void draw_vertical_text(DrawingContext context, string value, Point point, double size, IBrush brush)
    {
        var formatted = text(value, size, brush);
        using (context.PushTransform(Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(point.X + formatted.Height / 2, point.Y)))
            context.DrawText(formatted, new Point(0, 0));
    }

    private void draw_vertical_centered_text(DrawingContext context, string value, Point center, double size, IBrush brush)
    {
        var formatted = text(value, size, brush);
        using (context.PushTransform(Matrix.CreateTranslation(-formatted.Width / 2, -formatted.Height / 2) *
                                     Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
            context.DrawText(formatted, new Point());
    }
}
