using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using gated.Models;

namespace gated.Controls;

public sealed record CompensationPreviewPopulationChoice(string Name, Guid? GateId, PopulationRegion Region)
{
    public override string ToString() => Name;
}

public sealed class CompensationPreviewView : Control
{
    private const int cell_size = 96;
    private const int label_space = 82;
    private const int top_space = 28;
    private const int max_events = 100000;
    private static readonly Color raw_color = Color.FromRgb(150, 150, 150);
    private static readonly Color compensated_color = Color.FromRgb(220, 48, 48);

    private FlowGroup? group;
    private string[] channels = [];
    private float[,] values = new float[0, 0];
    private CompensationPreviewPopulationChoice? population_choice;
    private WriteableBitmap? cached_bitmap;

    public void Configure(FlowGroup? source_group, IReadOnlyList<string> channel_names, float[,] matrix_values, CompensationPreviewPopulationChoice? choice)
    {
        group = source_group;
        channels = channel_names.ToArray();
        values = (float[,])matrix_values.Clone();
        population_choice = choice;
        cached_bitmap = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int size = Math.Max(0, channels.Length - 1) * cell_size;
        return new Size(label_space + size, top_space + size);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), bounds);

        if (channels.Length < 2 || group is null)
        {
            draw_text(context, "No compensation preview data", new Point(12, 12), 12, Color.FromRgb(180, 180, 180), false);
            return;
        }

        cached_bitmap ??= create_preview_bitmap();
        if (cached_bitmap is not null)
            context.DrawImage(cached_bitmap, new Rect(label_space, top_space, (channels.Length - 1) * cell_size, (channels.Length - 1) * cell_size));

        for (int row = 1; row < channels.Length; row++)
        {
            double center = top_space + (row - 1) * cell_size + cell_size / 2.0;
            draw_text(context, channels[row], new Point(8, center - 8), 11, Color.FromRgb(218, 221, 228), true);
        }

        for (int column = 0; column < channels.Length - 1; column++)
        {
            draw_text(context, channels[column], new Point(label_space + column * cell_size + 4, 6), 11, Color.FromRgb(218, 221, 228), true);
        }
    }

    private WriteableBitmap? create_preview_bitmap()
    {
        int channel_count = channels.Length;
        int bitmap_size = Math.Max(0, channel_count - 1) * cell_size;
        if (bitmap_size <= 0 || group is null)
            return null;

        var pixels = new byte[bitmap_size * bitmap_size * 4];
        var samples = collect_sampled_events(group, population_choice).ToArray();
        if (samples.Length == 0 || !try_invert(values, out var inverse))
            return create_bitmap(pixels, bitmap_size);

        var transform = new LogicleTransform(new LogicleParameters());
        var transformed_minimum = new double[channel_count];
        var transformed_span = new double[channel_count];
        for (int channel = 0; channel < channel_count; channel++)
        {
            double maximum = group.Channels.FirstOrDefault(item => item.Name == channels[channel])?.Maximum ?? new LogicleParameters().T;
            if (maximum <= 0)
                maximum = new LogicleParameters().T;
            transformed_minimum[channel] = transform.Transform(0);
            transformed_span[channel] = transform.Transform(maximum) - transformed_minimum[channel];
            if (transformed_span[channel] <= 0)
                transformed_span[channel] = 1;
        }

        foreach (var point in samples)
        {
            var sample = point.Sample;
            var mapped_indices = channels.Select(sample.GetChannelIndex).ToArray();
            if (mapped_indices.Any(index => index < 0))
                continue;

            var raw = new double[channel_count];
            var compensated = new double[channel_count];
            for (int channel = 0; channel < channel_count; channel++)
                raw[channel] = sample.RawEvents[point.RowIndex, mapped_indices[channel]];

            for (int target = 0; target < channel_count; target++)
            {
                double value = 0;
                for (int source = 0; source < channel_count; source++)
                    value += raw[source] * inverse[source, target];
                compensated[target] = value;
            }

            for (int y_channel = 1; y_channel < channel_count; y_channel++)
            for (int x_channel = 0; x_channel < y_channel; x_channel++)
            {
                plot_point(pixels, bitmap_size, x_channel, y_channel, raw[x_channel], raw[y_channel], transformed_minimum, transformed_span, transform, raw_color);
                plot_point(pixels, bitmap_size, x_channel, y_channel, compensated[x_channel], compensated[y_channel], transformed_minimum, transformed_span, transform, compensated_color);
            }
        }

        draw_cell_frames(pixels, bitmap_size, channel_count);
        return create_bitmap(pixels, bitmap_size);
    }

    private static IEnumerable<SampledEvent> collect_sampled_events(FlowGroup group, CompensationPreviewPopulationChoice? choice)
    {
        var reservoir = new List<SampledEvent>(Math.Min(max_events, group.Samples.Sum(sample => sample.EventCount)));
        var random = new Random(17);
        int seen = 0;
        foreach (var sample in group.Samples)
        {
            foreach (int row_index in resolve_event_indices(sample, choice))
            {
                seen++;
                var item = new SampledEvent(sample, row_index);
                if (reservoir.Count < max_events)
                {
                    reservoir.Add(item);
                    continue;
                }

                int replace_index = random.Next(seen);
                if (replace_index < max_events)
                    reservoir[replace_index] = item;
            }
        }

        return reservoir;
    }

    private static IEnumerable<int> resolve_event_indices(FlowSample sample, CompensationPreviewPopulationChoice? choice)
    {
        if (choice?.GateId is null)
            return Enumerable.Range(0, sample.EventCount);

        var population = find_population(sample.Populations, choice.GateId.Value, choice.Region);
        return population?.EventIndices ?? Array.Empty<int>();
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, Guid gate_id, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate.Id == gate_id && population.Region == region)
                return population;

            var child = find_population(population.Children, gate_id, region);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static void plot_point(byte[] pixels, int bitmap_size, int x_channel, int y_channel, double x_value, double y_value, double[] transformed_minimum, double[] transformed_span, LogicleTransform transform, Color color)
    {
        if (double.IsNaN(x_value) || double.IsInfinity(x_value) || double.IsNaN(y_value) || double.IsInfinity(y_value))
            return;

        double x_normalized = (transform.Transform(x_value) - transformed_minimum[x_channel]) / transformed_span[x_channel];
        double y_normalized = (transform.Transform(y_value) - transformed_minimum[y_channel]) / transformed_span[y_channel];
        if (x_normalized < 0 || x_normalized > 1 || y_normalized < 0 || y_normalized > 1)
            return;

        int x = x_channel * cell_size + Math.Clamp((int)Math.Round(x_normalized * (cell_size - 1)), 0, cell_size - 1);
        int y = (y_channel - 1) * cell_size + cell_size - 1 - Math.Clamp((int)Math.Round(y_normalized * (cell_size - 1)), 0, cell_size - 1);
        set_pixel(pixels, bitmap_size, x, y, color);
    }

    private static void draw_cell_frames(byte[] pixels, int bitmap_size, int channel_count)
    {
        var border = Color.FromRgb(80, 80, 80);
        for (int y_channel = 1; y_channel < channel_count; y_channel++)
        for (int x_channel = 0; x_channel < y_channel; x_channel++)
        {
            int left = x_channel * cell_size;
            int top = (y_channel - 1) * cell_size;
            for (int offset = 0; offset < cell_size; offset++)
            {
                set_pixel(pixels, bitmap_size, left + offset, top, border);
                set_pixel(pixels, bitmap_size, left + offset, top + cell_size - 1, border);
                set_pixel(pixels, bitmap_size, left, top + offset, border);
                set_pixel(pixels, bitmap_size, left + cell_size - 1, top + offset, border);
            }
        }
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
                if (value > best_value)
                {
                    best_value = value;
                    best_row = row;
                }
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

    private static WriteableBitmap create_bitmap(byte[] pixels, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        int row_bytes = size * 4;
        if (frame.RowBytes == row_bytes)
        {
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            return bitmap;
        }

        for (int y = 0; y < size; y++)
            Marshal.Copy(pixels, y * row_bytes, IntPtr.Add(frame.Address, y * frame.RowBytes), row_bytes);
        return bitmap;
    }

    private static void set_pixel(byte[] pixels, int bitmap_size, int x, int y, Color color)
    {
        if (x < 0 || x >= bitmap_size || y < 0 || y >= bitmap_size)
            return;

        int offset = (y * bitmap_size + x) * 4;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
        pixels[offset + 3] = 255;
    }

    private void draw_text(DrawingContext context, string text, Point point, double size, Color color, bool bold)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size,
            new SolidColorBrush(color));
        context.DrawText(formatted, point);
    }

    private readonly record struct SampledEvent(FlowSample Sample, int RowIndex);
}
