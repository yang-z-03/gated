
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia;
using System;

namespace gated.Styles.Themes;

public static class MathHelpers
{
    internal const double DoubleEpsilon = 2.220446049250313E-16;
    internal const float FloatEpsilon = 1.1920929E-07f;

    private static (double min, double max) GetMinMax(double a, double b)
    {
        return a >= b ? (b, a) : (a, b);
    }

    private static (float min, float max) GetMinMax(float a, float b)
    {
        return a >= b ? (b, a) : (a, b);
    }

    private static (decimal min, decimal max) GetMinMax(decimal a, decimal b)
    {
        return a >= b ? (b, a) : (a, b);
    }

    private static (int min, int max) GetMinMax(int a, int b)
    {
        return a >= b ? (b, a) : (a, b);
    }

    /// <summary>
    ///     Clamps a value between a minimum and maximum value.
    /// </summary>
    /// <param name="value"> The value to clamp. </param>
    /// <param name="min"> The minimum value. </param>
    /// <param name="max"> The maximum value. </param>
    /// <returns></returns>
    public static double SafeClamp(double value, double min, double max)
    {
        (min, max) = GetMinMax(min, max);
        if (value < min) return min;
        return value > max ? max : value;
    }

    /// <summary>
    ///     Clamps a value between a minimum and maximum value.
    /// </summary>
    /// <param name="value"> The value to clamp. </param>
    /// <param name="min"> The minimum value. </param>
    /// <param name="max"> The maximum value. </param>
    /// <returns></returns>
    public static decimal SafeClamp(decimal value, decimal min, decimal max)
    {
        (min, max) = GetMinMax(min, max);
        if (value < min) return min;
        return value > max ? max : value;
    }

    /// <summary>
    ///     Clamps a value between a minimum and maximum value.
    /// </summary>
    /// <param name="value"> The value to clamp. </param>
    /// <param name="min"> The minimum value. </param>
    /// <param name="max"> The maximum value. </param>
    /// <returns></returns>
    public static int SafeClamp(int value, int min, int max)
    {
        (min, max) = GetMinMax(min, max);
        if (value < min) return min;
        return value > max ? max : value;
    }

    /// <summary>
    ///     Clamps a value between a minimum and maximum value.
    /// </summary>
    /// <param name="value"> The value to clamp. </param>
    /// <param name="min"> The minimum value. </param>
    /// <param name="max"> The maximum value. </param>
    /// <returns></returns>
    public static float SafeClamp(float value, float min, float max)
    {
        (min, max) = GetMinMax(min, max);
        if (value < min) return min;
        return value > max ? max : value;
    }

    /// <summary>
    ///     AreClose - Returns whether or not two doubles are "close".  That is, whether or
    ///     not they are within epsilon of each other. Note that this epsilon is proportional
    /// </summary>
    /// <param name="value1"></param>
    /// <param name="value2"></param>
    /// <returns></returns>
    public static bool AreClose(double value1, double value2)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value1 == value2) return true;
        var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DoubleEpsilon;
        var delta = value1 - value2;
        return -eps < delta && eps > delta;
    }

    /// <summary>
    /// AreClose - Returns whether or not two doubles are "close".  That is, whether or
    /// not they are within epsilon of each other.
    /// </summary>
    /// <param name="value1"> The first double to compare. </param>
    /// <param name="value2"> The second double to compare. </param>
    /// <param name="eps"> The fixed epsilon value used to compare.</param>
    public static bool AreClose(double value1, double value2, double eps)
    {
        //in case they are Infinities (then epsilon check does not work)
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value1 == value2) return true;
        var delta = value1 - value2;
        return -eps < delta && eps > delta;
    }

    /// <summary>
    ///     AreClose - Returns whether or not two doubles are "close".  That is, whether or
    ///     not they are within epsilon of each other. Note that this epsilon is proportional
    /// </summary>
    /// <param name="value1"></param>
    /// <param name="value2"></param>
    /// <returns></returns>
    public static bool AreClose(float value1, float value2)
    {
        //in case they are Infinities (then epsilon check does not work)
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value1 == value2) return true;
        var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0f) * FloatEpsilon;
        var delta = value1 - value2;
        return -eps < delta && eps > delta;
    }

    /// <summary>
    /// LessThan - Returns whether or not the first double is less than the second double.
    /// That is, whether or not the first is strictly less than *and* not within epsilon of
    /// the other number.
    /// </summary>
    /// <param name="value1"> The first double to compare. </param>
    /// <param name="value2"> The second double to compare. </param>
    public static bool LessThan(double value1, double value2)
    {
        return value1 < value2 && !AreClose(value1, value2);
    }

    /// <summary>
    /// LessThan - Returns whether or not the first float is less than the second float.
    /// That is, whether or not the first is strictly less than *and* not within epsilon of
    /// the other number.
    /// </summary>
    /// <param name="value1"> The first single float to compare. </param>
    /// <param name="value2"> The second single float to compare. </param>
    public static bool LessThan(float value1, float value2)
    {
        return value1 < value2 && !AreClose(value1, value2);
    }

    /// <summary>
    /// GreaterThan - Returns whether or not the first double is greater than the second double.
    /// That is, whether or not the first is strictly greater than *and* not within epsilon of
    /// the other number.
    /// </summary>
    /// <param name="value1"> The first double to compare. </param>
    /// <param name="value2"> The second double to compare. </param>
    public static bool GreaterThan(double value1, double value2)
    {
        return value1 > value2 && !AreClose(value1, value2);
    }

    /// <summary>
    /// GreaterThan - Returns whether or not the first float is greater than the second float.
    /// That is, whether or not the first is strictly greater than *and* not within epsilon of
    /// the other number.
    /// </summary>
    /// <param name="value1"> The first float to compare. </param>
    /// <param name="value2"> The second float to compare. </param>
    public static bool GreaterThan(float value1, float value2)
    {
        return value1 > value2 && !AreClose(value1, value2);
    }

    /// <summary>
    /// LessThanOrClose - Returns whether or not the first double is less than or close to
    /// the second double.  That is, whether or not the first is strictly less than or within
    /// epsilon of the other number.
    /// </summary>
    /// <param name="value1"> The first double to compare. </param>
    /// <param name="value2"> The second double to compare. </param>
    public static bool LessThanOrClose(double value1, double value2)
    {
        return value1 < value2 || AreClose(value1, value2);
    }

    /// <summary>
    /// LessThanOrClose - Returns whether or not the first float is less than or close to
    /// the second float.  That is, whether or not the first is strictly less than or within
    /// epsilon of the other number.
    /// </summary>
    /// <param name="value1"> The first float to compare. </param>
    /// <param name="value2"> The second float to compare. </param>
    public static bool LessThanOrClose(float value1, float value2)
    {
        return value1 < value2 || AreClose(value1, value2);
    }

    /// <summary>
    /// GreaterThanOrClose - Returns whether or not the first double is greater than or close to
    /// the second double.  That is, whether or not the first is strictly greater than or within
    /// epsilon of the other number.
    /// </summary>
    /// <param name="value1"> The first double to compare. </param>
    /// <param name="value2"> The second double to compare. </param>
    public static bool GreaterThanOrClose(double value1, double value2)
    {
        return value1 > value2 || AreClose(value1, value2);
    }

    /// <summary>
    /// GreaterThanOrClose - Returns whether or not the first float is greater than or close to
    /// the second float.  That is, whether or not the first is strictly greater than or within
    /// epsilon of the other number.
    /// </summary>
    /// <param name="value1"> The first float to compare. </param>
    /// <param name="value2"> The second float to compare. </param>
    public static bool GreaterThanOrClose(float value1, float value2)
    {
        return value1 > value2 || AreClose(value1, value2);
    }

    /// <summary>
    /// IsOne - Returns whether or not the double is "close" to 1.  Same as AreClose(double, 1),
    /// but this is faster.
    /// </summary>
    /// <param name="value"> The double to compare to 1. </param>
    public static bool IsOne(double value)
    {
        return Math.Abs(value - 1.0) < 10.0 * DoubleEpsilon;
    }

    /// <summary>
    /// IsOne - Returns whether or not the float is "close" to 1.  Same as AreClose(float, 1),
    /// but this is faster.
    /// </summary>
    /// <param name="value"> The float to compare to 1. </param>
    public static bool IsOne(float value)
    {
        return Math.Abs(value - 1.0f) < 10.0f * FloatEpsilon;
    }

    /// <summary>
    /// IsZero - Returns whether or not the double is "close" to 0.  Same as AreClose(double, 0),
    /// but this is faster.
    /// </summary>
    /// <param name="value"> The double to compare to 0. </param>
    public static bool IsZero(double value)
    {
        return Math.Abs(value) < 10.0 * DoubleEpsilon;
    }

    /// <summary>
    /// IsZero - Returns whether or not the float is "close" to 0.  Same as AreClose(float, 0),
    /// but this is faster.
    /// </summary>
    /// <param name="value"> The float to compare to 0. </param>
    public static bool IsZero(float value)
    {
        return Math.Abs(value) < 10.0f * FloatEpsilon;
    }

    /// <summary>
    /// Converts an angle in degrees to radians.
    /// </summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <returns>The angle in radians.</returns>
    public static double DegreeToRadians(double angle)
    {
        return angle * (Math.PI / 180d);
    }

    /// <summary>
    /// Converts an angle in gradians to radians.
    /// </summary>
    /// <param name="angle">The angle in gradians.</param>
    /// <returns>The angle in radians.</returns>
    public static double GradiansToRadians(double angle)
    {
        return angle * (Math.PI / 200d);
    }

    /// <summary>
    /// Converts an angle in turns to radians.
    /// </summary>
    /// <param name="angle">The angle in turns.</param>
    /// <returns>The angle in radians.</returns>
    public static double TurnToRadians(double angle)
    {
        return angle * 2 * Math.PI;
    }

    /// <summary>
    /// Calculates the point of an angle on an ellipse.
    /// </summary>
    /// <param name="centre">The centre point of the ellipse.</param>
    /// <param name="radiusX">The x radius of the ellipse.</param>
    /// <param name="radiusY">The y radius of the ellipse.</param>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>A point on the ellipse.</returns>
    public static Point GetEllipsePoint(Point centre, double radiusX, double radiusY, double angle)
    {
        return new Point(radiusX * Math.Cos(angle) + centre.X, radiusY * Math.Sin(angle) + centre.Y);
    }
}

public class SemiColorLightPalette : IColorPalette
{
    private static readonly Color[,] Colors = new[,]
    {
        {
            //Red
            Color.FromUInt32(0xFFFEF2ED),
            Color.FromUInt32(0xFFFEDDD2),
            Color.FromUInt32(0xFFFDB7A5),
            Color.FromUInt32(0xFFFB9078),
            Color.FromUInt32(0xFFFA664C),
            Color.FromUInt32(0xFFF93920),
            Color.FromUInt32(0xFFD52515),
            Color.FromUInt32(0xFFB2140C),
            Color.FromUInt32(0xFF8E0805),
            Color.FromUInt32(0xFF6A0103),
        },
        {
            //Pink
            Color.FromUInt32(0xFFFDECEF),
            Color.FromUInt32(0xFFFBCFD8),
            Color.FromUInt32(0xFFF6A0B5),
            Color.FromUInt32(0xFFF27396),
            Color.FromUInt32(0xFFED487B),
            Color.FromUInt32(0xFFE91E63),
            Color.FromUInt32(0xFFC51356),
            Color.FromUInt32(0xFFA20B48),
            Color.FromUInt32(0xFF7E053A),
            Color.FromUInt32(0xFF5A012B),
        },
        {
            //Purple
            Color.FromUInt32(0xFFF7E9F7),
            Color.FromUInt32(0xFFEFCAF0),
            Color.FromUInt32(0xFFDD9BE0),
            Color.FromUInt32(0xFFC96FD1),
            Color.FromUInt32(0xFFB449C2),
            Color.FromUInt32(0xFF9E28B3),
            Color.FromUInt32(0xFF871E9E),
            Color.FromUInt32(0xFF71168A),
            Color.FromUInt32(0xFF5C0F75),
            Color.FromUInt32(0xFF490A61),
        },
        {
            //Violet
            Color.FromUInt32(0xFFF3EDF9),
            Color.FromUInt32(0xFFE2D1F4),
            Color.FromUInt32(0xFFC4A7E9),
            Color.FromUInt32(0xFFA67FDD),
            Color.FromUInt32(0xFF885BD2),
            Color.FromUInt32(0xFF6A3AC7),
            Color.FromUInt32(0xFF572FB3),
            Color.FromUInt32(0xFF46259E),
            Color.FromUInt32(0xFF361C8A),
            Color.FromUInt32(0xFF281475),
        },
        {
            //Indigo
            Color.FromUInt32(0xFFECEFF8),
            Color.FromUInt32(0xFFD1D8F0),
            Color.FromUInt32(0xFFA7B3E1),
            Color.FromUInt32(0xFF8090D3),
            Color.FromUInt32(0xFF5E6FC4),
            Color.FromUInt32(0xFF3F51B5),
            Color.FromUInt32(0xFF3342A1),
            Color.FromUInt32(0xFF28348C),
            Color.FromUInt32(0xFF1F2878),
            Color.FromUInt32(0xFF171D63),
        },
        {
            //Blue
            Color.FromUInt32(0xFFEAF5FF),
            Color.FromUInt32(0xFFCBE7FE),
            Color.FromUInt32(0xFF98CDFD),
            Color.FromUInt32(0xFF65B2FC),
            Color.FromUInt32(0xFF3295FB),
            Color.FromUInt32(0xFF0064FA),
            Color.FromUInt32(0xFF0062D6),
            Color.FromUInt32(0xFF004FB3),
            Color.FromUInt32(0xFF003D8F),
            Color.FromUInt32(0xFF002C6B),
        },
        {
            //LightBlue
            Color.FromUInt32(0xFFE9F7FD),
            Color.FromUInt32(0xFFC9ECFC),
            Color.FromUInt32(0xFF95D8F8),
            Color.FromUInt32(0xFF62C3F5),
            Color.FromUInt32(0xFF30ACF1),
            Color.FromUInt32(0xFF0095EE),
            Color.FromUInt32(0xFF007BCA),
            Color.FromUInt32(0xFF0063A7),
            Color.FromUInt32(0xFF004B83),
            Color.FromUInt32(0xFF00355F),
        },
        {
            //Cyan
            Color.FromUInt32(0xFFE5F7F8),
            Color.FromUInt32(0xFFC2EFF0),
            Color.FromUInt32(0xFF8ADDE2),
            Color.FromUInt32(0xFF58CBD3),
            Color.FromUInt32(0xFF2CB8C5),
            Color.FromUInt32(0xFF05A4B6),
            Color.FromUInt32(0xFF038698),
            Color.FromUInt32(0xFF016979),
            Color.FromUInt32(0xFF004D5B),
            Color.FromUInt32(0xFF00323D),
        },
        {
            //Teal
            Color.FromUInt32(0xFFE4F7F4),
            Color.FromUInt32(0xFFC0F0E8),
            Color.FromUInt32(0xFF87E0D3),
            Color.FromUInt32(0xFF54D1C1),
            Color.FromUInt32(0xFF27C2B0),
            Color.FromUInt32(0xFF00B3A1),
            Color.FromUInt32(0xFF009589),
            Color.FromUInt32(0xFF00776F),
            Color.FromUInt32(0xFF005955),
            Color.FromUInt32(0xFF003C3A),
        },
        {
            //Green
            Color.FromUInt32(0xFFECF7EC),
            Color.FromUInt32(0xFFD0F0D1),
            Color.FromUInt32(0xFFA4E0A7),
            Color.FromUInt32(0xFF7DD182),
            Color.FromUInt32(0xFF5AC262),
            Color.FromUInt32(0xFF3BB346),
            Color.FromUInt32(0xFF30953B),
            Color.FromUInt32(0xFF25772F),
            Color.FromUInt32(0xFF1B5924),
            Color.FromUInt32(0xFF113C18),
        },
        {
            //LightGreen
            Color.FromUInt32(0xFFF3F8EC),
            Color.FromUInt32(0xFFE3F0D0),
            Color.FromUInt32(0xFFC8E2A5),
            Color.FromUInt32(0xFFADD37E),
            Color.FromUInt32(0xFF93C55B),
            Color.FromUInt32(0xFF7BB63C),
            Color.FromUInt32(0xFF649830),
            Color.FromUInt32(0xFF4E7926),
            Color.FromUInt32(0xFF395B1B),
            Color.FromUInt32(0xFF253D12),
        },
        {
            //Lime
            Color.FromUInt32(0xFFF2FAE6),
            Color.FromUInt32(0xFFE3F6C5),
            Color.FromUInt32(0xFFCBED8E),
            Color.FromUInt32(0xFFB7E35B),
            Color.FromUInt32(0xFFA7DA2C),
            Color.FromUInt32(0xFF9BD100),
            Color.FromUInt32(0xFF7EAE00),
            Color.FromUInt32(0xFF638B00),
            Color.FromUInt32(0xFF486800),
            Color.FromUInt32(0xFF2F4600),
        },
        {
            //Yellow
            Color.FromUInt32(0xFFFFFDEA),
            Color.FromUInt32(0xFFFEFBCB),
            Color.FromUInt32(0xFFFDF398),
            Color.FromUInt32(0xFFFCE865),
            Color.FromUInt32(0xFFFBDA32),
            Color.FromUInt32(0xFFFAC800),
            Color.FromUInt32(0xFFD0AA00),
            Color.FromUInt32(0xFFA78B00),
            Color.FromUInt32(0xFF7D6A00),
            Color.FromUInt32(0xFF534800),
        },
        {
            //Amber
            Color.FromUInt32(0xFFFEFBEB),
            Color.FromUInt32(0xFFFCF5CE),
            Color.FromUInt32(0xFFF9E89E),
            Color.FromUInt32(0xFFF6D86F),
            Color.FromUInt32(0xFFF3C641),
            Color.FromUInt32(0xFFF0B114),
            Color.FromUInt32(0xFFC88A0F),
            Color.FromUInt32(0xFFA0660A),
            Color.FromUInt32(0xFF784606),
            Color.FromUInt32(0xFF502B03),
        },
        {
            //Orange
            Color.FromUInt32(0xFFFFF8EA),
            Color.FromUInt32(0xFFFEEECC),
            Color.FromUInt32(0xFFFED998),
            Color.FromUInt32(0xFFFDC165),
            Color.FromUInt32(0xFFFDA633),
            Color.FromUInt32(0xFFFC8800),
            Color.FromUInt32(0xFFD26700),
            Color.FromUInt32(0xFFA84A00),
            Color.FromUInt32(0xFF7E3100),
            Color.FromUInt32(0xFF541D00),
        },
        {
            //Grey
            Color.FromUInt32(0xFFF9F9F9),
            Color.FromUInt32(0xFFE6E8EA),
            Color.FromUInt32(0xFFC6CACD),
            Color.FromUInt32(0xFFA7ABB0),
            Color.FromUInt32(0xFF888D92),
            Color.FromUInt32(0xFF6B7075),
            Color.FromUInt32(0xFF555B61),
            Color.FromUInt32(0xFF41464C),
            Color.FromUInt32(0xFF2E3238),
            Color.FromUInt32(0xFF1C1F23),
        },
        {
            //AIPurple
            Color.FromUInt32(0xFFF8EDFF),
            Color.FromUInt32(0xFFF2DAFF),
            Color.FromUInt32(0xFFE3B5FF),
            Color.FromUInt32(0xFFD191FF),
            Color.FromUInt32(0xFFBD6CFF),
            Color.FromUInt32(0xFFA647FF),
            Color.FromUInt32(0xFF8636DB),
            Color.FromUInt32(0xFF6928B8),
            Color.FromUInt32(0xFF4E1C94),
            Color.FromUInt32(0xFF361270),
        },
    };

    public Color GetColor(int colorIndex, int shadeIndex)
    {
        return Colors[
            MathHelpers.SafeClamp(colorIndex, 0, ColorCount - 1),
            MathHelpers.SafeClamp(shadeIndex, 0, ShadeCount - 1)
        ];
    }

    public int ColorCount => Colors.GetLength(0);

    public int ShadeCount => Colors.GetLength(1);
}

public class SemiColorDarkPalette : IColorPalette
{
    private static readonly Color[,] Colors = new[,]
    {
        {
            //Red
            Color.FromUInt32(0xFF6C090B),
            Color.FromUInt32(0xFF901110),
            Color.FromUInt32(0xFFB42019),
            Color.FromUInt32(0xFFD73324),
            Color.FromUInt32(0xFFFB4932),
            Color.FromUInt32(0xFFFC725A),
            Color.FromUInt32(0xFFFD9983),
            Color.FromUInt32(0xFFFDBEAC),
            Color.FromUInt32(0xFFFEE0D5),
            Color.FromUInt32(0xFFFFF3EF),
        },
        {
            //Pink
            Color.FromUInt32(0xFF5C0730),
            Color.FromUInt32(0xFF800E41),
            Color.FromUInt32(0xFFA41751),
            Color.FromUInt32(0xFFC72261),
            Color.FromUInt32(0xFFEB2F71),
            Color.FromUInt32(0xFFEF5686),
            Color.FromUInt32(0xFFF37E9F),
            Color.FromUInt32(0xFFF7A8BC),
            Color.FromUInt32(0xFFFBD3DC),
            Color.FromUInt32(0xFFFDEEF1),
        },
        {
            //Purple
            Color.FromUInt32(0xFF4A1061),
            Color.FromUInt32(0xFF5E1776),
            Color.FromUInt32(0xFF731F8A),
            Color.FromUInt32(0xFF89289F),
            Color.FromUInt32(0xFFA033B3),
            Color.FromUInt32(0xFFB553C2),
            Color.FromUInt32(0xFFCA78D1),
            Color.FromUInt32(0xFFDDA0E1),
            Color.FromUInt32(0xFFEFCEF0),
            Color.FromUInt32(0xFFF7EBF7),
        },
        {
            //Violet
            Color.FromUInt32(0xFF401B77),
            Color.FromUInt32(0xFF4C248C),
            Color.FromUInt32(0xFF582EA0),
            Color.FromUInt32(0xFF6439B5),
            Color.FromUInt32(0xFF7246C9),
            Color.FromUInt32(0xFF8865D4),
            Color.FromUInt32(0xFFA288DF),
            Color.FromUInt32(0xFFBEADE9),
            Color.FromUInt32(0xFFDDD4F4),
            Color.FromUInt32(0xFFF1EEFA),
        },
        {
            //Indigo
            Color.FromUInt32(0xFF171E65),
            Color.FromUInt32(0xFF20297A),
            Color.FromUInt32(0xFF29368E),
            Color.FromUInt32(0xFF3444A3),
            Color.FromUInt32(0xFF4053B7),
            Color.FromUInt32(0xFF5F71C5),
            Color.FromUInt32(0xFF8191D4),
            Color.FromUInt32(0xFFA7B4E2),
            Color.FromUInt32(0xFFD1D8F1),
            Color.FromUInt32(0xFFEDEFF8),
        },
        {
            //Blue
            Color.FromUInt32(0xFF053170),
            Color.FromUInt32(0xFF0A4694),
            Color.FromUInt32(0xFF135CB8),
            Color.FromUInt32(0xFF1D75DB),
            Color.FromUInt32(0xFF2990FF),
            Color.FromUInt32(0xFF54A9FF),
            Color.FromUInt32(0xFF7FC1FF),
            Color.FromUInt32(0xFFA9D7FF),
            Color.FromUInt32(0xFFD4ECFF),
            Color.FromUInt32(0xFFEFF8FF),
        },
        {
            //LightBlue
            Color.FromUInt32(0xFF003761),
            Color.FromUInt32(0xFF004D85),
            Color.FromUInt32(0xFF0366A9),
            Color.FromUInt32(0xFF0A81CC),
            Color.FromUInt32(0xFF139FF0),
            Color.FromUInt32(0xFF40B4F3),
            Color.FromUInt32(0xFF6EC8F6),
            Color.FromUInt32(0xFF9DDCF9),
            Color.FromUInt32(0xFFCEEEFC),
            Color.FromUInt32(0xFFEBF8FE),
        },
        {
            //Cyan
            Color.FromUInt32(0xFF04343D),
            Color.FromUInt32(0xFF074F5C),
            Color.FromUInt32(0xFF0A6C7B),
            Color.FromUInt32(0xFF0E8999),
            Color.FromUInt32(0xFF13A8B8),
            Color.FromUInt32(0xFF38BBC6),
            Color.FromUInt32(0xFF62CDD4),
            Color.FromUInt32(0xFF91DFE3),
            Color.FromUInt32(0xFFC6EFF1),
            Color.FromUInt32(0xFFE7F7F8),
        },
        {
            //Teal
            Color.FromUInt32(0xFF023C39),
            Color.FromUInt32(0xFF045A55),
            Color.FromUInt32(0xFF07776F),
            Color.FromUInt32(0xFF0A9588),
            Color.FromUInt32(0xFF0EB3A1),
            Color.FromUInt32(0xFF33C2B0),
            Color.FromUInt32(0xFF5ED1C1),
            Color.FromUInt32(0xFF8EE1D3),
            Color.FromUInt32(0xFFC4F0E8),
            Color.FromUInt32(0xFFE6F7F4),
        },
        {
            //Green
            Color.FromUInt32(0xFF123C19),
            Color.FromUInt32(0xFF1C5A25),
            Color.FromUInt32(0xFF277731),
            Color.FromUInt32(0xFF32953D),
            Color.FromUInt32(0xFF3EB349),
            Color.FromUInt32(0xFF5DC264),
            Color.FromUInt32(0xFF7FD184),
            Color.FromUInt32(0xFFA6E1A8),
            Color.FromUInt32(0xFFD0F0D1),
            Color.FromUInt32(0xFFECF7EC),
        },
        {
            //LightGreen
            Color.FromUInt32(0xFF263D13),
            Color.FromUInt32(0xFF3B5C1D),
            Color.FromUInt32(0xFF517B28),
            Color.FromUInt32(0xFF679934),
            Color.FromUInt32(0xFF7FB840),
            Color.FromUInt32(0xFF97C65F),
            Color.FromUInt32(0xFFB0D481),
            Color.FromUInt32(0xFFC9E3A7),
            Color.FromUInt32(0xFFE4F1D1),
            Color.FromUInt32(0xFFF3F8ED),
        },
        {
            //Lime
            Color.FromUInt32(0xFF314603),
            Color.FromUInt32(0xFF4B6905),
            Color.FromUInt32(0xFF678D09),
            Color.FromUInt32(0xFF84B00C),
            Color.FromUInt32(0xFFA2D311),
            Color.FromUInt32(0xFFAEDC3A),
            Color.FromUInt32(0xFFBDE566),
            Color.FromUInt32(0xFFCFED96),
            Color.FromUInt32(0xFFE5F6C9),
            Color.FromUInt32(0xFFF3FBE9),
        },
        {
            //Yellow
            Color.FromUInt32(0xFF544903),
            Color.FromUInt32(0xFF7E6C06),
            Color.FromUInt32(0xFFA88E0A),
            Color.FromUInt32(0xFFD2AF0F),
            Color.FromUInt32(0xFFFCCE14),
            Color.FromUInt32(0xFFFDDE43),
            Color.FromUInt32(0xFFFDEB71),
            Color.FromUInt32(0xFFFEF5A0),
            Color.FromUInt32(0xFFFEFBD0),
            Color.FromUInt32(0xFFFFFEEC),
        },
        {
            //Amber
            Color.FromUInt32(0xFF512E09),
            Color.FromUInt32(0xFF794B0F),
            Color.FromUInt32(0xFFA16B16),
            Color.FromUInt32(0xFFCA8F1E),
            Color.FromUInt32(0xFFF2B726),
            Color.FromUInt32(0xFFF5CA50),
            Color.FromUInt32(0xFFF7DB7A),
            Color.FromUInt32(0xFFFAEAA6),
            Color.FromUInt32(0xFFFCF6D2),
            Color.FromUInt32(0xFFFEFBED),
        },
        {
            //Orange
            Color.FromUInt32(0xFF551F03),
            Color.FromUInt32(0xFF803506),
            Color.FromUInt32(0xFFAA500A),
            Color.FromUInt32(0xFFD56F0F),
            Color.FromUInt32(0xFFFF9214),
            Color.FromUInt32(0xFFFFAE43),
            Color.FromUInt32(0xFFFFC772),
            Color.FromUInt32(0xFFFFDDA1),
            Color.FromUInt32(0xFFFFEFD0),
            Color.FromUInt32(0xFFFFF9ED),
        },
        {
            //Grey
            Color.FromUInt32(0xFF1C1F23),
            Color.FromUInt32(0xFF2E3238),
            Color.FromUInt32(0xFF41464C),
            Color.FromUInt32(0xFF555B61),
            Color.FromUInt32(0xFF6B7075),
            Color.FromUInt32(0xFF888D92),
            Color.FromUInt32(0xFFA7ABB0),
            Color.FromUInt32(0xFFC6CACD),
            Color.FromUInt32(0xFFE6E8EA),
            Color.FromUInt32(0xFFF9F9F9),
        },
        {
            //AIPurple
            Color.FromUInt32(0xFF3A1770),
            Color.FromUInt32(0xFF532394),
            Color.FromUInt32(0xFF6F31B8),
            Color.FromUInt32(0xFF8D41DB),
            Color.FromUInt32(0xFFA744FF),
            Color.FromUInt32(0xFFC375FF),
            Color.FromUInt32(0xFFD598FF),
            Color.FromUInt32(0xFFE5BAFF),
            Color.FromUInt32(0xFFF3DDFF),
            Color.FromUInt32(0xFFFBF3FF),
        },
    };

    public Color GetColor(int colorIndex, int shadeIndex)
    {
        return Colors[
            MathHelpers.SafeClamp(colorIndex, 0, ColorCount - 1),
            MathHelpers.SafeClamp(shadeIndex, 0, ShadeCount - 1)
        ];
    }

    public int ColorCount => Colors.GetLength(0);

    public int ShadeCount => Colors.GetLength(1);
}