using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace gated.Shared;

public interface IThemeResourceAware
{
    void RefreshThemeResources();
}

public static class ThemeResources
{
    public static void BindAppBrush(AvaloniaObject target, AvaloniaProperty property, string semantic_name) =>
        target.Bind(property, new DynamicResourceExtension($"AppBrush{semantic_name}"));

    public static Color Color(string resource_key, Color fallback)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.TryFindResource(resource_key, out var window_resource) == true)
            return resource_color(window_resource, fallback);

        if (Application.Current?.TryFindResource(resource_key, out var resource) == true)
            return resource_color(resource, fallback);
        return fallback;
    }

    public static Color Color(StyledElement? owner, string resource_key, Color fallback)
    {
        if (owner?.TryFindResource(resource_key, out var resource) == true)
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

    private static Color resource_color(object? resource, Color fallback) =>
        resource switch
        {
            Color color => color,
            SolidColorBrush brush => brush.Color,
            _ => fallback
        };
}
