using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using gated.Services;

namespace gated.Models;

public enum ChannelSemanticKind
{
    Time = 3,
    Scatter = 2,
    Optical = 0,
    Spectrum = 4,
    Mass = 5,
    Background = 6,
    Model = 7
}

public enum ExcitationLightKind
{
    Unknown,
    UV,
    Violet,
    Blue,
    Green,
    Yellow,
    Red,
    FarRed
}

public sealed class CytometerPreference
{
    public string Name { get; set; } = "";
    public ObservableCollection<SpectralDetectorPreference> Detectors { get; set; } = new();
    public ObservableCollection<ChannelAssumption> Channels { get; set; } = new();
}

public sealed class SpectralDetectorPreference : INotifyPropertyChanged
{
    private ChannelSemanticKind kind = ChannelSemanticKind.Optical;
    private CoordinateScaleKind scale = CoordinateScaleKind.Logicle;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ChannelName { get; set; } = "";
    public ChannelSemanticKind Kind
    {
        get => kind;
        set
        {
            if (kind == value) return;
            kind = value;
            scale = Configuration.DefaultScaleForKind(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }
    public CoordinateScaleKind Scale
    {
        get => scale;
        set
        {
            if (scale == value) return;
            scale = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }
    public bool UseObservedRange { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSpectral { get; set; }
    public ExcitationLightKind ExcitationLight { get; set; }
    public int PlotOrder { get; set; }
}

public sealed class ChannelAssumption
{
    private ChannelSemanticKind kind = ChannelSemanticKind.Optical;
    private CoordinateScaleKind scale = CoordinateScaleKind.Logicle;
    public string Pattern { get; set; } = "";
    public ChannelSemanticKind Kind
    {
        get => kind;
        set
        {
            if (kind == value) return;
            kind = value;
            scale = Configuration.DefaultScaleForKind(value);
        }
    }
    public CoordinateScaleKind Scale { get => scale; set => scale = value; }
    public bool UseObservedRange { get; set; }
}

public sealed class CytometerPreferenceStore
{
    public string SelectedCytometerName { get; set; } = Configuration.DefaultCytometerName;
    public string ThemeName { get; set; } = "Light";
    public ObservableCollection<CytometerPreference> Cytometers { get; set; } = new();
}

public static class Configuration
{
    public const string DefaultCytometerName = "Default";
    public const string CytometerMetadataKey = "Cytometer";
    private const string file_name = "cytometer-preferences.json";
    private static readonly JsonSerializerOptions json_options = new() { WriteIndented = true };
    private static CytometerPreferenceStore store = load_store();
    private static readonly Lazy<IReadOnlyList<string[]>> spectral_database = new(load_spectral_database);
    private static readonly Regex mass_channel_pattern = new(@"^(?:[A-Z][a-z]?\d{2,3}(?:Di|Dd)?|\d{2,3}[A-Z][a-z]?(?:Di|Dd)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        int order = preference.Detectors.Count;
        foreach (var channel in channels)
        {
            if (preference.Detectors.Any(item => string.Equals(item.ChannelName, channel.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            preference.Detectors.Add(infer_detector(channel.Name, name, order++));
            /* Legacy assumptions remain useful for old preference files. */
            if (preference.Channels.Any(item => pattern_matches(item.Pattern, channel.Name)))
                continue;
            var inferred = infer_channel(channel.Name);
            if (inferred.Kind == ChannelSemanticKind.Optical)
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
            normalize_preferences(store);
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
        !string.IsNullOrWhiteSpace(channel_name) && channel_name.Contains("FSC", StringComparison.OrdinalIgnoreCase);

    public static bool IsSscChannel(string channel_name) =>
        !string.IsNullOrWhiteSpace(channel_name) && channel_name.Contains("SSC", StringComparison.OrdinalIgnoreCase);

    public static ChannelSemanticKind ChannelKind(string channel_name, string? cytometer_name = null) =>
        detector_for_channel(channel_name, cytometer_name) is { } detector
            ? normalize_kind(detector.Kind, detector.IsSpectral)
            : assumption_for_channel(channel_name, cytometer_name)?.Kind ?? infer_channel(channel_name).Kind;

    public static PlatformTransformationKind DefaultPlatformTransformationForChannel(string channel_name) =>
        DefaultCoordinateScaleForChannel(channel_name) switch
        {
            CoordinateScaleKind.Linear => PlatformTransformationKind.Linear,
            CoordinateScaleKind.Logarithmic => PlatformTransformationKind.Logarithm,
            CoordinateScaleKind.Arcsinh => PlatformTransformationKind.Arcsinh,
            _ => PlatformTransformationKind.Logicle
        };

    public static CoordinateScaleKind DefaultCoordinateScaleForChannel(string channel_name, string? cytometer_name = null) =>
        detector_for_channel(channel_name, cytometer_name)?.Scale ??
        assumption_for_channel(channel_name, cytometer_name)?.Scale ?? infer_channel(channel_name).Scale;

    public static CoordinateScaleKind DefaultScaleForKind(ChannelSemanticKind kind) =>
        normalize_kind(kind, false) switch
        {
            ChannelSemanticKind.Time or ChannelSemanticKind.Scatter or ChannelSemanticKind.Model => CoordinateScaleKind.Linear,
            ChannelSemanticKind.Mass or ChannelSemanticKind.Background => CoordinateScaleKind.Arcsinh,
            _ => CoordinateScaleKind.Logicle
        };

    public static IReadOnlyList<SpectralDetectorPreference> SpectralDetectors(string? cytometer_name = null) =>
        ordered_preferences(cytometer_name)
            .SelectMany(item => item.Detectors)
            .Where(item => normalize_kind(item.Kind, item.IsSpectral) == ChannelSemanticKind.Spectrum)
            .GroupBy(item => item.ChannelName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.PlotOrder)
            .ThenBy(item => item.ChannelName, NaturalChannelNameComparer.Instance)
            .ToArray();

    public static string? ValidateSpectralDetectors(string? cytometer_name = null)
    {
        var detectors = SpectralDetectors(cytometer_name);
        if (detectors.Count == 0)
            return "No spectral detectors are configured for this cytometer.";
        if (detectors.Any(item => item.ExcitationLight == ExcitationLightKind.Unknown))
            return "Every spectral detector must have an excitation light.";
        if (detectors.GroupBy(item => item.PlotOrder).Any(group => group.Count() > 1))
            return "Spectral detector plot orders must be unique.";
        return null;
    }

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

        if (axis.ScaleKind == CoordinateScaleKind.Logarithmic)
        {
            foreach (double value in signed_log_ticks(axis.Minimum, axis.Maximum, major: true))
                yield return value;
            yield break;
        }

        if (axis.ScaleKind == CoordinateScaleKind.Arcsinh)
        {
            foreach (double value in arcsinh_ticks(axis, major: true))
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

        if (axis.ScaleKind == CoordinateScaleKind.Logarithmic)
        {
            foreach (double value in signed_log_ticks(axis.Minimum, axis.Maximum, major: false))
                yield return value;
            yield break;
        }

        if (axis.ScaleKind == CoordinateScaleKind.Arcsinh)
        {
            foreach (double value in arcsinh_ticks(axis, major: false))
                yield return value;
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
        normalize_preferences(loaded);
        if (loaded.Cytometers.All(item => item.Name != DefaultCytometerName))
            loaded.Cytometers.Insert(0, create_default_preference());
        if (string.IsNullOrWhiteSpace(loaded.SelectedCytometerName))
            loaded.SelectedCytometerName = DefaultCytometerName;
        loaded.ThemeName = string.Equals(loaded.ThemeName, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        return loaded;
    }

    private static void normalize_preferences(CytometerPreferenceStore loaded)
    {
        foreach (var preference in loaded.Cytometers)
        {
            preference.Detectors ??= new ObservableCollection<SpectralDetectorPreference>();
            preference.Channels ??= new ObservableCollection<ChannelAssumption>();
            foreach (var detector in preference.Detectors)
            {
                detector.Kind = normalize_kind(detector.Kind, detector.IsSpectral);
                detector.IsSpectral = false;
                detector.UseObservedRange = detector.Kind == ChannelSemanticKind.Time;
            }
            foreach (var channel in preference.Channels)
            {
                channel.Kind = normalize_kind(channel.Kind, false);
                channel.UseObservedRange = channel.Kind == ChannelSemanticKind.Time;
            }
        }
    }

    private static CytometerPreference create_default_preference() =>
        new()
        {
            Name = DefaultCytometerName,
            Channels =
            {
                new ChannelAssumption { Pattern = "FSC", Kind = ChannelSemanticKind.Scatter, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "SSC", Kind = ChannelSemanticKind.Scatter, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "Time", Kind = ChannelSemanticKind.Time, Scale = CoordinateScaleKind.Linear, UseObservedRange = true },
                new ChannelAssumption { Pattern = "BCKG", Kind = ChannelSemanticKind.Background, Scale = CoordinateScaleKind.Arcsinh },
                new ChannelAssumption { Pattern = "Width", Kind = ChannelSemanticKind.Model, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "Center", Kind = ChannelSemanticKind.Model, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "Offset", Kind = ChannelSemanticKind.Model, Scale = CoordinateScaleKind.Linear },
                new ChannelAssumption { Pattern = "Residual", Kind = ChannelSemanticKind.Model, Scale = CoordinateScaleKind.Linear }
            }
        };

    private static ChannelAssumption infer_channel(string channel_name)
    {
        if (string.IsNullOrWhiteSpace(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Optical, Scale = CoordinateScaleKind.Logicle };
        if (is_time_channel_name(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Time, Scale = CoordinateScaleKind.Linear, UseObservedRange = true };
        if (is_scatter_channel_name(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Scatter, Scale = CoordinateScaleKind.Linear };
        if (is_background_channel_name(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Background, Scale = CoordinateScaleKind.Arcsinh };
        if (is_model_channel_name(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Model, Scale = CoordinateScaleKind.Linear };
        if (is_mass_channel_name(channel_name))
            return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Mass, Scale = CoordinateScaleKind.Arcsinh };
        return new ChannelAssumption { Pattern = channel_name, Kind = ChannelSemanticKind.Optical, Scale = CoordinateScaleKind.Logicle };
    }

    private static SpectralDetectorPreference infer_detector(string channel_name, string cytometer_name, int order)
    {
        var inferred = infer_channel(channel_name);
        if (inferred.Kind is ChannelSemanticKind.Time or ChannelSemanticKind.Scatter or ChannelSemanticKind.Mass or ChannelSemanticKind.Background or ChannelSemanticKind.Model)
            return new SpectralDetectorPreference { ChannelName = channel_name, Kind = inferred.Kind, Scale = inferred.Scale, UseObservedRange = inferred.UseObservedRange, PlotOrder = order };

        bool area = channel_name.EndsWith("-A", StringComparison.OrdinalIgnoreCase);
        bool excluded = channel_name.Contains("COMP", StringComparison.OrdinalIgnoreCase) ||
                        channel_name.Contains("IMAG", StringComparison.OrdinalIgnoreCase) ||
                        channel_name.Contains("EVENT", StringComparison.OrdinalIgnoreCase) ||
                        channel_name.Contains("SORT", StringComparison.OrdinalIgnoreCase) ||
                        channel_name.Contains("LIGHTLOSS", StringComparison.OrdinalIgnoreCase);
        bool known = try_known_detector(channel_name, cytometer_name, out var known_light, out int known_order);
        var light = known ? known_light : infer_excitation(channel_name, cytometer_name);
        bool spectral = area && !excluded;
        return new SpectralDetectorPreference
        {
            ChannelName = channel_name,
            Kind = spectral ? ChannelSemanticKind.Spectrum : ChannelSemanticKind.Optical,
            Scale = CoordinateScaleKind.Logicle,
            ExcitationLight = light,
            PlotOrder = known ? known_order : order
        };
    }

    private static bool try_known_detector(string channel_name, string cytometer_name, out ExcitationLightKind light, out int order)
    {
        light = ExcitationLightKind.Unknown; order = -1;
        string name = cytometer_name.ToUpperInvariant();
        (int Channel, int Laser) columns = name.Contains("NORTHERN") ? (2, 3) : name.Contains("AURORA") ? (0, 1) :
            name.Contains("ID7000") ? (4, 5) : name.Contains("DISCOVER") ? (6, 7) : name.Contains("OPTEON") ? (8, 9) :
            name.Contains("MOSAIC") ? (10, 11) : name.Contains("XENITH") ? (12, 14) :
            name.Contains("SYMPHONY") || name.Contains("A5") ? (15, 16) : (-1, -1);
        if (columns.Channel < 0) return false;
        var rows = spectral_database.Value;
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Length <= Math.Max(columns.Channel, columns.Laser) || !string.Equals(row[columns.Channel], channel_name, StringComparison.OrdinalIgnoreCase)) continue;
            light = row[columns.Laser].Trim().ToUpperInvariant() switch
            {
                "DEEPUV" or "UV" => ExcitationLightKind.UV, "VIOLET" => ExcitationLightKind.Violet,
                "BLUE" => ExcitationLightKind.Blue, "GREEN" => ExcitationLightKind.Green,
                "YELLOWGREEN" or "YELLOW" => ExcitationLightKind.Yellow, "RED" => ExcitationLightKind.Red,
                "IR" or "FARRED" => ExcitationLightKind.FarRed, _ => ExcitationLightKind.Unknown
            };
            order = index;
            return light != ExcitationLightKind.Unknown;
        }
        return false;
    }

    private static IReadOnlyList<string[]> load_spectral_database()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://gated/.github/resources/AutoSpectral/inst/extdata/cytometer_database.csv"));
            using var reader = new StreamReader(stream);
            _ = reader.ReadLine();
            var rows = new List<string[]>();
            while (reader.ReadLine() is { } line) rows.Add(line.Split(','));
            return rows;
        }
        catch { return Array.Empty<string[]>(); }
    }

    private static ExcitationLightKind infer_excitation(string channel_name, string cytometer_name)
    {
        string name = channel_name.ToUpperInvariant();
        if (name.StartsWith("UV") || name.StartsWith("U") || name.StartsWith("355CH") || name.StartsWith("320CH")) return ExcitationLightKind.UV;
        if (name.StartsWith("V") || name.StartsWith("405CH")) return ExcitationLightKind.Violet;
        if (name.StartsWith("B") || name.StartsWith("488CH")) return ExcitationLightKind.Blue;
        if (name.StartsWith("G") || name.StartsWith("532CH")) return ExcitationLightKind.Green;
        if (name.StartsWith("YG") || name.StartsWith("Y") || name.StartsWith("561CH")) return ExcitationLightKind.Yellow;
        if (name.StartsWith("FR") || name.StartsWith("IR") || name.StartsWith("781CH") || name.StartsWith("808CH")) return ExcitationLightKind.FarRed;
        if (name.StartsWith("R") || name.StartsWith("637CH") || name.StartsWith("640CH")) return ExcitationLightKind.Red;
        if (cytometer_name.Contains("XENITH", StringComparison.OrdinalIgnoreCase) && try_xenith_light(name, out var xenith)) return xenith;
        return ExcitationLightKind.Unknown;
    }

    private static bool try_xenith_light(string name, out ExcitationLightKind light)
    {
        light = ExcitationLightKind.Unknown;
        if (!name.StartsWith("FL", StringComparison.Ordinal) || !int.TryParse(new string(name.Skip(2).TakeWhile(char.IsDigit).ToArray()), out int number))
            return false;
        light = number switch
        {
            0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 => ExcitationLightKind.UV,
            12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 => ExcitationLightKind.Violet,
            36 or 37 or 38 or 39 or 40 or 41 or 42 => ExcitationLightKind.Blue,
            24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 => ExcitationLightKind.Yellow,
            43 or 44 or 45 or 46 or 47 => ExcitationLightKind.Red,
            48 or 49 or 50 => ExcitationLightKind.FarRed,
            _ => ExcitationLightKind.Unknown
        };
        return light != ExcitationLightKind.Unknown;
    }

    private static ChannelAssumption? assumption_for_channel(string channel_name, string? cytometer_name)
    {
        cytometer_name = normalize_cytometer_name(cytometer_name);
        var ordered = string.IsNullOrWhiteSpace(cytometer_name)
            ? store.Cytometers
            : store.Cytometers.Where(item => item.Name == cytometer_name).Concat(store.Cytometers.Where(item => item.Name == DefaultCytometerName));
        return ordered.SelectMany(item => item.Channels).FirstOrDefault(item => pattern_matches(item.Pattern, channel_name));
    }

    private static SpectralDetectorPreference? detector_for_channel(string channel_name, string? cytometer_name) =>
        ordered_preferences(cytometer_name).SelectMany(item => item.Detectors)
            .FirstOrDefault(item => string.Equals(item.ChannelName, channel_name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<CytometerPreference> ordered_preferences(string? cytometer_name)
    {
        cytometer_name = normalize_cytometer_name(cytometer_name);
        return string.IsNullOrWhiteSpace(cytometer_name)
            ? store.Cytometers
            : store.Cytometers.Where(item => item.Name == cytometer_name).Concat(store.Cytometers.Where(item => item.Name == DefaultCytometerName));
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
        foreach (var detector in source.Detectors)
            clone.Detectors.Add(new SpectralDetectorPreference
            {
                ChannelName = detector.ChannelName,
                Kind = detector.Kind,
                Scale = detector.Scale,
                UseObservedRange = detector.UseObservedRange,
                IsSpectral = false,
                ExcitationLight = detector.ExcitationLight,
                PlotOrder = detector.PlotOrder
            });
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
        !string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(channel_name) &&
        (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)
            ? System.Text.RegularExpressions.Regex.IsMatch(channel_name, pattern[6..], System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : channel_name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static ChannelSemanticKind normalize_kind(ChannelSemanticKind kind, bool legacy_is_spectral) =>
        legacy_is_spectral ? ChannelSemanticKind.Spectrum :
        (int)kind switch
        {
            1 or 2 => ChannelSemanticKind.Scatter,
            3 => ChannelSemanticKind.Time,
            4 => ChannelSemanticKind.Spectrum,
            5 => ChannelSemanticKind.Mass,
            6 => ChannelSemanticKind.Background,
            7 => ChannelSemanticKind.Model,
            _ => ChannelSemanticKind.Optical
        };

    private static bool is_time_channel_name(string channel_name) =>
        string.Equals(channel_name.Trim(), "Time", StringComparison.OrdinalIgnoreCase) ||
        channel_name.Contains("TIME", StringComparison.OrdinalIgnoreCase);

    private static bool is_scatter_channel_name(string channel_name) =>
        channel_name.Contains("FSC", StringComparison.OrdinalIgnoreCase) ||
        channel_name.Contains("SSC", StringComparison.OrdinalIgnoreCase);

    private static bool is_background_channel_name(string channel_name) =>
        channel_name.StartsWith("BCKG", StringComparison.OrdinalIgnoreCase);

    private static bool is_model_channel_name(string channel_name)
    {
        string name = channel_name.Trim();
        return string.Equals(name, "Width", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Center", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Offset", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Residual", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Event_length", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Event length", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "EventLength", StringComparison.OrdinalIgnoreCase);
    }

    private static bool is_mass_channel_name(string channel_name) =>
        mass_channel_pattern.IsMatch(channel_name.Trim());

    private static IEnumerable<double> signed_log_ticks(double minimum, double maximum, bool major)
    {
        if (minimum <= 0 && maximum >= 0) yield return 0;
        double limit = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        if (limit <= 0) yield break;
        int top = Math.Max(0, (int)Math.Ceiling(Math.Log10(limit)));
        for (int power = 0; power <= top; power++)
        {
            double decade = Math.Pow(10, power);
            int first = major ? 1 : 2;
            int last = major ? 1 : 9;
            for (int factor = first; factor <= last; factor++)
            {
                double value = factor * decade;
                if (-value >= minimum && -value <= maximum) yield return -value;
                if (value >= minimum && value <= maximum) yield return value;
            }
        }
    }

    private static IEnumerable<double> arcsinh_ticks(AxisSettings axis, bool major)
    {
        double minimum = axis.Minimum;
        double maximum = axis.Maximum;
        if (minimum <= 0 && maximum >= 0)
            yield return 0;

        double cofactor = double.IsFinite(axis.ArcsinhCofactor) && axis.ArcsinhCofactor > 0
            ? axis.ArcsinhCofactor
            : 5.0;
        double limit = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        if (limit < cofactor)
            yield break;

        int top = Math.Max(0, (int)Math.Ceiling(Math.Log10(limit / cofactor)));
        for (int power = 0; power <= top; power++)
        {
            double decade = cofactor * Math.Pow(10, power);
            int first = major ? 1 : 2;
            int last = major ? 1 : 9;
            for (int factor = first; factor <= last; factor++)
            {
                double value = factor * decade;
                if (-value >= minimum && -value <= maximum) yield return -value;
                if (value >= minimum && value <= maximum) yield return value;
            }
        }
    }

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

internal sealed class NaturalChannelNameComparer : IComparer<string>
{
    public static NaturalChannelNameComparer Instance { get; } = new();
    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int xi = 0, yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            if (char.IsDigit(x[xi]) && char.IsDigit(y[yi]))
            {
                long xn = 0, yn = 0;
                while (xi < x.Length && char.IsDigit(x[xi])) xn = xn * 10 + x[xi++] - '0';
                while (yi < y.Length && char.IsDigit(y[yi])) yn = yn * 10 + y[yi++] - '0';
                int numeric = xn.CompareTo(yn);
                if (numeric != 0) return numeric;
                continue;
            }
            int character = char.ToUpperInvariant(x[xi++]).CompareTo(char.ToUpperInvariant(y[yi++]));
            if (character != 0) return character;
        }
        return (x.Length - xi).CompareTo(y.Length - yi);
    }
}
