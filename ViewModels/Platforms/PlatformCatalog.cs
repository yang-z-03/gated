using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using gated.Models;
using gated.Services;
using gated.Reduction;

namespace gated.ViewModels.Platforms;

public interface IPlatformImplementation
{
    PlatformKind Kind { get; }
    string NamePrefix { get; }
    string EditorTitle { get; }
    IReadOnlyList<PlatformLayoutOutput> LayoutOutputs { get; }
    Platform CreateModel();
    PlatformEditorViewModel CreateEditor(FlowWorkspace workspace, Platform platform);
    PlatformPresentation CreatePresentation(Platform platform);
    bool Prepare(FlowWorkspace workspace, Platform platform);
    Task ExecuteAsync(FlowWorkspace workspace, Platform platform);
    void WritePayload(BinaryWriter writer, Platform platform);
    void ReadPayload(BinaryReader reader, Platform platform, int payloadVersion);
}

public static class PlatformCatalog
{
    private static readonly IReadOnlyDictionary<PlatformKind, IPlatformImplementation> implementations =
        new IPlatformImplementation[]
        {
            new IntegrationPlatformImplementation(),
            new CellCyclePlatformImplementation(),
            new ProliferationPlatformImplementation(),
            new IntensityComparisonPlatformImplementation()
        }.ToDictionary(item => item.Kind);

    public static IReadOnlyCollection<IPlatformImplementation> Implementations => implementations.Values.ToArray();

    public static IPlatformImplementation Get(PlatformKind kind) =>
        implementations.TryGetValue(kind, out var implementation)
            ? implementation
            : throw new NotSupportedException($"Platform kind '{kind}' is not registered.");
}

internal abstract class PlatformImplementation<TPlatform> : IPlatformImplementation where TPlatform : Platform, new()
{
    public abstract PlatformKind Kind { get; }
    public abstract string NamePrefix { get; }
    public abstract string EditorTitle { get; }
    public abstract IReadOnlyList<PlatformLayoutOutput> LayoutOutputs { get; }
    public Platform CreateModel() => new TPlatform();
    public abstract PlatformEditorViewModel CreateEditor(FlowWorkspace workspace, Platform platform);
    public abstract PlatformPresentation CreatePresentation(Platform platform);
    public virtual bool Prepare(FlowWorkspace workspace, Platform platform) =>
        new PlatformInputMaterializer(workspace).Prepare(platform);
    public virtual async Task ExecuteAsync(FlowWorkspace workspace, Platform platform)
    {
        if (!Prepare(workspace, platform) || string.IsNullOrWhiteSpace(platform.ResourcePath))
            return;
        platform.ProgressText = "Running platform script";
        await Task.Run(() => gated.Python.PythonExtensionRuntime.ExecutePlatformScript(
            platform.ResourcePath,
            workspace,
            platform,
            $"platform:{platform.Id}",
            platform.Name));
    }
    public abstract void WritePayload(BinaryWriter writer, Platform platform);
    public abstract void ReadPayload(BinaryReader reader, Platform platform, int payloadVersion);

    protected static void WriteParameters(BinaryWriter writer, Platform platform)
    {
        writer.Write(platform.Parameters.Count);
        foreach (var parameter in platform.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.Write(parameter.Key);
            writer.Write(Platform.ParameterToJson(parameter.Value));
        }
    }

    protected static void ReadParameters(BinaryReader reader, Platform platform)
    {
        platform.Parameters.Clear();
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
            platform.Parameters[reader.ReadString()] = Platform.ParameterFromJson(reader.ReadString());
    }

    protected static void WriteSmoothing(BinaryWriter writer, UnivariatePlatform platform)
    {
        writer.Write(platform.Smoothing.HalfWindow);
        writer.Write(platform.Smoothing.Enabled);
    }

    protected static void ReadSmoothing(BinaryReader reader, UnivariatePlatform platform)
    {
        platform.Smoothing.HalfWindow = reader.ReadInt32();
        platform.Smoothing.Enabled = reader.ReadBoolean();
    }

    protected static void WriteDoubleArray(BinaryWriter writer, double[]? values)
    {
        writer.Write(values is not null);
        if (values is null) return;
        writer.Write(values.Length);
        foreach (double value in values) writer.Write(value);
    }

    protected static double[]? ReadDoubleArray(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        int count = reader.ReadInt32();
        var values = new double[count];
        for (int index = 0; index < count; index++) values[index] = reader.ReadDouble();
        return values;
    }

    protected static TPlatform Require(Platform platform) => platform as TPlatform
        ?? throw new ArgumentException($"Expected {typeof(TPlatform).Name}.", nameof(platform));
}

internal sealed class IntegrationPlatformImplementation : PlatformImplementation<IntegrationPlatform>
{
    public override PlatformKind Kind => PlatformKind.Integration;
    public override string NamePrefix => "Integration";
    public override string EditorTitle => "Integration";
    public override IReadOnlyList<PlatformLayoutOutput> LayoutOutputs =>
        [new("integration-histogram", "Selected-channel integration histogram", PlatformLayoutOutputKind.Plot, true)];
    public override PlatformEditorViewModel CreateEditor(FlowWorkspace workspace, Platform platform) =>
        new IntegrationPlatformEditorViewModel(workspace, Require(platform));
    public override PlatformPresentation CreatePresentation(Platform platform) =>
        PlatformPresentationBuilder.Integration(Require(platform));
    public override Task ExecuteAsync(FlowWorkspace workspace, Platform platform)
    {
        new PlatformInputMaterializer(workspace).RunIntegration(Require(platform));
        return Task.CompletedTask;
    }
    public override void WritePayload(BinaryWriter writer, Platform platform)
    {
        var integration = Require(platform);
        writer.Write(integration.BatchColumnName);
        writer.Write(integration.CytoNormOptions.QuantileCount);
        WriteDoubleArray(writer, integration.CytoNormOptions.Quantiles);
        writer.Write(integration.CytoNormOptions.MinimumCellsPerCluster);
        writer.Write((int)integration.CytoNormOptions.Goal);
        writer.Write(integration.CytoNormOptions.GoalBatch.HasValue);
        if (integration.CytoNormOptions.GoalBatch.HasValue) writer.Write(integration.CytoNormOptions.GoalBatch.Value);
        WriteDoubleArray(writer, integration.CytoNormOptions.Limits);
        WriteParameters(writer, integration);
    }
    public override void ReadPayload(BinaryReader reader, Platform platform, int payloadVersion)
    {
        var integration = Require(platform);
        integration.BatchColumnName = reader.ReadString();
        integration.CytoNormOptions = new CytoNormOptions
        {
            QuantileCount = reader.ReadInt32(),
            Quantiles = ReadDoubleArray(reader),
            MinimumCellsPerCluster = reader.ReadInt32(),
            Goal = (CytoNormGoal)reader.ReadInt32(),
            GoalBatch = reader.ReadBoolean() ? reader.ReadInt32() : null,
            Limits = ReadDoubleArray(reader)
        };
        ReadParameters(reader, integration);
    }
}

internal sealed class CellCyclePlatformImplementation : PlatformImplementation<CellCyclePlatform>
{
    public override PlatformKind Kind => PlatformKind.CellCycle;
    public override string NamePrefix => "Cell cycle";
    public override string EditorTitle => "Univariate cell cycle modeling";
    public override IReadOnlyList<PlatformLayoutOutput> LayoutOutputs =>
    [
        new("cell-cycle-plot", "Composite fitted histogram", PlatformLayoutOutputKind.Plot, true),
        new("cell_cycle", "Cell-cycle table", PlatformLayoutOutputKind.Table, true)
    ];
    public override PlatformEditorViewModel CreateEditor(FlowWorkspace workspace, Platform platform) =>
        new CellCyclePlatformEditorViewModel(workspace, Require(platform));
    public override PlatformPresentation CreatePresentation(Platform platform) =>
        PlatformPresentationBuilder.Univariate(Require(platform), "cell-cycle-plot", "Cell cycle", "cell_cycle", true, Require(platform).DrawModelSum, Require(platform).DrawComponents);
    public override void WritePayload(BinaryWriter writer, Platform platform)
    {
        var value = Require(platform);
        writer.Write((int)value.Model);
        writer.Write(value.DrawModelSum);
        writer.Write(value.DrawComponents);
        writer.Write(value.FillComponents);
        WriteSmoothing(writer, value);
        WriteParameters(writer, value);
    }
    public override void ReadPayload(BinaryReader reader, Platform platform, int payloadVersion)
    {
        var value = Require(platform);
        value.Model = (CellCycleModelKind)reader.ReadInt32();
        value.DrawModelSum = reader.ReadBoolean();
        value.DrawComponents = reader.ReadBoolean();
        value.FillComponents = reader.ReadBoolean();
        ReadSmoothing(reader, value);
        ReadParameters(reader, value);
    }
}

internal sealed class ProliferationPlatformImplementation : PlatformImplementation<ProliferationPlatform>
{
    public override PlatformKind Kind => PlatformKind.Proliferation;
    public override string NamePrefix => "Proliferation";
    public override string EditorTitle => "Proliferation modeling";
    public override IReadOnlyList<PlatformLayoutOutput> LayoutOutputs =>
    [
        new("proliferation-plot", "Composite fitted histogram", PlatformLayoutOutputKind.Plot, true),
        new("proliferation", "Proliferation summary", PlatformLayoutOutputKind.Table, true),
        new("proliferation_generations", "Generation fractions", PlatformLayoutOutputKind.Table, false)
    ];
    public override PlatformEditorViewModel CreateEditor(FlowWorkspace workspace, Platform platform) =>
        new ProliferationPlatformEditorViewModel(workspace, Require(platform));
    public override PlatformPresentation CreatePresentation(Platform platform) =>
        PlatformPresentationBuilder.Univariate(Require(platform), "proliferation-plot", "Proliferation", "proliferation", true, Require(platform).DrawModelSum, Require(platform).DrawComponents);
    public override void WritePayload(BinaryWriter writer, Platform platform)
    {
        var value = Require(platform);
        writer.Write(value.DrawModelSum);
        writer.Write(value.DrawComponents);
        writer.Write(value.MaxGenerations);
        writer.Write(value.PeakProminence);
        WriteSmoothing(writer, value);
        WriteParameters(writer, value);
    }
    public override void ReadPayload(BinaryReader reader, Platform platform, int payloadVersion)
    {
        var value = Require(platform);
        value.DrawModelSum = reader.ReadBoolean();
        value.DrawComponents = reader.ReadBoolean();
        value.MaxGenerations = reader.ReadInt32();
        value.PeakProminence = reader.ReadDouble();
        ReadSmoothing(reader, value);
        ReadParameters(reader, value);
    }
}

internal sealed class IntensityComparisonPlatformImplementation : PlatformImplementation<IntensityComparisonPlatform>
{
    public override PlatformKind Kind => PlatformKind.IntensityComparison;
    public override string NamePrefix => "Intensity comparison";
    public override string EditorTitle => "Population intensity comparison";
    public override IReadOnlyList<PlatformLayoutOutput> LayoutOutputs =>
    [
        new("intensity-comparison-plot", "Overlaid histogram", PlatformLayoutOutputKind.Plot, true),
        new("intensity_comparison", "Comparison table", PlatformLayoutOutputKind.Table, true)
    ];
    public override PlatformEditorViewModel CreateEditor(FlowWorkspace workspace, Platform platform) =>
        new IntensityComparisonPlatformEditorViewModel(workspace, Require(platform));
    public override PlatformPresentation CreatePresentation(Platform platform) =>
        PlatformPresentationBuilder.Univariate(Require(platform), "intensity-comparison-plot", "Intensity comparison", "intensity_comparison", true, true, true, false);
    public override void WritePayload(BinaryWriter writer, Platform platform)
    {
        var value = Require(platform);
        writer.Write(value.ReferenceSample);
        WriteSmoothing(writer, value);
        WriteParameters(writer, value);
    }
    public override void ReadPayload(BinaryReader reader, Platform platform, int payloadVersion)
    {
        var value = Require(platform);
        value.ReferenceSample = reader.ReadString();
        ReadSmoothing(reader, value);
        ReadParameters(reader, value);
    }
}
