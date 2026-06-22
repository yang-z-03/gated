using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using gated.Shared;
using System;
using update;

namespace gated.Updater;

public sealed class UpdaterApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new Theme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Environment.SetEnvironmentVariable("PYTHONHOME",
                PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory));
            Environment.SetEnvironmentVariable("PATH",
                AppContext.BaseDirectory + PlatformSupport.EnvironmentPathSeparator +
                PlatformSupport.EmbeddedPythonHome(AppContext.BaseDirectory));
            desktop.MainWindow = new UpdaterWindow(desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
