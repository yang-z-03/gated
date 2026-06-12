using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace gated.Services;

public enum PythonScriptRepositoryKind
{
    Macro,
    Statistic
}

public sealed class PythonScriptDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PythonScriptRepositoryKind Kind { get; set; }
    public string Name { get; set; } = "";
    public int FormatVersion { get; set; } = PythonScriptRepository.FormatVersion;
    public int ApiVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "";
    public string FilePath { get; set; } = "";

    public override string ToString() => Name;
}

public static class PythonScriptRepository
{
    public const int FormatVersion = 1;
    private const string Magic = "GATEDSCRIPT";

    public static string MacroDirectory => Path.Combine(Directory.GetCurrentDirectory(), "macros");
    public static string StatisticDirectory => Path.Combine(Directory.GetCurrentDirectory(), "statistics");

    public static IReadOnlyList<PythonScriptDefinition> LoadMacros() =>
        load_directory(MacroDirectory, PythonScriptRepositoryKind.Macro, "*.macro");

    public static IReadOnlyList<PythonScriptDefinition> LoadStatistics() =>
        load_directory(StatisticDirectory, PythonScriptRepositoryKind.Statistic, "*.statistic");

    public static PythonScriptDefinition NewMacro(string name) =>
        create(PythonScriptRepositoryKind.Macro, name, macro_template(name));

    public static PythonScriptDefinition NewStatistic(string name) =>
        create(PythonScriptRepositoryKind.Statistic, name, statistic_template(name));

    public static string PreviewFileName(PythonScriptRepositoryKind kind, string name) =>
        sanitize_file_stem(name) + (kind == PythonScriptRepositoryKind.Macro ? ".macro" : ".statistic");

    public static string TargetPath(PythonScriptRepositoryKind kind, string name)
    {
        string directory = kind == PythonScriptRepositoryKind.Macro ? MacroDirectory : StatisticDirectory;
        return Path.Combine(directory, PreviewFileName(kind, name));
    }

    public static string? ValidateForSave(PythonScriptDefinition definition)
    {
        string file_name = PreviewFileName(definition.Kind, definition.Name);
        string stem = Path.GetFileNameWithoutExtension(file_name);
        if (string.IsNullOrWhiteSpace(stem))
            return "The script name does not produce a legal file name.";

        string target_path = TargetPath(definition.Kind, definition.Name);
        if (File.Exists(target_path)
            && !string.Equals(Path.GetFullPath(target_path), Path.GetFullPath(definition.FilePath ?? ""), StringComparison.OrdinalIgnoreCase))
            return $"A script file named '{file_name}' already exists.";

        return null;
    }

    public static void Save(PythonScriptDefinition definition)
    {
        string? validation_error = ValidateForSave(definition);
        if (!string.IsNullOrWhiteSpace(validation_error))
            throw new InvalidOperationException(validation_error);

        string previous_path = definition.FilePath;
        definition.FilePath = TargetPath(definition.Kind, definition.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(definition.FilePath)!);
        definition.UpdatedAt = DateTimeOffset.UtcNow;
        using var stream = File.Create(definition.FilePath);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write((int)definition.Kind);
        writer.Write(definition.Id.ToByteArray());
        writer.Write(definition.Name ?? "");
        writer.Write(definition.ApiVersion);
        writer.Write(definition.CreatedAt.ToUnixTimeMilliseconds());
        writer.Write(definition.UpdatedAt.ToUnixTimeMilliseconds());
        writer.Write(definition.Source ?? "");

        if (!string.IsNullOrWhiteSpace(previous_path)
            && !string.Equals(Path.GetFullPath(previous_path), Path.GetFullPath(definition.FilePath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(previous_path))
            File.Delete(previous_path);
    }

    private static PythonScriptDefinition create(PythonScriptRepositoryKind kind, string name, string source)
    {
        var definition = new PythonScriptDefinition
        {
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim(),
            Source = source
        };
        return definition;
    }

    private static IReadOnlyList<PythonScriptDefinition> load_directory(string directory, PythonScriptRepositoryKind kind, string pattern)
    {
        Directory.CreateDirectory(directory);
        return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => try_load(path, kind))
            .Where(item => item is not null)
            .Cast<PythonScriptDefinition>()
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PythonScriptDefinition? try_load(string path, PythonScriptRepositoryKind expected_kind)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadString() != Magic)
                return null;
            int format_version = reader.ReadInt32();
            if (format_version > FormatVersion)
                return null;
            var kind = (PythonScriptRepositoryKind)reader.ReadInt32();
            if (kind != expected_kind)
                return null;
            var id = new Guid(reader.ReadBytes(16));
            string name = reader.ReadString();
            int api_version = reader.ReadInt32();
            var created_at = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
            var updated_at = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
            string source = reader.ReadString();
            return new PythonScriptDefinition
            {
                Id = id,
                Kind = kind,
                Name = name,
                FormatVersion = format_version,
                ApiVersion = api_version,
                CreatedAt = created_at,
                UpdatedAt = updated_at,
                Source = source,
                FilePath = path
            };
        }
        catch
        {
            return null;
        }
    }

    private static string sanitize_file_stem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = new List<char>();
        bool last_dash = false;
        foreach (char raw in name.Trim().ToLowerInvariant())
        {
            if (raw == ' ' || raw == '-' || raw == '_')
            {
                if (!last_dash && characters.Count > 0)
                {
                    characters.Add('-');
                    last_dash = true;
                }
                continue;
            }
            if (invalid.Contains(raw) || char.IsControl(raw))
                continue;
            characters.Add(raw);
            last_dash = false;
        }
        while (characters.Count > 0 && characters[^1] == '-')
            characters.RemoveAt(characters.Count - 1);
        return new string(characters.ToArray());
    }

    private static string macro_template(string name) =>
        $"""
        # {name}
        # workspace, np, pd, log, and msgbox are available.

        log("Running macro: {name}")
        """;

    private static string statistic_template(string name) =>
        $"""
        import numpy as np

        def entry(matrix: np.ndarray, channels: list[str], parameters: np.ndarray) -> float:
            # {name}
            return float(np.nanmean(matrix))
        """;
}
