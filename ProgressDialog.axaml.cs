using Avalonia.Controls;

namespace gated;

public partial class ProgressDialog : Window
{
    public ProgressDialog()
        : this("Working ...", "")
    {
    }

    public ProgressDialog(string message)
        : this(message, "")
    {
    }

    public ProgressDialog(string title, string subtitle)
    {
        DialogTitle = title;
        Subtitle = subtitle;
        InitializeComponent();
        DataContext = this;
    }

    public string DialogTitle { get; set; } = "Working ...";
    public string Subtitle { get; set; } = "";
}
