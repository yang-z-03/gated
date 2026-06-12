using System;
using System.Collections.ObjectModel;
using System.Linq;
using gated.Reduction;

namespace gated.Models;

public enum IntegrationJobStatus
{
    Draft,
    Ready,
    Running,
    Cancelled,
    Complete,
    Warning,
    Failed
}

public sealed class IntegrationJob : NotifyBase
{
    private string name = "Integration job";
    private IntegrationJobStatus status = IntegrationJobStatus.Draft;
    private string warning_text = "";
    private int current_step;
    private bool is_running;
    private double progress_fraction;
    private string progress_text = "";
    private bool cancellation_requested;
    private string batch_column_name = "";

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set => SetField(ref name, string.IsNullOrWhiteSpace(value) ? "Integration job" : value.Trim());
    }

    public IntegrationJobStatus Status
    {
        get => status;
        set
        {
            if (!SetField(ref status, value))
                return;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string WarningText
    {
        get => warning_text;
        set
        {
            if (!SetField(ref warning_text, value ?? ""))
                return;
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public bool IsRunning
    {
        get => is_running;
        set
        {
            if (!SetField(ref is_running, value))
                return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsRunning;

    public double ProgressFraction
    {
        get => progress_fraction;
        set => SetField(ref progress_fraction, Math.Clamp(value, 0, 1));
    }

    public string ProgressText
    {
        get => progress_text;
        set => SetField(ref progress_text, value ?? "");
    }

    public bool CancellationRequested
    {
        get => cancellation_requested;
        set => SetField(ref cancellation_requested, value);
    }

    public int CurrentStep
    {
        get => current_step;
        set => SetField(ref current_step, Math.Clamp(value, 0, 7));
    }

    public string StatusText => HasWarning ? WarningText : Status.ToString();

    public ObservableCollection<IntegrationJobPopulationSelection> Populations { get; } = new();
    public ObservableCollection<IntegrationJobFeatureSelection> Features { get; } = new();

    public string BatchColumnName
    {
        get => batch_column_name;
        set => SetField(ref batch_column_name, value ?? "");
    }

    public LogicleParameters Logicle { get; set; } = new();
    public double LogicleT
    {
        get => Logicle.T;
        set { Logicle = Logicle with { T = value }; OnPropertyChanged(); }
    }
    public double LogicleW
    {
        get => Logicle.W;
        set { Logicle = Logicle with { W = value }; OnPropertyChanged(); }
    }
    public double LogicleM
    {
        get => Logicle.M;
        set { Logicle = Logicle with { M = value }; OnPropertyChanged(); }
    }
    public double LogicleA
    {
        get => Logicle.A;
        set { Logicle = Logicle with { A = value }; OnPropertyChanged(); }
    }
    public CytoNormOptions CytoNormOptions { get; set; } = new();

    public ObservableCollection<IntegrationJobRowMap> RowMap { get; } = new();
    public float[,]? SourceData { get; set; }
    public int[] BatchIds { get; set; } = [];
    public float[,]? LogicleNormalized { get; set; }
    public float[,]? CytoNormNormalized { get; set; }

    public float[,]? CurrentMatrix => CytoNormNormalized ?? LogicleNormalized ?? SourceData;
    public bool HasIntegrated => LogicleNormalized is not null;
    public bool IsConfigurationLocked => HasIntegrated;

    public void InvalidateFromConfiguration()
    {
        RowMap.Clear();
        SourceData = null;
        BatchIds = [];
        LogicleNormalized = null;
        CytoNormNormalized = null;
        CurrentStep = Math.Min(CurrentStep, 3);
        WarningText = "Configuration changed. Rerun integration before downstream steps.";
        Status = IntegrationJobStatus.Warning;
    }

    public string[] SelectedFeatureNames => Features
        .Where(feature => feature.IsSelected && feature.IsChannel && !string.IsNullOrWhiteSpace(feature.ChannelName))
        .Select(feature => feature.ChannelName)
        .ToArray();
}

public sealed class IntegrationJobPopulationSelection : NotifyBase
{
    private bool is_selected = true;
    private bool is_expanded = true;
    private bool is_enabled = true;
    private bool is_indeterminate;
    private Guid? parent_key;
    private Guid row_key = Guid.NewGuid();

    public Guid RowKey
    {
        get => row_key;
        init => row_key = value;
    }
    public Guid? ParentKey
    {
        get => parent_key;
        init => parent_key = value;
    }
    public Guid GroupId { get; init; }
    public Guid SampleId { get; init; }
    public Guid GateId { get; init; }
    public PopulationRegion Region { get; init; } = PopulationRegion.Primary;
    public string GroupName { get; init; } = "";
    public string SampleName { get; init; } = "";
    public string PopulationName { get; init; } = "";
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
    public bool IsPopulation { get; init; } = true;

    public bool IsSelected
    {
        get => is_selected;
        set => SetField(ref is_selected, value);
    }

    public bool IsEnabled
    {
        get => is_enabled;
        set => SetField(ref is_enabled, value);
    }

    public bool IsIndeterminate
    {
        get => is_indeterminate;
        set => SetField(ref is_indeterminate, value);
    }

    public bool IsExpanded
    {
        get => is_expanded;
        set => SetField(ref is_expanded, value);
    }

    public string DisplayName => IsPopulation ? PopulationName : $"{GroupName} / {SampleName}";
}

public sealed class IntegrationJobFeatureSelection : NotifyBase
{
    private bool is_selected = true;
    private bool is_enabled = true;
    private bool is_indeterminate;
    private bool is_expanded = true;
    private Guid row_key = Guid.NewGuid();

    public Guid RowKey
    {
        get => row_key;
        init => row_key = value;
    }
    public Guid? ParentKey { get; init; }
    public string ChannelName { get; init; } = "";
    public string Label { get; init; } = "";
    public string GroupName { get; init; } = "";
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
    public bool IsChannel { get; init; } = true;

    public bool IsSelected
    {
        get => is_selected;
        set => SetField(ref is_selected, value);
    }

    public bool IsEnabled
    {
        get => is_enabled;
        set => SetField(ref is_enabled, value);
    }

    public bool IsIndeterminate
    {
        get => is_indeterminate;
        set => SetField(ref is_indeterminate, value);
    }

    public bool IsExpanded
    {
        get => is_expanded;
        set => SetField(ref is_expanded, value);
    }

    public string DisplayName => IsChannel ? ChannelName : GroupName;
}

public sealed class IntegrationJobRowMap
{
    public Guid GroupId { get; init; }
    public Guid SampleId { get; init; }
    public Guid GateId { get; init; }
    public PopulationRegion Region { get; init; } = PopulationRegion.Primary;
    public int EventIndex { get; init; }
}
