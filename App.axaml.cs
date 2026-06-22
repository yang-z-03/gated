using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using gated.Python;
using gated.Shared;

namespace gated;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => PythonExtensionRuntime.Shutdown();
            BindingPlugins.PropertyAccessors.Add(new DataRowViewPropertyAccessorPlugin());
            
            var window = new MainWindow();
            desktop.MainWindow = window;
            var args = desktop.Args ?? [];
            window.Opened += async (_, _) =>
            {
                if (await window.BootstrapPythonIfMissingAsync())
                    return;

                PythonExtensionRuntime.StartBackground();
                if (args.Length > 0)
                    await window.OpenCommandLineFilesAsync(args);
                await window.CheckForUpdatesAtStartupAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
