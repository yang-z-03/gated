using System;
using System.Linq;
using Avalonia.Controls;
using gated.Models;

namespace gated;

public partial class PreferencesWindow : Window
{
    private readonly string original_theme_name;

    public PreferencesWindow()
    {
        InitializeComponent();
        original_theme_name = App.NormalizeThemeName(Configuration.Preferences.ThemeName);
        Tag = new PreferenceChoices();
        preferencePageList.ItemsSource = new[] { "Cytometry", "Appearance" };
        preferencePageList.SelectionChanged += (_, _) => bind_page();
        preferencePageList.SelectedIndex = 0;
        cytometerCombo.SelectionChanged += (_, _) => bind_selected();
        refresh_cytometer_combo(Configuration.Preferences.SelectedCytometerName);
        set_theme_selection(original_theme_name);

        addCytometerButton.Click += (_, _) => add_cytometer();
        addChannelButton.Click += (_, _) => add_channel();
        removeChannelButton.Click += (_, _) => remove_channel();
        resetButton.Click += (_, _) => reset_defaults();
        lightThemeRadio.IsCheckedChanged += (_, _) => preview_selected_theme();
        darkThemeRadio.IsCheckedChanged += (_, _) => preview_selected_theme();
        cancelButton.Click += (_, _) =>
        {
            App.ApplyThemePreference(original_theme_name);
            Close(false);
        };
        saveButton.Click += (_, _) =>
        {
            if (cytometerCombo.SelectedItem is CytometerPreference preference)
                Configuration.Preferences.SelectedCytometerName = preference.Name;
            Configuration.Preferences.ThemeName = selected_theme_name();
            App.ApplyThemePreference(Configuration.Preferences.ThemeName);
            Configuration.SavePreferences();
            Close(true);
        };
    }

    private static CytometerPreference[] editable_cytometers() =>
        Configuration.Preferences.Cytometers
            .Where(item => !string.Equals(item.Name, Configuration.DefaultCytometerName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private void refresh_cytometer_combo(string? selected_name = null)
    {
        var cytometers = editable_cytometers();
        cytometerCombo.ItemsSource = cytometers;
        cytometerCombo.SelectedItem = cytometers.FirstOrDefault(item => string.Equals(item.Name, selected_name, StringComparison.OrdinalIgnoreCase))
            ?? cytometers.FirstOrDefault();
        channelGrid.ItemsSource = (cytometerCombo.SelectedItem as CytometerPreference)?.Detectors;
    }

    private void bind_selected()
    {
        if (cytometerCombo.SelectedItem is not CytometerPreference preference)
        {
            channelGrid.ItemsSource = null;
            return;
        }
        channelGrid.ItemsSource = preference.Detectors;
    }

    private void bind_page()
    {
        bool cytometry = string.Equals(preferencePageList.SelectedItem as string, "Cytometry", StringComparison.Ordinal);
        cytometryPage.IsVisible = cytometry;
        appearancePage.IsVisible = !cytometry;
    }

    private void set_theme_selection(string theme_name)
    {
        if (App.NormalizeThemeName(theme_name) == "Dark")
            darkThemeRadio.IsChecked = true;
        else
            lightThemeRadio.IsChecked = true;
    }

    private string selected_theme_name() =>
        darkThemeRadio.IsChecked == true ? "Dark" : "Light";

    private void preview_selected_theme() =>
        App.ApplyThemePreference(selected_theme_name());

    private void add_cytometer()
    {
        int index = Configuration.Preferences.Cytometers.Count + 1;
        var preference = Configuration.CreatePreferenceFromDefault($"Cytometer {index}");
        while (Configuration.Preferences.Cytometers.Any(item => string.Equals(item.Name, preference.Name, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            preference.Name = $"Cytometer {index}";
        }
        Configuration.Preferences.Cytometers.Add(preference);
        refresh_cytometer_combo(preference.Name);
    }

    private void add_channel()
    {
        if (cytometerCombo.SelectedItem is not CytometerPreference preference)
            return;
        preference.Detectors.Add(new SpectralDetectorPreference
        {
            ChannelName = "Channel-A",
            Kind = ChannelSemanticKind.Optical,
            Scale = CoordinateScaleKind.Logicle,
            PlotOrder = preference.Detectors.Count
        });
    }

    private void remove_channel()
    {
        if (cytometerCombo.SelectedItem is not CytometerPreference preference ||
            channelGrid.SelectedItem is not SpectralDetectorPreference assumption)
            return;
        preference.Detectors.Remove(assumption);
    }

    private void reset_defaults()
    {
        string theme_name = Configuration.Preferences.ThemeName;
        Configuration.ResetPreferences();
        Configuration.Preferences.ThemeName = theme_name;
        refresh_cytometer_combo();
    }

    private sealed class PreferenceChoices
    {
        public ChannelSemanticKind[] KindChoices { get; } =
        [
            ChannelSemanticKind.Time,
            ChannelSemanticKind.Scatter,
            ChannelSemanticKind.Optical,
            ChannelSemanticKind.Spectrum,
            ChannelSemanticKind.Mass,
            ChannelSemanticKind.Background,
            ChannelSemanticKind.Model
        ];
        public Array ScaleChoices { get; } = Enum.GetValues<CoordinateScaleKind>();
        public Array ExcitationChoices { get; } = Enum.GetValues<ExcitationLightKind>();
    }
}
