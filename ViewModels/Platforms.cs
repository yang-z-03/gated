using System.ComponentModel;
using System.Threading.Tasks;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed partial class MainWindowViewModel 
{
    public Platform? SelectedIntegrationJob
    {
        get => selected_integration_job;
        private set
        {
            unsubscribe_selected_integration_job();
            if (!SetField(ref selected_integration_job, value))
            {
                subscribe_selected_integration_job();
                return;
            }
            subscribe_selected_integration_job();
            if (value is not null)
                set_view_state(MainWindowViewState.Platform, value.StatusText);
            else if (IsIntegrationJobMode)
                set_view_state(MainWindowViewState.Analysis, "Analysis view");
            OnPropertyChanged(nameof(IsIntegrationJobMode));
            raise_command_states();
        }
    }

    private async Task create_platform_async(PlatformKind kind)
    {
        if (!await TryLeavePythonScriptEditorAsync())
            return;

        var job = PlatformJobInitializer.Create(Workspace, kind, selected_group);
        Workspace.IntegrationJobs.Add(job);
        SelectedIntegrationJob = job;
        refresh_project_tree();
        SelectedNode = find_project_node($"workspace:integration-job:{job.Id}");
        StatusText = $"Created platform: {job.Name}";
        raise_command_states();
    }

    private async Task rename_selected_integration_job_async()
    {
        if (selected_integration_job is null || RequestTextInputAsync is null)
            return;

        string? name = await RequestTextInputAsync("Rename integration job", selected_integration_job.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        selected_integration_job.Name = name.Trim();
        refresh_project_tree();
    }

    private void subscribe_selected_integration_job()
    {
        subscribed_integration_job = selected_integration_job;
        if (subscribed_integration_job is not null)
            subscribed_integration_job.PropertyChanged += selected_integration_job_changed;
    }

    private void unsubscribe_selected_integration_job()
    {
        if (subscribed_integration_job is not null)
            subscribed_integration_job.PropertyChanged -= selected_integration_job_changed;
        subscribed_integration_job = null;
    }

    private void selected_integration_job_changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, selected_integration_job))
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
