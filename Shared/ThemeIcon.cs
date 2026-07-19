using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace gated.Shared;

public sealed class ThemeIcon : Image
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<ThemeIcon, string>(nameof(Icon), "");

    static ThemeIcon()
    {
        IconProperty.Changed.AddClassHandler<ThemeIcon>((control, _) => control.refresh_source());
    }

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        refresh_source();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == nameof(ActualThemeVariant))
            refresh_source();
    }

    private void refresh_source()
    {
        if (string.IsNullOrWhiteSpace(Icon))
            Source = null;
        else
            Source = ThemeResources.Icon(this, Icon);
    }
}
