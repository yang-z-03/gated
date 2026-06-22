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
        
        Environment.SetEnvironmentVariable("PYTHONHOME", 
            Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory), 
            EnvironmentVariableTarget.Process);
        
        var syspath = Environment.GetEnvironmentVariable("PATH");
        if (syspath != null) syspath = Shared.PlatformSupport.EnvironmentPathSeparator + syspath;
        Environment.SetEnvironmentVariable("PATH",
            AppContext.BaseDirectory + Shared.PlatformSupport.EnvironmentPathSeparator +
            Shared.PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory) + (syspath ?? ""),
            EnvironmentVariableTarget.Process);

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