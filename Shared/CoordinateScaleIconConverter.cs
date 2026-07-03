using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using gated.Models;

namespace gated.Shared;

public sealed class CoordinateScaleIconConverter : IValueConverter
{
    private static readonly Geometry linear_icon = Geometry.Parse(
        "M 1.25,2.5 L 2.75,2.5 L 2.75,13.5 L 1.25,13.5 Z "
        + "M 13.25,2.5 L 14.75,2.5 L 14.75,13.5 L 13.25,13.5 Z "
        + "M 4.35,5 L 5.65,5 L 5.65,11 L 4.35,11 Z "
        + "M 7.35,5 L 8.65,5 L 8.65,11 L 7.35,11 Z "
        + "M 10.35,5 L 11.65,5 L 11.65,11 L 10.35,11 Z");

    private static readonly Geometry logicle_icon = Geometry.Parse(
        "M 1.25,2.5 L 2.75,2.5 L 2.75,13.5 L 1.25,13.5 Z "
        + "M 13.25,2.5 L 14.75,2.5 L 14.75,13.5 L 13.25,13.5 Z "
        + "M 4.35,5 L 5.65,5 L 5.65,11 L 4.35,11 Z "
        + "M 8.85,5 L 10.15,5 L 10.15,11 L 8.85,11 Z "
        + "M 11.35,5 L 12.65,5 L 12.65,11 L 11.35,11 Z");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CoordinateScaleKind.Logicle ? logicle_icon : linear_icon;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CoordinateScaleToolTipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CoordinateScaleKind.Logicle ? "Logicle scale" : "Linear scale";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
