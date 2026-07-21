using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using gated.Controls;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed class IndexDemultiplexViewModel : NotifyBase
{
    private readonly MainWindowViewModel owner;
    private readonly IndexDemultiplexService service = new();
    private readonly Dictionary<(Guid SampleId, string Channel), IndexDemultiplexFitResult> fits = new();
    private readonly Dictionary<(Guid SampleId, string Channel), CancellationTokenSource> fit_tokens = new();
    private readonly Dictionary<Guid, int[]> assignments = new();
    private FlowGroup? group;
    private IndexDemultiplexSampleRowViewModel? selected_row;
    private IndexChannelViewModel? selected_channel;
    private ChannelDefinition? selected_available_channel;
    private DemultiplexOutputGroupChoice? selected_output_group_choice;
    private bool is_busy;
    private bool is_applying;
    private double apply_progress;
    private string apply_progress_text = "";
    private string status = "Select index channels and drop samples to begin.";
    private string warning_text = "";
    private bool show_presumed_index_only = true;
    private CancellationTokenSource? apply_cancellation;

    public ObservableCollection<IndexChannelViewModel> Channels { get; } = new();
    public ObservableCollection<ChannelDefinition> AvailableChannels { get; } = new();
    public ObservableCollection<IndexDemultiplexSampleRowViewModel> Rows { get; } = new();
    public ObservableCollection<IndexDemultiplexSubsetViewModel> Subsets { get; } = new();
    public ObservableCollection<IndexDemultiplexSubsetViewModel> VisibleSubsets { get; } = new();
    public ObservableCollection<DemultiplexOutputGroupChoice> OutputGroupChoices { get; } = new();
    public ObservableCollection<HistogramSeries> HistogramSeries { get; } = new();
    public ObservableCollection<HistogramCurveSeries> HistogramCurves { get; } = new();
    public ObservableCollection<HistogramModelLayer> HistogramModels { get; } = new();
    public ObservableCollection<HistogramLineAnnotation> HistogramAnnotations { get; } = new();

    public ICommand AddChannelCommand { get; }
    public ICommand RemoveChannelCommand { get; }
    public ICommand DropSampleCommand { get; }
    public ICommand RemoveSampleCommand { get; }
    public ICommand AnnotationCommittedCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelApplyCommand { get; }

    public event EventHandler? StructureChanged;

    public IndexDemultiplexViewModel(MainWindowViewModel owner)
    {
        this.owner = owner;
        AddChannelCommand = new RelayCommand(_ => add_selected_channel(), _ => selected_available_channel is not null && Channels.Count < IndexDemultiplexService.MaximumIndexChannels && !IsBusy);
        RemoveChannelCommand = new RelayCommand(parameter => remove_channel(parameter as IndexChannelViewModel), _ => !IsBusy);
        DropSampleCommand = new RelayCommand(parameter => drop_sample(parameter as ProjectNode), parameter =>
            parameter is ProjectNode { Kind: ProjectNodeKind.Sample, Sample: not null } node && group is not null && node.Group?.Id == group.Id && !IsBusy);
        RemoveSampleCommand = new RelayCommand(parameter => remove_sample(parameter as IndexDemultiplexSampleRowViewModel), _ => !IsBusy);
        AnnotationCommittedCommand = new RelayCommand(parameter => threshold_annotation_committed(parameter as HistogramLineAnnotation));
        ApplyCommand = new RelayCommand(_ => _ = apply_async(), _ => can_apply());
        CancelApplyCommand = new RelayCommand(_ => apply_cancellation?.Cancel(), _ => IsApplying);
    }

    public bool HasRows => Rows.Count > 0;
    public bool HasChannels => Channels.Count > 0;
    public bool HasSubsets => Subsets.Count > 0;
    public bool ShowPresumedIndexOnly
    {
        get => show_presumed_index_only;
        set
        {
            if (!SetField(ref show_presumed_index_only, value)) return;
            refresh_visible_subsets();
        }
    }
    public bool IsBusy { get => is_busy; private set { if (SetField(ref is_busy, value)) raise_commands(); } }
    public bool IsApplying { get => is_applying; private set { if (SetField(ref is_applying, value)) raise_commands(); } }
    public double ApplyProgress { get => apply_progress; private set => SetField(ref apply_progress, value); }
    public string ApplyProgressText { get => apply_progress_text; private set => SetField(ref apply_progress_text, value ?? ""); }
    public string Status { get => status; private set => SetField(ref status, value ?? ""); }
    public string WarningText { get => warning_text; private set { if (SetField(ref warning_text, value ?? "")) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public double HistogramMinimum => 0;
    public double HistogramMaximum => selected_fit()?.FitMaximum is > 0 and var maximum ? maximum : selected_channel_definition()?.Maximum is > 0 and var channel_maximum ? channel_maximum : 1;
    public HistogramAxisScaleKind HistogramAxisScale => HistogramAxisScaleKind.Arcsinh;
    public string HistogramXTitle => selected_channel?.Name ?? "Index signal";

    public ChannelDefinition? SelectedAvailableChannel
    {
        get => selected_available_channel;
        set { if (SetField(ref selected_available_channel, value)) raise_commands(); }
    }
    public IndexDemultiplexSampleRowViewModel? SelectedRow
    {
        get => selected_row;
        set
        {
            if (!SetField(ref selected_row, value)) return;
            refresh_channel_rows();
            refresh_plot();
        }
    }
    public IndexChannelViewModel? SelectedChannel
    {
        get => selected_channel;
        set { if (SetField(ref selected_channel, value)) refresh_plot(); }
    }
    public DemultiplexOutputGroupChoice? SelectedOutputGroupChoice
    {
        get => selected_output_group_choice;
        set
        {
            if (!SetField(ref selected_output_group_choice, value)) return;
            if (group is not null) group.IndexDemultiplex.LinkedOutputGroupId = value?.Group?.Id;
            raise_commands();
        }
    }

    public void SetGroup(FlowGroup? value)
    {
        cancel_fit_work();
        apply_cancellation?.Cancel();
        group = value;
        fits.Clear(); assignments.Clear(); Channels.Clear(); AvailableChannels.Clear(); Rows.Clear(); Subsets.Clear(); VisibleSubsets.Clear();
        HistogramSeries.Clear(); HistogramCurves.Clear(); HistogramModels.Clear(); HistogramAnnotations.Clear(); OutputGroupChoices.Clear();
        SelectedRow = null; SelectedChannel = null; SelectedAvailableChannel = null; WarningText = "";
        if (group is null)
        {
            Status = "Select a group to configure index demultiplexing.";
            notify_structure();
            return;
        }
        var valid_channel_names = group.Channels.Select(channel => channel.Name).ToHashSet(StringComparer.Ordinal);
        for (int index = group.IndexDemultiplex.SelectedChannels.Count - 1; index >= 0; index--)
            if (!valid_channel_names.Contains(group.IndexDemultiplex.SelectedChannels[index]) || index >= IndexDemultiplexService.MaximumIndexChannels)
                group.IndexDemultiplex.SelectedChannels.RemoveAt(index);
        var valid_sample_ids = group.Samples.Select(sample => sample.Id).ToHashSet();
        for (int index = group.IndexDemultiplex.Rows.Count - 1; index >= 0; index--)
            if (!valid_sample_ids.Contains(group.IndexDemultiplex.Rows[index].SampleId)) group.IndexDemultiplex.Rows.RemoveAt(index);
        foreach (string channel in group.IndexDemultiplex.SelectedChannels)
            if (group.Channels.FirstOrDefault(definition => definition.Name == channel) is { } definition)
                Channels.Add(new IndexChannelViewModel(this, definition, RemoveChannelCommand));
        rebuild_available_channels();
        ensure_subsets(reset: group.IndexDemultiplex.Subsets.Count != (1 << Channels.Count));
        foreach (var subset in group.IndexDemultiplex.Subsets)
            if (subset.Name == $"#{subset.Mask}") subset.Name = $"Subset {subset.Mask}";
        foreach (var state in group.IndexDemultiplex.Rows)
        {
            synchronize_cutoffs(state);
            if (group.Samples.FirstOrDefault(sample => sample.Id == state.SampleId) is { } sample)
                Rows.Add(new IndexDemultiplexSampleRowViewModel(this, state, sample));
        }
        foreach (var subset in group.IndexDemultiplex.Subsets) Subsets.Add(new IndexDemultiplexSubsetViewModel(this, subset));
        refresh_visible_subsets();
        rebuild_output_groups();
        SelectedChannel = Channels.FirstOrDefault();
        SelectedRow = Rows.FirstOrDefault();
        Status = Rows.Count == 0 ? "Drag regular samples into the sample table." : Channels.Count == 0 ? "Add at least one index channel." : "Preparing demultiplex cutoffs ...";
        notify_structure();
        foreach (var row in Rows)
        foreach (var channel in Channels)
        {
            var cutoff = row.CutoffState(channel.Name);
            bool preserve = cutoff.FitMaximum > 0 && (cutoff.Cutoff.HasValue || double.IsFinite(cutoff.LinearRss) || double.IsFinite(cutoff.LogLogisticRss));
            _ = rebuild_fit_async(row, channel.Name, force: true, preserve_cutoff: preserve);
        }
    }

    internal void CutoffEdited(IndexDemultiplexSampleRowViewModel row, string channel, double value)
    {
        var state = row.CutoffState(channel);
        if (!double.IsFinite(value) || value < 0) return;
        state.Cutoff = value;
        state.IsManual = true;
        state.FitError = "";
        recompute_assignments(row);
        row.RefreshCutoffs();
        if (ReferenceEquals(row, SelectedRow)) refresh_channel_rows();
        if (ReferenceEquals(row, SelectedRow) && selected_channel?.Name == channel) refresh_plot();
        update_validation();
    }

    internal int Count(IndexDemultiplexSubsetState subset, FlowSample sample)
    {
        if (!assignments.TryGetValue(sample.Id, out var values)) return 0;
        return values.Count(mask => mask == subset.Mask);
    }

    internal string Sign(IndexDemultiplexSubsetState subset, int channel_index) => ((subset.Mask >> channel_index) & 1) != 0 ? "+" : "−";

    internal void SubsetChanged()
    {
        foreach (var subset in Subsets) subset.Refresh();
        update_validation();
    }

    private void add_selected_channel()
    {
        if (group is null || selected_available_channel is null || Channels.Count >= IndexDemultiplexService.MaximumIndexChannels ||
            group.IndexDemultiplex.SelectedChannels.Contains(selected_available_channel.Name)) return;
        group.IndexDemultiplex.SelectedChannels.Add(selected_available_channel.Name);
        var channel = new IndexChannelViewModel(this, selected_available_channel, RemoveChannelCommand);
        Channels.Add(channel);
        foreach (var row in group.IndexDemultiplex.Rows)
            row.Cutoffs.Add(new IndexDemultiplexCutoffState { ChannelName = channel.Name });
        rebuild_available_channels();
        assignments.Clear();
        reset_subsets();
        SelectedChannel = channel;
        notify_structure();
        foreach (var row in Rows) _ = rebuild_fit_async(row, channel.Name, force: true);
    }

    private void remove_channel(IndexChannelViewModel? channel)
    {
        if (group is null || channel is null) return;
        group.IndexDemultiplex.SelectedChannels.Remove(channel.Name);
        Channels.Remove(channel);
        foreach (var row in group.IndexDemultiplex.Rows)
        {
            var cutoff = row.Cutoffs.FirstOrDefault(item => item.ChannelName == channel.Name);
            if (cutoff is not null) row.Cutoffs.Remove(cutoff);
        }
        foreach (var row in Rows) row.RebuildCutoffs();
        rebuild_available_channels();
        reset_subsets();
        SelectedChannel = Channels.FirstOrDefault();
        assignments.Clear();
        foreach (var row in Rows) recompute_assignments(row);
        notify_structure();
        refresh_plot();
    }

    private void drop_sample(ProjectNode? node)
    {
        if (group is null || node?.Sample is not { } sample || node.Group?.Id != group.Id || Rows.Any(row => row.Sample.Id == sample.Id)) return;
        var state = new IndexDemultiplexSampleRow { SampleId = sample.Id };
        foreach (var channel in Channels) state.Cutoffs.Add(new IndexDemultiplexCutoffState { ChannelName = channel.Name });
        group.IndexDemultiplex.Rows.Add(state);
        var row = new IndexDemultiplexSampleRowViewModel(this, state, sample);
        Rows.Add(row); SelectedRow = row;
        foreach (var subset in Subsets) subset.Refresh();
        refresh_visible_subsets();
        notify_structure();
        owner.RefreshProjectTreeForSpectral();
        foreach (var channel in Channels) _ = rebuild_fit_async(row, channel.Name, force: true);
        if (Channels.Count == 0) Status = "Sample added. Select at least one index channel.";
    }

    private void remove_sample(IndexDemultiplexSampleRowViewModel? row)
    {
        if (group is null || row is null) return;
        group.IndexDemultiplex.Rows.Remove(row.State);
        Rows.Remove(row); assignments.Remove(row.Sample.Id);
        foreach (var key in fits.Keys.Where(key => key.SampleId == row.Sample.Id).ToArray()) fits.Remove(key);
        SelectedRow = Rows.FirstOrDefault();
        foreach (var subset in Subsets) subset.Refresh();
        refresh_visible_subsets();
        notify_structure();
        owner.RefreshProjectTreeForSpectral();
    }

    private async Task rebuild_fit_async(IndexDemultiplexSampleRowViewModel row, string channel, bool force, bool preserve_cutoff = false)
    {
        var state = row.CutoffState(channel);
        var key = (row.Sample.Id, channel);
        if (!force && state.Cutoff.HasValue && state.FitMaximum > 0)
        {
            var restored = restored_fit(state);
            fits[key] = restored;
            recompute_assignments(row);
            if (ReferenceEquals(row, SelectedRow) && selected_channel?.Name == channel) refresh_plot();
            return;
        }
        if (fit_tokens.Remove(key, out var old)) { old.Cancel(); old.Dispose(); }
        var cancellation = new CancellationTokenSource();
        fit_tokens[key] = cancellation;
        row.IsPreparing = true;
        try
        {
            var result = await Task.Run(() => service.FitCutoff(row.Sample, channel, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !Rows.Contains(row) || !ReferenceEquals(fit_tokens.GetValueOrDefault(key), cancellation)) return;
            fits[key] = result;
            if (!preserve_cutoff && !state.IsManual)
            {
                state.Cutoff = result.Cutoff;
                state.FitError = result.Error;
            }
            state.FitMaximum = result.FitMaximum;
            state.LinearSlope = result.LinearSlope;
            state.LinearIntercept = result.LinearIntercept;
            state.LinearRss = result.LinearRss;
            state.LogLogisticSlope = result.LogLogisticSlope;
            state.LogLogisticUpper = result.LogLogisticUpper;
            state.LogLogisticMidpoint = result.LogLogisticMidpoint;
            state.LogLogisticRss = result.LogLogisticRss;
            row.RefreshCutoffs();
            if (ReferenceEquals(row, SelectedRow)) refresh_channel_rows();
            recompute_assignments(row);
            if (ReferenceEquals(row, SelectedRow) && selected_channel?.Name == channel) refresh_plot();
            update_validation();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(fit_tokens.GetValueOrDefault(key), cancellation)) fit_tokens.Remove(key);
            cancellation.Dispose();
            row.IsPreparing = false;
        }
    }

    private void recompute_assignments(IndexDemultiplexSampleRowViewModel row)
    {
        if (Channels.Count == 0 || Channels.Any(channel => !row.CutoffState(channel.Name).Cutoff.HasValue))
            assignments.Remove(row.Sample.Id);
        else
            assignments[row.Sample.Id] = service.Classify(row.Sample, row.State, Channels.Select(channel => channel.Name).ToArray());
        foreach (var subset in Subsets) subset.Refresh();
        refresh_visible_subsets();
    }

    private void threshold_annotation_committed(HistogramLineAnnotation? annotation)
    {
        if (annotation is null || SelectedRow is null || SelectedChannel is null) return;
        CutoffEdited(SelectedRow, SelectedChannel.Name, annotation.Value);
    }

    private void refresh_plot()
    {
        HistogramSeries.Clear(); HistogramCurves.Clear(); HistogramModels.Clear(); HistogramAnnotations.Clear();
        if (SelectedRow is null || SelectedChannel is null)
        {
            notify_histogram();
            return;
        }
        var state = SelectedRow.CutoffState(SelectedChannel.Name);
        var fit = selected_fit() ?? restored_fit(state);
        if (fit.SampledValues.Length > 0)
            HistogramSeries.Add(new HistogramSeries { Name = SelectedChannel.Name, Values = fit.SampledValues, SortedValues = fit.SampledValues, BinCount = 256, Color = color("Theme4") });
        if (fit.YieldX.Length > 0)
            HistogramCurves.Add(new HistogramCurveSeries
            {
                Name = "Inverse cumulative density",
                Color = color("Theme6"),
                Points = fit.YieldX.Zip(fit.YieldY, (x, y) => new HistogramPoint(x, y)).ToArray()
            });
        if (double.IsFinite(state.LinearRss))
            HistogramModels.Add(new HistogramModelLayer
            {
                Name = "Linear fit",
                Color = Color.FromRgb(54, 124, 220),
                Model = new HistogramLinearModel(state.LinearSlope, state.LinearIntercept),
                XInputScale = HistogramAxisScaleKind.Arcsinh,
                XArcsinhCofactor = IndexDemultiplexService.FitArcsinhCofactor
            });
        if (double.IsFinite(state.LogLogisticRss) && state.LogLogisticMidpoint > 0)
            HistogramModels.Add(new HistogramModelLayer
            {
                Name = "Log-logistic fit",
                Color = Color.FromRgb(218, 68, 83),
                Model = new HistogramLogLogisticModel(state.LogLogisticSlope, state.LogLogisticUpper, state.LogLogisticMidpoint),
                XInputScale = HistogramAxisScaleKind.Arcsinh,
                XArcsinhCofactor = IndexDemultiplexService.FitArcsinhCofactor
            });
        if (state.Cutoff is { } cutoff && double.IsFinite(cutoff))
            HistogramAnnotations.Add(new HistogramLineAnnotation
            {
                Orientation = HistogramAnnotationOrientation.Vertical,
                Value = cutoff,
                Text = "",
                Color = color("DangerBorder4"),
                IsEditable = true
            });
        notify_histogram();
    }

    private async Task apply_async()
    {
        if (group is null) return;
        apply_cancellation?.Cancel();
        apply_cancellation?.Dispose();
        var cancellation = apply_cancellation = new CancellationTokenSource();
        IsBusy = true; IsApplying = true; ApplyProgress = 0; ApplyProgressText = "Preparing demultiplexing ..."; WarningText = "";
        try
        {
            IReadOnlySet<int>? allowed_masks = null;
            if (ShowPresumedIndexOnly)
            {
                allowed_masks = presumed_subset_masks();
                if (allowed_masks.Count == 0)
                    throw new InvalidOperationException("No presumed index subsets were detected. Disable the presumed-index filter to export other checked subsets.");
            }
            var target = service.EnsureOutputGroup(owner.Workspace, group);
            var progress = new Progress<IndexDemultiplexProgress>(update =>
            {
                ApplyProgress = Math.Clamp(update.Fraction * 100, 0, 100);
                ApplyProgressText = $"{update.Detail} — {ApplyProgress:0}%";
                Status = ApplyProgressText;
            });
            var preparation = await Task.Run(
                () => service.PrepareApply(group, target, progress, cancellation.Token, allowed_masks),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            service.CommitApply(group, target, preparation);
            rebuild_output_groups();
            SelectedOutputGroupChoice = OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == target.Id);
            Status = $"Demultiplexed samples written to {target.Name}.";
            WarningText = string.Join(Environment.NewLine, preparation.Warnings);
            owner.RefreshProjectTreeForSpectral();
        }
        catch (OperationCanceledException) { Status = "Demultiplexing cancelled."; }
        catch (Exception exception) { Status = exception.Message; WarningText = exception.Message; }
        finally
        {
            if (ReferenceEquals(apply_cancellation, cancellation)) apply_cancellation = null;
            cancellation.Dispose();
            IsApplying = false; IsBusy = false;
        }
    }

    private bool can_apply()
    {
        if (group is null || IsBusy || Rows.Count == 0 || Channels.Count == 0) return false;
        if (Rows.Any(row => Channels.Any(channel => row.CutoffState(channel.Name).Cutoff is not { } value || !double.IsFinite(value)))) return false;
        HashSet<int>? allowed_masks = ShowPresumedIndexOnly ? presumed_subset_masks() : null;
        var included = Subsets.Where(subset => subset.IsIncluded && (allowed_masks is null || allowed_masks.Contains(subset.State.Mask))).ToArray();
        if (included.Length == 0) return false;
        var names = included.Select(subset => subset.Name.Trim()).ToArray();
        return names.All(name => name.Length > 0) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length;
    }

    private void update_validation()
    {
        var errors = Rows.SelectMany(row => Channels.Select(channel => row.CutoffState(channel.Name)))
            .Where(state => !state.Cutoff.HasValue && !string.IsNullOrWhiteSpace(state.FitError))
            .Select(state => $"{state.ChannelName}: {state.FitError}").Distinct().ToArray();
        WarningText = string.Join(Environment.NewLine, errors);
        if (Rows.Count > 0 && Channels.Count > 0 && errors.Length == 0) Status = "Cutoffs ready. Review subset counts and split when ready.";
        raise_commands();
    }

    private void reset_subsets()
    {
        if (group is null) return;
        group.IndexDemultiplex.Subsets.Clear();
        ensure_subsets(reset: true);
        Subsets.Clear();
        foreach (var subset in group.IndexDemultiplex.Subsets) Subsets.Add(new IndexDemultiplexSubsetViewModel(this, subset));
        refresh_visible_subsets();
    }

    private void refresh_visible_subsets()
    {
        IEnumerable<IndexDemultiplexSubsetViewModel> visible = Subsets;
        if (ShowPresumedIndexOnly && assignments.Count > 0)
        {
            var presumed_masks = presumed_subset_masks();
            if (presumed_masks.Count > 0)
                visible = Subsets.Where(subset => presumed_masks.Contains(subset.State.Mask));
        }

        var desired = visible.ToArray();
        if (VisibleSubsets.SequenceEqual(desired)) return;
        VisibleSubsets.Clear();
        foreach (var subset in desired) VisibleSubsets.Add(subset);
    }

    private HashSet<int> presumed_subset_masks()
    {
        var result = new HashSet<int>();
        bool found_separation = false;
        foreach (var row in Rows)
        {
            if (!assignments.ContainsKey(row.Sample.Id)) continue;
            int[] counts = Subsets.Select(subset => Count(subset.State, row.Sample)).ToArray();
            if (!try_significant_count_threshold(counts, out int threshold)) continue;
            found_separation = true;
            for (int index = 0; index < counts.Length; index++)
                if (counts[index] >= threshold)
                    result.Add(Subsets[index].State.Mask);
        }
        return found_separation ? result : [];
    }

    private static bool try_significant_count_threshold(int[] counts, out int threshold)
    {
        threshold = 0;
        if (counts.Length < 2) return false;
        int[] sorted = counts.OrderBy(value => value).ToArray();
        double best_score = double.NegativeInfinity;
        for (int index = 0; index + 1 < sorted.Length; index++)
        {
            int lower = sorted[index];
            int upper = sorted[index + 1];
            if (upper < 20 || upper <= lower) continue;
            double ratio = (upper + 1.0) / (lower + 1.0);
            double standardized_gap = (upper - lower) / Math.Sqrt(lower + 1.0);
            if (ratio < 3 || standardized_gap < 5) continue;
            double score = Math.Log(ratio) * standardized_gap;
            if (score <= best_score) continue;
            best_score = score;
            threshold = upper;
        }
        return threshold > 0;
    }

    private void ensure_subsets(bool reset)
    {
        if (group is null) return;
        int count = Channels.Count == 0 ? 0 : 1 << Channels.Count;
        if (!reset && group.IndexDemultiplex.Subsets.Count == count) return;
        group.IndexDemultiplex.Subsets.Clear();
        for (int mask = 0; mask < count; mask++)
            group.IndexDemultiplex.Subsets.Add(new IndexDemultiplexSubsetState
            {
                Mask = mask,
                Signature = IndexDemultiplexService.Signature(Channels.Select(channel => channel.Name).ToArray(), mask),
                Name = $"Subset {mask}",
                IsIncluded = true
            });
    }

    private void synchronize_cutoffs(IndexDemultiplexSampleRow row)
    {
        foreach (var cutoff in row.Cutoffs.Where(cutoff => Channels.All(channel => channel.Name != cutoff.ChannelName)).ToArray()) row.Cutoffs.Remove(cutoff);
        foreach (var channel in Channels)
            if (row.Cutoffs.All(cutoff => cutoff.ChannelName != channel.Name)) row.Cutoffs.Add(new IndexDemultiplexCutoffState { ChannelName = channel.Name });
    }

    private void rebuild_available_channels()
    {
        AvailableChannels.Clear();
        if (group is not null)
            foreach (var channel in group.Channels.Where(channel => Channels.All(selected => selected.Name != channel.Name))) AvailableChannels.Add(channel);
        SelectedAvailableChannel = AvailableChannels.FirstOrDefault();
    }

    private void rebuild_output_groups()
    {
        if (group is null) return;
        OutputGroupChoices.Clear();
        OutputGroupChoices.Add(new DemultiplexOutputGroupChoice($"{group.Name} demultiplexed (new)", null));
        foreach (var candidate in owner.Workspace.Groups.Where(candidate => candidate.Id != group.Id &&
                     (candidate.Samples.Count == 0 || candidate.IndexDemultiplexSourceGroupId == group.Id)))
            OutputGroupChoices.Add(new DemultiplexOutputGroupChoice(candidate.Name, candidate));
        SelectedOutputGroupChoice = group.IndexDemultiplex.LinkedOutputGroupId is { } linked
            ? OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == linked) ?? OutputGroupChoices[0]
            : OutputGroupChoices[0];
    }

    private IndexDemultiplexFitResult? selected_fit() => SelectedRow is null || SelectedChannel is null
        ? null : fits.GetValueOrDefault((SelectedRow.Sample.Id, SelectedChannel.Name));

    private ChannelDefinition? selected_channel_definition() => group?.Channels.FirstOrDefault(channel => channel.Name == selected_channel?.Name);

    internal string ChannelMinimumText(string channel_name)
    {
        var fit = selected_row is null ? null : fits.GetValueOrDefault((selected_row.Sample.Id, channel_name));
        double value = fit?.SampledValues.Length > 0 ? fit.SampledValues[0] : 0;
        return Configuration.FormatAxisValue(value);
    }

    internal string ChannelMaximumText(string channel_name, double registered_maximum)
    {
        var fit = selected_row is null ? null : fits.GetValueOrDefault((selected_row.Sample.Id, channel_name));
        double value = fit?.FitMaximum is > 0 and var maximum ? maximum : registered_maximum;
        return Configuration.FormatAxisValue(value);
    }

    internal string ChannelThresholdText(string channel_name)
    {
        double? value = selected_row?.State.Cutoffs.FirstOrDefault(cutoff => cutoff.ChannelName == channel_name)?.Cutoff;
        return value.HasValue && double.IsFinite(value.Value) ? Configuration.FormatAxisValue(value.Value) : "—";
    }

    private void refresh_channel_rows()
    {
        foreach (var channel in Channels) channel.RefreshValues();
    }

    private static IndexDemultiplexFitResult restored_fit(IndexDemultiplexCutoffState state) =>
        new([], [], [], state.Cutoff, state.FitMaximum, state.LinearSlope, state.LinearIntercept, state.LinearRss,
            state.LogLogisticSlope, state.LogLogisticUpper, state.LogLogisticMidpoint, state.LogLogisticRss, state.FitError);

    private void notify_histogram()
    {
        OnPropertyChanged(nameof(HistogramMinimum)); OnPropertyChanged(nameof(HistogramMaximum));
        OnPropertyChanged(nameof(HistogramAxisScale)); OnPropertyChanged(nameof(HistogramXTitle));
    }

    private void notify_structure()
    {
        OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(HasChannels)); OnPropertyChanged(nameof(HasSubsets));
        StructureChanged?.Invoke(this, EventArgs.Empty);
        raise_commands();
    }

    private void raise_commands()
    {
        (AddChannelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveChannelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DropSampleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void cancel_fit_work()
    {
        foreach (var token in fit_tokens.Values) { token.Cancel(); token.Dispose(); }
        fit_tokens.Clear();
    }

    private static Color color(string key) => gated.Shared.ThemeResources.AppColor(key);
}

public sealed class IndexChannelViewModel : NotifyBase
{
    private readonly IndexDemultiplexViewModel owner;
    private readonly double registered_maximum;

    public IndexChannelViewModel(IndexDemultiplexViewModel owner, ChannelDefinition channel, ICommand remove_command)
    {
        this.owner = owner;
        Name = channel.Name;
        registered_maximum = channel.Maximum;
        RemoveCommand = remove_command;
    }

    public string Name { get; }
    public string MinimumText => owner.ChannelMinimumText(Name);
    public string MaximumText => owner.ChannelMaximumText(Name, registered_maximum);
    public string ThresholdText => owner.ChannelThresholdText(Name);
    public ICommand RemoveCommand { get; }

    public void RefreshValues()
    {
        OnPropertyChanged(nameof(MinimumText));
        OnPropertyChanged(nameof(MaximumText));
        OnPropertyChanged(nameof(ThresholdText));
    }
}

public sealed class IndexDemultiplexSampleRowViewModel : NotifyBase
{
    private readonly IndexDemultiplexViewModel owner;
    private bool is_preparing;
    public IndexDemultiplexSampleRow State { get; }
    public FlowSample Sample { get; }
    public ObservableCollection<IndexDemultiplexCutoffCellViewModel> Cutoffs { get; } = new();
    public ICommand RemoveCommand => owner.RemoveSampleCommand;
    public string SampleName => Sample.Name;
    public string SampleIcon => "tube.svg";
    public string RemoveIcon => "delete.svg";
    public bool IsPreparing { get => is_preparing; set { if (SetField(ref is_preparing, value)) OnPropertyChanged(nameof(StatusText)); } }
    public string StatusText => IsPreparing ? "Fitting ..." : State.Cutoffs.All(cutoff => cutoff.Cutoff.HasValue) ? "Ready" : "Cutoff required";

    public IndexDemultiplexSampleRowViewModel(IndexDemultiplexViewModel owner, IndexDemultiplexSampleRow state, FlowSample sample)
    {
        this.owner = owner; State = state; Sample = sample; RebuildCutoffs();
    }

    public IndexDemultiplexCutoffState CutoffState(string channel) => State.Cutoffs.First(cutoff => cutoff.ChannelName == channel);
    public void RebuildCutoffs()
    {
        Cutoffs.Clear();
        foreach (var cutoff in State.Cutoffs) Cutoffs.Add(new IndexDemultiplexCutoffCellViewModel(owner, this, cutoff));
        OnPropertyChanged(nameof(Cutoffs)); OnPropertyChanged(nameof(StatusText));
    }
    public void RefreshCutoffs()
    {
        foreach (var cutoff in Cutoffs) cutoff.Refresh();
        OnPropertyChanged(nameof(StatusText));
    }
}

public sealed class IndexDemultiplexCutoffCellViewModel : NotifyBase
{
    private readonly IndexDemultiplexViewModel owner;
    private readonly IndexDemultiplexSampleRowViewModel row;
    public IndexDemultiplexCutoffState State { get; }
    public string ChannelName => State.ChannelName;
    public string ValueText
    {
        get => State.Cutoff?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) owner.CutoffEdited(row, State.ChannelName, parsed);
            OnPropertyChanged();
        }
    }
    public IndexDemultiplexCutoffCellViewModel(IndexDemultiplexViewModel owner, IndexDemultiplexSampleRowViewModel row, IndexDemultiplexCutoffState state)
    { this.owner = owner; this.row = row; State = state; }
    public void Refresh() => OnPropertyChanged(nameof(ValueText));
}

public sealed class IndexDemultiplexSubsetViewModel : NotifyBase
{
    private readonly IndexDemultiplexViewModel owner;
    public IndexDemultiplexSubsetState State { get; }
    public ObservableCollection<DemultiplexCellValue> Signs { get; } = new();
    public ObservableCollection<DemultiplexCellValue> Counts { get; } = new();
    public IndexDemultiplexSubsetViewModel(IndexDemultiplexViewModel owner, IndexDemultiplexSubsetState state) { this.owner = owner; State = state; rebuild_cells(); }
    public bool IsIncluded { get => State.IsIncluded; set { if (State.IsIncluded == value) return; State.IsIncluded = value; OnPropertyChanged(); owner.SubsetChanged(); } }
    public string Name { get => State.Name; set { if (State.Name == value) return; State.Name = value ?? ""; OnPropertyChanged(); owner.SubsetChanged(); } }
    public string SignAt(int channel) => owner.Sign(State, channel);
    public int CountFor(FlowSample sample) => owner.Count(State, sample);
    public void Refresh() { rebuild_cells(); OnPropertyChanged(nameof(IsIncluded)); OnPropertyChanged(nameof(Name)); }
    private void rebuild_cells()
    {
        Signs.Clear();
        for (int index = 0; index < owner.Channels.Count; index++) Signs.Add(new DemultiplexCellValue(owner.Sign(State, index)));
        Counts.Clear();
        foreach (var row in owner.Rows) Counts.Add(new DemultiplexCellValue(owner.Count(State, row.Sample).ToString("N0")));
    }
}

public sealed record DemultiplexCellValue(string Value);

public sealed record DemultiplexOutputGroupChoice(string Name, FlowGroup? Group)
{
    public override string ToString() => Name;
}
