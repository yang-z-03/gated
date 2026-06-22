using Avalonia;
using System;

namespace gated;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    
    [STAThread]
    public static void Main(string[] args) {
        
        System.Environment.SetEnvironmentVariable("PYTHONHOME", 
            gated.Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory));
        System.Environment.SetEnvironmentVariable("PATH",
            AppContext.BaseDirectory + gated.Shared.PlatformSupport.EnvironmentPathSeparator +
            gated.Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory));

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