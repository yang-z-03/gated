using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace gated.Models;

public static class Configuration
{
    private static readonly string[] linear_channel_name_fragments = ["FSC", "SSC"];

    public static bool IsTimeChannel(string channel_name) =>
        !string.IsNullOrWhiteSpace(channel_name) &&
        channel_name.Contains("TIME", StringComparison.OrdinalIgnoreCase);

    public static bool PreferLinearChannel(string channel_name)
    {
        if (string.IsNullOrWhiteSpace(channel_name))
            return true;
        if (IsTimeChannel(channel_name))
            return true;
        return linear_channel_name_fragments.Any(fragment =>
            channel_name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static PlatformTransformationKind DefaultPlatformTransformationForChannel(string channel_name) =>
        PreferLinearChannel(channel_name) ? PlatformTransformationKind.Linear : PlatformTransformationKind.Logicle;

    public static CoordinateScaleKind DefaultCoordinateScaleForChannel(string channel_name) =>
        PreferLinearChannel(channel_name) ? CoordinateScaleKind.Linear : CoordinateScaleKind.Logicle;

    public static (double Minimum, double Maximum) DefaultChannelRange(double channel_maximum)
    {
        if (!double.IsFinite(channel_maximum) || channel_maximum <= 0)
            channel_maximum = new LogicleParameters().T;
        double nice_maximum = nice_ceiling(channel_maximum);
        return (0, nice_maximum);
    }

    public static IEnumerable<double> MajorAxisTicks(AxisSettings axis)
    {
        if (axis.Maximum <= axis.Minimum)
            yield break;

        if (axis.ScaleKind == CoordinateScaleKind.Logicle)
        {
            foreach (double value in logicle_major_ticks(axis.Minimum, axis.Maximum))
                yield return value;
            yield break;
        }

        foreach (double value in linear_ticks(axis.Minimum, axis.Maximum, 4, 6))
            yield return value;
    }

    public static IEnumerable<double> MinorAxisTicks(AxisSettings axis)
    {
        if (axis.Maximum <= axis.Minimum)
            yield break;

        if (axis.ScaleKind == CoordinateScaleKind.Logicle)
        {
            var major = MajorAxisTicks(axis).ToHashSet();
            foreach (double value in logicle_ticks(axis.Minimum, axis.Maximum, include_minor_decade_ticks: true))
            {
                if (!major.Contains(value))
                    yield return value;
            }
            yield break;
        }

        double step = choose_linear_step(axis.Minimum, axis.Maximum, 8, 12);
        for (double value = Math.Ceiling(axis.Minimum / step) * step; value <= axis.Maximum + step * 0.001; value += step)
        {
            if (MajorAxisTicks(axis).Any(major => Math.Abs(major - value) < step * 0.01))
                continue;
            yield return value;
        }
    }

    public static string FormatAxisValue(double value)
    {
        if (Math.Abs(value) >= 1_000_000)
            return (value / 1_000_000).ToString("0.#M", CultureInfo.InvariantCulture);
        if (Math.Abs(value) >= 1000)
            return (value / 1000).ToString("0.#k", CultureInfo.InvariantCulture);
        return value.ToString(Math.Abs(value) < 1 ? "0.##" : "0.#", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<double> linear_ticks(double minimum, double maximum, int minimum_count, int maximum_count)
    {
        double step = choose_linear_step(minimum, maximum, minimum_count, maximum_count);
        for (double value = Math.Ceiling(minimum / step) * step; value <= maximum + step * 0.001; value += step)
            yield return Math.Abs(value) < step * 1e-9 ? 0 : value;
    }

    private static double choose_linear_step(double minimum, double maximum, int minimum_count, int maximum_count)
    {
        double span = Math.Max(maximum - minimum, 1e-9);
        double raw = span / Math.Max(1, maximum_count - 1);
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double[] multipliers = [1, 2, 2.5, 5, 10];
        foreach (double multiplier in multipliers)
        {
            double step = multiplier * power;
            int count = (int)Math.Floor(maximum / step) - (int)Math.Ceiling(minimum / step) + 1;
            if (count >= minimum_count && count <= maximum_count)
                return step;
        }
        return 10 * power;
    }

    private static IEnumerable<double> logicle_ticks(double minimum, double maximum, bool include_minor_decade_ticks)
    {
        if (minimum <= 0 && maximum >= 0)
            yield return 0;
        double limit = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        if (limit <= 0)
            yield break;
        double max_power = Math.Ceiling(Math.Log10(limit));
        for (double power = 1; power <= max_power; power++)
        {
            double decade = Math.Pow(10, power);
            for (int multiplier = 1; multiplier <= 9; multiplier++)
            {
                if (!include_minor_decade_ticks && multiplier != 1)
                    continue;
                double value = multiplier * decade;
                if (value >= minimum && value <= maximum)
                    yield return value;
            }
        }
    }

    private static IEnumerable<double> logicle_major_ticks(double minimum, double maximum)
    {
        if (minimum <= 0 && maximum >= 0)
            yield return 0;
        if (maximum <= 0)
            yield break;
        double top_power = Math.Floor(Math.Log10(maximum));
        for (int offset = 2; offset >= 0; offset--)
        {
            double value = Math.Pow(10, top_power - offset);
            if (value >= minimum && value <= maximum)
                yield return value;
        }
    }

    private static double nice_ceiling(double value)
    {
        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double multiplier in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            double candidate = multiplier * power;
            if (candidate >= value)
                return candidate;
        }
        return 10 * power;
    }
}
