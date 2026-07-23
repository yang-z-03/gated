using System.ComponentModel;
using System.Threading.Tasks;
using gated.Models;
using gated.Services;
using gated.ViewModels.Platforms;

namespace gated.ViewModels;

public sealed partial class MainWindowViewModel 
{
    public Platform? SelectedPlatform
    {
        get => selected_platform;
        private set
        {
            unsubscribe_selected_platform();
            if (!SetField(ref selected_platform, value))
            {
                subscribe_selected_platform();
                return;
            }
            subscribe_selected_platform();
            SelectedPlatformEditor = value is null
                ? null
                : PlatformCatalog.Get(value.Kind).CreateEditor(Workspace, value);
            if (value is not null)
                set_view_state(MainWindowViewState.Platform, value.StatusText);
            else if (IsPlatformMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
            OnPropertyChanged(nameof(IsPlatformMode));
            raise_command_states();
        }
    }

    public PlatformEditorViewModel? SelectedPlatformEditor
    {
        get => selected_platform_editor;
        private set
        {
            if (ReferenceEquals(selected_platform_editor, value))
                return;
            selected_platform_editor?.Dispose();
            if (SetField(ref selected_platform_editor, value))
                OnPropertyChanged();
        }
    }

    private async Task create_platform_async(PlatformKind kind)
    {
        if (!await TryLeavePythonScriptEditorAsync())
            return;

        var platform = PlatformInitializer.Create(Workspace, kind, selected_group);
        Workspace.Platforms.Add(platform);
        SelectedPlatform = platform;
        refresh_project_tree();
        SelectedNode = find_project_node($"workspace:platform:{platform.Id}");
        StatusText = $"Created platform: {platform.Name}";
        raise_command_states();
    }

    private async Task rename_selected_platform_async()
    {
        if (selected_platform is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename platform", selected_platform.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_platform.Name = name.Trim();
        refresh_project_tree();
    }

    private void subscribe_selected_platform()
    {
        subscribed_platform = selected_platform;
        if (subscribed_platform is not null)
            subscribed_platform.PropertyChanged += selected_platform_changed;
    }

    private void unsubscribe_selected_platform()
    {
        if (subscribed_platform is not null)
            subscribed_platform.PropertyChanged -= selected_platform_changed;
        subscribed_platform = null;
    }

    private void selected_platform_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, selected_platform))
            return;
        if (e.PropertyName is nameof(Platform.Name) or
            nameof(Platform.HasIntegrated) or
            nameof(Platform.HasResults) or
            nameof(Platform.IsConfigurationLocked) or
            nameof(Platform.Status) or
            nameof(Platform.CurrentStep))
            refresh_project_tree();
        raise_command_states();
    }
}
