using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Gated.Models;

public enum StatisticType
{
    Count,
    FreqOfParent,
    FreqOfTotal,
    Median,
    Mean,
    GeometricMean,
    StdDev,
    CV,
    Min,
    Max
}

public class StatisticDefinition : INode
{
    public StatisticDefinition(StatisticType type, string? channelName = null)
    {
        Type = type;
        ChannelName = channelName;
        Name = type switch
        {
            StatisticType.Count => "# Cells",
            StatisticType.FreqOfParent => "% Parent",
            StatisticType.FreqOfTotal => "% Total",
            StatisticType.Median => channelName != null ? $"Median-{channelName}" : "Median",
            StatisticType.Mean => channelName != null ? $"Mean-{channelName}" : "Mean",
            StatisticType.GeometricMean => channelName != null ? $"GeoMean-{channelName}" : "GeoMean",
            StatisticType.StdDev => channelName != null ? $"SD-{channelName}" : "SD",
            StatisticType.CV => channelName != null ? $"CV-{channelName}" : "CV",
            StatisticType.Min => channelName != null ? $"Min-{channelName}" : "Min",
            StatisticType.Max => channelName != null ? $"Max-{channelName}" : "Max",
            _ => "Statistic"
        };
    }

    public StatisticType Type { get; set; }
    public string? ChannelName { get; set; }
    public string Name { get; set; }
    public string Identifier { get; set; } = "statistic-def";
    public ObservableCollection<INode> Children => new();
    public bool IsExpanded { get; set; } = false;
}

public static class StatisticsComputer
{
    public static Dictionary<string, double> Compute(
        Population parent, Subset subset, IReadOnlyList<StatisticDefinition> definitions)
    {
        var results = new Dictionary<string, double>();
        long n = subset.EventCount;
        long n_parent = parent.EventCount;

        foreach (var def in definitions)
        {
            double value = def.Type switch
            {
                StatisticType.Count => n,
                StatisticType.FreqOfParent => n_parent > 0 ? 100.0 * n / n_parent : 0,
                StatisticType.FreqOfTotal => FreqOfTotal(parent, n),
                StatisticType.Median or StatisticType.Mean or StatisticType.GeometricMean
                    or StatisticType.StdDev or StatisticType.CV or StatisticType.Min or StatisticType.Max
                    => ComputeChannelStat(subset, def),
                _ => 0
            };
            results[def.Name] = value;
        }
        return results;
    }

    private static double ComputeChannelStat(Subset subset, StatisticDefinition def)
    {
        var channel = subset.Channels.Values
            .FirstOrDefault(c => c.Name == def.ChannelName);
        if (channel == null) return 0;

        var data = subset.GetValues(subset.EventCount, channel);
        var values = data[channel];
        if (values == null || values.Length == 0) return 0;

        return def.Type switch
        {
            StatisticType.Median => Percentile(values, 0.5),
            StatisticType.Mean => values.Average(),
            StatisticType.GeometricMean => GeometricMean(values),
            StatisticType.StdDev => StdDev(values),
            StatisticType.CV => MeanCV(values),
            StatisticType.Min => values.Min(),
            StatisticType.Max => values.Max(),
            _ => 0
        };
    }

    private static float Percentile(float[] sorted, double p)
    {
        var s = (float[])sorted.Clone();
        Array.Sort(s);
        int idx = (int)Math.Round(p * (s.Length - 1));
        return s[Math.Clamp(idx, 0, s.Length - 1)];
    }

    private static double GeometricMean(float[] values)
    {
        double sumLog = 0;
        int count = 0;
        foreach (var v in values)
        {
            if (v > 0)
            {
                sumLog += Math.Log(v);
                count++;
            }
        }
        return count > 0 ? Math.Exp(sumLog / count) : 0;
    }

    private static double StdDev(float[] values)
    {
        double avg = values.Average();
        double sumSq = 0;
        foreach (var v in values) sumSq += (v - avg) * (v - avg);
        return Math.Sqrt(sumSq / values.Length);
    }

    private static double MeanCV(float[] values)
    {
        double avg = values.Average();
        if (avg == 0) return 0;
        double sd = StdDev(values);
        return 100.0 * sd / Math.Abs(avg);
    }

    private static double FreqOfTotal(Population parent, long n)
    {
        Population root = parent;
        while (root.Parent != null) root = root.Parent;
        long total = root.EventCount;
        return total > 0 ? 100.0 * n / total : 0;
    }
}
