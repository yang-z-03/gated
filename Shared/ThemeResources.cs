using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using Avalonia.Styling;

namespace gated.Shared;

public interface IThemeResourceAware
{
    void RefreshThemeResources();
}

public static class ThemeResources
{
    private const string icon_base_uri = "avares://gated/Resources/";
    private static readonly Dictionary<string, SvgImage> icon_cache = new();

    public static void BindAppBrush(AvaloniaObject target, AvaloniaProperty property, string semantic_name) =>
        target.Bind(property, new DynamicResourceExtension($"AppBrush{semantic_name}"));

    public static Color Color(string resource_key, Color fallback)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } window &&
            window.TryFindResource(resource_key, actual_theme_variant(window), out var window_resource))
            return resource_color(window_resource, fallback);

        if (Application.Current is { } application &&
            application.TryGetResource(resource_key, actual_theme_variant(application), out var resource))
            return resource_color(resource, fallback);
        return fallback;
    }

    public static Color Color(StyledElement? owner, string resource_key, Color fallback)
    {
        if (owner is not null &&
            owner.TryFindResource(resource_key, actual_theme_variant(owner), out var resource))
            return resource_color(resource, fallback);
        return Color(resource_key, fallback);
    }

    public static Color AppColor(string semantic_name, Color fallback) =>
        Color($"AppColor{semantic_name}", fallback);

    public static Color AppColor(string semantic_name) =>
        AppColor(semantic_name, Colors.Transparent);

    public static Color AppColor(StyledElement? owner, string semantic_name, Color fallback) =>
        Color(owner, $"AppColor{semantic_name}", fallback);

    public static Color AppColor(StyledElement? owner, string semantic_name) =>
        AppColor(owner, semantic_name, Colors.Transparent);

    public static SvgImage Icon(StyledElement? owner, string icon_name_or_uri) =>
        icon(resolve_theme_variant(owner), icon_name_or_uri);

    public static SvgImage Icon(string icon_name_or_uri) =>
        icon(resolve_theme_variant(null), icon_name_or_uri);

    private static Color resource_color(object? resource, Color fallback) =>
        resource switch
        {
            Color color => color,
            SolidColorBrush brush => brush.Color,
            _ => fallback
        };

    private static ThemeVariant actual_theme_variant(IThemeVariantHost host) =>
        host.ActualThemeVariant == ThemeVariant.Default
            ? ThemeVariant.Light
            : host.ActualThemeVariant;

    private static ThemeVariant resolve_theme_variant(StyledElement? owner)
    {
        if (owner is not null)
            return actual_theme_variant(owner);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } window)
            return actual_theme_variant(window);
        if (Application.Current is { } application)
            return actual_theme_variant(application);
        return ThemeVariant.Light;
    }

    private static SvgImage icon(ThemeVariant variant, string icon_name_or_uri)
    {
        string icon_name = icon_name_from(icon_name_or_uri);
        string variant_name = variant == ThemeVariant.Dark ? "Dark" : "Light";
        string cache_key = $"{variant_name}:{icon_name}";
        if (icon_cache.TryGetValue(cache_key, out var cached))
            return cached;

        string themed_uri = $"{icon_base_uri}{variant_name}/{icon_name}";
        string fallback_uri = $"{icon_base_uri}{icon_name}";
        var selected_uri = asset_exists(themed_uri) ? themed_uri : fallback_uri;
        var image = new SvgImage { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri(selected_uri))) };
        icon_cache[cache_key] = image;
        return image;
    }

    private static string icon_name_from(string icon_name_or_uri)
    {
        if (!Uri.TryCreate(icon_name_or_uri, UriKind.Absolute, out var uri))
            return icon_name_or_uri.TrimStart('/', '\\');
        string path = uri.AbsolutePath;
        int marker = path.LastIndexOf("/Resources/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            path = path[(marker + "/Resources/".Length)..];
        return path.TrimStart('/', '\\');
    }

    private static bool asset_exists(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
