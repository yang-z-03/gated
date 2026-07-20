using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using gated.Models;

namespace gated;

public partial class PreferencesWindow : Window
{
    private readonly string original_theme_name;
    private ObservableCollection<ElementBeadTypePreference> bead_types;
    private ObservableCollection<IsotopePreferenceRow> isotope_rows;
    private bool binding_beads;

    public PreferencesWindow()
    {
        InitializeComponent();
        original_theme_name = App.NormalizeThemeName(Configuration.Preferences.ThemeName);
        bead_types = clone_bead_types(Configuration.Preferences.ElementBeads);
        isotope_rows = clone_isotope_rows(Configuration.Preferences.Isotopes);
        Tag = new PreferenceChoices();
        preferencePageList.ItemsSource = new[] { "Appearance", "Cytometry", "Isotopes", "Element beads" };
        preferencePageList.SelectionChanged += (_, _) => bind_page();
        preferencePageList.SelectedIndex = 0;
        cytometerCombo.SelectionChanged += (_, _) => bind_selected();
        refresh_cytometer_combo(Configuration.Preferences.SelectedCytometerName);
        refresh_bead_types();
        isotopeElementList.ItemsSource = isotope_rows;
        set_theme_selection(original_theme_name);

        addCytometerButton.Click += (_, _) => add_cytometer();
        addChannelButton.Click += (_, _) => add_channel();
        removeChannelButton.Click += (_, _) => remove_channel();
        resetButton.Click += (_, _) => reset_defaults();
        addIsotopeElementButton.Click += (_, _) => add_isotope_element();
        beadTypeCombo.SelectionChanged += (_, _) => bind_bead_type();
        beadLotCombo.SelectionChanged += (_, _) => bind_bead_lot();
        beadTypeNameBox.TextChanged += (_, _) => update_bead_names();
        beadLotNameBox.TextChanged += (_, _) => update_bead_names();
        addBeadTypeButton.Click += async (_, _) => await add_bead_type();
        addIsotopeButton.Click += async (_, _) => await add_isotope();
        addLotButton.Click += async (_, _) => await add_lot();
        removeLotButton.Click += async (_, _) => await remove_lot();
        lightThemeRadio.IsCheckedChanged += (_, _) => preview_selected_theme();
        darkThemeRadio.IsCheckedChanged += (_, _) => preview_selected_theme();
        cancelButton.Click += (_, _) =>
        {
            App.ApplyThemePreference(original_theme_name);
            Close(false);
        };
        saveButton.Click += async (_, _) =>
        {
            if (validate_bead_types() is { } validation)
            {
                await show_message("Element beads", validation);
                return;
            }
            if (validate_isotopes() is { } isotope_validation)
            {
                await show_message("Isotopes", isotope_validation);
                return;
            }
            if (cytometerCombo.SelectedItem is CytometerPreference preference)
                Configuration.Preferences.SelectedCytometerName = preference.Name;
            Configuration.Preferences.ElementBeads.Clear();
            foreach (var type in clone_bead_types(bead_types)) Configuration.Preferences.ElementBeads.Add(type);
            Configuration.Preferences.ElementBeadsInitialized = true;
            Configuration.Preferences.Isotopes.Clear();
            foreach (var element in snapshot_isotopes(isotope_rows)) Configuration.Preferences.Isotopes.Add(element);
            Configuration.Preferences.IsotopesInitialized = true;
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
        bool element_beads = string.Equals(preferencePageList.SelectedItem as string, "Element beads", StringComparison.Ordinal);
        bool isotopes = string.Equals(preferencePageList.SelectedItem as string, "Isotopes", StringComparison.Ordinal);
        cytometryPage.IsVisible = cytometry;
        elementBeadsPage.IsVisible = element_beads;
        isotopesPage.IsVisible = isotopes;
        appearancePage.IsVisible = !cytometry && !element_beads && !isotopes;
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
        bead_types = clone_bead_types(Configuration.Preferences.ElementBeads);
        isotope_rows = clone_isotope_rows(Configuration.Preferences.Isotopes);
        isotopeElementList.ItemsSource = isotope_rows;
        refresh_bead_types();
    }

    private void add_isotope_element()
    {
        isotope_rows.Add(new IsotopePreferenceRow { ElementSymbol = "" });
    }

    private void isotope_mass_editor_key_down(object? sender, KeyEventArgs event_args)
    {
        if (event_args.Key != Key.Enter || sender is not TextBox { DataContext: IsotopePreferenceRow row })
            return;
        event_args.Handled = true;
        commit_pending_isotope_mass(row);
    }

    private void isotope_mass_editor_lost_focus(object? sender, Avalonia.Interactivity.RoutedEventArgs event_args)
    {
        if (sender is TextBox { DataContext: IsotopePreferenceRow row })
            commit_pending_isotope_mass(row);
    }

    private static void commit_pending_isotope_mass(IsotopePreferenceRow row)
    {
        string pending = row.PendingMass.Trim();
        if (pending.Length == 0 || !int.TryParse(pending, out int mass) || mass <= 0)
            return;
        if (row.Tags.Any(tag => tag.Mass == mass))
        {
            row.PendingMass = "";
            return;
        }
        row.Tags.Add(new IsotopeMassTag(row, mass));
        row.SortTags();
        row.PendingMass = "";
    }

    private void remove_isotope_mass_from_tag(object? sender, Avalonia.Interactivity.RoutedEventArgs event_args)
    {
        if (sender is Button { DataContext: IsotopeMassTag tag })
            tag.Owner.Tags.Remove(tag);
    }

    private async void remove_isotope_element_from_row(object? sender, Avalonia.Interactivity.RoutedEventArgs event_args)
    {
        if (sender is not Button { DataContext: IsotopePreferenceRow row } ||
            !await confirm("Remove element", $"Remove the isotope definition for {row.ElementSymbol}?")) return;
        isotope_rows.Remove(row);
    }

    private string? validate_isotopes()
    {
        if (isotope_rows.GroupBy(row => row.ElementSymbol.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            return "Element symbols must be non-empty and unique.";
        foreach (var row in isotope_rows)
        {
            string symbol = row.ElementSymbol.Trim();
            if (symbol.Length > 3 || symbol.Any(character => !char.IsLetter(character)))
                return $"{row.ElementSymbol} is not a valid one-to-three letter element symbol.";
            if (row.Tags.Count == 0) return $"{row.ElementSymbol} must define at least one isotope mass.";
            if (row.Tags.Any(tag => tag.Mass <= 0) || row.Tags.Select(tag => tag.Mass).Distinct().Count() != row.Tags.Count)
                return $"{row.ElementSymbol} contains an invalid or duplicate isotope mass.";
        }
        return null;
    }

    private static ObservableCollection<IsotopePreferenceRow> clone_isotope_rows(IEnumerable<IsotopeElementPreference> source)
    {
        var result = new ObservableCollection<IsotopePreferenceRow>();
        foreach (var element in source)
        {
            var row = new IsotopePreferenceRow { ElementSymbol = element.ElementSymbol };
            foreach (int mass in element.IsotopeMasses.OrderBy(mass => mass)) row.Tags.Add(new IsotopeMassTag(row, mass));
            result.Add(row);
        }
        return result;
    }

    private static IEnumerable<IsotopeElementPreference> snapshot_isotopes(IEnumerable<IsotopePreferenceRow> rows) =>
        rows.Select(row => new IsotopeElementPreference
        {
            ElementSymbol = row.ElementSymbol.Trim(),
            IsotopeMasses = new ObservableCollection<int>(row.Tags.Select(tag => tag.Mass).Distinct().OrderBy(mass => mass))
        });

    private void refresh_bead_types(Guid? selected_id = null)
    {
        binding_beads = true;
        beadTypeCombo.ItemsSource = bead_types;
        beadTypeCombo.SelectedItem = selected_id.HasValue
            ? bead_types.FirstOrDefault(type => type.Id == selected_id.Value) ?? bead_types.FirstOrDefault()
            : bead_types.FirstOrDefault();
        binding_beads = false;
        bind_bead_type();
    }

    private void bind_bead_type()
    {
        if (binding_beads) return;
        binding_beads = true;
        var type = beadTypeCombo.SelectedItem as ElementBeadTypePreference;
        beadTypeNameBox.Text = type?.Name ?? "";
        beadLotCombo.ItemsSource = type?.Lots;
        beadLotCombo.SelectedItem = type?.Lots.FirstOrDefault();
        binding_beads = false;
        bind_bead_lot();
    }

    private void bind_bead_lot()
    {
        if (binding_beads) return;
        binding_beads = true;
        var lot = beadLotCombo.SelectedItem as ElementBeadLotPreference;
        beadLotNameBox.Text = lot?.Name ?? "";
        beadReferenceGrid.ItemsSource = lot?.References;
        binding_beads = false;
    }

    private void update_bead_names()
    {
        if (binding_beads) return;
        var selected_type = type_or_null();
        var selected_lot = lot_or_null();
        if (selected_type is not null) selected_type.Name = beadTypeNameBox.Text ?? "";
        if (selected_lot is not null) selected_lot.Name = beadLotNameBox.Text ?? "";
        binding_beads = true;
        beadTypeCombo.ItemsSource = null; beadTypeCombo.ItemsSource = bead_types;
        beadTypeCombo.SelectedItem = selected_type;
        if (selected_type is not null)
        {
            beadLotCombo.ItemsSource = null; beadLotCombo.ItemsSource = selected_type.Lots; beadLotCombo.SelectedItem = selected_lot;
        }
        binding_beads = false;
    }

    private ElementBeadTypePreference? type_or_null() => beadTypeCombo.SelectedItem as ElementBeadTypePreference;
    private ElementBeadLotPreference? lot_or_null() => beadLotCombo.SelectedItem as ElementBeadLotPreference;

    private async Task add_bead_type()
    {
        string? name = await prompt("Add bead type", "Bead type name");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (bead_types.Any(type => string.Equals(type.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            await show_message("Add bead type", "Bead type names must be unique."); return;
        }
        var type = new ElementBeadTypePreference { Name = name.Trim() };
        type.Lots.Add(new ElementBeadLotPreference());
        bead_types.Add(type); refresh_bead_types(type.Id);
    }

    private async Task add_isotope()
    {
        if (type_or_null() is not { } type) return;
        string? text = await prompt("Add isotope definition", "Mass number");
        if (!int.TryParse(text, out int mass) || mass <= 0)
        {
            if (text is not null) await show_message("Add isotope definition", "Enter a positive integer mass number.");
            return;
        }
        if (type.Isotopes.Contains(mass)) { await show_message("Add isotope definition", $"Mass {mass} already exists in this bead type."); return; }
        type.Isotopes.Add(mass);
        var sorted = type.Isotopes.OrderBy(value => value).ToArray(); type.Isotopes.Clear(); foreach (int value in sorted) type.Isotopes.Add(value);
        foreach (var lot in type.Lots)
        {
            lot.References.Add(new ElementBeadReferencePreference
            {
                MassNumber = mass,
                ReferenceIntensity = Configuration.DefaultElementBeadReferenceIntensity
            });
            var refs = lot.References.OrderBy(reference => reference.MassNumber).ToArray(); lot.References.Clear(); foreach (var reference in refs) lot.References.Add(reference);
        }
        bind_bead_lot();
    }

    private async Task add_lot()
    {
        if (type_or_null() is not { } type) return;
        string? name = await prompt("Add lot definition", "Bead lot name");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (type.Lots.Any(lot => string.Equals(lot.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            await show_message("Add lot definition", "Lot names must be unique within a bead type."); return;
        }
        var lot = new ElementBeadLotPreference { Name = name.Trim() };
        foreach (int mass in type.Isotopes)
            lot.References.Add(new ElementBeadReferencePreference
            {
                MassNumber = mass,
                ReferenceIntensity = Configuration.DefaultElementBeadReferenceIntensity
            });
        type.Lots.Add(lot); bind_bead_type(); beadLotCombo.SelectedItem = lot;
    }

    private async Task remove_lot()
    {
        if (type_or_null() is not { } type || lot_or_null() is not { } lot || !await confirm("Remove lot", $"Remove {lot.Name}?")) return;
        type.Lots.Remove(lot); bind_bead_type();
    }

    private async void remove_isotope_from_row(object? sender, Avalonia.Interactivity.RoutedEventArgs event_args)
    {
        if (sender is Button { DataContext: ElementBeadReferencePreference reference })
            await remove_isotope(reference);
    }

    private async Task remove_isotope(ElementBeadReferencePreference reference)
    {
        if (type_or_null() is not { } type ||
            !await confirm("Remove isotope", $"Remove mass {reference.MassNumber} from every lot in {type.Name}?")) return;
        type.Isotopes.Remove(reference.MassNumber);
        foreach (var lot in type.Lots)
            for (int index = lot.References.Count - 1; index >= 0; index--)
                if (lot.References[index].MassNumber == reference.MassNumber) lot.References.RemoveAt(index);
        bind_bead_lot();
    }

    private string? validate_bead_types()
    {
        if (bead_types.GroupBy(type => type.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            return "Bead type names must be non-empty and unique.";
        foreach (var type in bead_types)
        {
            if (type.Isotopes.Count < 2) return $"{type.Name} must define at least two isotope masses.";
            if (type.Isotopes.Any(mass => mass <= 0) || type.Isotopes.Distinct().Count() != type.Isotopes.Count) return $"{type.Name} contains an invalid or duplicate mass.";
            if (type.Lots.Count == 0) return $"{type.Name} must define at least one lot.";
            if (type.Lots.GroupBy(lot => lot.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1)) return $"{type.Name} has empty or duplicate lot names.";
            foreach (var lot in type.Lots)
                if (lot.References.Count != type.Isotopes.Count || lot.References.Any(reference => !double.IsFinite(reference.ReferenceIntensity) || reference.ReferenceIntensity <= 0))
                    return $"Every reference intensity in {type.Name} / {lot.Name} must be finite and positive.";
        }
        return null;
    }

    private static ObservableCollection<ElementBeadTypePreference> clone_bead_types(IEnumerable<ElementBeadTypePreference> source)
    {
        var result = new ObservableCollection<ElementBeadTypePreference>();
        foreach (var item in source)
        {
            var type = new ElementBeadTypePreference { Id = item.Id, Name = item.Name };
            foreach (int mass in item.Isotopes) type.Isotopes.Add(mass);
            foreach (var source_lot in item.Lots)
            {
                var lot = new ElementBeadLotPreference { Id = source_lot.Id, Name = source_lot.Name };
                foreach (var reference in source_lot.References) lot.References.Add(new ElementBeadReferencePreference { MassNumber = reference.MassNumber, ReferenceIntensity = reference.ReferenceIntensity });
                type.Lots.Add(lot);
            }
            result.Add(type);
        }
        return result;
    }

    private async Task<string?> prompt(string title, string placeholder)
    {
        var dialog = create_bead_dialog(title, 390);
        var input = new TextBox { Watermark = placeholder };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        cancel.Classes.Add("Small");
        ok.Classes.Add("Small");
        cancel.Click += (_, _) => dialog.Close(null);
        ok.Click += (_, _) => dialog.Close(input.Text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } };
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { input, buttons } };
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task show_message(string title, string message)
    {
        var dialog = create_bead_dialog(title, 430);
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Classes.Add("Small");
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3"))
                },
                ok
            }
        };
        await dialog.ShowDialog(this);
    }

    private async Task<bool> confirm(string title, string message)
    {
        var dialog = create_bead_dialog(title, 430);
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var remove = new Button { Content = "Remove", MinWidth = 80, IsDefault = true };
        cancel.Classes.Add("Small");
        remove.Classes.Add("Small");
        cancel.Click += (_, _) => dialog.Close(false); remove.Click += (_, _) => dialog.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, remove } };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3"))
                },
                buttons
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private Window create_bead_dialog(string title, double width) => new()
    {
        Title = title,
        Width = width,
        CanResize = false,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor(this, "WindowBackground"))
    };

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

    public sealed class IsotopePreferenceRow : NotifyBase
    {
        private string element_symbol = "";
        private string pending_mass = "";
        public string ElementSymbol { get => element_symbol; set => SetField(ref element_symbol, value ?? ""); }
        public string PendingMass { get => pending_mass; set => SetField(ref pending_mass, value ?? ""); }
        public ObservableCollection<IsotopeMassTag> Tags { get; } = new();
        public void SortTags()
        {
            var ordered = Tags.OrderBy(tag => tag.Mass).ToArray();
            Tags.Clear();
            foreach (var tag in ordered) Tags.Add(tag);
        }
    }

    public sealed record IsotopeMassTag(IsotopePreferenceRow Owner, int Mass);
}
