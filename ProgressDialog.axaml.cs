using Avalonia.Controls;

namespace gated;

public partial class ProgressDialog : Window
{
    public ProgressDialog()
        : this("Working ...")
    {
    }

    public ProgressDialog(string message)
    {
        Message = message;
        InitializeComponent();
        DataContext = this;
    }

    public string Message { get; set; } = "Working ...";
}
