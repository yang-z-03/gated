using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using gated.Models;
using gated.Python;
using gated.Shared;

namespace gated;

public partial class App : Application
{
    public static string NormalizeThemeName(string? theme_name) =>
        string.Equals(theme_name, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";

    public static void ApplyThemePreference(string? theme_name)
    {
        if (Application.Current is null)
            return;
        Application.Current.RequestedThemeVariant = NormalizeThemeName(theme_name) == "Dark"
            ? ThemeVariant.Dark
            : ThemeVariant.Light;
        RefreshThemeResources();
    }

    private static void RefreshThemeResources()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows)
            refresh_theme_resources(window);
    }

    private static void refresh_theme_resources(Control control)
    {
        if (control is IThemeResourceAware theme_resource_aware)
            theme_resource_aware.RefreshThemeResources();
        control.InvalidateVisual();

        foreach (var child in control.GetVisualChildren().OfType<Control>())
            refresh_theme_resources(child);
    }

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
            ApplyThemePreference(Configuration.Preferences.ThemeName);
            
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
