using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using gated.Python;

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
            PythonExtensionRuntime.StartBackground();
            desktop.Exit += (_, _) => PythonExtensionRuntime.Shutdown();
            var window = new MainWindow();
            desktop.MainWindow = window;
            var args = desktop.Args ?? [];
            window.Opened += async (_, _) =>
            {
                if (args.Length > 0)
                    await window.OpenCommandLineFilesAsync(args);
                await window.CheckForUpdatesAtStartupAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
        BindingPlugins.PropertyAccessors.Add(new DataRowViewPropertyAccessorPlugin());
    }
}
