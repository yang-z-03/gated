using System;
using System.IO;
using System.Linq;
using gated.Controls;
using gated.Models;
using gated.Services;
using gated.ViewModels;
using gated.ViewModels.Platforms;
using gated.Reduction;
using Xunit;

namespace gated.Tests;

public sealed class PlatformSystemTests
{
    [Fact]
    public void Integration_initializer_builds_population_hierarchy_and_defaults_batch_to_sample()
    {
        var (workspace, group, _, parent, child) = integration_workspace();

        var platform = Assert.IsType<IntegrationPlatform>(
            PlatformInitializer.Create(workspace, PlatformKind.Integration, group));

        Assert.Equal("Sample", platform.BatchColumnName);
        Assert.Collection(platform.Populations,
            root =>
            {
                Assert.False(root.IsPopulation);
                Assert.Equal("All events", root.PopulationName);
                Assert.True(root.IsSelected);
                Assert.True(root.IsEnabled);
                Assert.Null(root.ParentKey);
            },
            row =>
            {
                Assert.Equal(parent.Gate.Id, row.GateId);
                Assert.True(row.IsSelected);
                Assert.False(row.IsEnabled);
                Assert.Equal(platform.Populations[0].RowKey, row.ParentKey);
            },
            row =>
            {
                Assert.Equal(child.Gate.Id, row.GateId);
                Assert.True(row.IsSelected);
                Assert.False(row.IsEnabled);
                Assert.Equal(platform.Populations[1].RowKey, row.ParentKey);
            });
        Assert.Same(platform.Populations[0], Assert.Single(PlatformInitializer.SelectedPopulationInputs(platform)));
        Assert.Equal("Signal", Assert.Single(platform.Features).ChannelName);
    }

    [Fact]
    public void Integration_materialization_uses_only_selected_enabled_population_rows()
    {
        var (workspace, group, _, parent, _) = integration_workspace();
        var platform = Assert.IsType<IntegrationPlatform>(
            PlatformInitializer.Create(workspace, PlatformKind.Integration, group));
        var root = platform.Populations[0];
        var parent_row = platform.Populations[1];
        var child_row = platform.Populations[2];
        root.IsSelected = false;
        root.IsEnabled = true;
        parent_row.IsSelected = true;
        parent_row.IsEnabled = true;
        child_row.IsSelected = true;
        child_row.IsEnabled = false;
        PlatformInitializer.RefreshFeatures(workspace, platform);

        Assert.True(new PlatformInputMaterializer(workspace).Prepare(platform));
        Assert.Equal(parent.EventIndices.Length, platform.RowMap.Count);
        Assert.All(platform.RowMap.SourceIds, source_id => Assert.Equal(0, source_id));
        Assert.Equal(parent.Gate.Id, Assert.Single(platform.RowMap.Sources).GateId);
    }

    [Fact]
    public void Integration_channel_rows_detect_modality_and_expose_per_channel_normalization()
    {
        var workspace = new FlowWorkspace();
        var group = new FlowGroup { Name = "Group" };
        group.Samples.Add(new FlowSample(
            "Sample",
            [
                new ChannelDefinition(0, "Ir191Di", "Iridium", 1000, 1),
                new ChannelDefinition(1, "Time", "Time", 100, 1)
            ],
            new float[,] { { 10, 1 }, { 20, 2 } }));
        workspace.Groups.Add(group);
        var platform = Assert.IsType<IntegrationPlatform>(
            PlatformInitializer.Create(workspace, PlatformKind.Integration, group));
        var editor = new IntegrationPlatformEditorViewModel(workspace, platform);
        try
        {
            var mass = editor.IntegrationChannelRows.Single(row => row.ChannelName == "Ir191Di");
            Assert.Equal(ChannelSemanticKind.Mass, mass.ChannelKind);
            Assert.Equal(PlatformTransformationKind.Arcsinh, mass.SelectedNormalization?.Value);
            Assert.True(mass.IsArcsinh);
            Assert.Equal(5, mass.ArcsinhA);

            var time = editor.IntegrationChannelRows.Single(row => row.ChannelName == "Time");
            Assert.Equal(ChannelSemanticKind.Time, time.ChannelKind);
            Assert.Equal(PlatformTransformationKind.Linear, time.SelectedNormalization?.Value);

            mass.SelectedNormalization = mass.NormalizationChoices.Single(choice => choice.Value == PlatformTransformationKind.Logicle);
            mass.LogicleT = 12345;
            mass.LogicleW = 0.4;
            var configured = platform.Transformations["Ir191Di"];
            Assert.Equal(PlatformTransformationKind.Logicle, configured.Kind);
            Assert.Equal(12345, configured.Logicle.T);
            Assert.Equal(0.4, configured.Logicle.W);
            Assert.False(configured.IsAutomatic);
        }
        finally
        {
            editor.Dispose();
        }
    }

    [Fact]
    public void Integration_applies_each_channels_configured_normalization_and_parameters()
    {
        var workspace = new FlowWorkspace();
        var group = new FlowGroup { Name = "Group" };
        group.Samples.Add(new FlowSample(
            "Sample",
            [
                new ChannelDefinition(0, "Signal", "Signal", 100, 1),
                new ChannelDefinition(1, "Ir191Di", "Iridium", 100, 1)
            ],
            new float[,] { { -9, 10 }, { 99, 100 } }));
        workspace.Groups.Add(group);
        var platform = Assert.IsType<IntegrationPlatform>(
            PlatformInitializer.Create(workspace, PlatformKind.Integration, group));
        platform.Transformations["Signal"] = new PlatformChannelTransformation
        {
            Kind = PlatformTransformationKind.Logarithm,
            IsAutomatic = false
        };
        platform.Transformations["Ir191Di"] = new PlatformChannelTransformation
        {
            Kind = PlatformTransformationKind.Arcsinh,
            ArcsinhCofactor = 10,
            IsAutomatic = false
        };

        Assert.True(new PlatformInputMaterializer(workspace).RunIntegration(platform));
        var normalized = Assert.IsType<float[,]>(platform.Normalized);
        int signal = Array.IndexOf(platform.SelectedFeatureNames, "Signal");
        int mass = Array.IndexOf(platform.SelectedFeatureNames, "Ir191Di");
        Assert.Equal(-Math.Log10(10), normalized[0, signal], 5);
        Assert.Equal(Math.Log10(100), normalized[1, signal], 5);
        Assert.Equal(Math.Asinh(1), normalized[0, mass], 5);
        Assert.Equal(Math.Asinh(10), normalized[1, mass], 5);
        var mass_plot = Assert.IsType<PlatformPlotDocument>(
            PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plot("integration:Ir191Di"));
        Assert.Equal(CoordinateScaleKind.Arcsinh, mass_plot.XScale.Kind);
        Assert.Equal(10, mass_plot.XScale.ArcsinhCofactor);
    }

    [Fact]
    public void Integration_histogram_uses_mass_scale_and_reuses_subsampled_sorted_series()
    {
        const int event_count = 25_000;
        var raw = new float[event_count, 1];
        var normalized = new float[event_count, 1];
        for (int row = 0; row < event_count; row++)
        {
            raw[row, 0] = row;
            normalized[row, 0] = (float)Math.Asinh(row / 5.0);
        }

        var workspace = new FlowWorkspace();
        var group = new FlowGroup { Name = "Group" };
        var sample = new FlowSample("Sample", [new ChannelDefinition(0, "Ir191Di", "Iridium", event_count, 1)], raw);
        group.Samples.Add(sample);
        workspace.Groups.Add(group);
        var platform = new IntegrationPlatform { Name = "Integration", Normalized = normalized };
        platform.Compensated = raw;
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "Ir191Di", IsSelected = true });
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group.Id,
            SampleId = sample.Id,
            PopulationName = "All events",
            IsPopulation = false,
            IsPlatformDropped = true,
            IsSelected = true
        });
        platform.RowMap.Set(
            [new PlatformRowMapSource { GroupId = group.Id, SampleId = sample.Id }],
            Enumerable.Repeat(0, event_count).ToArray(),
            Enumerable.Range(0, event_count).ToArray());

        var editor = new IntegrationPlatformEditorViewModel(workspace, platform);
        try
        {
            Assert.Equal(HistogramAxisScaleKind.Arcsinh, editor.IntegrationHistogramAxisScale);
            var normalized_series = Assert.Single(editor.IntegrationHistogramSeries);
            Assert.Equal(20_000, normalized_series.Values.Count);
            Assert.Same(normalized_series.Values, normalized_series.SortedValues);
            Assert.True(normalized_series.Values.Zip(normalized_series.Values.Skip(1), (left, right) => left <= right).All(value => value));

            editor.ShowNormalizedHistogram = false;
            _ = Assert.Single(editor.IntegrationHistogramSeries);
            editor.ShowNormalizedHistogram = true;
            Assert.Same(normalized_series, Assert.Single(editor.IntegrationHistogramSeries));
        }
        finally
        {
            editor.Dispose();
        }
    }

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
            var platform = implementation.CreateModel();
            Assert.Equal(implementation.Kind, platform.Kind);
            Assert.Empty(implementation.LayoutOutputs(platform));
        });
    }

    [Fact]
    public void Integration_layout_outputs_are_absent_until_results_exist()
    {
        var platform = new IntegrationPlatform();
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "Ir191Di", IsSelected = true });
        var implementation = PlatformCatalog.Get(PlatformKind.Integration);

        Assert.Empty(implementation.LayoutOutputs(platform));
        Assert.Empty(implementation.CreatePresentation(platform).Plots);
    }

    [Fact]
    public void Integration_registers_one_scaled_layout_plot_for_each_integrated_channel()
    {
        var group_id = Guid.NewGuid();
        var sample_id = Guid.NewGuid();
        var platform = new IntegrationPlatform
        {
            Normalized = new float[,]
            {
                { (float)Math.Asinh(10 / 5.0), 0 },
                { (float)Math.Asinh(100 / 5.0), 1 },
                { (float)Math.Asinh(1000 / 5.0), 2 }
            }
        };
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "Ir191Di", IsSelected = true });
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "Time", IsSelected = true });
        platform.Transformations["Ir191Di"] = new PlatformChannelTransformation
        {
            Kind = PlatformTransformationKind.Arcsinh,
            Minimum = -10,
            Maximum = 1000
        };
        platform.Transformations["Time"] = new PlatformChannelTransformation
        {
            Kind = PlatformTransformationKind.Linear,
            Minimum = 0,
            Maximum = 2
        };
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group_id,
            SampleId = sample_id,
            PopulationName = "All events",
            IsPopulation = false,
            IsSelected = true,
            IsEnabled = true
        });
        platform.RowMap.Set(
            [new PlatformRowMapSource { GroupId = group_id, SampleId = sample_id }],
            [0, 0, 0],
            [0, 1, 2]);

        var implementation = PlatformCatalog.Get(PlatformKind.Integration);
        var outputs = implementation.LayoutOutputs(platform);
        var presentation = implementation.CreatePresentation(platform);

        Assert.Equal(2, outputs.Count);
        Assert.Equal(2, outputs.Select(output => output.Key).Distinct().Count());
        Assert.Contains(outputs, output => output.Key == "integration:Ir191Di");
        Assert.Contains(outputs, output => output.Key == "integration:Time");
        Assert.Equal(new[] { "Ir191Di", "Time" }, outputs.Select(output => output.Title));
        Assert.All(outputs, output => Assert.False(output.IsDefault));
        Assert.Equal(2, presentation.Plots.Count);

        var mass = Assert.IsType<PlatformPlotDocument>(presentation.Plot("integration:Ir191Di"));
        Assert.Equal(CoordinateScaleKind.Arcsinh, mass.XScale.Kind);
        Assert.True(double.IsFinite(mass.Minimum));
        Assert.True(double.IsFinite(mass.Maximum));
        Assert.True(mass.Maximum > mass.Minimum);
        Assert.NotEmpty(mass.Series);

        var time = Assert.IsType<PlatformPlotDocument>(presentation.Plot("integration:Time"));
        Assert.Equal(CoordinateScaleKind.Linear, time.XScale.Kind);
        Assert.True(double.IsFinite(time.Minimum));
        Assert.True(double.IsFinite(time.Maximum));
        Assert.True(time.Maximum > time.Minimum);
        Assert.NotEmpty(time.Series);
    }

    [Fact]
    public void Layout_accepts_available_platform_outputs_but_not_whole_platform_nodes()
    {
        var platform = new IntegrationPlatform
        {
            Normalized = new float[,] { { (float)Math.Asinh(10 / 5.0) } }
        };
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "Ir191Di", IsSelected = true });
        platform.Transformations["Ir191Di"] = new PlatformChannelTransformation
        {
            Kind = PlatformTransformationKind.Arcsinh,
            Minimum = 0,
            Maximum = 10
        };
        var group_id = Guid.NewGuid();
        var sample_id = Guid.NewGuid();
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group_id,
            SampleId = sample_id,
            IsPopulation = false,
            IsSelected = true
        });
        platform.RowMap.Set(
            [new PlatformRowMapSource { GroupId = group_id, SampleId = sample_id }],
            [0],
            [0]);
        var output = Assert.Single(PlatformCatalog.Get(platform.Kind).LayoutOutputs(platform));
        var platform_node = new ProjectNode(ProjectNodeKind.Platform, "Integration", "platform", platform: platform);
        var output_node = new ProjectNode(ProjectNodeKind.PlatformOutput, output.Title, "output", platform: platform, platform_output: output);
        var view_model = new MainWindowViewModel();

        Assert.False(view_model.CanAddProjectNodeToLayout(platform_node));
        Assert.True(view_model.CanAddProjectNodeToLayout(output_node));
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
        var output = Assert.Single(PlatformCatalog.Get(platform.Kind).LayoutOutputs(platform));
        Assert.Equal(document.Key, output.Key);
    }

    [Fact]
    public void Univariate_presentation_samples_parameterized_fit_and_component_curves()
    {
        var platform = new CellCyclePlatform();
        platform.Axis.Minimum = 0;
        platform.Axis.Maximum = 10;
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "DNA", IsSelected = true });
        var source = new PlatformRowMapSource { GroupId = Guid.NewGuid(), SampleId = Guid.NewGuid(), GateId = Guid.NewGuid() };
        platform.RowMap.Set([source], [], []);
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = source.GroupId,
            SampleId = source.SampleId,
            GateId = source.GateId,
            IsPlatformDropped = true,
            IsSelected = true
        });
        platform.FitCurves.Add(new PlatformFitCurve
        {
            Key = "fit_0",
            SourceId = 0,
            Role = PlatformSeriesRole.Fit,
            Kind = PlatformFitCurveKind.GaussianSum,
            Parameters = [1, 3, 0.5, 0.7, 6, 0.8]
        });
        platform.FitCurves.Add(new PlatformFitCurve
        {
            Key = "component_0",
            SourceId = 0,
            Role = PlatformSeriesRole.Component,
            Kind = PlatformFitCurveKind.Gaussian,
            Parameters = [1, 3, 0.5]
        });

        var document = Assert.Single(PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plots);
        Assert.Collection(document.Series,
            fit =>
            {
                Assert.Equal(PlatformSeriesRole.Fit, fit.Role);
                Assert.Equal(500, fit.X.Length);
                Assert.All(fit.Y, value => Assert.True(double.IsFinite(value)));
            },
            component =>
            {
                Assert.Equal(PlatformSeriesRole.Component, component.Role);
                Assert.Equal(500, component.X.Length);
                Assert.InRange(component.Y.Max(), 0.99, 1.01);
            });

        platform.DrawModelSum = false;
        Assert.Equal(PlatformSeriesRole.Component,
            Assert.Single(Assert.Single(PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plots).Series).Role);
    }

    [Fact]
    public void Intensity_comparison_uses_automatic_population_bounds_and_default_binning()
    {
        var raw = new float[,] { { 2 }, { 4 }, { 10 } };
        var workspace = new FlowWorkspace();
        var group = new FlowGroup { Name = "Group" };
        var sample = new FlowSample(
            "Sample",
            [new ChannelDefinition(0, "Signal", "Marker", 100, 1)],
            raw);
        group.Samples.Add(sample);
        workspace.Groups.Add(group);
        var platform = new IntensityComparisonPlatform();
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group.Id,
            SampleId = sample.Id,
            GroupName = group.Name,
            SampleName = sample.Name,
            PopulationName = "All events",
            IsPopulation = false,
            IsPlatformDropped = true,
            IsSelected = true
        });
        platform.Features.Add(new PlatformFeatureSelection
        {
            ChannelName = "Signal",
            Label = "Marker",
            IsSelected = true
        });

        var editor = new IntensityComparisonPlatformEditorViewModel(workspace, platform);
        try
        {
            Assert.Equal(100, platform.DistributionBinning);
            Assert.Equal(-0.1, platform.Axis.Minimum, 10);
            Assert.Equal(10, platform.Axis.Maximum, 10);
            Assert.Equal(0, Assert.Single(platform.Populations).PlotColorIndex);
            var first_series = Assert.Single(editor.PlotDocument!.Series);
            Assert.Equal(500, first_series.X.Length);
            Assert.Same(first_series, Assert.Single(editor.PlotDocument!.Series));

            platform.DistributionBinning = 256;
            Assert.Equal(500, Assert.Single(editor.PlotDocument!.Series).X.Length);

            var table = new PlatformResultTable
            {
                Key = "intensity_comparison",
                Title = "Intensity comparison",
                Columns = ["Sample", "Population", "Events"]
            };
            table.Rows.Add(["Sample", "All events", "3"]);
            platform.ResultTables.Add(table);
            Assert.True(editor.HasIntensityComparisonResults);
            var result = Assert.Single(editor.IntensityComparisonResultRows);
            Assert.Equal("Sample", result.Sample);
            Assert.Equal("All events", result.Population);
            Assert.Equal("3", result.Events);
            result.Sample = "Edited sample";
            Assert.Equal("Edited sample", table.Rows[0][0]);
        }
        finally
        {
            editor.Dispose();
        }
    }

    [Theory]
    [InlineData(PlatformKind.CellCycle)]
    [InlineData(PlatformKind.Proliferation)]
    [InlineData(PlatformKind.IntensityComparison)]
    public void Univariate_population_visibility_does_not_exclude_analysis_input(PlatformKind kind)
    {
        var platform = PlatformCatalog.Get(kind).CreateModel();
        var hidden = new PlatformPopulationInput
        {
            IsPlatformDropped = true,
            IsSelected = false
        };
        platform.Populations.Add(hidden);

        Assert.Same(hidden, Assert.Single(PlatformInitializer.SelectedPopulationInputs(platform)));
    }

    [Fact]
    public void Univariate_population_toggle_only_filters_plot_and_preserves_fitted_state()
    {
        var workspace = new FlowWorkspace();
        var group = new FlowGroup { Name = "Group" };
        var first_sample = new FlowSample("First", [new ChannelDefinition(0, "Signal", "Signal", 100, 1)], new float[,] { { 2 }, { 4 }, { 6 } });
        var second_sample = new FlowSample("Second", [new ChannelDefinition(0, "Signal", "Signal", 100, 1)], new float[,] { { 8 }, { 10 }, { 12 } });
        group.Samples.Add(first_sample);
        group.Samples.Add(second_sample);
        workspace.Groups.Add(group);
        var platform = new IntensityComparisonPlatform();
        platform.Features.Add(new PlatformFeatureSelection { ChannelName = "Signal", IsSelected = true });
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group.Id,
            SampleId = first_sample.Id,
            GroupName = group.Name,
            SampleName = first_sample.Name,
            PopulationName = "All events",
            IsPopulation = false,
            IsPlatformDropped = true,
            IsSelected = false
        });
        platform.Populations.Add(new PlatformPopulationInput
        {
            GroupId = group.Id,
            SampleId = second_sample.Id,
            GroupName = group.Name,
            SampleName = second_sample.Name,
            PopulationName = "All events",
            IsPopulation = false,
            IsPlatformDropped = true,
            IsSelected = true
        });

        var editor = new IntensityComparisonPlatformEditorViewModel(workspace, platform);
        try
        {
            Assert.Equal(2, platform.RowMap.Sources.Count);
            Assert.Equal(6, platform.RowMap.Count);
            platform.Populations[0].IsSelected = true;
            var table = new PlatformResultTable { Key = "intensity_comparison", Columns = ["Sample"] };
            table.Rows.Add(["First"]);
            platform.ResultTables.Add(table);
            platform.FitCurves.Add(new PlatformFitCurve
            {
                Key = "fit_0",
                SourceId = 0,
                Role = PlatformSeriesRole.Fit,
                Kind = PlatformFitCurveKind.Gaussian,
                Parameters = [1, 4, 1]
            });
            platform.FitCurves.Add(new PlatformFitCurve
            {
                Key = "fit_1",
                SourceId = 1,
                Role = PlatformSeriesRole.Fit,
                Kind = PlatformFitCurveKind.Gaussian,
                Parameters = [1, 10, 1]
            });
            platform.Status = PlatformStatus.Complete;
            var compensated = platform.Compensated;
            var transformed = platform.Transformed;
            var source_ids = platform.RowMap.SourceIds;
            double minimum = platform.Axis.Minimum;
            double maximum = platform.Axis.Maximum;
            var observed_before_toggle = PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plots
                .SelectMany(plot => plot.Series)
                .Single(series => series.SourceId == 0 && series.Role == PlatformSeriesRole.Observed);
            var refreshed_properties = new System.Collections.Generic.HashSet<string?>();
            editor.PropertyChanged += (_, args) => refreshed_properties.Add(args.PropertyName);

            platform.Populations[0].IsSelected = false;

            Assert.Contains(nameof(PlatformEditorViewModel.PlotDocument), refreshed_properties);
            Assert.Contains(nameof(PlatformEditorViewModel.PlatformHistogramCurves), refreshed_properties);
            Assert.Equal(2, editor.PlatformHistogramCurves.Count);
            Assert.Same(compensated, platform.Compensated);
            Assert.Same(transformed, platform.Transformed);
            Assert.Same(source_ids, platform.RowMap.SourceIds);
            Assert.Same(table, Assert.Single(platform.ResultTables));
            Assert.Equal(2, platform.FitCurves.Count);
            Assert.Equal(PlatformStatus.Complete, platform.Status);
            Assert.Equal(minimum, platform.Axis.Minimum);
            Assert.Equal(maximum, platform.Axis.Maximum);
            var visible = Assert.Single(PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plots).Series;
            Assert.Equal(2, visible.Count);
            Assert.All(visible, series => Assert.Equal(1, series.SourceId));

            refreshed_properties.Clear();
            platform.Populations[0].IsSelected = true;
            Assert.Contains(nameof(PlatformEditorViewModel.PlotDocument), refreshed_properties);
            Assert.Contains(nameof(PlatformEditorViewModel.PlatformHistogramCurves), refreshed_properties);
            var observed_after_toggle = PlatformCatalog.Get(platform.Kind).CreatePresentation(platform).Plots
                .SelectMany(plot => plot.Series)
                .Single(series => series.SourceId == 0 && series.Role == PlatformSeriesRole.Observed);
            Assert.Same(observed_before_toggle, observed_after_toggle);
            Assert.Same(transformed, platform.Transformed);
            Assert.Same(table, Assert.Single(platform.ResultTables));
            Assert.Equal(PlatformStatus.Complete, platform.Status);
        }
        finally
        {
            editor.Dispose();
        }
    }

    [Fact]
    public void Histogram_linear_axis_uses_human_readable_ticks_within_display_range()
    {
        var axis = new AxisSettings
        {
            Minimum = 0,
            Maximum = 0.44,
            ScaleKind = CoordinateScaleKind.Linear
        };

        var ticks = Configuration.MajorAxisTicks(axis).ToArray();

        Assert.Equal(5, ticks.Length);
        Assert.Equal(0.0, ticks[0], 10);
        Assert.Equal(0.1, ticks[1], 10);
        Assert.Equal(0.2, ticks[2], 10);
        Assert.Equal(0.3, ticks[3], 10);
        Assert.Equal(0.4, ticks[4], 10);
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
        var integration = new IntegrationPlatform
        {
            Name = "Integration",
            BatchColumnName = "Batch",
            CytoNormOptions = new CytoNormOptions { QuantileCount = 77, Goal = CytoNormGoal.BatchMedian }
        };
        integration.Transformations["Ir191Di"] = new PlatformChannelTransformation
        {
            Kind = PlatformTransformationKind.Arcsinh,
            ArcsinhCofactor = 7.5,
            IsAutomatic = false
        };
        workspace.Platforms.Add(integration);
        workspace.Platforms.Add(new CellCyclePlatform
        {
            Name = "Cell cycle",
            Model = CellCycleModelKind.DeanJettFox,
            FillComponents = false,
            ArcsinhCofactor = 6.5
        });
        workspace.Platforms.Add(new ProliferationPlatform
        {
            Name = "Proliferation",
            MaxGenerations = 13,
            FillComponents = false,
            ArcsinhCofactor = 8.5
        });
        workspace.Platforms.Add(new IntensityComparisonPlatform
        {
            Name = "Comparison",
            ReferenceSample = "Sample - All events",
            DistributionBinning = 256,
            ArcsinhCofactor = 7.5
        });

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
                    var transformation = Assert.Single(value.Transformations).Value;
                    Assert.Equal(PlatformTransformationKind.Arcsinh, transformation.Kind);
                    Assert.Equal(7.5, transformation.ArcsinhCofactor);
                    Assert.False(transformation.IsAutomatic);
                },
                item =>
                {
                    var value = Assert.IsType<CellCyclePlatform>(item);
                    Assert.Equal(CellCycleModelKind.DeanJettFox, value.Model);
                    Assert.False(value.FillComponents);
                    Assert.Equal(6.5, value.ArcsinhCofactor);
                },
                item =>
                {
                    var value = Assert.IsType<ProliferationPlatform>(item);
                    Assert.Equal(13, value.MaxGenerations);
                    Assert.False(value.FillComponents);
                    Assert.Equal(8.5, value.ArcsinhCofactor);
                },
                item =>
                {
                    var value = Assert.IsType<IntensityComparisonPlatform>(item);
                    Assert.Equal("Sample - All events", value.ReferenceSample);
                    Assert.Equal(256, value.DistributionBinning);
                    Assert.Equal(7.5, value.ArcsinhCofactor);
                });
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

    private static (FlowWorkspace Workspace, FlowGroup Group, FlowSample Sample, PopulationResult Parent, PopulationResult Child) integration_workspace()
    {
        var raw = new float[,]
        {
            { 10 },
            { 20 },
            { 30 },
            { 40 }
        };
        var sample = new FlowSample(
            "Sample A",
            [new ChannelDefinition(0, "Signal", "Signal", 100, 1)],
            raw);
        var parent = new PopulationResult
        {
            Gate = new GateDefinition { Name = "Parent" },
            EventIndices = [0, 1],
            EventCount = 2
        };
        var child = new PopulationResult
        {
            Gate = new GateDefinition { Name = "Child" },
            EventIndices = [0],
            EventCount = 1
        };
        parent.Children.Add(child);
        sample.Populations.Add(parent);
        var group = new FlowGroup { Name = "Group A" };
        group.Samples.Add(sample);
        var workspace = new FlowWorkspace();
        workspace.Groups.Add(group);
        return (workspace, group, sample, parent, child);
    }
}
