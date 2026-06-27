using Avalonia;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace gated.Updater;

internal static class NativeEnvironment
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    public static void Set(string name, string value)
    {
        if (setenv(name, value, 1) != 0)
            throw new InvalidOperationException("setenv failed");
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("PYTHONHOME", 
            Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory), 
            EnvironmentVariableTarget.Process);
        
        Environment.SetEnvironmentVariable("PATH",
            AppContext.BaseDirectory + Shared.PlatformSupport.EnvironmentPathSeparator +
            Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory),
            EnvironmentVariableTarget.Process);

        if (Shared.PlatformSupport.CurrentPlatform != "windows")
        {
            NativeEnvironment.Set("PYTHONHOME", Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory));
            NativeEnvironment.Set("PATH",
                AppContext.BaseDirectory + Shared.PlatformSupport.EnvironmentPathSeparator +
                Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory)
            );
        }

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
