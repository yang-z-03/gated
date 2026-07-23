using System;
using System.IO;
using System.Linq;
using gated.Models;
using gated.Services;
using gated.ViewModels.Platforms;
using gated.Reduction;
using Xunit;

namespace gated.Tests;

public sealed class PlatformSystemTests
{
    [Fact]
    public void Catalog_registers_only_the_four_supported_platforms()
    {
        var kinds = PlatformCatalog.Implementations.Select(item => item.Kind).Order().ToArray();
        Assert.Equal(new[]
        {
            PlatformKind.Integration,
            PlatformKind.CellCycle,
            PlatformKind.Proliferation,
            PlatformKind.IntensityComparison
        }, kinds);
        Assert.DoesNotContain("Kinetics", Enum.GetNames<PlatformKind>());
        Assert.All(PlatformCatalog.Implementations, implementation =>
        {
            Assert.Equal(implementation.Kind, implementation.CreateModel().Kind);
            Assert.NotEmpty(implementation.LayoutOutputs);
            Assert.Contains(implementation.LayoutOutputs, output => output.IsDefault);
        });
    }

    [Fact]
    public void Plot_presentation_uses_explicit_series_roles()
    {
        var platform = new CellCyclePlatform();
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "DNA", IsSelected = true });
        var source = new PlatformRowMapSource { GroupId = Guid.NewGuid(), SampleId = Guid.NewGuid(), GateId = Guid.NewGuid() };
        platform.RowMap.Set([new(), new(), new(), source], [], []);
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = source.GroupId,
            SampleId = source.SampleId,
            GateId = source.GateId,
            IsPlatformDropped = true,
            IsSelected = true
        });
        platform.PlotSeries.Add(new PlatformPlotSeries
        {
            Key = "arbitrary-name",
            SourceId = 3,
            Role = PlatformSeriesRole.Component,
            X = [0, 1],
            Y = [0, 1]
        });

        var document = PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plots.Single();
        var series = Assert.Single(document.Series);
        Assert.Equal(PlatformSeriesRole.Component, series.Role);
        Assert.Equal(3, series.SourceId);
    }

    [Fact]
    public void Version_53_round_trip_preserves_platform_payload_roles_and_output_keys()
    {
        var workspace = new FlowWorkspace { Name = "Platform round trip" };
        var platform = new ProliferationPlatform { Name = "Proliferation 1", MaxGenerations = 12 };
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "CTV", Label = "CTV", IsSelected = true });
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = Guid.NewGuid(),
            SampleId = Guid.NewGuid(),
            GroupName = "Group",
            SampleName = "Sample",
            PopulationName = "All events",
            EventCount = 42,
            IsPopulation = false,
            IsPlatformDropped = true,
            IsSelected = false
        });
        platform.PlotSeries.Add(new PlatformPlotSeries
        {
            Key = "curve",
            SourceId = 0,
            Role = PlatformSeriesRole.Fit,
            X = [1, 2],
            Y = [3, 4]
        });
        var table = new PlatformResultTable { Key = "proliferation", Title = "Summary", Columns = ["Value"] };
        table.Rows.Add(["1"]);
        platform.ResultTables.Add(table);
        workspace.Platforms.Add(platform);
        var layout = new PageLayout { Name = "Layout" };
        layout.Elements.Add(new PlatformPlotElement { Platform = platform, OutputKey = "proliferation-plot" });
        layout.Elements.Add(new PlatformStatisticTableElement { Platform = platform, OutputKey = "proliferation" });
        workspace.PageLayouts.Add(layout);

        string path = Path.Combine(Path.GetTempPath(), $"gated-platform-{Guid.NewGuid():N}.gated");
        try
        {
            new WorkspaceBinarySerializer().Save(workspace, path);
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                _ = reader.ReadUInt32();
                Assert.Equal(53, reader.ReadInt32());
            }
            var loaded = new WorkspaceBinarySerializer().Load(path);
            var loaded_platform = Assert.IsType<ProliferationPlatform>(Assert.Single(loaded.Platforms));
            Assert.Equal(12, loaded_platform.MaxGenerations);
            Assert.Equal(42, Assert.Single(loaded_platform.Populations).EventCount);
            var loaded_series = Assert.Single(loaded_platform.PlotSeries);
            Assert.Equal(PlatformSeriesRole.Fit, loaded_series.Role);
            Assert.Equal(0, loaded_series.SourceId);
            var loaded_layout = Assert.Single(loaded.PageLayouts);
            Assert.Equal("proliferation-plot", Assert.IsType<PlatformPlotElement>(loaded_layout.Elements[0]).OutputKey);
            Assert.Equal("proliferation", Assert.IsType<PlatformStatisticTableElement>(loaded_layout.Elements[1]).OutputKey);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Version_53_round_trips_each_registered_implementation_payload()
    {
        var workspace = new FlowWorkspace { Name = "All platforms" };
        workspace.Platforms.Add(new IntegrationPlatform
        {
            Name = "Integration",
            BatchColumnName = "Batch",
            CytoNormOptions = new CytoNormOptions { QuantileCount = 77, Goal = CytoNormGoal.BatchMedian }
        });
        workspace.Platforms.Add(new CellCyclePlatform
        {
            Name = "Cell cycle",
            Model = CellCycleModelKind.DeanJettFox,
            FillComponents = false
        });
        workspace.Platforms.Add(new ProliferationPlatform { Name = "Proliferation", MaxGenerations = 13 });
        workspace.Platforms.Add(new IntensityComparisonPlatform { Name = "Comparison", ReferenceSample = "Sample - All events" });

        string path = Path.Combine(Path.GetTempPath(), $"gated-platforms-{Guid.NewGuid():N}.gated");
        try
        {
            var serializer = new WorkspaceBinarySerializer();
            serializer.Save(workspace, path);
            var loaded = serializer.Load(path);
            Assert.Collection(loaded.Platforms,
                item =>
                {
                    var value = Assert.IsType<IntegrationPlatform>(item);
                    Assert.Equal("Batch", value.BatchColumnName);
                    Assert.Equal(77, value.CytoNormOptions.QuantileCount);
                    Assert.Equal(CytoNormGoal.BatchMedian, value.CytoNormOptions.Goal);
                },
                item =>
                {
                    var value = Assert.IsType<CellCyclePlatform>(item);
                    Assert.Equal(CellCycleModelKind.DeanJettFox, value.Model);
                    Assert.False(value.FillComponents);
                },
                item => Assert.Equal(13, Assert.IsType<ProliferationPlatform>(item).MaxGenerations),
                item => Assert.Equal("Sample - All events", Assert.IsType<IntensityComparisonPlatform>(item).ReferenceSample));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(52)]
    public void Legacy_platform_sections_are_consumed_and_discarded(int version)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gated-legacy-{version}-{Guid.NewGuid():N}.gated");
        try
        {
            write_legacy_workspace(path, version);
            var loaded = new WorkspaceBinarySerializer().Load(path);
            Assert.Equal($"Legacy {version}", loaded.Name);
            Assert.Empty(loaded.Platforms);
            Assert.Equal(MetadataColumnKind.String, loaded.MetadataColumns["LegacyColumn"]);
            if (version >= 47) Assert.Equal("Legacy purpose", loaded.PurposeAndHypothesis);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void write_legacy_workspace(string path, int version)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x44544731u);
        writer.Write(version);
        writer.Write($"Legacy {version}");
        writer.Write(0); // groups
        writer.Write(1); // legacy platforms
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write(4); // legacy Kinetics kind
        writer.Write("Discard me");
        writer.Write((int)PlatformStatus.Complete);
        writer.Write("");
        writer.Write(4);
        writer.Write(262144d); writer.Write(0.5d); writer.Write(4.5d); writer.Write(0d); // logicle
        writer.Write(99); writer.Write(false); writer.Write(50); writer.Write((int)CytoNormGoal.BatchMean); writer.Write(false); writer.Write(false);
        writer.Write("Batch");
        writer.Write((int)PlatformTransformationKind.Linear);
        writer.Write((int)CellCycleModelKind.WatsonPragmatic);
        writer.Write(true); writer.Write(true); writer.Write(false);
        writer.Write(4); writer.Write(true);
        writer.Write(true); writer.Write(true); writer.Write(8); writer.Write(0.03d);
        writer.Write(0); writer.Write(64); writer.Write(3d); writer.Write(5); // legacy Kinetics options
        writer.Write(""); writer.Write(-100d); writer.Write(1000d);
        writer.Write(0); // parameters
        writer.Write(0); // population rows
        writer.Write(0); // feature rows
        writer.Write(0); // row-map sources
        writer.Write(false); writer.Write(false); // row-map arrays
        writer.Write(false); writer.Write(false); // compensated/raw matrices
        writer.Write(false); // batch ids
        writer.Write(false); writer.Write(false); writer.Write(false); // legacy normalized/transformed matrices
        writer.Write("");
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); // result collections
        writer.Write(0); // layouts
        writer.Write(0); // recent files
        writer.Write(1); writer.Write("LegacyColumn"); writer.Write((int)MetadataColumnKind.String);
        if (version >= 47)
        {
            writer.Write("Legacy purpose");
            writer.Write(""); writer.Write(""); writer.Write("");
        }
    }
}
