using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using gated.Models;
using gated.Python;
using gated.Shared;

namespace gated;

public partial class App : Application
{
    private const string embedded_ui_font = "avares://gated/Fonts#Roboto";

    public static string NormalizeThemeName(string? theme_name) =>
        string.Equals(theme_name, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";

    public static IReadOnlyList<string> SystemFontFamilyNames()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static void ApplyUiFontPreference(bool use_embedded, string? family_name)
    {
        if (Application.Current is null)
            return;

        FontFamily font_family = FontFamily.Parse(embedded_ui_font);
        if (!use_embedded && !string.IsNullOrWhiteSpace(family_name))
        {
            try
            {
                var system_font = FontManager.Current.SystemFonts.FirstOrDefault(font =>
                    string.Equals(font.Name, family_name.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (system_font is not null)
                    font_family = system_font;
            }
            catch
            {
                // The embedded font remains available if system font discovery fails.
            }
        }

        Application.Current.Resources["SemiFontFamilyRegular"] = font_family;
        Application.Current.Resources["DefaultFontFamily"] = font_family;
        RefreshThemeResources();
    }

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

#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => PythonExtensionRuntime.Shutdown();
            ApplyThemePreference(Configuration.Preferences.ThemeName);
            ApplyUiFontPreference(
                Configuration.Preferences.UseEmbeddedUiFont,
                Configuration.Preferences.CustomUiFontFamily);
            
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
