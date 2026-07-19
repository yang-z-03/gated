using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed class MassNormalizationViewModel : NotifyBase
{
    private readonly MainWindowViewModel owner;
    private readonly MassNormalizationService service = new();
    private readonly Dictionary<Guid, CancellationTokenSource> annotation_tokens = new();
    private FlowGroup? group;
    private MassNormalizationRowViewModel? selected_row;
    private MassGatePlotViewModel? selected_gate_plot;
    private MassOutputGroupChoice? selected_output_group_choice;
    private bool is_busy;
    private bool is_applying;
    private double apply_progress;
    private string apply_progress_text = "";
    private string status = "Drop samples to begin.";
    private string warning_text = "";

    public ObservableCollection<MassNormalizationRowViewModel> Rows { get; } = new();
    public ObservableCollection<MassGatePlotViewModel> GatePlots { get; } = new();
    public ObservableCollection<MassOutputGroupChoice> OutputGroupChoices { get; } = new();
    public ICommand DropSampleCommand { get; }
    public ICommand RemoveSampleCommand { get; }
    public ICommand GateCommittedCommand { get; }
    public ICommand SelectPreviousGatePlotCommand { get; }
    public ICommand SelectNextGatePlotCommand { get; }
    public ICommand ApplyCommand { get; }

    public MassNormalizationViewModel(MainWindowViewModel owner)
    {
        this.owner = owner;
        DropSampleCommand = new RelayCommand(parameter => drop_sample(parameter as ProjectNode), parameter =>
            parameter is ProjectNode { Kind: ProjectNodeKind.Sample, Sample: not null } node && group is not null && node.Group?.Id == group.Id);
        RemoveSampleCommand = new RelayCommand(parameter => remove_sample(parameter as MassNormalizationRowViewModel));
        GateCommittedCommand = new RelayCommand(_ => gate_committed());
        SelectPreviousGatePlotCommand = new RelayCommand(_ => select_relative_gate_plot(-1));
        SelectNextGatePlotCommand = new RelayCommand(_ => select_relative_gate_plot(1));
        ApplyCommand = new RelayCommand(_ => _ = apply_async(), _ => can_apply());
    }

    public MassNormalizationRowViewModel? SelectedRow
    {
        get => selected_row;
        set
        {
            if (!SetField(ref selected_row, value)) return;
            rebuild_gate_plots();
            OnPropertyChanged(nameof(TimeDynamics));
        }
    }
    public MassGatePlotViewModel? SelectedGatePlot
    {
        get => selected_gate_plot;
        set => SetField(ref selected_gate_plot, value);
    }
    public MassTimeDynamicsData? TimeDynamics => SelectedRow?.State.TimeDynamics;
    public bool HasRows => Rows.Count > 0;
    public bool IsBusy { get => is_busy; private set { if (SetField(ref is_busy, value)) raise_commands(); } }
    public bool IsApplying { get => is_applying; private set => SetField(ref is_applying, value); }
    public double ApplyProgress { get => apply_progress; private set => SetField(ref apply_progress, value); }
    public string ApplyProgressText { get => apply_progress_text; private set => SetField(ref apply_progress_text, value); }
    public string Status { get => status; private set => SetField(ref status, value ?? ""); }
    public string WarningText { get => warning_text; private set { if (SetField(ref warning_text, value ?? "")) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public bool RemoveBeadsAndDoublets
    {
        get => group?.MassNormalization.RemoveBeadsAndDoublets ?? true;
        set { if (group is null || group.MassNormalization.RemoveBeadsAndDoublets == value) return; group.MassNormalization.RemoveBeadsAndDoublets = value; OnPropertyChanged(); }
    }
    public MassOutputGroupChoice? SelectedOutputGroupChoice
    {
        get => selected_output_group_choice;
        set
        {
            if (!SetField(ref selected_output_group_choice, value)) return;
            if (group is not null) group.MassNormalization.LinkedOutputGroupId = value?.Group?.Id;
        }
    }

    public void SetGroup(FlowGroup? value)
    {
        foreach (var token in annotation_tokens.Values) token.Cancel();
        annotation_tokens.Clear();
        group = value;
        Rows.Clear(); GatePlots.Clear(); SelectedGatePlot = null; OutputGroupChoices.Clear();
        if (group is null)
        {
            SelectedRow = null;
            notify_visibility();
            return;
        }
        for (int index = group.MassNormalization.Rows.Count - 1; index >= 0; index--)
            if (group.Samples.All(sample => sample.Id != group.MassNormalization.Rows[index].SampleId))
                group.MassNormalization.Rows.RemoveAt(index);
        foreach (var state in group.MassNormalization.Rows)
            if (group.Samples.FirstOrDefault(sample => sample.Id == state.SampleId) is { } sample)
                Rows.Add(new MassNormalizationRowViewModel(this, state, sample));
        rebuild_output_choices();
        SelectedRow = Rows.FirstOrDefault();
        OnPropertyChanged(nameof(RemoveBeadsAndDoublets));
        Status = Rows.Count == 0 ? "Drop samples to begin." : "Preparing bead annotations ...";
        notify_visibility();
        foreach (var row in Rows) _ = annotate_async(row, reset_gates: row.State.Gates.Count == 0);
    }

    internal void BeadTypeChanged(MassNormalizationRowViewModel row, ElementBeadTypePreference type)
    {
        var lot = type.Lots.FirstOrDefault();
        if (lot is null)
        {
            row.State.References.Clear();
            row.State.Gates.Clear();
            row.State.CacheError = "The selected bead type has no lot definition.";
            row.RefreshStatus();
            return;
        }
        snapshot_lot(row.State, type, lot);
        row.RebuildLotChoices(lot.Id);
        _ = annotate_async(row, reset_gates: true);
    }

    internal void BeadLotChanged(MassNormalizationRowViewModel row, ElementBeadTypePreference type, ElementBeadLotPreference lot)
    {
        bool masses_changed = !row.State.References.Select(item => item.MassNumber).SequenceEqual(lot.References.OrderBy(item => item.MassNumber).Select(item => item.MassNumber));
        snapshot_lot(row.State, type, lot);
        _ = annotate_async(row, reset_gates: masses_changed || row.State.Gates.Count == 0);
    }

    internal void DnaChannelChanged(MassNormalizationRowViewModel row) => _ = annotate_async(row, reset_gates: true);

    private void drop_sample(ProjectNode? node)
    {
        if (group is null || node?.Sample is not { } sample || group.Samples.All(item => item.Id != sample.Id) || Rows.Any(row => row.Sample.Id == sample.Id)) return;
        var type = compatible_type(sample) ?? Configuration.Preferences.ElementBeads.FirstOrDefault();
        var state = new MassNormalizationRow { SampleId = sample.Id, DnaChannel = infer_dna_channel(sample) };
        if (type is not null && type.Lots.FirstOrDefault() is { } lot) snapshot_lot(state, type, lot);
        else state.CacheError = "No bead type with a lot definition is registered.";
        group.MassNormalization.Rows.Add(state);
        var row = new MassNormalizationRowViewModel(this, state, sample);
        Rows.Add(row);
        SelectedRow = row;
        notify_visibility();
        _ = annotate_async(row, reset_gates: true);
        owner.RefreshProjectTreeForSpectral();
    }

    private void remove_sample(MassNormalizationRowViewModel? row)
    {
        if (group is null || row is null) return;
        if (annotation_tokens.Remove(row.Sample.Id, out var token)) token.Cancel();
        group.MassNormalization.Rows.Remove(row.State);
        Rows.Remove(row);
        SelectedRow = Rows.FirstOrDefault();
        notify_visibility();
        owner.RefreshProjectTreeForSpectral();
    }

    private async Task annotate_async(MassNormalizationRowViewModel row, bool reset_gates)
    {
        if (annotation_tokens.Remove(row.Sample.Id, out var previous)) previous.Cancel();
        var cancellation = new CancellationTokenSource();
        annotation_tokens[row.Sample.Id] = cancellation;
        row.IsPreparing = true;
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (reset_gates) service.Annotate(row.Sample, row.State);
                else service.RebuildCaches(row.Sample, row.State);
                cancellation.Token.ThrowIfCancellationRequested();
            }, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            row.RefreshStatus();
            if (ReferenceEquals(SelectedRow, row))
            {
                rebuild_gate_plots();
                OnPropertyChanged(nameof(TimeDynamics));
            }
            rebuild_warning();
            Status = row.State.CacheError.Length == 0 ? $"{row.Sample.Name}: {row.State.QcBeadIndices.Length:N0} QC-passed beads." : row.State.CacheError;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            row.State.CacheError = exception.Message;
            row.RefreshStatus();
            Status = exception.Message;
        }
        finally
        {
            row.IsPreparing = false;
            if (annotation_tokens.TryGetValue(row.Sample.Id, out var current) && ReferenceEquals(current, cancellation)) annotation_tokens.Remove(row.Sample.Id);
            IsBusy = annotation_tokens.Count > 0;
            raise_commands();
        }
    }

    private void gate_committed()
    {
        if (SelectedRow is null) return;
        _ = annotate_async(SelectedRow, reset_gates: false);
    }

    private async Task apply_async()
    {
        if (group is null) return;
        IsBusy = true; IsApplying = true; ApplyProgress = 0; ApplyProgressText = "Preparing mass normalization ..."; WarningText = "";
        try
        {
            var target = service.EnsureOutputGroup(owner.Workspace, group, SelectedOutputGroupChoice?.Group);
            var progress = new Progress<MassNormalizationProgress>(update =>
            {
                ApplyProgress = Math.Clamp(update.Fraction * 100, 0, 100);
                ApplyProgressText = $"{update.Detail} — {ApplyProgress:0}%";
                Status = ApplyProgressText;
            });
            var preparation = await Task.Run(() => service.PrepareApply(group, target, progress));
            service.CommitApply(group, target, preparation);
            group.MassNormalization.LinkedOutputGroupId = target.Id;
            rebuild_output_choices();
            SelectedOutputGroupChoice = OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == target.Id);
            Status = $"Normalized samples written to {target.Name}.";
            rebuild_warning();
            owner.RefreshProjectTreeForSpectral();
        }
        catch (Exception exception) { Status = exception.Message; WarningText = exception.Message; }
        finally { IsApplying = false; IsBusy = false; raise_commands(); }
    }

    private bool can_apply() => group is not null && Rows.Count > 0 && !IsBusy && Rows.All(row => row.State.CacheError.Length == 0 && row.State.QcBeadIndices.Length >= 20);

    private void rebuild_gate_plots()
    {
        int? selected_mass = SelectedGatePlot?.MassNumber;
        GatePlots.Clear();
        if (SelectedRow is null)
        {
            SelectedGatePlot = null;
            return;
        }
        string dna = SelectedRow.State.DnaChannel;
        var dna_definition = SelectedRow.Sample.Channels.FirstOrDefault(channel => channel.Name == dna);
        foreach (var gate in SelectedRow.State.Gates.OrderBy(gate => gate.MassNumber))
        {
            var definition = SelectedRow.Sample.Channels.FirstOrDefault(channel => channel.Name == gate.ChannelName);
            GatePlots.Add(new MassGatePlotViewModel(
                gate.MassNumber,
                gate.ChannelName,
                dna,
                SelectedRow.PreviewSample,
                gate.Vertices,
                Math.Max(1, definition?.Maximum ?? 262144),
                Math.Max(1, dna_definition?.Maximum ?? 262144)));
        }
        SelectedGatePlot = GatePlots.FirstOrDefault(plot => plot.MassNumber == selected_mass) ?? GatePlots.FirstOrDefault();
        OnPropertyChanged(nameof(GatePlots));
    }

    private void select_relative_gate_plot(int direction)
    {
        if (GatePlots.Count == 0) return;
        int index = SelectedGatePlot is null ? 0 : GatePlots.IndexOf(SelectedGatePlot);
        if (index < 0) index = 0;
        SelectedGatePlot = GatePlots[(index + direction + GatePlots.Count) % GatePlots.Count];
    }

    private void rebuild_output_choices()
    {
        OutputGroupChoices.Clear();
        if (group is null) return;
        var generated = new MassOutputGroupChoice($"{group.Name} normalized (new)", null, true);
        OutputGroupChoices.Add(generated);
        foreach (var candidate in owner.Workspace.Groups.Where(candidate => candidate.Id != group.Id && (candidate.Samples.Count == 0 || candidate.MassNormalizationSourceGroupId == group.Id)))
            OutputGroupChoices.Add(new MassOutputGroupChoice(candidate.Name, candidate, false));
        selected_output_group_choice = group.MassNormalization.LinkedOutputGroupId is { } linked
            ? OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == linked) ?? generated
            : generated;
        OnPropertyChanged(nameof(SelectedOutputGroupChoice));
    }

    private void rebuild_warning()
    {
        var extrapolated = Rows.SelectMany(row => service.ExtrapolatedMasses(row.Sample, row.State)).Distinct().OrderBy(mass => mass).ToArray();
        string extrapolation = extrapolated.Length == 0 ? "" : $"Linear extrapolation will be used for mass channels: \n{string.Join(", ", extrapolated)}.";
        string low_count = Rows.Any(row => row.State.QcBeadIndices.Length is >= 20 and < 100) ? " Fewer than 100 QC beads were found in one or more samples." : "";
        WarningText = (extrapolation + low_count).Trim();
    }

    private ElementBeadTypePreference? compatible_type(FlowSample sample)
    {
        string cytometer = Configuration.CytometerNameForSample(sample);
        var masses = sample.Channels.Select(channel => Configuration.MassNumberForChannel(channel.Name, cytometer)).Where(mass => mass.HasValue).Select(mass => mass!.Value).ToHashSet();
        return Configuration.Preferences.ElementBeads.FirstOrDefault(type => type.Lots.Count > 0 && type.Isotopes.Count >= 2 && type.Isotopes.All(masses.Contains));
    }

    private static string infer_dna_channel(FlowSample sample)
    {
        foreach (string token in new[] { "DNA1", "DNA2", "DNA" })
            if (sample.Channels.FirstOrDefault(channel => channel.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) is { } channel) return channel.Name;
        return "";
    }

    private static void snapshot_lot(MassNormalizationRow state, ElementBeadTypePreference type, ElementBeadLotPreference lot)
    {
        state.BeadTypeId = type.Id; state.BeadTypeName = type.Name;
        state.BeadLotId = lot.Id; state.BeadLotName = lot.Name;
        state.References.Clear();
        foreach (var reference in lot.References.OrderBy(reference => reference.MassNumber))
            state.References.Add(new MassReferenceSnapshot { MassNumber = reference.MassNumber, ReferenceIntensity = reference.ReferenceIntensity });
    }

    private void notify_visibility() { OnPropertyChanged(nameof(HasRows)); raise_commands(); }
    private void raise_commands() => (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
}

public sealed class MassNormalizationRowViewModel : NotifyBase
{
    private readonly MassNormalizationViewModel owner;
    private bool initializing = true;
    private bool is_preparing;
    private ElementBeadTypePreference? selected_bead_type;
    private ElementBeadLotPreference? selected_bead_lot;
    private string population_text = "Preparing ...";
    public MassNormalizationRow State { get; }
    public FlowSample Sample { get; }
    public ControlSample PreviewSample { get; }
    public ObservableCollection<ElementBeadTypePreference> BeadTypes { get; } = new();
    public ObservableCollection<ElementBeadLotPreference> BeadLots { get; } = new();
    public IReadOnlyList<string> DnaChannels { get; }
    public ICommand RemoveCommand => owner.RemoveSampleCommand;
    public string SampleName => Sample.Name;
    public string SampleIcon => "tube.svg";
    public string RemoveIcon => "delete.svg";
    public string PopulationIcon => State.CacheError.Length == 0 && State.QcBeadIndices.Length >= 20 ? "ok.svg" : "warning.svg";
    public string PopulationText { get => population_text; private set => SetField(ref population_text, value); }
    public bool IsPreparing { get => is_preparing; set { if (SetField(ref is_preparing, value)) RefreshStatus(); } }

    public MassNormalizationRowViewModel(MassNormalizationViewModel owner, MassNormalizationRow state, FlowSample sample)
    {
        this.owner = owner; State = state; Sample = sample;
        PreviewSample = new ControlSample(sample.Name, sample.Channels, sample.RawEvents);
        DnaChannels = sample.Channels.Select(channel => channel.Name).ToArray();
        foreach (var type in Configuration.Preferences.ElementBeads) BeadTypes.Add(type);
        selected_bead_type = BeadTypes.FirstOrDefault(type => type.Id == state.BeadTypeId);
        if (selected_bead_type is null && state.References.Count > 0)
        {
            selected_bead_type = new ElementBeadTypePreference { Id = state.BeadTypeId, Name = string.IsNullOrWhiteSpace(state.BeadTypeName) ? "Saved bead type" : state.BeadTypeName };
            foreach (int mass in state.References.Select(reference => reference.MassNumber)) selected_bead_type.Isotopes.Add(mass);
            var saved_lot = new ElementBeadLotPreference { Id = state.BeadLotId, Name = string.IsNullOrWhiteSpace(state.BeadLotName) ? "Saved lot" : state.BeadLotName };
            foreach (var reference in state.References) saved_lot.References.Add(new ElementBeadReferencePreference { MassNumber = reference.MassNumber, ReferenceIntensity = reference.ReferenceIntensity });
            selected_bead_type.Lots.Add(saved_lot); BeadTypes.Add(selected_bead_type);
        }
        selected_bead_type ??= BeadTypes.FirstOrDefault();
        RebuildLotChoices(state.BeadLotId);
        initializing = false;
        RefreshStatus();
    }

    public ElementBeadTypePreference? SelectedBeadType
    {
        get => selected_bead_type;
        set
        {
            if (!SetField(ref selected_bead_type, value) || value is null || initializing) return;
            owner.BeadTypeChanged(this, value);
            OnPropertyChanged(nameof(SelectedBeadLot));
        }
    }
    public ElementBeadLotPreference? SelectedBeadLot
    {
        get => selected_bead_lot;
        set
        {
            if (!SetField(ref selected_bead_lot, value) || value is null || selected_bead_type is null || initializing) return;
            owner.BeadLotChanged(this, selected_bead_type, value);
        }
    }
    public string DnaChannel
    {
        get => State.DnaChannel;
        set
        {
            value ??= "";
            if (State.DnaChannel == value) return;
            State.DnaChannel = value; OnPropertyChanged();
            if (!initializing) owner.DnaChannelChanged(this);
        }
    }

    internal void RebuildLotChoices(Guid selected_id)
    {
        bool prior = initializing; initializing = true;
        BeadLots.Clear();
        if (selected_bead_type is not null) foreach (var lot in selected_bead_type.Lots) BeadLots.Add(lot);
        selected_bead_lot = BeadLots.FirstOrDefault(lot => lot.Id == selected_id) ?? BeadLots.FirstOrDefault();
        OnPropertyChanged(nameof(BeadLots)); OnPropertyChanged(nameof(SelectedBeadLot));
        initializing = prior;
    }

    internal void RefreshStatus()
    {
        PopulationText = IsPreparing ? "Preparing ..." : State.CacheError.Length > 0 ? State.CacheError : $"{State.QcBeadIndices.Length:N0} QC beads ({State.QcBeadIndices.Length / (double)Math.Max(1, Sample.EventCount):P1})";
        OnPropertyChanged(nameof(PopulationIcon));
    }
}

public sealed record MassOutputGroupChoice(string Name, FlowGroup? Group, bool IsGenerated)
{
    public override string ToString() => Name;
}

public sealed record MassGatePlotViewModel(
    int MassNumber,
    string XChannel,
    string YChannel,
    ControlSample Sample,
    ObservableCollection<Point> Vertices,
    double XMaximum,
    double YMaximum)
{
    public string Title => $"{MassNumber} — {XChannel} / {YChannel}";
    public AxisScale XScale { get; } = new() { Kind = CoordinateScaleKind.Arcsinh };
    public AxisScale YScale { get; } = new() { Kind = CoordinateScaleKind.Arcsinh };
}
