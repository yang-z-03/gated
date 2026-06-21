using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using gated.Shared;

namespace gated.Services;

public static class WindowPlacementStore
{
    private const string placement_file_name = "window-placement.json";
    private const double minimum_project_tree_width = 260;
    private const double minimum_statistics_panel_height = 120;
    private static readonly JsonSerializerOptions json_options = new() { WriteIndented = true };

    public static string ConfigDirectory =>
        PlatformSupport.PersistenceDirectory;

    private static string placement_file_path => Path.Combine(ConfigDirectory, placement_file_name);

    public static void Restore(Window window)
    {
        RestoreWindow(window);
    }

    public static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(placement_file_path))
                return null;

            var placement = JsonSerializer.Deserialize<WindowPlacement>(
                File.ReadAllText(placement_file_path),
                json_options);
            if (placement is null || !placement.IsUsable())
                return null;

            return placement;
        }
        catch
        {
            // Placement is best-effort. A corrupt config file must not block startup.
            return null;
        }
    }

    public static void RestoreWindow(Window window)
    {
        try
        {
            var placement = Load();
            if (placement is null)
                return;

            window.Width = Math.Max(window.MinWidth, placement.Width);
            window.Height = Math.Max(window.MinHeight, placement.Height);
            window.Position = new PixelPoint(placement.X, placement.Y);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (Enum.TryParse<WindowState>(placement.WindowState, out var state)
                && state is WindowState.Normal or WindowState.Maximized)
                window.WindowState = state;
        }
        catch
        {
            // Placement is best-effort. A corrupt config file must not block startup.
        }
    }

    public static void Save(Window window)
    {
        Save(window, null, null);
    }

    public static void Save(Window window, double? project_tree_width, double? statistics_panel_height)
    {
        try
        {
            var bounds = window.Bounds;
            if (!is_finite(bounds.Width) || !is_finite(bounds.Height))
                return;

            var placement = new WindowPlacement
            {
                X = window.Position.X,
                Y = window.Position.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                WindowState = window.WindowState == WindowState.Minimized
                    ? WindowState.Normal.ToString()
                    : window.WindowState.ToString(),
                ProjectTreeWidth = sanitize_dimension(project_tree_width, minimum_project_tree_width),
                StatisticsPanelHeight = sanitize_dimension(statistics_panel_height, minimum_statistics_panel_height)
            };
            if (!placement.IsUsable())
                return;

            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(placement_file_path, JsonSerializer.Serialize(placement, json_options));
        }
        catch
        {
            // Failing to remember placement should never interfere with closing the app.
        }
    }

    private static bool is_finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double? sanitize_dimension(double? value, double minimum) =>
        value is { } actual && is_finite(actual) && actual >= minimum ? actual : null;

    public sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string WindowState { get; set; } = nameof(Avalonia.Controls.WindowState.Normal);
        public double? ProjectTreeWidth { get; set; }
        public double? StatisticsPanelHeight { get; set; }

        public bool IsUsable() =>
            is_finite(Width)
            && is_finite(Height)
            && Width >= 400
            && Height >= 300;
    }
}
