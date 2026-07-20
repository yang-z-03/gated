using System;
using System.Collections.Generic;
using System.Linq;
using gated.Models;

namespace gated.Services;

public sealed record MassChannelDescriptor(string ChannelName, string ElementSymbol, int MassNumber)
{
    public string DisplayName => $"{ElementSymbol}{MassNumber}";
    public override string ToString() => string.Equals(ChannelName, DisplayName, StringComparison.OrdinalIgnoreCase)
        ? DisplayName
        : $"{DisplayName} — {ChannelName}";
}

public sealed record MassCompensationControlInput(ControlSample Sample, string SourceChannelName);

public sealed record MassCompensationFitResult(
    CompensationMatrix Matrix,
    IReadOnlyList<MassChannelDescriptor> Channels,
    MassLeakageKind[,] Annotations);

public sealed class MassCompensationService
{
    public const int FitSampleSize = 5000;

    public IReadOnlyList<MassChannelDescriptor> DescribeChannels(FlowGroup group)
    {
        string cytometer = Configuration.CytometerNameForSample(group.Samples.FirstOrDefault());
        return group.Channels
            .Where(channel => Configuration.ChannelKind(channel.Name, cytometer) == ChannelSemanticKind.Mass)
            .Select(channel => new MassChannelDescriptor(
                channel.Name,
                Configuration.ElementSymbolForChannel(channel.Name, cytometer),
                Configuration.MassNumberForChannel(channel.Name, cytometer) ?? 0))
            .Where(channel => channel.MassNumber > 0 && !string.IsNullOrWhiteSpace(channel.ElementSymbol))
            .OrderBy(channel => channel.MassNumber)
            .ThenBy(channel => channel.ElementSymbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(channel => channel.ChannelName, NaturalChannelNameComparer.Instance)
            .ToArray();
    }

    public MassCompensationFitResult Fit(
        FlowGroup group,
        IReadOnlyList<MassCompensationControlInput> controls,
        string matrix_name)
    {
        var channels = DescribeChannels(group);
        if (channels.Count == 0)
            throw new InvalidOperationException("The group has no mass channels with element and mass metadata.");
        var ambiguous = channels.GroupBy(channel => (channel.ElementSymbol.ToUpperInvariant(), channel.MassNumber))
            .FirstOrDefault(grouping => grouping.Count() > 1);
        if (ambiguous is not null)
            throw new InvalidOperationException($"Multiple channels are registered as {ambiguous.First().ElementSymbol}{ambiguous.Key.MassNumber}; element/mass pairs must be unique.");
        if (controls.Count == 0)
            throw new InvalidOperationException("Add at least one single-staining control tube.");

        var source_groups = controls.GroupBy(control => control.SourceChannelName, StringComparer.Ordinal).ToArray();
        var duplicate = source_groups.FirstOrDefault(grouping => grouping.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Only one control tube may be assigned to {display_name(channels, duplicate.Key)}.");

        var values = new float[channels.Count, channels.Count];
        var annotations = new MassLeakageKind[channels.Count, channels.Count];
        for (int source = 0; source < channels.Count; source++)
        {
            values[source, source] = 1;
            for (int receiving = 0; receiving < channels.Count; receiving++)
                if (source != receiving)
                    annotations[source, receiving] = ClassifyInteraction(channels[source], channels[receiving], Configuration.Preferences.Isotopes);
        }

        foreach (var control in controls)
        {
            int source = index_of_channel(channels, control.SourceChannelName);
            if (source < 0)
                throw new InvalidOperationException($"{control.Sample.Name} is assigned to a channel that is not a valid mass isotope in the group.");
            int source_index = control.Sample.GetChannelIndex(channels[source].ChannelName);
            if (source_index < 0)
                throw new InvalidOperationException($"{control.Sample.Name} does not contain source channel {channels[source].ChannelName}.");
            int[] event_indices = DeterministicSampleIndices(control.Sample.EventCount, FitSampleSize);
            foreach (int receiving in Enumerable.Range(0, channels.Count))
            {
                if (source == receiving || annotations[source, receiving] == MassLeakageKind.None)
                    continue;
                int receiving_index = control.Sample.GetChannelIndex(channels[receiving].ChannelName);
                if (receiving_index < 0)
                    throw new InvalidOperationException($"{control.Sample.Name} does not contain receiving channel {channels[receiving].ChannelName}.");
                values[source, receiving] = Convert.ToSingle(Math.Max(0.0, fit_slope(
                    control.Sample.RawEvents,
                    event_indices,
                    source_index,
                    receiving_index,
                    control.Sample.Name,
                    channels[source].DisplayName,
                    channels[receiving].DisplayName)));
            }
        }

        return new MassCompensationFitResult(
            CompensationMatrix.Create(matrix_name, channels.Select(channel => channel.ChannelName).ToArray(), values),
            channels,
            annotations);
    }

    public static MassLeakageKind ClassifyInteraction(
        MassChannelDescriptor source,
        MassChannelDescriptor receiving,
        IEnumerable<IsotopeElementPreference> isotope_elements)
    {
        if (string.Equals(source.ChannelName, receiving.ChannelName, StringComparison.Ordinal))
            return MassLeakageKind.None;
        MassLeakageKind result = MassLeakageKind.None;
        if (Math.Abs(source.MassNumber - receiving.MassNumber) == 1)
            result |= MassLeakageKind.AbundanceSensitivity;
        if (receiving.MassNumber - source.MassNumber == 16)
            result |= MassLeakageKind.OxideFormation;
        if (string.Equals(source.ElementSymbol, receiving.ElementSymbol, StringComparison.OrdinalIgnoreCase) &&
            source.MassNumber != receiving.MassNumber &&
            isotope_elements.FirstOrDefault(element => string.Equals(element.ElementSymbol, source.ElementSymbol, StringComparison.OrdinalIgnoreCase)) is { } element &&
            element.IsotopeMasses.Contains(source.MassNumber) && element.IsotopeMasses.Contains(receiving.MassNumber))
            result |= MassLeakageKind.IsotopicImpurity;
        return result;
    }

    public static int[] DeterministicSampleIndices(int event_count, int maximum_count)
    {
        if (event_count <= 0 || maximum_count <= 0)
            return [];
        int count = Math.Min(event_count, maximum_count);
        if (count == event_count)
            return Enumerable.Range(0, event_count).ToArray();
        var result = new int[count];
        for (int index = 0; index < count; index++)
            result[index] = (int)((long)index * event_count / count);
        return result;
    }

    public static bool IsFiniteAndInvertible(float[,] matrix)
    {
        int size = matrix.GetLength(0);
        if (size == 0 || matrix.GetLength(1) != size)
            return false;
        var values = new double[size, size];
        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
        {
            if (!float.IsFinite(matrix[row, column]))
                return false;
            values[row, column] = matrix[row, column];
        }
        for (int pivot = 0; pivot < size; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < size; row++)
                if (Math.Abs(values[row, pivot]) > Math.Abs(values[best, pivot])) best = row;
            if (Math.Abs(values[best, pivot]) < 1e-12)
                return false;
            if (best != pivot)
                for (int column = 0; column < size; column++)
                    (values[pivot, column], values[best, column]) = (values[best, column], values[pivot, column]);
            double divisor = values[pivot, pivot];
            for (int row = pivot + 1; row < size; row++)
            {
                double factor = values[row, pivot] / divisor;
                for (int column = pivot; column < size; column++) values[row, column] -= factor * values[pivot, column];
            }
        }
        return true;
    }

    private static double fit_slope(
        float[,] events,
        IReadOnlyList<int> event_indices,
        int source_column,
        int receiving_column,
        string sample_name,
        string source_name,
        string receiving_name)
    {
        double sum_x = 0, sum_y = 0;
        int count = 0;
        foreach (int row in event_indices)
        {
            double x = events[row, source_column];
            double y = events[row, receiving_column];
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            sum_x += x; sum_y += y; count++;
        }
        if (count < 2)
            throw fit_error(sample_name, source_name, receiving_name, "fewer than two finite observations");
        double mean_x = sum_x / count;
        double mean_y = sum_y / count;
        double numerator = 0, denominator = 0;
        foreach (int row in event_indices)
        {
            double x = events[row, source_column];
            double y = events[row, receiving_column];
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            double dx = x - mean_x;
            numerator += dx * (y - mean_y);
            denominator += dx * dx;
        }
        if (!double.IsFinite(denominator) || denominator <= 1e-12)
            throw fit_error(sample_name, source_name, receiving_name, "source variance is zero");
        double slope = numerator / denominator;
        if (!double.IsFinite(slope))
            throw fit_error(sample_name, source_name, receiving_name, "the fitted slope is not finite");
        return slope;
    }

    private static InvalidOperationException fit_error(string sample, string source, string receiving, string detail) =>
        new($"Unable to fit {source} leakage into {receiving} from {sample}: {detail}.");

    private static int index_of_channel(IReadOnlyList<MassChannelDescriptor> channels, string channel_name)
    {
        for (int index = 0; index < channels.Count; index++)
            if (string.Equals(channels[index].ChannelName, channel_name, StringComparison.Ordinal)) return index;
        return -1;
    }

    private static string display_name(IReadOnlyList<MassChannelDescriptor> channels, string channel_name) =>
        channels.FirstOrDefault(channel => string.Equals(channel.ChannelName, channel_name, StringComparison.Ordinal))?.DisplayName ?? channel_name;
}
