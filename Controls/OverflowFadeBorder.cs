using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace gated.Controls;

/// <summary>
/// Clips its child horizontally and applies an opacity mask only when the
/// child's natural width exceeds the available width.
/// </summary>
public sealed class OverflowFadeBorder : Decorator
{
    public static readonly StyledProperty<IBrush?> OverflowOpacityMaskProperty =
        AvaloniaProperty.Register<OverflowFadeBorder, IBrush?>(nameof(OverflowOpacityMask));

    public static readonly StyledProperty<double> OverflowClearanceProperty =
        AvaloniaProperty.Register<OverflowFadeBorder, double>(nameof(OverflowClearance), 4);

    private double _contentWidth;

    static OverflowFadeBorder()
    {
        AffectsMeasure<OverflowFadeBorder>(OverflowClearanceProperty);
    }

    public IBrush? OverflowOpacityMask
    {
        get => GetValue(OverflowOpacityMaskProperty);
        set => SetValue(OverflowOpacityMaskProperty, value);
    }

    /// <summary>
    /// Extra width requested after the natural content width. This prevents
    /// auto-sized controls from fading due to layout rounding at the edge.
    /// </summary>
    public double OverflowClearance
    {
        get => GetValue(OverflowClearanceProperty);
        set => SetValue(OverflowClearanceProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
        {
            _contentWidth = 0;
            return default;
        }

        Child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        Size desiredSize = Child.DesiredSize;
        _contentWidth = desiredSize.Width;

        double desiredWidth = desiredSize.Width > 0
            ? desiredSize.Width + Math.Max(0, OverflowClearance)
            : 0;
        double width = double.IsPositiveInfinity(availableSize.Width)
            ? desiredWidth
            : Math.Min(desiredWidth, availableSize.Width);

        return new Size(width, desiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not null)
        {
            double contentWidth = Math.Max(finalSize.Width, _contentWidth);
            Child.Arrange(new Rect(0, 0, contentWidth, finalSize.Height));
        }

        OpacityMask = _contentWidth > finalSize.Width + 0.5
            ? OverflowOpacityMask
            : null;

        return finalSize;
    }
}
