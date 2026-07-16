using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using gated.Models;
using gated.ViewModels;

namespace gated.Controls;

public enum SpectralMatrixPalette
{
    Auto,
    Reds,
    Blues,
    Turbo,
    DivergingBlueRed
}

public sealed class SpectralMatrixView : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<SpectralMatrixRowViewModel>?> RowsProperty =
        AvaloniaProperty.Register<SpectralMatrixView, IReadOnlyList<SpectralMatrixRowViewModel>?>(nameof(Rows));
    public static readonly StyledProperty<IReadOnlyList<string>?> ColumnLabelsProperty =
        AvaloniaProperty.Register<SpectralMatrixView, IReadOnlyList<string>?>(nameof(ColumnLabels));
    public static readonly StyledProperty<string> XAxisTitleProperty =
        AvaloniaProperty.Register<SpectralMatrixView, string>(nameof(XAxisTitle), "");
    public static readonly StyledProperty<string> YAxisTitleProperty =
        AvaloniaProperty.Register<SpectralMatrixView, string>(nameof(YAxisTitle), "");
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<SpectralMatrixView, bool>(nameof(IsEditMode));
    public static readonly StyledProperty<bool> ShowAnnotationsProperty =
        AvaloniaProperty.Register<SpectralMatrixView, bool>(nameof(ShowAnnotations));
    public static readonly StyledProperty<double> StressAboveProperty =
        AvaloniaProperty.Register<SpectralMatrixView, double>(nameof(StressAbove), double.NaN);
    public static readonly StyledProperty<SpectralMatrixPalette> PaletteProperty =
        AvaloniaProperty.Register<SpectralMatrixView, SpectralMatrixPalette>(nameof(Palette), SpectralMatrixPalette.Auto);
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<SpectralMatrixView, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<SpectralMatrixView, IBrush?>(nameof(GridBrush));

    private readonly TextBox editor;
    private Rect[,] cell_rects = new Rect[0, 0];
    private int highlighted_row = -1;
    private int highlighted_column = -1;
    private SpectralMatrixCellViewModel? edited_cell;
    private INotifyCollectionChanged? rows_collection;
    private readonly List<INotifyPropertyChanged> observed_cells = new();

    static SpectralMatrixView()
    {
        AffectsRender<SpectralMatrixView>(
            RowsProperty,
            ColumnLabelsProperty,
            XAxisTitleProperty,
            YAxisTitleProperty,
            IsEditModeProperty,
            ShowAnnotationsProperty,
            StressAboveProperty,
            PaletteProperty,
            TextBrushProperty,
            GridBrushProperty);
    }

    public SpectralMatrixView()
    {
        ClipToBounds = true;
        editor = new TextBox
        {
            IsVisible = false,
            Classes = { "Small" },
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        editor.LostFocus += (_, _) => commit_editor();
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { commit_editor(); e.Handled = true; }
            if (e.Key == Key.Escape) { close_editor(commit: false); e.Handled = true; }
        };
        Child = editor;
        PointerMoved += on_pointer_moved;
        PointerExited += (_, _) =>
        {
            highlighted_row = -1;
            highlighted_column = -1;
            InvalidateVisual();
        };
        DoubleTapped += on_double_tapped;
    }

    public IReadOnlyList<SpectralMatrixRowViewModel>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public IReadOnlyList<string>? ColumnLabels
    {
        get => GetValue(ColumnLabelsProperty);
        set => SetValue(ColumnLabelsProperty, value);
    }

    public string XAxisTitle
    {
        get => GetValue(XAxisTitleProperty);
        set => SetValue(XAxisTitleProperty, value);
    }

    public string YAxisTitle
    {
        get => GetValue(YAxisTitleProperty);
        set => SetValue(YAxisTitleProperty, value);
    }

    public bool IsEditMode
    {
        get => GetValue(IsEditModeProperty);
        set => SetValue(IsEditModeProperty, value);
    }

    public bool ShowAnnotations
    {
        get => GetValue(ShowAnnotationsProperty);
        set => SetValue(ShowAnnotationsProperty, value);
    }

    public double StressAbove
    {
        get => GetValue(StressAboveProperty);
        set => SetValue(StressAboveProperty, value);
    }

    public SpectralMatrixPalette Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
            observe_rows(change.NewValue as IReadOnlyList<SpectralMatrixRowViewModel>);
        if (change.Property == RowsProperty || change.Property == ColumnLabelsProperty || change.Property == ShowAnnotationsProperty)
            InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Rows?.Count ?? 0;
        int columns = maximum_column_count();
        var metrics = calculate_cell_metrics(rows, columns, availableSize);
        double desired_width = metrics.Left + Math.Max(1, columns) * metrics.CellWidth + 18;
        double desired_height = metrics.Top + Math.Max(1, rows) * metrics.CellHeight + 30;
        if (!ShowAnnotations && double.IsFinite(availableSize.Width))
            desired_width = availableSize.Width;
        Child?.Measure(availableSize);
        return new Size(desired_width, desired_height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (editor.IsVisible && edited_cell is not null)
        {
            var rect = current_editor_rect();
            Child?.Arrange(rect);
        }
        else
        {
            Child?.Arrange(new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rows = Rows ?? [];
        int row_count = rows.Count;
        int column_count = maximum_column_count();
        var labels = ColumnLabels ?? [];
        var text_brush = TextBrush ?? new SolidColorBrush(Color.FromRgb(226, 231, 241));
        var muted_brush = new SolidColorBrush(Color.FromRgb(142, 148, 160));
        var grid_brush = GridBrush ?? new SolidColorBrush(Color.FromArgb(72, 128, 138, 152));
        var grid_pen = new Pen(grid_brush, 1);
        var stress_pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 92, 92)), 2.4);
        var highlight_brush = new SolidColorBrush(Color.FromArgb(70, 180, 180, 180));
        var mask_brush = new SolidColorBrush(Color.FromRgb(32, 32, 34));

        var layout = calculate_layout(row_count, column_count);
        draw_titles(context, layout, text_brush);
        cell_rects = new Rect[Math.Max(0, row_count), Math.Max(0, column_count)];

        if (row_count == 0 || column_count == 0)
        {
            var empty = text("No matrix values", 13, muted_brush);
            context.DrawText(empty, new Point(layout.Matrix.Left, layout.Matrix.Top));
            return;
        }

        var values = rows.SelectMany(row => row.Cells.Select(cell => (double)cell.Value)).Where(double.IsFinite).ToArray();
        var norm = calculate_norm(values);
        int label_stride = column_label_stride(labels, column_count, layout.CellWidth);
        bool draw_column_grid = ShowAnnotations || layout.CellWidth >= 3.0;

        if (draw_column_grid)
        {
            for (int column = 0; column < column_count; column++)
            {
                double x = layout.Matrix.Left + column * layout.CellWidth;
                context.DrawLine(grid_pen, new Point(x, layout.Matrix.Top), new Point(x, layout.Matrix.Bottom));
            }
            context.DrawLine(grid_pen, new Point(layout.Matrix.Right, layout.Matrix.Top), new Point(layout.Matrix.Right, layout.Matrix.Bottom));
        }

        for (int row = 0; row < row_count; row++)
        {
            double y = layout.Matrix.Top + row * layout.CellHeight;
            context.DrawLine(grid_pen, new Point(layout.Matrix.Left, y), new Point(layout.Matrix.Right, y));
            draw_row_label(context, rows[row].Name, new Point(layout.Matrix.Left - 8, y + layout.CellHeight / 2), row == highlighted_row, text_brush, muted_brush);
            for (int column = 0; column < column_count; column++)
            {
                var rect = new Rect(layout.Matrix.Left + column * layout.CellWidth, y, layout.CellWidth, layout.CellHeight);
                cell_rects[row, column] = rect;
                if (column >= rows[row].Cells.Count)
                    continue;

                double value = rows[row].Cells[column].Value;
                double row_limit = norm.Diverging ? row_diverging_limit(rows[row]) : 1.0;
                var fill_rect = layout.CellWidth <= 1.2 || layout.CellHeight <= 1.2 ? rect : rect.Deflate(0.5);
                context.FillRectangle(new SolidColorBrush(color_for(value, norm, row_limit)), fill_rect);
                if (ShowAnnotations)
                {
                    bool highlighted = row == highlighted_row || column == highlighted_column;
                    IBrush value_brush = value > 0.75 ? new SolidColorBrush(Color.FromRgb(34, 34, 34)) : Brushes.White;
                    var value_text = text(format_value(value), 13, value_brush, highlighted ? FontWeight.Bold : FontWeight.Normal);
                    context.DrawText(value_text, new Point(rect.Center.X - value_text.Width / 2, rect.Center.Y - value_text.Height / 2));
                }
                if (double.IsFinite(StressAbove) && value > StressAbove)
                    context.DrawRectangle(null, stress_pen, rect.Deflate(1.2));
            }
        }
        context.DrawLine(grid_pen, new Point(layout.Matrix.Left, layout.Matrix.Bottom), new Point(layout.Matrix.Right, layout.Matrix.Bottom));

        for (int column = 0; column < column_count; column++)
        {
            double x = layout.Matrix.Left + column * layout.CellWidth;
            string label = labels.ElementAtOrDefault(column) ?? (column + 1).ToString(CultureInfo.InvariantCulture);
            bool draw_label = column % label_stride == 0;
            context.DrawLine(grid_pen, new Point(x + layout.CellWidth / 2, layout.Matrix.Top - 5), new Point(x + layout.CellWidth / 2, layout.Matrix.Top));
            if (!draw_label)
                continue;
            draw_top_vertical_label(context, label, new Point(x + layout.CellWidth / 2, layout.Matrix.Top - 8), column == highlighted_column, text_brush, muted_brush, null);
        }

        if (highlighted_row >= 0)
        {
            var y = layout.Matrix.Top + highlighted_row * layout.CellHeight;
            context.DrawRectangle(null, new Pen(highlight_brush, 2), new Rect(layout.Matrix.Left, y, layout.Matrix.Width, layout.CellHeight).Deflate(0.7));
        }
        if (highlighted_column >= 0)
        {
            var x = layout.Matrix.Left + highlighted_column * layout.CellWidth;
            context.DrawRectangle(null, new Pen(highlight_brush, 2), new Rect(x, layout.Matrix.Top, layout.CellWidth, layout.Matrix.Height).Deflate(0.7));
            if (highlighted_column < column_count && highlighted_column % label_stride != 0)
            {
                string label = labels.ElementAtOrDefault(highlighted_column) ?? (highlighted_column + 1).ToString(CultureInfo.InvariantCulture);
                draw_top_vertical_label(context, label, new Point(x + layout.CellWidth / 2, layout.Matrix.Top - 8), true, text_brush, muted_brush, mask_brush);
            }
        }
    }

    private void observe_rows(IReadOnlyList<SpectralMatrixRowViewModel>? rows)
    {
        if (rows_collection is not null)
            rows_collection.CollectionChanged -= rows_collection_changed;
        foreach (var cell in observed_cells)
            cell.PropertyChanged -= cell_property_changed;
        observed_cells.Clear();
        rows_collection = rows as INotifyCollectionChanged;
        if (rows_collection is not null)
            rows_collection.CollectionChanged += rows_collection_changed;
        observe_cells(rows);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void rows_collection_changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var cell in observed_cells)
            cell.PropertyChanged -= cell_property_changed;
        observed_cells.Clear();
        observe_cells(Rows);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void observe_cells(IReadOnlyList<SpectralMatrixRowViewModel>? rows)
    {
        if (rows is null)
            return;
        foreach (var cell in rows.SelectMany(row => row.Cells).OfType<INotifyPropertyChanged>())
        {
            cell.PropertyChanged += cell_property_changed;
            observed_cells.Add(cell);
        }
    }

    private void cell_property_changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private MatrixLayout calculate_layout(int row_count, int column_count)
    {
        var metrics = calculate_cell_metrics(row_count, column_count, Bounds.Size);
        var matrix = new Rect(metrics.Left, metrics.Top, metrics.CellWidth * column_count, metrics.CellHeight * row_count);
        return new MatrixLayout(matrix, metrics.CellWidth, metrics.CellHeight);
    }

    private MatrixMetrics calculate_cell_metrics(int row_count, int column_count, Size available)
    {
        double left = Math.Max(138, longest_row_label_width() + 28);
        double top = maximum_column_label_height() + 18;
        double right = 18;
        double bottom = 36;
        int columns = Math.Max(1, column_count);
        int rows = Math.Max(1, row_count);
        double value_width = ShowAnnotations ? Math.Max(42, maximum_annotation_width() + 12) : 1.0;
        double value_height = text("100", 13, Brushes.White, FontWeight.Bold).Height + 4;
        double finite_height = double.IsFinite(available.Height) && available.Height > top + bottom
            ? Math.Max(10, (available.Height - top - bottom) / rows)
            : value_height;
        double cell_height = Math.Max(value_height, Math.Min(28, finite_height));
        double finite_width = double.IsFinite(available.Width) && available.Width > left + right
            ? Math.Max(0.01, (available.Width - left - right) / columns)
            : value_width;

        double cell_width = ShowAnnotations
            ? value_width
            : Math.Min(cell_height, finite_width);
        return new MatrixMetrics(left, top, cell_width, cell_height);
    }

    private int maximum_column_count() => Rows?.Select(row => row.Cells.Count).DefaultIfEmpty(0).Max() ?? 0;

    private double longest_row_label_width()
    {
        var rows = Rows ?? [];
        return rows.Select(row => text(row.Name, 13, Brushes.White, FontWeight.Bold).Width).DefaultIfEmpty(110).Max();
    }

    private double maximum_annotation_width()
    {
        var rows = Rows ?? [];
        return rows.SelectMany(row => row.Cells.Select(cell => text(format_value(cell.Value), 13, Brushes.White, FontWeight.Bold).Width)).DefaultIfEmpty(34).Max();
    }

    private double maximum_column_label_height()
    {
        var labels = ColumnLabels ?? [];
        return labels.Select(label => text(label, 13, Brushes.White, FontWeight.Bold).Width).DefaultIfEmpty(0).Max();
    }

    private void draw_titles(DrawingContext context, MatrixLayout layout, IBrush text_brush)
    {
        if (!string.IsNullOrWhiteSpace(XAxisTitle))
        {
            var title = text(XAxisTitle, 13, text_brush, FontWeight.Bold);
            context.DrawText(title, new Point(layout.Matrix.Left + layout.Matrix.Width / 2 - title.Width / 2, layout.Matrix.Bottom + 8));
        }
        if (!string.IsNullOrWhiteSpace(YAxisTitle))
        {
            var title = text(YAxisTitle, 13, text_brush, FontWeight.Bold);
            double title_x = Math.Max(6, layout.Matrix.Left - longest_row_label_width() - 32);
            using (context.PushTransform(Matrix.CreateTranslation(-title.Width / 2, -title.Height / 2) *
                                         Matrix.CreateRotation(-Math.PI / 2) *
                                         Matrix.CreateTranslation(title_x, layout.Matrix.Top + layout.Matrix.Height / 2)))
                context.DrawText(title, new Point());
        }
    }

    private void draw_top_vertical_label(DrawingContext context, string label, Point anchor, bool highlighted, IBrush text_brush, IBrush muted_brush, IBrush? background)
    {
        var formatted = text(label, 13, highlighted ? text_brush : muted_brush, highlighted ? FontWeight.Bold : FontWeight.Normal);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(anchor.X - formatted.Height / 2, anchor.Y)))
        {
            if (background is not null)
                context.FillRectangle(background, new Rect(-3, -2, formatted.Width + 6, formatted.Height + 4));
            context.DrawText(formatted, new Point());
        }
    }

    private void draw_row_label(DrawingContext context, string label, Point anchor, bool highlighted, IBrush text_brush, IBrush muted_brush)
    {
        var formatted = text(label, 13, highlighted ? text_brush : muted_brush, highlighted ? FontWeight.Bold : FontWeight.Normal);
        context.DrawText(formatted, new Point(anchor.X - formatted.Width, anchor.Y - formatted.Height / 2));
    }

    private FormattedText text(string value, double size, IBrush brush, FontWeight weight = default)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, weight == default ? FontWeight.Normal : weight);
        return new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
    }

    private void on_pointer_moved(object? sender, PointerEventArgs e)
    {
        var hit = hit_cell(e.GetPosition(this));
        if (hit.Row == highlighted_row && hit.Column == highlighted_column)
            return;
        highlighted_row = hit.Row;
        highlighted_column = hit.Column;
        InvalidateVisual();
    }

    private void on_double_tapped(object? sender, TappedEventArgs e)
    {
        if (!IsEditMode || !ShowAnnotations)
            return;
        var hit = hit_cell(e.GetPosition(this));
        if (hit.Row < 0 || hit.Column < 0 || Rows is null || hit.Row >= Rows.Count || hit.Column >= Rows[hit.Row].Cells.Count)
            return;
        var cell = Rows[hit.Row].Cells[hit.Column];
        if (!cell.IsEditable)
            return;
        edited_cell = cell;
        highlighted_row = hit.Row;
        highlighted_column = hit.Column;
        editor.Text = format_value(cell.Value);
        editor.IsVisible = true;
        editor.SelectAll();
        InvalidateArrange();
        editor.Focus();
    }

    private (int Row, int Column) hit_cell(Point point)
    {
        for (int row = 0; row < cell_rects.GetLength(0); row++)
        for (int column = 0; column < cell_rects.GetLength(1); column++)
            if (cell_rects[row, column].Contains(point))
                return (row, column);
        return (-1, -1);
    }

    private Rect current_editor_rect()
    {
        if (highlighted_row >= 0 && highlighted_column >= 0 &&
            highlighted_row < cell_rects.GetLength(0) &&
            highlighted_column < cell_rects.GetLength(1))
            return cell_rects[highlighted_row, highlighted_column].Deflate(2);
        return new Rect(0, 0, 0, 0);
    }

    private void commit_editor()
    {
        if (edited_cell is null)
            return;
        string raw = editor.Text ?? "";
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) ||
            float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out invariant))
            edited_cell.Value = invariant / 100.0f;
        close_editor(commit: true);
    }

    private void close_editor(bool commit)
    {
        _ = commit;
        edited_cell = null;
        editor.IsVisible = false;
        InvalidateArrange();
        InvalidateVisual();
    }

    private MatrixNorm calculate_norm(double[] values)
    {
        if (values.Length == 0)
            return new MatrixNorm(0, 1, false);
        double minimum = values.Min();
        double maximum = values.Max();
        bool diverging = minimum < 0 && maximum > 0;
        if (Palette == SpectralMatrixPalette.DivergingBlueRed)
            diverging = true;
        if (diverging)
        {
            double limit = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
            return new MatrixNorm(-limit, limit <= 0 ? 1 : limit, true);
        }
        if (Math.Abs(maximum - minimum) < double.Epsilon)
            maximum = minimum + 1;
        return new MatrixNorm(minimum, maximum, false);
    }

    private int column_label_stride(IReadOnlyList<string> labels, int column_count, double cell_width)
    {
        if (cell_width <= 0)
            return Math.Max(1, column_count);
        double required = labels.Take(column_count)
            .Select(label => text(label, 13, Brushes.White).Height + 4)
            .DefaultIfEmpty(text("0", 13, Brushes.White).Height + 4)
            .Max();
        return Math.Max(1, (int)Math.Ceiling(required / cell_width));
    }

    private static double row_diverging_limit(SpectralMatrixRowViewModel row)
    {
        double limit = row.Cells
            .Select(cell => Math.Abs(Math.Clamp((double)cell.Value, -1.0, 1.0)))
            .Where(double.IsFinite)
            .DefaultIfEmpty(0)
            .Max();
        return limit <= 1e-12 ? 1.0 : limit;
    }

    private Color color_for(double value, MatrixNorm norm, double row_diverging_limit)
    {
        if (!double.IsFinite(value))
            return Color.FromRgb(64, 64, 64);

        if (norm.Diverging)
        {
            double clipped = Math.Clamp(value, -1.0, 1.0);
            double magnitude = Math.Clamp(Math.Abs(clipped) / Math.Max(row_diverging_limit, 1e-12), 0, 1);
            var neutral = Color.FromRgb(54, 54, 58);
            return clipped < 0 ? ramp(neutral, Color.FromRgb(56, 132, 255), magnitude)
                               : ramp(neutral, Color.FromRgb(255, 74, 70), magnitude);
        }

        double t = Math.Clamp((value - norm.Minimum) / Math.Max(norm.Maximum - norm.Minimum, 1e-12), 0, 1);
        return PlotColorMaps.ColorAt(PlotColorPalette.Viridis, t);
    }

    private static Color ramp(Color low, Color high, double t)
    {
        byte channel(byte a, byte b) => (byte)Math.Clamp(a + (b - a) * t, 0, 255);
        return Color.FromRgb(channel(low.R, high.R), channel(low.G, high.G), channel(low.B, high.B));
    }

    private static string format_value(double value)
    {
        double percent = value * 100.0;
        if (Math.Abs(percent) < 0.05)
            return "0";
        if (Math.Abs(percent - Math.Round(percent)) < 0.05 && Math.Abs(percent) >= 99.95)
            return Math.Round(percent).ToString("0", CultureInfo.InvariantCulture);
        return percent.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private readonly record struct MatrixMetrics(double Left, double Top, double CellWidth, double CellHeight);
    private readonly record struct MatrixLayout(Rect Matrix, double CellWidth, double CellHeight);
    private readonly record struct MatrixNorm(double Minimum, double Maximum, bool Diverging);
}
