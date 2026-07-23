using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Models;

public enum PlatformLayoutOutputKind
{
    Plot,
    Table
}

public sealed record PlatformLayoutOutput(
    string Key,
    string Title,
    PlatformLayoutOutputKind Kind,
    bool IsDefault);

public sealed class PlatformPresentation
{
    public static PlatformPresentation Empty { get; } = new();

    public IReadOnlyList<PlatformPlotDocument> Plots { get; init; } = [];
    public IReadOnlyList<PlatformTableDocument> Tables { get; init; } = [];
    public IReadOnlyList<PlatformLayoutOutput> Outputs { get; init; } = [];

    public PlatformPlotDocument? Plot(string key) =>
        Plots.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

    public PlatformTableDocument? Table(string key) =>
        Tables.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
}

public sealed class PlatformPlotDocument
{
    public string Key { get; init; } = "plot";
    public string Title { get; init; } = "Plot";
    public string XLabel { get; init; } = "Intensity";
    public string YLabel { get; init; } = "Frequency";
    public PlatformTransformationKind XTransform { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public LogicleParameters Logicle { get; init; } = new();
    public IReadOnlyList<PlatformPlotSeries> Series { get; init; } = [];
}

public sealed class PlatformTableDocument
{
    public string Key { get; init; } = "table";
    public string Title { get; init; } = "Results";
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<string[]> Rows { get; init; } = [];
}
