using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace gated.Services;

internal static class WindowPlacementStore
{
    private const string config_directory_name = "gated-config";
    private const string placement_file_name = "window-placement.json";
    private static readonly JsonSerializerOptions json_options = new() { WriteIndented = true };

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), config_directory_name);

    private static string placement_file_path => Path.Combine(ConfigDirectory, placement_file_name);

    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(placement_file_path))
                return;

            var placement = JsonSerializer.Deserialize<WindowPlacement>(
                File.ReadAllText(placement_file_path),
                json_options);
            if (placement is null || !placement.IsUsable())
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
                    : window.WindowState.ToString()
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

    private sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string WindowState { get; set; } = nameof(Avalonia.Controls.WindowState.Normal);

        public bool IsUsable() =>
            is_finite(Width)
            && is_finite(Height)
            && Width >= 400
            && Height >= 300;
    }
}
