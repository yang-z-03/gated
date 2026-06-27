using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using gated.Services;

namespace gated.Models;

public enum ChannelSemanticKind
{
    Other,
    FSC,
    SSC,
    Time
}

public sealed class CytometerPreference
{
    public string Name { get; set; } = "";
    public ObservableCollection<ChannelAssumption> Channels { get; set; } = new();
}

public sealed class ChannelAssumption
{
    public string Pattern { get; set; } = "";
    public ChannelSemanticKind Kind { get; set; }
    public CoordinateScaleKind Scale { get; set; } = CoordinateScaleKind.Logicle;
    public bool UseObservedRange { get; set; }
}

public sealed class CytometerPreferenceStore
{
    public string SelectedCytometerName { get; set; } = Configuration.DefaultCytometerName;
    public ObservableCollection<CytometerPreference> Cytometers { get; set; } = new();
}

public static class Configuration
{
    public const string DefaultCytometerName = "Default";
    public const string CytometerMetadataKey = "Cytometer";
    private const string file_name = "cytometer-preferences.json";
    private static readonly JsonSerializerOptions json_options = new() { WriteIndented = true };
    private static CytometerPreferenceStore store = load_store();

    public static CytometerPreferenceStore Preferences => store;

    public static IReadOnlyList<string> CytometerNames =>
        store.Cytometers.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

    public static CytometerPreference CreatePreferenceFromDefault(string name)
    {
        var source = store.Cytometers.FirstOrDefault(item => item.Name == DefaultCytometerName) ?? create_default_preference();
        var created = clone_preference(source);
        created.Name = normalize_cytometer_name(name);
        return created;
    }

    public static void RememberCytometer(string? name, IEnumerable<ChannelDefinition> channels)
    {
        name = normalize_cytometer_name(name);
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultCytometerName;

        var preference = get_or_create_preference(name);
        foreach (var channel in channels)
        {
            if (preference.Channels.Any(item => pattern_matches(item.Pattern, channel.Name)))
                continue;
            var inferred = infer_channel(channel.Name);
            if (inferred.Kind == ChannelSemanticKind.Other)
                continue;
            preference.Channels.Add(inferred);
        }
        SavePreferences();
    }

    public static string CytometerNameForSample(FlowSample? sample)
    {
        if (sample is null)
            return DefaultCytometerName;
        if (!sample.Metadata.TryGetValue(CytometerMetadataKey, out var name) || string.IsNullOrWhiteSpace(name))
        {
            sample.Metadata[CytometerMetadataKey] = DefaultCytometerName;
            return DefaultCytometerName;
        }
        return normalize_cytometer_name(name);
    }

    public static void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(WindowPlacementStore.ConfigDirectory);
            File.WriteAllText(store_path(), JsonSerializer.Serialize(store, json_options));
        }
        catch
        {
        }
    }

    public static void ReloadPreferences() => store = load_store();

    public static void ResetPreferences()
    {
        store = ensure_defaults(new CytometerPreferenceStore());
        SavePreferences();
    }

    public static bool IsTimeChannel(string channel_name) =>
        ChannelKind(channel_name) == ChannelSemanticKind.Time;

    public static bool IsFscChannel(string channel_name) =>
        ChannelKind(channel_name) == ChannelSemanticKind.FSC;

    public static bool IsSscChannel(string channel_name) =>
        ChannelKind(channel_name) == ChannelSemanticKind.SSC;

    public static ChannelSemanticKind ChannelKind(string channel_name, string? cytometer_name = null) =>
        assumption_for_channel(channel_name, cytometer_name)?.Kind ?? infer_channel(channel_name).Kind;

    public static PlatformTransformationKind DefaultPlatformTransformationForChannel(string channel_name) =>
        DefaultCoordinateScaleForChannel(channel_name) == CoordinateScaleKind.Linear
            ? PlatformTransformationKind.Linear
            : PlatformTransformationKind.Logicle;

    public static CoordinateScaleKind DefaultCoordinateScaleForChannel(string channel_name, string? cytometer_name = null) =>
        assumption_for_channel(channel_name, cytometer_name)?.Scale ?? infer_channel(channel_name).Scale;

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

    private static CytometerPreferenceStore load_store()
    {
        try
        {
            string path = store_path();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<CytometerPreferenceStore>(File.ReadAllText(path), json_options);
                if (loaded is not null)
                    return ensure_defaults(loaded);
            }
        }
        catch
        {
        }

        return ensure_defaults(new CytometerPreferenceStore());
    }

    private static CytometerPreferenceStore ensure_defaults(CytometerPreferenceStore loaded)
    {
        if (loaded.Cytometers.All(item => item.Name != DefaultCytometerName))
            loaded.Cytometers.Insert(0, create_default_preference());
        if (string.IsNullOrWhiteSpace(loaded.SelectedCytometerName))
            loaded.SelectedCytometerName = DefaultCytometerName;
        return loaded;
    }

    private static CytometerPreference create_default_preference() =>
        new()
        {
            Name = DefaultCytometerName,
            Channels =
            {
                new ChannelAssumption { Pattern = "FSC-A", Kind = ChannelSemanticKind.FSC, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "FSC-H", Kind = ChannelSemanticKind.FSC, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "FSC-W", Kind = ChannelSemanticKind.FSC, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "SSC-A", Kind = ChannelSemanticKind.SSC, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "SSC-H", Kind = ChannelSemanticKind.SSC, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "SSC-W", Kind = ChannelSemanticKind.SSC, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "Time", Kind = ChannelSemanticKind.Time, Scale = CoordinateScaleKind.Linear, UseObservedRange = true }
            }
        };

    private static ChannelAssumption infer_channel(string channel_name)
    {
        if (string.IsNullOrWhiteSpace(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Other };
        if (channel_name.Contains("TIME", StringComparison.OrdinalIgnoreCase))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Time, Scale = CoordinateScaleKind.Linear, UseObservedRange = true };
        if (channel_name.Contains("FSC", StringComparison.OrdinalIgnoreCase))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.FSC, Scale = CoordinateScaleKind.Linear };
        if (channel_name.Contains("SSC", StringComparison.OrdinalIgnoreCase))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.SSC, Scale = CoordinateScaleKind.Linear };
        return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Other, Scale = CoordinateScaleKind.Logicle };
    }

    private static ChannelAssumption? assumption_for_channel(string channel_name, string? cytometer_name)
    {
        cytometer_name = normalize_cytometer_name(cytometer_name);
        var ordered = string.IsNullOrWhiteSpace(cytometer_name)
            ? store.Cytometers
            : store.Cytometers.Where(item => item.Name == cytometer_name).Concat(store.Cytometers.Where(item => item.Name == DefaultCytometerName));
        return ordered.SelectMany(item => item.Channels).FirstOrDefault(item => pattern_matches(item.Pattern, channel_name));
    }

    private static CytometerPreference get_or_create_preference(string name)
    {
        var existing = store.Cytometers.FirstOrDefault(item => item.Name == name);
        if (existing is not null)
            return existing;
        var created = CreatePreferenceFromDefault(name);
        store.Cytometers.Add(created);
        return created;
    }

    private static CytometerPreference clone_preference(CytometerPreference source)
    {
        var clone = new CytometerPreference { Name = source.Name };
        foreach (var assumption in source.Channels)
            clone.Channels.Add(new ChannelAssumption
            {
                Pattern = assumption.Pattern,
                Kind = assumption.Kind,
                Scale = assumption.Scale,
                UseObservedRange = assumption.UseObservedRange
            });
        return clone;
    }

    private static bool pattern_matches(string pattern, string channel_name) =>
        !string.IsNullOrWhiteSpace(pattern) &&
        !string.IsNullOrWhiteSpace(channel_name) &&
        channel_name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static string normalize_cytometer_name(string? name) =>
        string.IsNullOrWhiteSpace(name) ? DefaultCytometerName : name.Trim();

    private static string store_path() => Path.Combine(WindowPlacementStore.ConfigDirectory, file_name);

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
        for (int offset = 1; offset >= 0; offset--)
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
