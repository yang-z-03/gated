using System;
using System.Linq;
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

    public static Color ColorForSeriesKey(string key)
    {
        int? source_id = trailing_number(key);
        return source_id.HasValue ? ColorForIndex(source_id.Value) : ColorForIndex(0);
    }

    private static int? trailing_number(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var digits = new string(key.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out int value) ? value : null;
    }
}
