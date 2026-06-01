using Avalonia.Controls;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace gated;

public partial class ProgressDialog : Window, INotifyPropertyChanged
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
    public string Subtitle { get; private set; } = "";
    public bool ShowProgress { get; private set; }
    public bool IsIndeterminate { get; private set; }
    public double ProgressValue { get; private set; }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public void SetProgress(string title, string subtitle, double? fraction)
    {
        DialogTitle = title;
        Subtitle = subtitle;
        ShowProgress = true;
        IsIndeterminate = fraction is null;
        ProgressValue = fraction is null ? 0 : Math.Clamp(fraction.Value * 100, 0, 100);
        on_property_changed(nameof(DialogTitle));
        on_property_changed(nameof(Subtitle));
        on_property_changed(nameof(ShowProgress));
        on_property_changed(nameof(IsIndeterminate));
        on_property_changed(nameof(ProgressValue));
    }

    private void on_property_changed([CallerMemberName] string? property_name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property_name));
}
