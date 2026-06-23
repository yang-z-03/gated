using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace gated;


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

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    
    [STAThread]
    public static void Main(string[] args) {
        
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

        Console.WriteLine($"Initialized PYTHONHOME = {Environment.GetEnvironmentVariable("PYTHONHOME")}");
        Console.WriteLine($"Initialized PATH = {Environment.GetEnvironmentVariable("PATH")}");

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}