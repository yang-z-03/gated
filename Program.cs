using Avalonia;
using System;
using System.IO;
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
        Directory.CreateDirectory(Shared.PlatformSupport.PersistenceDirectory);
        string python_home = Shared.PlatformSupport.EmbeddedPythonHome();
        string path = string.Join(
            Shared.PlatformSupport.EnvironmentPathSeparator,
            [
                AppContext.BaseDirectory,
                python_home,
                Environment.GetEnvironmentVariable("PATH") ?? ""
            ]);

        Environment.SetEnvironmentVariable("PYTHONHOME", 
            python_home,
            EnvironmentVariableTarget.Process);
        
        Environment.SetEnvironmentVariable("PATH",
            path,
            EnvironmentVariableTarget.Process);

        if (Shared.PlatformSupport.CurrentPlatform != "windows")
        {
            NativeEnvironment.Set("PYTHONHOME", python_home);
            NativeEnvironment.Set("PATH", path);
        }

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
