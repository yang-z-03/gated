using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace gated.Services;

public static class RecentFileStore
{
    private const int maximum_count = 20;

    private static string store_path() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gated", "recent-files.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            string path = store_path();
            if (!File.Exists(path))
                return Array.Empty<string>();

            var items = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
            return items
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maximum_count)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void Save(IEnumerable<string> paths)
    {
        try
        {
            string path = store_path();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var items = paths
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maximum_count)
                .ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
