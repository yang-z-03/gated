using System;
using Avalonia.Media;

namespace gated.Controls;

internal static class PlatformPalette
{
    private static readonly Color[] colors =
    [
        Color.FromRgb(92, 168, 255),
        Color.FromRgb(255, 155, 92),
        Color.FromRgb(92, 214, 147),
        Color.FromRgb(184, 135, 255),
        Color.FromRgb(255, 105, 178),
        Color.FromRgb(245, 208, 90),
        Color.FromRgb(82, 219, 216),
        Color.FromRgb(236, 132, 132)
    ];

    public static Color ColorForIndex(int index) => colors[Math.Abs(index) % colors.Length];

}
