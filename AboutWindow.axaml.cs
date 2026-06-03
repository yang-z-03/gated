using Avalonia.Controls;
using Avalonia.Interactivity;

namespace gated;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        this.version.Content = $"Version {GetType().Assembly.GetName().Version}";
    }

    private void ok_button_click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
