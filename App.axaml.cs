using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;

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
            var window = new MainWindow();
            desktop.MainWindow = window;
            var args = desktop.Args ?? [];
            if (args.Length > 0)
                window.Opened += async (_, _) => await window.OpenCommandLineFilesAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
        BindingPlugins.PropertyAccessors.Add(new DataRowViewPropertyAccessorPlugin());
    }
}
