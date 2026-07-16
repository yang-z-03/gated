using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using gated.Controls;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed class SpectralUnmixingViewModel : NotifyBase
{
    private const int PeakInferenceSampleCount = 5000;
    private const int MaximumPositiveEventCount = 5000;
    private const int MaximumPlotEventCount = 3000;
    private readonly MainWindowViewModel owner;
    private readonly SpectralUnmixingService service = new();
    private FlowGroup? group;
    private SpectralControlRowViewModel? selected_row;
    private string status = "Drop spectral controls to begin.";
    private string warning_text = "";
    private bool is_busy;
    private int active_cache_updates;
    private bool is_preparing_row_caches;
    private ControlSample? scatter_sample_cache;
    private string scatter_sample_cache_key = "";
    private ControlGatePreset? selected_gate_preset;
    public ObservableCollection<SpectralControlRowViewModel> Rows { get; } = new();
    public ObservableCollection<ControlGatePreset> GatePresets { get; } = new();
    public ObservableCollection<HistogramSeries> HistogramSeries { get; } = new();
    public ObservableCollection<SpectralMatrixRowViewModel> SimilarityRows { get; } = new();
    public ObservableCollection<SpectralMatrixRowViewModel> SignatureRows { get; } = new();
    public ObservableCollection<SpectralMatrixRowViewModel> CoefficientRows { get; } = new();
    public ObservableCollection<string> SimilarityColumnLabels { get; } = new();
    public ObservableCollection<string> DetectorColumnLabels { get; } = new();
    public ObservableCollection<SpectralDetectorPreference> Detectors { get; } = new();
    public ObservableCollection<FlowGroup> OutputGroups { get; } = new();
    public ObservableCollection<SpectralOutputGroupChoice> OutputGroupChoices { get; } = new();
    private FlowGroup? selected_output_group;
    private SpectralOutputGroupChoice? selected_output_group_choice;
    public ICommand DropControlCommand { get; }
    public ICommand RemoveControlCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand GateCommittedCommand { get; }
    public ICommand NewGatePresetCommand { get; }

    public SpectralUnmixingViewModel(MainWindowViewModel owner)
    {
        this.owner = owner;
        DropControlCommand = new RelayCommand(parameter => drop_control(parameter as ProjectNode), parameter => parameter is ProjectNode { Kind: ProjectNodeKind.ControlSample });
        RemoveControlCommand = new RelayCommand(parameter => remove_control(parameter as SpectralControlRowViewModel));
        FitCommand = new RelayCommand(_ => _ = fit_async(), _ => group is not null && !IsBusy);
        ApplyCommand = new RelayCommand(_ => apply(), _ => group is not null && !IsBusy && !group.SpectralUnmixing.IsStale && group.SpectralUnmixing.Coefficients.Length > 0);
        GateCommittedCommand = new RelayCommand(_ => gate_committed());
        NewGatePresetCommand = new RelayCommand(_ => new_gate_preset(), _ => group is not null);
    }

    public bool IsBusy { get => is_busy; private set { if (SetField(ref is_busy, value)) raise_commands(); } }
    public bool IsPreparingRowCaches { get => is_preparing_row_caches; private set => SetField(ref is_preparing_row_caches, value); }
    public string Status { get => status; private set => SetField(ref status, value); }
    public string WarningText { get => warning_text; private set { if (SetField(ref warning_text, value ?? "")) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public SpectralControlRowViewModel? SelectedRow
    {
        get => selected_row;
        set { if (!SetField(ref selected_row, value)) return; if (value is not null) SelectedGatePreset = GatePresets.FirstOrDefault(preset => preset.Id == value.State.GatePresetId) ?? GatePresets.FirstOrDefault(); refresh_selected_plots(); }
    }
    public ControlGatePreset? SelectedGatePreset
    {
        get => selected_gate_preset;
        set
        {
            if (!SetField(ref selected_gate_preset, value)) return;
            if (SelectedRow is not null && value is not null && SelectedRow.State.GatePresetId != value.Id)
            {
                SelectedRow.State.GatePresetId = value.Id;
                SelectedRow.State.PositiveSelection = null;
                SelectedRow.NotifyPopulationChanged();
                mark_stale();
                _ = rebuild_row_cache_async(SelectedRow);
            }
            notify_gate();
        }
    }
    public ControlSample? SelectedSample => SelectedRow?.Sample;
    public ControlSample? ScatterSample => spectral_scatter_sample();
    public ObservableCollection<Point>? GateVertices => SelectedGatePreset?.Vertices;
    public string GateXChannel => SelectedGatePreset?.XChannel ?? "FSC-A";
    public string GateYChannel => SelectedGatePreset?.YChannel ?? "SSC-A";
    public AxisScale GateXScale => SelectedGatePreset?.XAxis.Scale ?? new AxisScale();
    public AxisScale GateYScale => SelectedGatePreset?.YAxis.Scale ?? new AxisScale();
    public double GateXMinimum => SelectedGatePreset?.XAxis.Minimum ?? 0;
    public double GateXMaximum => SelectedGatePreset?.XAxis.Maximum ?? 262144;
    public double GateYMinimum => SelectedGatePreset?.YAxis.Minimum ?? 0;
    public double GateYMaximum => SelectedGatePreset?.YAxis.Maximum ?? 262144;
    public HistogramRangeSelection? HistogramSelection
    {
        get => SelectedRow?.State.PositiveSelection is { } range ? new HistogramRangeSelection(range.Minimum, range.Maximum) : null;
        set
        {
            if (SelectedRow is null) return;
            SelectedRow.State.PositiveSelection = value is null ? null : new SpilloverRangeSelection(value.Minimum, value.Maximum);
            SelectedRow.NotifyPopulationChanged();
            mark_stale();
            _ = rebuild_row_cache_async(SelectedRow);
        }
    }
    public IReadOnlyList<int> SpectralEventIndices { get; private set; } = [];
    public SpectralPlotData? SelectedPlotData => SelectedRow?.State.PlotCache;
    public IReadOnlyList<float> SelectedSignature { get; private set; } = [];
    public double SelectedSignatureAmplitude { get; private set; } = 1.0;
    public AxisScale SpectralScale { get; } = new() { Kind = CoordinateScaleKind.Logicle };
    public bool HasFit => group?.SpectralUnmixing.Coefficients.Length > 0;
    public bool HasFitResults => group is not null && !group.SpectralUnmixing.IsStale && group.SpectralUnmixing.Coefficients.Length > 0;
    public bool HasControls => Rows.Count > 0;
    public string GeneratedOutputGroupName => group is null ? "Unmixed output" : $"{group.Name} unmixed";
    public FlowGroup? SelectedOutputGroup
    {
        get => selected_output_group;
        set
        {
            if (!SetField(ref selected_output_group, value)) return;
            if (group is not null) group.SpectralUnmixing.LinkedOutputGroupId = value?.Id;
            selected_output_group_choice = OutputGroupChoices.FirstOrDefault(choice => Equals(choice.Group, value)) ?? OutputGroupChoices.FirstOrDefault(choice => choice.Group is null);
            OnPropertyChanged(nameof(SelectedOutputGroupChoice));
        }
    }
    public SpectralOutputGroupChoice? SelectedOutputGroupChoice
    {
        get => selected_output_group_choice;
        set
        {
            if (!SetField(ref selected_output_group_choice, value)) return;
            selected_output_group = value?.Group;
            OnPropertyChanged(nameof(SelectedOutputGroup));
            if (group is not null) group.SpectralUnmixing.LinkedOutputGroupId = value?.Group?.Id;
            raise_commands();
        }
    }

    public void SetGroup(FlowGroup? value)
    {
        group = value; selected_output_group = null; selected_output_group_choice = null; Rows.Clear(); GatePresets.Clear(); Detectors.Clear(); SimilarityRows.Clear(); SignatureRows.Clear(); CoefficientRows.Clear(); SimilarityColumnLabels.Clear(); DetectorColumnLabels.Clear(); HistogramSeries.Clear(); OutputGroups.Clear(); OutputGroupChoices.Clear(); WarningText = "";
        invalidate_scatter_sample();
        if (group is null) { SelectedRow = null; notify_visibility_state(); return; }
        var state = group.SpectralUnmixing; _ = state.DefaultGatePreset;
        initialize_gate_presets(state.GatePresets, group);
        foreach (var preset in state.GatePresets) GatePresets.Add(preset);
        string cytometer = Configuration.CytometerNameForSample(group.Samples.FirstOrDefault());
        foreach (var detector in Configuration.SpectralDetectors(cytometer)) Detectors.Add(detector);
        foreach (var row in state.Rows)
        {
            var sample = group.ControlSamples.FirstOrDefault(item => item.Id == row.ControlSampleId);
            if (sample is not null) Rows.Add(new SpectralControlRowViewModel(this, row, sample));
        }
        rebuild_output_group_choices();
        SelectedOutputGroupChoice = state.LinkedOutputGroupId is { } linked
            ? OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == linked) ?? OutputGroupChoices.FirstOrDefault(choice => choice.Group is null)
            : OutputGroupChoices.FirstOrDefault(choice => choice.Group is null);
        SelectedRow = Rows.FirstOrDefault(); rebuild_matrices();
        Status = Rows.Count == 0 ? "Drop spectral controls to begin." : state.IsStale ? "Spectral model requires fitting." : "Spectral model ready.";
        WarningText = "";
        raise_commands();
        notify_visibility_state();
        foreach (var row in Rows.Where(item => item.State.PlotCache is null)) _ = rebuild_row_cache_async(row);
    }

    internal void RowChanged(SpectralControlRowViewModel row)
    {
        mark_stale();
        _ = rebuild_row_cache_async(row);
        if (ReferenceEquals(row, SelectedRow)) refresh_selected_plots();
    }
    internal string InferPeakForDisplay(SpectralControlRowViewModel row)
    {
        if (Detectors.Count == 0) return "";
        if (!string.IsNullOrWhiteSpace(row.State.CachedPeakChannel)) return row.State.CachedPeakChannel;
        return Detectors.First().ChannelName;
    }

    private void drop_control(ProjectNode? node)
    {
        if (group is null || node?.ControlSample is not { } sample || group.ControlSamples.All(item => item.Id != sample.Id) || Rows.Any(item => item.Sample.Id == sample.Id)) return;
        var state = new SpectralControlRow { ControlSampleId = sample.Id, MoleculeName = sample.Name, GatePresetId = group.SpectralUnmixing.DefaultGatePreset.Id };
        if (is_background_name(sample.Name)) { state.Role = SpectralControlRole.UnstainedAf; state.MoleculeName = "AF"; }
        group.SpectralUnmixing.Rows.Add(state); var row = new SpectralControlRowViewModel(this, state, sample); Rows.Add(row); invalidate_scatter_sample(); SelectedRow = row; notify_visibility_state(); mark_stale(); _ = rebuild_row_cache_async(row); owner.RefreshProjectTreeForSpectral();
    }

    private void remove_control(SpectralControlRowViewModel? row)
    {
        if (group is null || row is null) return; group.SpectralUnmixing.Rows.Remove(row.State); Rows.Remove(row); invalidate_scatter_sample(); SelectedRow = Rows.FirstOrDefault(); notify_visibility_state(); mark_stale(); owner.RefreshProjectTreeForSpectral();
    }

    private async Task fit_async()
    {
        if (group is null) return; IsBusy = true; Status = "Fitting spectral signatures ..."; WarningText = "";
        try
        {
            var outcome = await Task.Run(() => service.Fit(group));
            string target_name = group.SpectralUnmixing.LinkedOutputGroupId is { } linked
                ? owner.Workspace.Groups.FirstOrDefault(candidate => candidate.Id == linked)?.Name ?? $"{group.Name} unmixed"
                : $"{group.Name} unmixed";
            Status = $"Fit complete (rank {outcome.Rank}). Apply will create or update {target_name}.";
            WarningText = outcome.Warning;
            rebuild_output_group_choices();
            foreach (var row in Rows) row.Refresh(); rebuild_matrices(); refresh_selected_plots(); OnPropertyChanged(nameof(HasFit)); OnPropertyChanged(nameof(HasFitResults)); owner.RefreshProjectTreeForSpectral();
        }
        catch (Exception exception) { Status = exception.Message; WarningText = exception.Message; }
        finally { IsBusy = false; raise_commands(); }
    }

    private void apply()
    {
        if (group is null) return;
        try { var target = service.Apply(owner.Workspace, group); if (!OutputGroups.Contains(target)) OutputGroups.Add(target); rebuild_output_group_choices(); SelectedOutputGroupChoice = OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == target.Id); Status = $"Unmixed samples written to {target.Name}."; WarningText = ""; owner.RefreshProjectTreeForSpectral(); }
        catch (Exception exception) { Status = exception.Message; WarningText = exception.Message; }
    }

    private void gate_committed()
    {
        if (SelectedGatePreset is null) return;
        var affected = Rows.Where(row => row.State.GatePresetId == SelectedGatePreset.Id).ToArray();
        foreach (var row in affected) { row.State.PositiveSelection = null; row.NotifyPopulationChanged(); }
        mark_stale();
        foreach (var row in affected) _ = rebuild_row_cache_async(row);
    }
    private void new_gate_preset()
    {
        if (group is null) return; var source = SelectedGatePreset ?? group.SpectralUnmixing.DefaultGatePreset;
        int index = group.SpectralUnmixing.GatePresets.Count + 1;
        var preset = new ControlGatePreset { Name = $"Gate {index}", XChannel = source.XChannel, YChannel = source.YChannel, XAxis = clone_axis(source.XAxis), YAxis = clone_axis(source.YAxis) };
        foreach (var point in source.Vertices) preset.Vertices.Add(point);
        group.SpectralUnmixing.GatePresets.Add(preset); GatePresets.Add(preset); SelectedGatePreset = preset; mark_stale();
    }

    private void mark_stale() { if (group is null) return; group.SpectralUnmixing.IsStale = true; Status = "Spectral model requires fitting."; WarningText = ""; OnPropertyChanged(nameof(HasFitResults)); raise_commands(); }
    private void refresh_selected_plots()
    {
        HistogramSeries.Clear(); SpectralEventIndices = []; SelectedSignature = []; SelectedSignatureAmplitude = 1.0;
        if (SelectedRow is null) { notify_plots(); return; }
        SpectralEventIndices = SelectedRow.State.PositiveEventCache.Length > 0
            ? SelectedRow.State.PositiveEventCache.Take(MaximumPlotEventCount).ToArray()
            : SelectedRow.State.GatedEventCache.Take(MaximumPlotEventCount).ToArray();
        string peak = SelectedRow.ResolvedPeakChannel;
        if (!string.IsNullOrWhiteSpace(peak))
        {
            var peak_column = SelectedRow.Sample.GetChannelValues(peak);
            var peak_values = SelectedRow.State.GatedEventCache
                .Where(index => index >= 0 && index < peak_column.Length && float.IsFinite(peak_column[index]))
                .Select(index => (double)peak_column[index])
                .OrderBy(value => value)
                .ToArray();
            HistogramSeries.Add(new HistogramSeries { Name = peak, Values = peak_values, Color = Colors.DodgerBlue });
            var selected_peak_values = SpectralEventIndices
                .Where(index => index >= 0 && index < peak_column.Length && float.IsFinite(peak_column[index]))
                .Select(index => (double)peak_column[index])
                .OrderBy(value => value)
                .ToArray();
            if (selected_peak_values.Length > 0) SelectedSignatureAmplitude = selected_peak_values[selected_peak_values.Length / 2];
        }
        else if (SelectedRow.IsBackground && Detectors.Count > 0)
        {
            SelectedSignatureAmplitude = Detectors.Select(detector => median_value(SelectedRow.Sample.GetChannelValues(detector.ChannelName, SpectralEventIndices.ToArray()))).DefaultIfEmpty(1).Max();
        }
        if (SelectedRow.State.CachedFingerprint.Length > 0)
            SelectedSignature = SelectedRow.State.CachedFingerprint;
        notify_plots();
    }

    private async Task rebuild_row_cache_async(SpectralControlRowViewModel row)
    {
        var preset = GatePresets.FirstOrDefault(item => item.Id == row.State.GatePresetId);
        string x_channel = preset?.XChannel ?? "FSC-A";
        string y_channel = preset?.YChannel ?? "SSC-A";
        Point[] vertices = preset?.Vertices.ToArray() ?? [];
        var detectors = Detectors.ToArray();
        string manual_peak = row.State.UseAutomaticPeak ? "" : row.State.PeakChannel;
        SpilloverRangeSelection? positive_selection = row.State.PositiveSelection;
        bool is_background = row.IsBackground;
        int generation = row.BeginCacheUpdate();
        begin_cache_work();
        try
        {
            var result = await Task.Run(() => build_plot_cache(row.Sample, x_channel, y_channel, vertices, detectors, manual_peak, positive_selection, is_background));
            if (!Rows.Contains(row) || !row.CompleteCacheUpdate(generation, result.GatedIndices, result.PositiveIndices, result.PeakChannel, result.PositiveSelection, result.Fingerprint, result.PositiveCount, result.PopulationCount, result.PlotData)) return;
            if (ReferenceEquals(row, SelectedRow)) refresh_selected_plots();
        }
        catch (Exception exception)
        {
            if (row.IsCurrentCacheUpdate(generation))
            {
                row.FailCacheUpdate(generation);
                Status = $"Could not prepare {row.SampleName}: {exception.Message}";
                WarningText = Status;
            }
        }
        finally
        {
            end_cache_work();
        }
    }

    private static (int[] GatedIndices, int[] PositiveIndices, string PeakChannel, SpilloverRangeSelection? PositiveSelection, float[] Fingerprint, int PositiveCount, int PopulationCount, SpectralPlotData PlotData) build_plot_cache(
        ControlSample sample, string x_channel, string y_channel, IReadOnlyList<Point> vertices,
        IReadOnlyList<SpectralDetectorPreference> detectors, string manual_peak_channel,
        SpilloverRangeSelection? positive_selection, bool is_background)
    {
        int[] gated;
        if (vertices.Count < 3)
            gated = Enumerable.Range(0, sample.EventCount).ToArray();
        else
        {
            var x = sample.GetChannelValues(x_channel); var y = sample.GetChannelValues(y_channel);
            gated = Enumerable.Range(0, Math.Min(x.Length, y.Length)).Where(index => contains_polygon(vertices, x[index], y[index])).ToArray();
        }

        int[] columns = detectors.Select(detector => sample.GetChannelIndex(detector.ChannelName)).ToArray();
        string peak_channel = resolve_peak_from_sample(sample, gated, detectors, columns, manual_peak_channel);
        int peak_column = sample.GetChannelIndex(peak_channel);
        int positive_target = Math.Min(MaximumPositiveEventCount, Math.Max(1, (int)Math.Ceiling(gated.Length * 0.1)));
        int[] positives = is_background
            ? deterministic_sample(gated, Math.Min(MaximumPlotEventCount, gated.Length))
            : select_positive_indices(sample, gated, peak_column, positive_selection, positive_target, out positive_selection);
        int[] selected = positives.Length > MaximumPlotEventCount ? deterministic_sample(positives, MaximumPlotEventCount) : positives;
        float[] fingerprint = is_background
            ? calculate_af_fingerprint(sample, selected, columns)
            : calculate_peak_normalized_fingerprint(sample, selected, columns, peak_column);
        double raw_maximum = selected.SelectMany(index => columns.Where(column => column >= 0).Select(column => (double)sample.RawEvents[index, column]))
            .Where(double.IsFinite).Select(value => Math.Max(0, value)).DefaultIfEmpty(1).Max();
        if (raw_maximum <= 0) raw_maximum = 1;
        const int bins = 96;
        var density = new int[detectors.Count, bins];
        double transformed_maximum = spectral_intensity_transform(raw_maximum);
        if (transformed_maximum <= 0) transformed_maximum = 1;
        for (int detector = 0; detector < columns.Length; detector++)
        {
            int column = columns[detector]; if (column < 0) continue;
            foreach (int index in selected)
            {
                double value = sample.RawEvents[index, column]; if (!double.IsFinite(value)) continue;
                double normalized = spectral_intensity_transform(value) / transformed_maximum;
                density[detector, Math.Clamp((int)(normalized * bins), 0, bins - 1)]++;
            }
        }
        return (gated, positives, peak_channel, positive_selection, fingerprint, positives.Length, gated.Length,
            new SpectralPlotData(detectors.Select(item => item.ChannelName).ToArray(), detectors.Select(item => item.ExcitationLight).ToArray(), density, raw_maximum, peak_channel, positive_selection));
    }

    private static float[] calculate_peak_normalized_fingerprint(ControlSample sample, int[] rows, int[] columns, int peak_column)
    {
        var fingerprint = new float[columns.Length];
        if (rows.Length == 0 || peak_column < 0)
            return fingerprint;

        double peak = finite_median(sample, rows, peak_column);
        if (!double.IsFinite(peak) || Math.Abs(peak) <= double.Epsilon)
            peak = columns.Where(column => column >= 0).Select(column => finite_median(sample, rows, column)).Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(1).Max();
        if (peak <= double.Epsilon)
            peak = 1;

        for (int detector = 0; detector < columns.Length; detector++)
        {
            int column = columns[detector];
            if (column < 0)
                continue;
            double median = finite_median(sample, rows, column);
            fingerprint[detector] = double.IsFinite(median) ? (float)(median / peak) : 0;
        }

        int peak_detector = Array.IndexOf(columns, peak_column);
        if (peak_detector >= 0)
            fingerprint[peak_detector] = 1.0f;
        return fingerprint;
    }

    private static float[] calculate_af_fingerprint(ControlSample sample, int[] rows, int[] columns)
    {
        var sums = new double[columns.Length];
        int count = 0;
        foreach (int row in rows)
        {
            double norm = 0;
            for (int detector = 0; detector < columns.Length; detector++)
            {
                int column = columns[detector];
                if (column < 0)
                    continue;
                double value = sample.RawEvents[row, column];
                if (double.IsFinite(value))
                    norm = Math.Max(norm, Math.Abs(value));
            }
            if (norm <= double.Epsilon)
                continue;
            for (int detector = 0; detector < columns.Length; detector++)
            {
                int column = columns[detector];
                if (column < 0)
                    continue;
                double value = sample.RawEvents[row, column];
                if (double.IsFinite(value))
                    sums[detector] += value / norm;
            }
            count++;
        }
        if (count == 0)
            return new float[columns.Length];
        double maximum = sums.Select(value => Math.Abs(value / count)).DefaultIfEmpty(0).Max();
        if (maximum <= double.Epsilon)
            maximum = 1;
        var fingerprint = new float[columns.Length];
        for (int detector = 0; detector < columns.Length; detector++)
            fingerprint[detector] = (float)(sums[detector] / count / maximum);
        return fingerprint;
    }

    private static double finite_median(ControlSample sample, int[] rows, int column)
    {
        var values = new double[rows.Length];
        int count = 0;
        foreach (int row in rows)
        {
            double value = sample.RawEvents[row, column];
            if (!double.IsFinite(value))
                continue;
            values[count++] = value;
        }
        if (count == 0)
            return double.NaN;
        Array.Sort(values, 0, count);
        int middle = count / 2;
        return count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2.0 : values[middle];
    }

    private static double spectral_intensity_transform(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0;
        return value <= 1000.0 ? value / 1000.0 : 1.0 + Math.Log10(value / 1000.0);
    }

    private static string resolve_peak_from_sample(
        ControlSample sample,
        int[] gated,
        IReadOnlyList<SpectralDetectorPreference> detectors,
        int[] columns,
        string manual_peak_channel)
    {
        if (!string.IsNullOrWhiteSpace(manual_peak_channel) && detectors.Any(detector => detector.ChannelName == manual_peak_channel))
            return manual_peak_channel;
        if (detectors.Count == 0)
            return "";

        int[] sampled = deterministic_sample(gated, Math.Min(PeakInferenceSampleCount, gated.Length));
        double best_mean = double.NegativeInfinity;
        int best = 0;
        for (int detector = 0; detector < detectors.Count; detector++)
        {
            int column = columns[detector];
            if (column < 0)
                continue;
            double sum = 0;
            int count = 0;
            foreach (int row in sampled)
            {
                double value = sample.RawEvents[row, column];
                if (!double.IsFinite(value))
                    continue;
                sum += value;
                count++;
            }
            double mean = count == 0 ? double.NegativeInfinity : sum / count;
            if (mean <= best_mean)
                continue;
            best_mean = mean;
            best = detector;
        }
        return detectors[best].ChannelName;
    }

    private static int[] select_positive_indices(
        ControlSample sample,
        int[] gated,
        int peak_column,
        SpilloverRangeSelection? requested_selection,
        int target,
        out SpilloverRangeSelection? effective_selection)
    {
        effective_selection = requested_selection;
        if (peak_column < 0 || gated.Length == 0 || target <= 0)
            return [];

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        foreach (int row in gated)
        {
            double value = sample.RawEvents[row, peak_column];
            if (!double.IsFinite(value))
                continue;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return [];
        if (Math.Abs(maximum - minimum) < double.Epsilon)
        {
            effective_selection ??= new SpilloverRangeSelection(minimum, maximum);
            return deterministic_sample(gated, Math.Min(target, gated.Length));
        }

        double lower = requested_selection is { } range ? Math.Min(range.Minimum, range.Maximum) : histogram_top_threshold(sample, gated, peak_column, minimum, maximum, target);
        double upper = requested_selection is { } range2 ? Math.Max(range2.Minimum, range2.Maximum) : maximum;
        if (requested_selection is null)
        {
            double next = next_finite_value_above(sample, gated, peak_column, lower, maximum);
            if (double.IsFinite(next) && next > lower)
                lower = next;
        }
        effective_selection ??= new SpilloverRangeSelection(lower, upper);

        var selected = new List<int>(Math.Min(target, gated.Length));
        foreach (int row in gated)
        {
            double value = sample.RawEvents[row, peak_column];
            if (!double.IsFinite(value) || value < lower || value > upper)
                continue;
            selected.Add(row);
            if (selected.Count >= target)
                break;
        }
        return selected.ToArray();
    }

    private static double next_finite_value_above(ControlSample sample, int[] rows, int column, double threshold, double fallback)
    {
        double best = double.PositiveInfinity;
        foreach (int row in rows)
        {
            double value = sample.RawEvents[row, column];
            if (!double.IsFinite(value) || value <= threshold || value >= best)
                continue;
            best = value;
        }
        return double.IsFinite(best) ? best : fallback;
    }

    private static double histogram_top_threshold(ControlSample sample, int[] rows, int column, double minimum, double maximum, int target)
    {
        const int bins = 2048;
        var counts = new int[bins];
        double span = maximum - minimum;
        foreach (int row in rows)
        {
            double value = sample.RawEvents[row, column];
            if (!double.IsFinite(value))
                continue;
            int bin = Math.Clamp((int)((value - minimum) / span * (bins - 1)), 0, bins - 1);
            counts[bin]++;
        }

        int accumulated = 0;
        for (int bin = bins - 1; bin >= 0; bin--)
        {
            accumulated += counts[bin];
            if (accumulated >= target)
                return minimum + span * bin / (bins - 1);
        }
        return minimum;
    }

    private static int[] deterministic_sample(int[] source, int maximum)
    {
        if (maximum <= 0 || source.Length == 0)
            return [];
        if (source.Length <= maximum)
            return source.ToArray();
        var result = new int[maximum];
        for (int index = 0; index < maximum; index++)
            result[index] = source[(int)((long)index * source.Length / maximum)];
        return result;
    }

    private static bool contains_polygon(IReadOnlyList<Point> vertices, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
            if (((vertices[i].Y > y) != (vertices[j].Y > y)) && x < (vertices[j].X - vertices[i].X) * (y - vertices[i].Y) / (vertices[j].Y - vertices[i].Y) + vertices[i].X) inside = !inside;
        return inside;
    }

    private void rebuild_matrices()
    {
        SimilarityRows.Clear(); SignatureRows.Clear(); CoefficientRows.Clear(); SimilarityColumnLabels.Clear(); DetectorColumnLabels.Clear(); if (group is null) return; var state = group.SpectralUnmixing;
        foreach (string name in state.SignatureNames)
            SimilarityColumnLabels.Add(name);
        foreach (string detector in state.DetectorNames)
            DetectorColumnLabels.Add(detector);
        for (int row = 0; row < state.Similarity.GetLength(0); row++) SimilarityRows.Add(new SpectralMatrixRowViewModel(state.SignatureNames.ElementAtOrDefault(row) ?? "", Enumerable.Range(0, state.Similarity.GetLength(1)).Select(column => new SpectralMatrixCellViewModel(state.Similarity[row, column], false, null)).ToArray()));
        for (int row = 0; row < state.Signatures.GetLength(0); row++)
        {
            float maximum = Enumerable.Range(0, state.Signatures.GetLength(1))
                .Select(column => MathF.Abs(state.Signatures[row, column]))
                .DefaultIfEmpty(0)
                .Max();
            if (maximum <= 0 || !float.IsFinite(maximum))
                maximum = 1;
            int captured = row;
            SignatureRows.Add(new SpectralMatrixRowViewModel(state.SignatureNames.ElementAtOrDefault(row) ?? "", Enumerable.Range(0, state.Signatures.GetLength(1)).Select(column => new SpectralMatrixCellViewModel(MathF.Max(0, state.Signatures[captured, column] / maximum), false, null)).ToArray()));
        }
        for (int row = 0; row < state.Coefficients.GetLength(0); row++)
        {
            int captured = row;
            CoefficientRows.Add(new SpectralMatrixRowViewModel(state.SignatureNames.ElementAtOrDefault(row) ?? "", Enumerable.Range(0, state.Coefficients.GetLength(1)).Select(column => { int c = column; return new SpectralMatrixCellViewModel(state.Coefficients[captured, c], true, value => update_coefficient(captured, c, value)); }).ToArray()));
        }
    }
    private void update_coefficient(int row, int column, float value) { if (group is null) return; var clone = (float[,])group.SpectralUnmixing.Coefficients.Clone(); clone[row, column] = value; group.SpectralUnmixing.ReplaceCoefficients(clone); }
    private static AxisSettings clone_axis(AxisSettings source) => new() { ChannelName = source.ChannelName, Minimum = source.Minimum, Maximum = source.Maximum, Scale = source.Scale.Clone() };
    private static void initialize_gate_presets(IEnumerable<ControlGatePreset> presets, FlowGroup group)
    {
        string fsc = group.Channels.FirstOrDefault(channel => Configuration.IsFscChannel(channel.Name))?.Name ?? group.Channels.FirstOrDefault()?.Name ?? "FSC-A";
        string ssc = group.Channels.FirstOrDefault(channel => Configuration.IsSscChannel(channel.Name))?.Name ?? group.Channels.Skip(1).FirstOrDefault()?.Name ?? fsc;
        foreach (var preset in presets)
        {
            if (string.Equals(preset.Name, "default", StringComparison.Ordinal))
                preset.Name = "Default";
            if (group.Channels.All(channel => channel.Name != preset.XChannel)) preset.XChannel = fsc;
            if (group.Channels.All(channel => channel.Name != preset.YChannel) ||
                string.Equals(preset.YChannel, preset.XChannel, StringComparison.Ordinal))
                preset.YChannel = distinct_channel(group, ssc, preset.XChannel);
            preset.XAxis.ChannelName = preset.XChannel; preset.YAxis.ChannelName = preset.YChannel;
            preset.XAxis.ScaleKind = Configuration.DefaultCoordinateScaleForChannel(preset.XChannel); preset.YAxis.ScaleKind = Configuration.DefaultCoordinateScaleForChannel(preset.YChannel);
            preset.XAxis.Maximum = Math.Max(1, group.Channels.FirstOrDefault(channel => channel.Name == preset.XChannel)?.Maximum ?? 262144);
            preset.YAxis.Maximum = Math.Max(1, group.Channels.FirstOrDefault(channel => channel.Name == preset.YChannel)?.Maximum ?? 262144);
        }
    }
    private static string distinct_channel(FlowGroup group, string preferred, string other)
    {
        if (!string.Equals(preferred, other, StringComparison.Ordinal))
            return preferred;
        return group.Channels.FirstOrDefault(channel => !string.Equals(channel.Name, other, StringComparison.Ordinal))?.Name ?? preferred;
    }
    private static bool is_background_name(string value) => value.Equals("unstained", StringComparison.OrdinalIgnoreCase) || value.Equals("blank", StringComparison.OrdinalIgnoreCase) || value.Equals("af", StringComparison.OrdinalIgnoreCase);
    private static double median_value(IEnumerable<float> source) { var values = source.Where(float.IsFinite).Select(value => (double)value).OrderBy(value => value).ToArray(); return values.Length == 0 ? 0 : values[values.Length / 2]; }
    private static int[] gated_indices(ControlSample sample, ControlGatePreset? preset)
    {
        if (preset is null || preset.Vertices.Count < 3) return Enumerable.Range(0, sample.EventCount).ToArray(); var x = sample.GetChannelValues(preset.XChannel); var y = sample.GetChannelValues(preset.YChannel); var result = new List<int>();
        for (int index = 0; index < Math.Min(x.Length, y.Length); index++) { bool inside = false; for (int i = 0, j = preset.Vertices.Count - 1; i < preset.Vertices.Count; j = i++) if (((preset.Vertices[i].Y > y[index]) != (preset.Vertices[j].Y > y[index])) && x[index] < (preset.Vertices[j].X - preset.Vertices[i].X) * (y[index] - preset.Vertices[i].Y) / (preset.Vertices[j].Y - preset.Vertices[i].Y) + preset.Vertices[i].X) inside = !inside; if (inside) result.Add(index); }
        return result.ToArray();
    }
    private ControlSample? spectral_scatter_sample()
    {
        if (Rows.Count == 0)
            return null;

        string x_channel = GateXChannel;
        string y_channel = GateYChannel;
        string key = string.Join("|", Rows.Select(row => $"{row.Sample.Id:N}:{row.Sample.EventCount}")) + $"|{x_channel}|{y_channel}";
        if (scatter_sample_cache is not null && string.Equals(scatter_sample_cache_key, key, StringComparison.Ordinal))
            return scatter_sample_cache;

        var x_definition = Rows.Select(row => row.Sample.Channels.FirstOrDefault(channel => channel.Name == x_channel)).FirstOrDefault(channel => channel is not null);
        var y_definition = Rows.Select(row => row.Sample.Channels.FirstOrDefault(channel => channel.Name == y_channel)).FirstOrDefault(channel => channel is not null);
        var channels = new[]
        {
            new ChannelDefinition(0, x_channel, x_definition?.Label ?? "", x_definition?.Maximum ?? 262144, x_definition?.Gain ?? 1),
            new ChannelDefinition(1, y_channel, y_definition?.Label ?? "", y_definition?.Maximum ?? 262144, y_definition?.Gain ?? 1)
        };

        const int maximum_rows_per_control = 12000;
        int total = Rows.Sum(row => Math.Min(maximum_rows_per_control, row.Sample.EventCount));
        var raw = new float[total, 2];
        int offset = 0;
        foreach (var row in Rows)
        {
            int x_index = row.Sample.GetChannelIndex(x_channel);
            int y_index = row.Sample.GetChannelIndex(y_channel);
            if (x_index < 0 || y_index < 0)
                continue;
            int count = Math.Min(maximum_rows_per_control, row.Sample.EventCount);
            for (int index = 0; index < count; index++)
            {
                int source = count == row.Sample.EventCount ? index : (int)((long)index * row.Sample.EventCount / count);
                raw[offset, 0] = row.Sample.RawEvents[source, x_index];
                raw[offset, 1] = row.Sample.RawEvents[source, y_index];
                offset++;
            }
        }

        if (offset != total)
        {
            var trimmed = new float[offset, 2];
            for (int row = 0; row < offset; row++)
            {
                trimmed[row, 0] = raw[row, 0];
                trimmed[row, 1] = raw[row, 1];
            }
            raw = trimmed;
        }

        scatter_sample_cache = new ControlSample("Spectral control gate source", channels, raw);
        scatter_sample_cache_key = key;
        return scatter_sample_cache;
    }

    private void invalidate_scatter_sample()
    {
        scatter_sample_cache = null;
        scatter_sample_cache_key = "";
        OnPropertyChanged(nameof(ScatterSample));
    }

    private void notify_gate() { invalidate_scatter_sample(); OnPropertyChanged(nameof(SelectedSample)); OnPropertyChanged(nameof(GateVertices)); OnPropertyChanged(nameof(GateXChannel)); OnPropertyChanged(nameof(GateYChannel)); OnPropertyChanged(nameof(GateXScale)); OnPropertyChanged(nameof(GateYScale)); OnPropertyChanged(nameof(GateXMinimum)); OnPropertyChanged(nameof(GateXMaximum)); OnPropertyChanged(nameof(GateYMinimum)); OnPropertyChanged(nameof(GateYMaximum)); }
    private void notify_plots() { OnPropertyChanged(nameof(SpectralEventIndices)); OnPropertyChanged(nameof(SelectedPlotData)); OnPropertyChanged(nameof(SelectedSignature)); OnPropertyChanged(nameof(SelectedSignatureAmplitude)); OnPropertyChanged(nameof(HistogramSelection)); OnPropertyChanged(nameof(SelectedSample)); }
    private void raise_commands()
    {
        (FitCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NewGatePresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
    private void notify_visibility_state()
    {
        OnPropertyChanged(nameof(HasControls));
        OnPropertyChanged(nameof(HasFit));
        OnPropertyChanged(nameof(HasFitResults));
        OnPropertyChanged(nameof(GeneratedOutputGroupName));
    }
    private void rebuild_output_group_choices()
    {
        if (group is null)
            return;
        OutputGroups.Clear();
        OutputGroupChoices.Clear();
        var generated = new SpectralOutputGroupChoice($"{GeneratedOutputGroupName} (new)", null, true);
        OutputGroupChoices.Add(generated);
        foreach (var candidate in owner.Workspace.Groups.Where(candidate => candidate.Id != group.Id && (candidate.Samples.Count == 0 || candidate.SpectralSourceGroupId == group.Id)))
        {
            OutputGroups.Add(candidate);
            OutputGroupChoices.Add(new SpectralOutputGroupChoice(candidate.Name, candidate, false));
        }
        selected_output_group_choice = selected_output_group is { } selected
            ? OutputGroupChoices.FirstOrDefault(choice => choice.Group?.Id == selected.Id) ?? generated
            : generated;
        OnPropertyChanged(nameof(OutputGroupChoices));
        OnPropertyChanged(nameof(SelectedOutputGroupChoice));
    }
    private void begin_cache_work() { active_cache_updates++; IsPreparingRowCaches = active_cache_updates > 0; }
    private void end_cache_work() { active_cache_updates = Math.Max(0, active_cache_updates - 1); IsPreparingRowCaches = active_cache_updates > 0; }
}

public sealed record SpectralOutputGroupChoice(string Name, FlowGroup? Group, bool IsGenerated)
{
    public override string ToString() => Name;
}

public sealed class SpectralControlRowViewModel : NotifyBase
{
    private static readonly SvgImage SampleSvg = load_svg("avares://gated/Resources/tube.svg");
    private static readonly SvgImage OkSvg = load_svg("avares://gated/Resources/ok.svg");
    private static readonly SvgImage WarningSvg = load_svg("avares://gated/Resources/warning.svg");
    private static readonly SvgImage DeleteSvg = load_svg("avares://gated/Resources/delete.svg");
    private readonly SpectralUnmixingViewModel owner;
    private int cache_generation;
    private bool updating_from_cache;
    private readonly ObservableCollection<SpectralPeakChoiceViewModel> maximum_channel_choices = new();
    public SpectralControlRow State { get; }
    public ControlSample Sample { get; }
    public SpectralControlRowViewModel(SpectralUnmixingViewModel owner, SpectralControlRow state, ControlSample sample)
    {
        this.owner = owner; State = state; Sample = sample; SelectCommand = new RelayCommand(_ => owner.SelectedRow = this);
        rebuild_maximum_channel_choices();
    }
    public ICommand SelectCommand { get; }
    public ICommand RemoveCommand => owner.RemoveControlCommand;
    public string SampleName => Sample.Name;
    public SvgImage SampleIcon => SampleSvg;
    public SvgImage PopulationIcon => IsBackground || State.PositiveSelection is not null ? OkSvg : WarningSvg;
    public SvgImage RemoveIcon => DeleteSvg;
    public string MoleculeName
    {
        get => State.MoleculeName;
        set
        {
            string requested = value ?? "";
            bool background = is_background(requested);
            var role = background ? SpectralControlRole.UnstainedAf : SpectralControlRole.Molecule;
            string molecule = background ? "AF" : requested;
            if (State.Role == role && string.Equals(State.MoleculeName, molecule, StringComparison.Ordinal))
                return;

            State.Role = role;
            State.MoleculeName = molecule;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBackground));
            rebuild_maximum_channel_choices();
            OnPropertyChanged(nameof(MaximumChannel));
            NotifyPopulationChanged();
            owner.RowChanged(this);
        }
    }
    public bool IsBackground => State.Role == SpectralControlRole.UnstainedAf;
    public bool UseAutomaticPeak
    {
        get => State.UseAutomaticPeak;
        set
        {
            if (State.UseAutomaticPeak == value)
                return;
            State.UseAutomaticPeak = value;
            State.PositiveSelection = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolvedPeakChannel));
            owner.RowChanged(this);
        }
    }
    public string PeakChannel
    {
        get => State.PeakChannel;
        set
        {
            string channel = value ?? "";
            if (!State.UseAutomaticPeak && string.Equals(State.PeakChannel, channel, StringComparison.Ordinal))
                return;
            State.PeakChannel = channel;
            State.UseAutomaticPeak = false;
            State.PositiveSelection = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolvedPeakChannel));
            OnPropertyChanged(nameof(MaximumChannel));
            owner.RowChanged(this);
        }
    }
    public string ResolvedPeakChannel => IsBackground ? "" : State.UseAutomaticPeak ? (!string.IsNullOrWhiteSpace(State.CachedPeakChannel) ? State.CachedPeakChannel : owner.InferPeakForDisplay(this)) : State.PeakChannel;
    public ObservableCollection<SpectralPeakChoiceViewModel> MaximumChannelChoices => maximum_channel_choices;
    public SpectralPeakChoiceViewModel? MaximumChannel
    {
        get
        {
            if (IsBackground)
                return maximum_channel_choices.FirstOrDefault();
            if (State.UseAutomaticPeak)
                return maximum_channel_choices.FirstOrDefault(choice => choice.IsAutomatic);
            return maximum_channel_choices.FirstOrDefault(choice => string.Equals(choice.ChannelName, State.PeakChannel, StringComparison.Ordinal));
        }
        set
        {
            if (updating_from_cache || IsBackground || value is null)
                return;

            bool automatic = value.IsAutomatic;
            string peak = automatic ? "" : value.ChannelName;
            if (!automatic && !owner.Detectors.Any(detector => string.Equals(detector.ChannelName, peak, StringComparison.Ordinal)))
                return;
            if (State.UseAutomaticPeak == automatic && string.Equals(State.PeakChannel, peak, StringComparison.Ordinal))
                return;

            State.UseAutomaticPeak = automatic;
            State.PeakChannel = peak;
            State.PositiveSelection = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolvedPeakChannel));
            NotifyPopulationChanged();
            owner.RowChanged(this);
        }
    }
    public string PositivePercentText
    {
        get
        {
            if (State.CachedPopulationCount <= 0)
                return IsCacheUpdating ? "Calculating ..." : "Selected 0 (0.0%)";
            return $"Selected {State.CachedPositiveCount} ({State.CachedPositiveCount / (double)State.CachedPopulationCount:P1})";
        }
    }
    public bool IsCacheUpdating { get; private set; }
    public void Refresh() { rebuild_maximum_channel_choices(); OnPropertyChanged(nameof(PeakChannel)); OnPropertyChanged(nameof(ResolvedPeakChannel)); OnPropertyChanged(nameof(MaximumChannel)); OnPropertyChanged(nameof(IsBackground)); NotifyPopulationChanged(); }
    public void NotifyPopulationChanged() { OnPropertyChanged(nameof(PositivePercentText)); OnPropertyChanged(nameof(PopulationIcon)); }
    internal int BeginCacheUpdate() { IsCacheUpdating = true; OnPropertyChanged(nameof(IsCacheUpdating)); NotifyPopulationChanged(); return ++cache_generation; }
    internal bool IsCurrentCacheUpdate(int generation) => generation == cache_generation;
    internal bool CompleteCacheUpdate(
        int generation,
        int[] gated,
        int[] positives,
        string peak_channel,
        SpilloverRangeSelection? positive_selection,
        float[] fingerprint,
        int positive_count,
        int population_count,
        SpectralPlotData plot)
    {
        if (!IsCurrentCacheUpdate(generation)) return false;
        IsCacheUpdating = false;
        State.GatedEventCache = gated;
        State.PositiveEventCache = positives;
        State.CachedPeakChannel = peak_channel;
        State.CachedFingerprint = fingerprint;
        State.PositiveSelection = positive_selection;
        State.CachedPositiveCount = positive_count;
        State.CachedPopulationCount = population_count;
        State.PlotCache = plot;
        OnPropertyChanged(nameof(IsCacheUpdating));
        updating_from_cache = true;
        try
        {
            rebuild_maximum_channel_choices();
            OnPropertyChanged(nameof(MaximumChannel));
            OnPropertyChanged(nameof(ResolvedPeakChannel));
        }
        finally
        {
            updating_from_cache = false;
        }
        NotifyPopulationChanged();
        return true;
    }
    internal void FailCacheUpdate(int generation)
    {
        if (!IsCurrentCacheUpdate(generation))
            return;
        IsCacheUpdating = false;
        OnPropertyChanged(nameof(IsCacheUpdating));
        NotifyPopulationChanged();
    }
    private static bool is_background(string value) => value.Equals("unstained", StringComparison.OrdinalIgnoreCase) || value.Equals("blank", StringComparison.OrdinalIgnoreCase) || value.Equals("af", StringComparison.OrdinalIgnoreCase);
    private static SvgImage load_svg(string uri) => new() { Source = SvgSource.LoadFromStream(AssetLoader.Open(new Uri(uri))) };
    private void rebuild_maximum_channel_choices()
    {
        if (maximum_channel_choices.Count == 0)
            maximum_channel_choices.Add(new SpectralPeakChoiceViewModel(true, "", ""));

        if (IsBackground)
        {
            maximum_channel_choices[0].DisplayName = "—";
            while (maximum_channel_choices.Count > 1)
                maximum_channel_choices.RemoveAt(maximum_channel_choices.Count - 1);
            OnPropertyChanged(nameof(MaximumChannelChoices));
            return;
        }

        maximum_channel_choices[0].DisplayName = $"Auto ({ResolvedPeakChannel})";
        foreach (var detector in owner.Detectors)
        {
            if (!maximum_channel_choices.Any(choice => !choice.IsAutomatic && string.Equals(choice.ChannelName, detector.ChannelName, StringComparison.Ordinal)))
                maximum_channel_choices.Add(new SpectralPeakChoiceViewModel(false, detector.ChannelName, detector.ChannelName));
        }
        for (int index = maximum_channel_choices.Count - 1; index >= 1; index--)
        {
            if (!owner.Detectors.Any(detector => string.Equals(detector.ChannelName, maximum_channel_choices[index].ChannelName, StringComparison.Ordinal)))
                maximum_channel_choices.RemoveAt(index);
        }
        OnPropertyChanged(nameof(MaximumChannelChoices));
    }
}

public sealed class SpectralPeakChoiceViewModel : NotifyBase
{
    private string display_name;
    public SpectralPeakChoiceViewModel(bool is_automatic, string channel_name, string display_name)
    {
        IsAutomatic = is_automatic;
        ChannelName = channel_name;
        this.display_name = display_name;
    }
    public bool IsAutomatic { get; }
    public string ChannelName { get; }
    public string DisplayName { get => display_name; set => SetField(ref display_name, value ?? ""); }
    public override string ToString() => DisplayName;
}

public sealed record SpectralMatrixRowViewModel(string Name, IReadOnlyList<SpectralMatrixCellViewModel> Cells);
public sealed class SpectralMatrixCellViewModel : NotifyBase
{
    private float value; private readonly Action<float>? changed;
    public SpectralMatrixCellViewModel(float value, bool editable, Action<float>? changed) { this.value = value; IsEditable = editable; this.changed = changed; }
    public float Value { get => value; set { if (SetField(ref this.value, value)) changed?.Invoke(value); } }
    public bool IsEditable { get; }
    public string Display => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
