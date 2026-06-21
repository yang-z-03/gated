using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using gated.Models;

namespace gated;

public sealed record SplitSampleFragment(string Name, double Start, double End);

public sealed record SplitSampleResult(IReadOnlyList<SplitSampleFragment> Fragments);

public partial class SplitSampleWindow : Window
{
    private FlowSample sample = null!;
    private string time_channel = "";
    private double minimum;
    private double maximum;
    private readonly List<double> splits = new();
    private float[] time_values = [];
    private float[]? ssc_values;
    private PartitionStrip strip = null!;
    private readonly List<TextBox> name_boxes = new();
    private readonly List<NumericUpDown> start_boxes = new();
    private readonly List<NumericUpDown> end_boxes = new();

    public SplitSampleWindow()
    {
        InitializeComponent();
    }

    public SplitSampleWindow(FlowSample sample, string time_channel, string? ssc_channel = null)
    {
        InitializeComponent();
        this.sample = sample;
        this.time_channel = time_channel;
        time_values = sample.GetChannelValues(time_channel);
        ssc_values = string.IsNullOrWhiteSpace(ssc_channel) ? null : sample.GetChannelValues(ssc_channel);
        minimum = time_values.Where(value => !float.IsNaN(value) && !float.IsInfinity(value)).DefaultIfEmpty(0).Min();
        maximum = time_values.Where(value => !float.IsNaN(value) && !float.IsInfinity(value)).DefaultIfEmpty(1).Max();
        minimum = Math.Round(minimum);
        maximum = Math.Round(maximum);
        if (maximum <= minimum)
            maximum = minimum + 1;

        strip = new PartitionStrip(minimum, maximum, splits, time_values, ssc_values);
        strip.SplitsChanged += (_, _) => update_table_from_splits();
        strip.SplitAdded += (_, value) =>
        {
            if (value <= minimum || value >= maximum)
                return;
            splits.Add(round_time(value));
            splits.Sort();
            refresh_fragments(preserve_names: true);
        };
        partitionHost.Content = strip;
        clearButton.Click += (_, _) =>
        {
            splits.Clear();
            refresh_fragments(preserve_names: false);
        };
        cancelButton.Click += (_, _) => Close(null);
        okButton.Click += (_, _) => close_with_result();
        refresh_fragments(preserve_names: false);
    }

    private void refresh_fragments(bool preserve_names)
    {
        var old_names = name_boxes.Select(box => box.Text ?? "").ToArray();
        strip.InvalidateVisual();
        fragmentPanel.Children.Clear();
        name_boxes.Clear();
        start_boxes.Clear();
        end_boxes.Clear();
        var ranges = ranges_from_splits();
        fragmentPanel.Children.Add(new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(140)),
                new ColumnDefinition(new GridLength(140))
            },
            ColumnSpacing = 12,
            Children =
            {
                header("Sample split name"),
                header("Start time"),
                header("End time")
            }
        });
        Grid.SetColumn(((Grid)fragmentPanel.Children[^1]).Children[1], 1);
        Grid.SetColumn(((Grid)fragmentPanel.Children[^1]).Children[2], 2);
        for (int index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            var name = new TextBox
            {
                Classes = { "Small" },
                Text = preserve_names && index < old_names.Length && !string.IsNullOrWhiteSpace(old_names[index])
                    ? old_names[index]
                    : $"{sample.Name} part {index + 1}",
                MinWidth = 220
            };
            var start = numeric(range.Start, minimum, maximum);
            var end = numeric(range.End, minimum, maximum);
            int captured_index = index;
            start.ValueChanged += (_, _) => update_range_from_table(captured_index, is_start: true, (double)(start.Value ?? 0));
            end.ValueChanged += (_, _) => update_range_from_table(captured_index, is_start: false, (double)(end.Value ?? 0));
            name_boxes.Add(name);
            start_boxes.Add(start);
            end_boxes.Add(end);
            fragmentPanel.Children.Add(new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(140)),
                    new ColumnDefinition(new GridLength(140))
                },
                ColumnSpacing = 12,
                Children =
                {
                    name,
                    start,
                    end
                }
            });
            Grid.SetColumn(((Grid)fragmentPanel.Children[^1]).Children[1], 1);
            Grid.SetColumn(((Grid)fragmentPanel.Children[^1]).Children[2], 2);
        }
    }

    private static TextBlock header(string text) =>
        new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 168, 178)),
            FontWeight = FontWeight.SemiBold
        };

    private NumericUpDown numeric(double value, double min, double max)
    {
        var input = new NumericUpDown
        {
            Classes = { "Small" },
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = 100,
            FormatString = "0",
            Value = (decimal)round_time(value)
        };
        return input;
    }

    private bool updating_table;

    private void update_range_from_table(int range_index, bool is_start, double value)
    {
        if (updating_table)
            return;
        if (range_index < 0)
            return;

        if (is_start && range_index > 0)
            splits[range_index - 1] = Math.Clamp(round_time(value), minimum, maximum);
        else if (!is_start && range_index < splits.Count)
            splits[range_index] = Math.Clamp(round_time(value), minimum, maximum);
        else
            return;

        splits.Sort();
        updating_table = true;
        try
        {
            refresh_fragments(preserve_names: true);
        }
        finally
        {
            updating_table = false;
        }
    }

    private void update_table_from_splits()
    {
        var ranges = ranges_from_splits();
        updating_table = true;
        try
        {
            for (int index = 0; index < ranges.Count && index < start_boxes.Count && index < end_boxes.Count; index++)
            {
                start_boxes[index].Value = (decimal)round_time(ranges[index].Start);
                end_boxes[index].Value = (decimal)round_time(ranges[index].End);
            }
        }
        finally
        {
            updating_table = false;
        }
    }

    private IReadOnlyList<(double Start, double End)> ranges_from_splits()
    {
        var points = new List<double> { minimum };
        points.AddRange(splits.Where(value => value > minimum && value < maximum).Distinct());
        points.Add(maximum);
        points.Sort();
        var ranges = new List<(double Start, double End)>();
        for (int index = 0; index < points.Count - 1; index++)
            ranges.Add((points[index], points[index + 1]));
        return ranges;
    }

    private void close_with_result()
    {
        var ranges = ranges_from_splits();
        var fragments = new List<SplitSampleFragment>();
        for (int index = 0; index < ranges.Count; index++)
        {
            string name = name_boxes[index].Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                statusText.Text = "Every fragment needs a name.";
                return;
            }

            fragments.Add(new SplitSampleFragment(name, round_time(ranges[index].Start), round_time(ranges[index].End)));
        }

        Close(new SplitSampleResult(fragments));
    }

    private static double round_time(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private sealed class PartitionStrip : Control
    {
        private readonly double minimum;
        private readonly double maximum;
        private readonly List<double> splits;
        private readonly float[] time_values;
        private readonly float[]? ssc_values;
        private int dragging_index = -1;
        private int hover_index = -1;
        private int cached_width;
        private Color[] cached_colors = [];
        private readonly float ssc_minimum;
        private readonly float ssc_maximum;
        private const double handle_width = 20;

        public event EventHandler<double>? SplitAdded;
        public event EventHandler? SplitsChanged;

        public PartitionStrip(double minimum, double maximum, List<double> splits, float[] time_values, float[]? ssc_values)
        {
            this.minimum = minimum;
            this.maximum = maximum;
            this.splits = splits;
            this.time_values = time_values;
            this.ssc_values = ssc_values;
            Cursor = new Cursor(StandardCursorType.Hand);
            if (ssc_values is not null && ssc_values.Length > 0)
            {
                ssc_minimum = ssc_values.Where(value => !float.IsNaN(value) && !float.IsInfinity(value)).DefaultIfEmpty(0).Min();
                ssc_maximum = ssc_values.Where(value => !float.IsNaN(value) && !float.IsInfinity(value)).DefaultIfEmpty(1).Max();
                if (ssc_maximum <= ssc_minimum)
                    ssc_maximum = ssc_minimum + 1;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetPosition(this);
            var rect = strip_rect();
            dragging_index = hit_split(point, rect);
            if (dragging_index >= 0)
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            double fraction = Math.Clamp((point.X - rect.Left) / Math.Max(1, rect.Width), 0, 1);
            SplitAdded?.Invoke(this, minimum + (maximum - minimum) * fraction);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            var rect = strip_rect();
            int hit = hit_split(point, rect);
            Cursor = hit >= 0 ? new Cursor(StandardCursorType.SizeWestEast) : new Cursor(StandardCursorType.Hand);
            if (hit != hover_index)
            {
                hover_index = hit;
                InvalidateVisual();
            }
            if (dragging_index < 0)
                return;

            double fraction = Math.Clamp((point.X - rect.Left) / Math.Max(1, rect.Width), 0, 1);
            double value = minimum + (maximum - minimum) * fraction;
            double min_span = (maximum - minimum) * (20.0 / Math.Max(1, rect.Width));
            double lower = dragging_index == 0 ? minimum + min_span : splits[dragging_index - 1] + min_span;
            double upper = dragging_index == splits.Count - 1 ? maximum - min_span : splits[dragging_index + 1] - min_span;
            splits[dragging_index] = Math.Clamp(round_time(value), lower, upper);
            SplitsChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            dragging_index = -1;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (hover_index == -1 || dragging_index >= 0)
                return;
            hover_index = -1;
            Cursor = new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var rect = strip_rect();
            draw_ssc_strip(context, rect);
            var points = new List<double> { minimum };
            points.AddRange(splits);
            points.Add(maximum);
            points.Sort();

            for (int split_index = 0; split_index < splits.Count; split_index++)
            {
                double split = splits[split_index];
                double x = rect.Left + rect.Width * ((split - minimum) / (maximum - minimum));
                bool active = split_index == hover_index || split_index == dragging_index;
                var color = active ? Color.FromRgb(76, 132, 255) : Color.FromRgb(194, 198, 208);
                double thickness = active ? 5 : 2;
                context.DrawLine(new Pen(new SolidColorBrush(color), thickness), new Point(x, rect.Top), new Point(x, rect.Bottom));
                if (active)
                    draw_handle_icon(context, x, rect);
            }

            draw_label(context, minimum.ToString("N0", CultureInfo.CurrentCulture), new Point(rect.Left, 52), Color.FromRgb(164, 168, 178));
            draw_label(context, maximum.ToString("N0", CultureInfo.CurrentCulture), new Point(Math.Max(rect.Left, rect.Right - 80), 52), Color.FromRgb(164, 168, 178));
        }

        private Rect strip_rect() => new(10, 24, Math.Max(1, Bounds.Width - 20), 24);

        private int hit_split(Point point, Rect rect)
        {
            for (int index = 0; index < splits.Count; index++)
            {
                double x = rect.Left + rect.Width * ((splits[index] - minimum) / (maximum - minimum));
                if (Math.Abs(point.X - x) <= handle_width / 2 && point.Y >= rect.Top - 20 && point.Y <= rect.Bottom + 12)
                    return index;
            }
            return -1;
        }

        private void draw_ssc_strip(DrawingContext context, Rect rect)
        {
            if (ssc_values is null || ssc_values.Length != time_values.Length || ssc_values.Length == 0)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(42, 47, 56)), rect, 4);
                return;
            }

            int columns = Math.Max(1, (int)rect.Width);
            if (columns != cached_width)
                rebuild_color_cache(columns);
            for (int column = 0; column < columns; column++)
                context.FillRectangle(new SolidColorBrush(cached_colors[column]), new Rect(rect.Left + column, rect.Top, 1, rect.Height));
        }

        private void rebuild_color_cache(int columns)
        {
            cached_width = columns;
            cached_colors = new Color[columns];
            var sums = new double[columns];
            var counts = new int[columns];
            double span = maximum - minimum;
            if (ssc_values is not null)
            {
                for (int index = 0; index < time_values.Length; index++)
                {
                    float time = time_values[index];
                    float ssc = ssc_values[index];
                    if (float.IsNaN(time) || float.IsInfinity(time) || float.IsNaN(ssc) || float.IsInfinity(ssc))
                        continue;
                    if (time < minimum || time > maximum)
                        continue;
                    int column = (int)Math.Floor((time - minimum) / span * columns);
                    column = Math.Clamp(column, 0, columns - 1);
                    sums[column] += ssc;
                    counts[column]++;
                }
            }

            for (int column = 0; column < columns; column++)
            {
                float ssc = counts[column] == 0 ? ssc_minimum : (float)(sums[column] / counts[column]);
                cached_colors[column] = turbo((ssc - ssc_minimum) / Math.Max(1e-6f, ssc_maximum - ssc_minimum));
            }
        }

        private static void draw_handle_icon(DrawingContext context, double center_x, Rect strip)
        {
            var rect = new Rect(center_x - 12, strip.Center.Y - 9, 24, 18);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(76, 132, 255)), rect, 5);
            var pen = new Pen(Brushes.White, 1.6);
            double cy = rect.Center.Y;
            context.DrawLine(pen, new Point(center_x - 4, cy - 4), new Point(center_x - 8, cy));
            context.DrawLine(pen, new Point(center_x - 8, cy), new Point(center_x - 4, cy + 4));
            context.DrawLine(pen, new Point(center_x + 4, cy - 4), new Point(center_x + 8, cy));
            context.DrawLine(pen, new Point(center_x + 8, cy), new Point(center_x + 4, cy + 4));
        }

        private static Color turbo(float x)
        {
            x = Math.Clamp(x, 0, 1);
            float r = 0.13572138f + 4.61539260f * x - 42.66032258f * x * x + 132.13108234f * x * x * x - 152.94239396f * x * x * x * x + 59.28637943f * x * x * x * x * x;
            float g = 0.09140261f + 2.19418839f * x + 4.84296658f * x * x - 14.18503333f * x * x * x + 4.27729857f * x * x * x * x + 2.82956604f * x * x * x * x * x;
            float b = 0.10667330f + 12.64194608f * x - 60.58204836f * x * x + 110.36276771f * x * x * x - 89.90310912f * x * x * x * x + 27.34824973f * x * x * x * x * x;
            return Color.FromRgb(to_byte(r), to_byte(g), to_byte(b));
        }

        private static byte to_byte(float value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

        private static void draw_label(DrawingContext context, string text, Point point, Color color) =>
            context.DrawText(new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                new SolidColorBrush(color)), point);
    }
}
