using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace gated.Controls;

public partial class PlatformPopulationGrid : UserControl
{
    public static readonly StyledProperty<bool> ShowPopulationColorsProperty =
        AvaloniaProperty.Register<PlatformPopulationGrid, bool>(nameof(ShowPopulationColors));

    public bool ShowPopulationColors
    {
        get => GetValue(ShowPopulationColorsProperty);
        set => SetValue(ShowPopulationColorsProperty, value);
    }

    public PlatformPopulationGrid()
    {
        InitializeComponent();
    }
}

public sealed class PlatformPopulationColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && index >= 0
            ? new SolidColorBrush(PlatformPalette.ColorForIndex(index))
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
