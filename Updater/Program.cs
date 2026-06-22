using Avalonia;
using System;
using System.Reflection;

namespace gated.Updater;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(updater_version());
            return;
        }

        AppBuilder.Configure<UpdaterApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    private static string updater_version()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString()
            : informational;

        return normalize_version(version);
    }

    private static string normalize_version(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0.0.0";

        var parts = value.Split('+')[0].Split('-')[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(".", [
            parts.Length > 0 ? parts[0] : "0",
            parts.Length > 1 ? parts[1] : "0",
            parts.Length > 2 ? parts[2] : "0"
        ]);
    }
}
