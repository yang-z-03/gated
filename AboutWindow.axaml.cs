using Avalonia.Controls;
using Avalonia.Interactivity;

namespace gated;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var ver = GetType().Assembly.GetName().Version;
        this.version.Content = $"Version {ver!.Major}.{ver!.Minor} " + (
            ver.Build > 0 ? $"Patch {ver.Build}" : ""
        );
    }

    private void ok_button_click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
