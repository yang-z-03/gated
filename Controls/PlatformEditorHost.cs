using Avalonia;
using Avalonia.Controls;
using gated.Models;
using gated.ViewModels.Platforms;

namespace gated.Controls;

public sealed class PlatformEditorHost : ContentControl
{
    public static readonly StyledProperty<FlowWorkspace?> WorkspaceProperty =
        AvaloniaProperty.Register<PlatformEditorHost, FlowWorkspace?>(nameof(Workspace));

    public static readonly StyledProperty<Platform?> PlatformProperty =
        AvaloniaProperty.Register<PlatformEditorHost, Platform?>(nameof(Platform));

    private PlatformEditorViewModel? view_model;

    public FlowWorkspace? Workspace
    {
        get => GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    public Platform? Platform
    {
        get => GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WorkspaceProperty || change.Property == PlatformProperty)
            rebuild();
    }

    private void rebuild()
    {
        view_model?.Dispose();
        view_model = null;
        if (Workspace is null || Platform is null)
        {
            Content = null;
            return;
        }

        Control view;
        switch (Platform.Kind)
        {
            case PlatformKind.CellCycle:
                view_model = new CellCyclePlatformEditorViewModel(Workspace, Platform);
                view = new CellCyclePlatformView();
                break;
            case PlatformKind.Proliferation:
                view_model = new ProliferationPlatformEditorViewModel(Workspace, Platform);
                view = new ProliferationPlatformView();
                break;
            case PlatformKind.IntensityComparison:
                view_model = new IntensityComparisonPlatformEditorViewModel(Workspace, Platform);
                view = new IntensityComparisonPlatformView();
                break;
            case PlatformKind.Kinetics:
                view_model = new KineticsPlatformEditorViewModel(Workspace, Platform);
                view = new KineticsPlatformView();
                break;
            default:
                view_model = new IntegrationPlatformEditorViewModel(Workspace, Platform);
                view = new IntegrationPlatformView();
                break;
        }

        view.DataContext = view_model;
        Content = view;
    }
}
