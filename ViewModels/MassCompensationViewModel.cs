using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed class MassCompensationViewModel : NotifyBase
{
    private readonly MainWindowViewModel owner;
    private readonly MassCompensationService service = new();
    private FlowGroup? group;
    private MassCompensationRowViewModel? selected_row;
    private CompensationMatrix? draft_matrix;
    private bool is_busy;
    private bool is_stale;
    private string status = "Drop single-staining controls to begin.";

    public ObservableCollection<MassCompensationRowViewModel> Rows { get; } = new();
    public ObservableCollection<SpectralMatrixRowViewModel> MatrixRows { get; } = new();
    public ObservableCollection<string> MatrixColumnLabels { get; } = new();
    public ICommand DropControlCommand { get; }
    public ICommand RemoveControlCommand { get; }
    public ICommand CalculateCommand { get; }
    public ICommand ApplyCommand { get; }

    public MassCompensationViewModel(MainWindowViewModel owner)
    {
        this.owner = owner;
        DropControlCommand = new RelayCommand(parameter => drop_control(parameter as ProjectNode), parameter =>
            parameter is ProjectNode { Kind: ProjectNodeKind.ControlSample, ControlSample: not null } node &&
            group is not null && node.Group?.Id == group.Id && !IsBusy);
        RemoveControlCommand = new RelayCommand(parameter => remove_control(parameter as MassCompensationRowViewModel));
        CalculateCommand = new RelayCommand(_ => _ = calculate_async(), _ => group is not null && Rows.Count > 0 && !IsBusy);
        ApplyCommand = new RelayCommand(_ => apply(), _ => group is not null && draft_matrix is not null && !IsBusy && !IsStale);
    }

    public MassCompensationRowViewModel? SelectedRow { get => selected_row; set => SetField(ref selected_row, value); }
    public bool HasRows => Rows.Count > 0;
    public bool HasMatrix => draft_matrix is not null;
    public bool IsBusy { get => is_busy; private set { if (SetField(ref is_busy, value)) raise_commands(); } }
    public bool IsStale { get => is_stale; private set { if (SetField(ref is_stale, value)) raise_commands(); } }
    public string Status { get => status; private set => SetField(ref status, value ?? ""); }
    public string MatrixName
    {
        get => group?.MassCompensation.MatrixName ?? "Mass compensation";
        set
        {
            if (group is null || group.MassCompensation.MatrixName == value) return;
            group.MassCompensation.MatrixName = value;
            OnPropertyChanged();
        }
    }

    public void SetGroup(FlowGroup? value)
    {
        group = value;
        Rows.Clear(); MatrixRows.Clear(); MatrixColumnLabels.Clear(); draft_matrix = null; IsStale = false;
        if (group is null)
        {
            SelectedRow = null;
            Status = "Select a group to configure mass compensation.";
            notify_visibility();
            return;
        }
        var valid_ids = group.ControlSamples.Select(sample => sample.Id).ToHashSet();
        for (int index = group.MassCompensation.Rows.Count - 1; index >= 0; index--)
            if (!valid_ids.Contains(group.MassCompensation.Rows[index].ControlSampleId))
                group.MassCompensation.Rows.RemoveAt(index);
        var descriptors = service.DescribeChannels(group);
        foreach (var state in group.MassCompensation.Rows)
            if (group.ControlSamples.FirstOrDefault(sample => sample.Id == state.ControlSampleId) is { } sample)
                Rows.Add(new MassCompensationRowViewModel(this, state, sample, choices_for(sample, descriptors)));
        SelectedRow = Rows.FirstOrDefault();
        Status = descriptors.Count == 0
            ? "No mass channels with element and mass metadata are registered for this group."
            : Rows.Count == 0 ? "Drop single-staining controls to begin." : "Ready to calculate mass compensation.";
        OnPropertyChanged(nameof(MatrixName));
        notify_visibility();
    }

    internal void SourceChanged() => mark_stale();

    private void drop_control(ProjectNode? node)
    {
        if (group is null || node?.ControlSample is not { } sample || node.Group?.Id != group.Id ||
            Rows.Any(row => row.Sample.Id == sample.Id)) return;
        var descriptors = service.DescribeChannels(group);
        var choices = choices_for(sample, descriptors);
        if (choices.Count == 0)
        {
            Status = $"{sample.Name} has no compatible registered mass channels.";
            return;
        }
        var occupied = Rows.Select(row => row.SourceChannelName).ToHashSet(StringComparer.Ordinal);
        var guessed = choices.FirstOrDefault(choice =>
            !occupied.Contains(choice.ChannelName) &&
            (sample.Name.Contains(choice.DisplayName, StringComparison.OrdinalIgnoreCase) || sample.Name.Contains(choice.ChannelName, StringComparison.OrdinalIgnoreCase)))
            ?? choices.FirstOrDefault(choice => !occupied.Contains(choice.ChannelName))
            ?? choices[0];
        var state = new MassCompensationControlRow { ControlSampleId = sample.Id, SourceChannelName = guessed.ChannelName };
        group.MassCompensation.Rows.Add(state);
        var row = new MassCompensationRowViewModel(this, state, sample, choices);
        Rows.Add(row); SelectedRow = row;
        mark_stale(); notify_visibility(); owner.RefreshProjectTreeForSpectral();
        Status = $"Added {sample.Name} as {guessed.DisplayName}.";
    }

    private void remove_control(MassCompensationRowViewModel? row)
    {
        if (group is null || row is null) return;
        group.MassCompensation.Rows.Remove(row.State);
        Rows.Remove(row); SelectedRow = Rows.FirstOrDefault();
        mark_stale(); notify_visibility(); owner.RefreshProjectTreeForSpectral();
    }

    private async Task calculate_async()
    {
        if (group is null) return;
        if (Rows.Any(row => string.IsNullOrWhiteSpace(row.SourceChannelName)))
        {
            Status = "Every control tube must have a single-staining isotope.";
            return;
        }
        var current_group = group;
        var controls = Rows.Select(row => new MassCompensationControlInput(row.Sample, row.SourceChannelName)).ToArray();
        IsBusy = true; Status = "Calculating mass compensation from up to 5,000 events per tube ...";
        try
        {
            var result = await Task.Run(() => service.Fit(current_group, controls, MatrixName));
            if (!ReferenceEquals(group, current_group)) return;
            build_matrix(result);
            IsStale = false;
            Status = "Fit complete. Review or edit the spillover matrix, then apply it.";
        }
        catch (Exception exception) { Status = exception.Message; }
        finally { IsBusy = false; }
    }

    private void build_matrix(MassCompensationFitResult result)
    {
        draft_matrix = result.Matrix;
        MatrixColumnLabels.Clear();
        foreach (var channel in result.Channels) MatrixColumnLabels.Add(channel.DisplayName);
        MatrixRows.Clear();
        var values = (float[,])result.Matrix.Values.Clone();
        for (int source = 0; source < result.Channels.Count; source++)
        {
            int captured_source = source;
            var cells = new List<SpectralMatrixCellViewModel>();
            for (int receiving = 0; receiving < result.Channels.Count; receiving++)
            {
                int captured_receiving = receiving;
                cells.Add(new SpectralMatrixCellViewModel(
                    values[source, receiving],
                    source != receiving,
                    source == receiving ? null : value =>
                    {
                        values[captured_source, captured_receiving] = value;
                        draft_matrix?.ReplaceValues(values);
                    },
                    result.Annotations[source, receiving]));
            }
            MatrixRows.Add(new SpectralMatrixRowViewModel(result.Channels[source].DisplayName, cells));
        }
        OnPropertyChanged(nameof(HasMatrix));
        raise_commands();
    }

    private void apply()
    {
        if (group is null || draft_matrix is null) return;
        int size = MatrixRows.Count;
        var values = new float[size, size];
        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
            values[row, column] = row == column ? 1 : MatrixRows[row].Cells[column].Value;
        if (!MassCompensationService.IsFiniteAndInvertible(values))
        {
            Status = "The spillover matrix must contain finite values and be invertible before it can be applied.";
            return;
        }
        var committed = CompensationMatrix.Create(MatrixName, draft_matrix.ChannelNames, values);
        committed = group.RegisterCompensation(committed, make_applied_if_first: false);
        owner.RefreshProjectTreeForSpectral();
        Status = $"Created compensation matrix: {committed.Name}";
    }

    private void mark_stale()
    {
        if (draft_matrix is not null) IsStale = true;
        raise_commands();
    }

    private static IReadOnlyList<MassChannelChoice> choices_for(ControlSample sample, IReadOnlyList<MassChannelDescriptor> channels) =>
        channels.Where(channel => sample.GetChannelIndex(channel.ChannelName) >= 0)
            .Select(channel => new MassChannelChoice(channel.ChannelName, channel.DisplayName, channel.ToString()))
            .ToArray();

    private void notify_visibility()
    {
        OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(HasMatrix)); raise_commands();
    }

    private void raise_commands()
    {
        (DropControlCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CalculateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class MassCompensationRowViewModel : NotifyBase
{
    private readonly MassCompensationViewModel owner;
    public MassCompensationControlRow State { get; }
    public ControlSample Sample { get; }
    public IReadOnlyList<MassChannelChoice> IsotopeChoices { get; }
    public ICommand RemoveCommand => owner.RemoveControlCommand;
    public string SampleName => Sample.Name;
    public string SampleIcon => "tube.svg";
    public string RemoveIcon => "delete.svg";

    public MassCompensationRowViewModel(
        MassCompensationViewModel owner,
        MassCompensationControlRow state,
        ControlSample sample,
        IReadOnlyList<MassChannelChoice> choices)
    {
        this.owner = owner; State = state; Sample = sample; IsotopeChoices = choices;
    }

    public string SourceChannelName
    {
        get => State.SourceChannelName;
        set
        {
            value ??= "";
            if (State.SourceChannelName == value) return;
            State.SourceChannelName = value; OnPropertyChanged(); owner.SourceChanged();
        }
    }
    public MassChannelChoice? SelectedChoice
    {
        get => IsotopeChoices.FirstOrDefault(choice => string.Equals(choice.ChannelName, SourceChannelName, StringComparison.Ordinal));
        set
        {
            if (value is null || string.Equals(SourceChannelName, value.ChannelName, StringComparison.Ordinal)) return;
            SourceChannelName = value.ChannelName;
            OnPropertyChanged();
        }
    }
}

public sealed record MassChannelChoice(string ChannelName, string DisplayName, string Description)
{
    public override string ToString() => Description;
}
