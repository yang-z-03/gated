using System;
using System.Linq;
using Avalonia.Controls;
using gated.Models;

namespace gated;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        Tag = new PreferenceChoices();
        cytometerList.ItemsSource = Configuration.Preferences.Cytometers;
        cytometerList.SelectionChanged += (_, _) => bind_selected();
        cytometerList.SelectedItem = Configuration.Preferences.Cytometers.FirstOrDefault(item => item.Name == Configuration.Preferences.SelectedCytometerName)
            ?? Configuration.Preferences.Cytometers.FirstOrDefault();
        channelGrid.ItemsSource = (cytometerList.SelectedItem as CytometerPreference)?.Channels;
        
        addCytometerButton.Click += (_, _) => add_cytometer();
        addChannelButton.Click += (_, _) => add_channel();
        removeChannelButton.Click += (_, _) => remove_channel();
        resetButton.Click += (_, _) => reset_defaults();
        cancelButton.Click += (_, _) => Close(false);
        saveButton.Click += (_, _) =>
        {
            if (cytometerList.SelectedItem is CytometerPreference preference)
                Configuration.Preferences.SelectedCytometerName = preference.Name;
            Configuration.SavePreferences();
            Close(true);
        };
    }

    private void bind_selected()
    {
        if (cytometerList.SelectedItem is not CytometerPreference preference) return;
        channelGrid.ItemsSource = preference.Channels;
    }

    private void add_cytometer()
    {
        int index = Configuration.Preferences.Cytometers.Count + 1;
        var preference = Configuration.CreatePreferenceFromDefault($"Cytometer {index}");
        Configuration.Preferences.Cytometers.Add(preference);
        cytometerList.SelectedItem = preference;
    }

    private void add_channel()
    {
        if (cytometerList.SelectedItem is not CytometerPreference preference)
            return;
        preference.Channels.Add(new ChannelAssumption
        {
            Pattern = "Channel",
            Kind = ChannelSemanticKind.Other,
            Scale = CoordinateScaleKind.Logicle
        });
    }

    private void remove_channel()
    {
        if (cytometerList.SelectedItem is not CytometerPreference preference ||
            channelGrid.SelectedItem is not ChannelAssumption assumption)
            return;
        preference.Channels.Remove(assumption);
    }

    private void reset_defaults()
    {
        Configuration.ResetPreferences();
        cytometerList.ItemsSource = null;
        cytometerList.ItemsSource = Configuration.Preferences.Cytometers;
        cytometerList.SelectedItem = Configuration.Preferences.Cytometers.FirstOrDefault();
    }

    private sealed class PreferenceChoices
    {
        public Array KindChoices { get; } = Enum.GetValues<ChannelSemanticKind>();
        public Array ScaleChoices { get; } = Enum.GetValues<CoordinateScaleKind>();
    }
}
