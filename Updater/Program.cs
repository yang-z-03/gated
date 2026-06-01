using Avalonia;
using System;

namespace gated.Updater;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<UpdaterApp>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args);
}
