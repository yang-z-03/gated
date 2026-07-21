using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace gated.Controls;

public sealed class SpinnerView : Control
{
    private readonly DispatcherTimer timer;
    private int frame;

    public SpinnerView()
    {
        Width = 48;
        Height = 48;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            frame = (frame + 1) % 12;
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        timer.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == nameof(ActualThemeVariant))
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        double size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0)
            return;

        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        double outer = size * 0.46;
        double inner = size * 0.25;
        var color = ActualThemeVariant == ThemeVariant.Dark
            ? Colors.White
            : Color.FromRgb(72, 72, 72);
        for (int index = 0; index < 12; index++)
        {
            int age = (index - frame + 12) % 12;
            byte alpha = Convert.ToByte(42 + (11 - age) * 15);
            double angle = index / 12.0 * Math.PI * 2.0;
            var start = new Point(center.X + Math.Cos(angle) * inner, center.Y + Math.Sin(angle) * inner);
            var end = new Point(center.X + Math.Cos(angle) * outer, center.Y + Math.Sin(angle) * outer);
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), Math.Max(1.2, size * 0.08), lineCap: PenLineCap.Round), start, end);
        }
    }
}
