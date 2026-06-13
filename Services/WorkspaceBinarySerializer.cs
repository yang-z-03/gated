using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using gated.Models;
using gated.Reduction;

namespace gated.Services;

public sealed class WorkspaceBinarySerializer
{
    private const uint magic = 0x44544731;
    private const int version = 25;

    public void Save(FlowWorkspace workspace, string file_path)
    {
        sync_identity_metadata(workspace);
        using var stream = new FileStream(file_path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(magic);
        writer.Write(version);
        write_string(writer, workspace.Name);
        writer.Write(workspace.Groups.Count);
        foreach (var group in workspace.Groups)
            write_group(writer, group);

        write_page_layouts(writer, workspace);
        write_integration_jobs(writer, workspace);
        write_recent_file_paths(writer, workspace);
        write_metadata_columns(writer, workspace);
    }

    public FlowWorkspace Load(string file_path)
    {
        using var stream = new FileStream(file_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != magic)
            throw new InvalidDataException("The file is not a Gated workspace.");

        int file_version = reader.ReadInt32();
        if (file_version != version)
            throw new NotSupportedException($"Unsupported Gated workspace version: {file_version}.");

        var workspace = new FlowWorkspace { Name = read_string(reader) };
        int group_count = reader.ReadInt32();
        for (int index = 0; index < group_count; index++)
            workspace.Groups.Add(read_group(reader));

        read_page_layouts(reader, workspace);
        read_integration_jobs(reader, workspace);
        read_recent_file_paths(reader, workspace);
        read_metadata_columns(reader, workspace);

        return workspace;
    }

    private static void write_metadata_columns(BinaryWriter writer, FlowWorkspace workspace)
    {
        workspace.MetadataColumns["Group"] = MetadataColumnKind.String;
        workspace.MetadataColumns["Sample"] = MetadataColumnKind.String;
        writer.Write(workspace.MetadataColumns.Count);
        foreach (var column in workspace.MetadataColumns.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            write_string(writer, column.Key);
            writer.Write((int)column.Value);
        }
    }

    private static void read_metadata_columns(BinaryReader reader, FlowWorkspace workspace)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
            workspace.MetadataColumns[read_string(reader)] = (MetadataColumnKind)reader.ReadInt32();
        workspace.MetadataColumns["Group"] = MetadataColumnKind.String;
        workspace.MetadataColumns["Sample"] = MetadataColumnKind.String;
        sync_identity_metadata(workspace);
    }

    private static void sync_identity_metadata(FlowWorkspace workspace)
    {
        foreach (var group in workspace.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
        }
    }

    private static void write_group(BinaryWriter writer, FlowGroup group)
    {
        writer.Write(group.Id.ToByteArray());
        write_string(writer, group.Name);
        write_statistics(writer, group.Statistics);

        writer.Write(group.CompensationCandidates.Count);
        int applied_index = group.AppliedCompensation is null
            ? -1
            : group.CompensationCandidates.IndexOf(group.AppliedCompensation);
        writer.Write(applied_index);
        foreach (var compensation in group.CompensationCandidates)
            write_compensation(writer, compensation);

        writer.Write(group.Gates.Count);
        foreach (var gate in group.Gates)
            write_gate(writer, gate);

        writer.Write(group.Samples.Count);
        foreach (var sample in group.Samples)
            write_sample(writer, sample);
    }

    private static FlowGroup read_group(BinaryReader reader)
    {
        Guid id = new Guid(reader.ReadBytes(16));
        var group = new FlowGroup { Id = id, Name = read_string(reader) };
        read_statistics(reader, group.Statistics);

        int compensation_count = reader.ReadInt32();
        int applied_index = reader.ReadInt32();
        for (int index = 0; index < compensation_count; index++)
            group.CompensationCandidates.Add(read_compensation(reader));
        if (applied_index >= 0 && applied_index < group.CompensationCandidates.Count)
            group.SetAppliedCompensation(group.CompensationCandidates[applied_index], manual: true);
        else if (group.CompensationCandidates.Count > 0)
            group.SetAppliedCompensation(group.CompensationCandidates[0], manual: false);

        int gate_count = reader.ReadInt32();
        for (int index = 0; index < gate_count; index++)
            group.Gates.Add(read_gate(reader, parent: null));

        int sample_count = reader.ReadInt32();
        for (int index = 0; index < sample_count; index++)
            group.AddSample(read_sample(reader, group.Gates), recalculate: false);

        foreach (var sample in group.Samples)
            sample.ApplyCompensation(group.AppliedCompensation, force: true);
        return group;
    }

    private static void write_sample(BinaryWriter writer, FlowSample sample)
    {
        writer.Write(sample.Id.ToByteArray());
        write_string(writer, sample.Name);
        writer.Write(sample.Channels.Count);
        foreach (var channel in sample.Channels)
        {
            writer.Write(channel.Index);
            write_string(writer, channel.Name);
            write_string(writer, channel.Label);
            writer.Write(channel.Maximum);
            writer.Write(channel.Gain);
        }

        writer.Write(sample.EventCount);
        writer.Write(sample.ChannelCount);
        for (int row = 0; row < sample.EventCount; row++)
        for (int column = 0; column < sample.ChannelCount; column++)
            writer.Write(sample.RawEvents[row, column]);

        writer.Write(sample.Embeddings.Count);
        foreach (var embedding in sample.Embeddings.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            write_string(writer, embedding.Key);
            writer.Write((int)embedding.Value.Kind);
            writer.Write(embedding.Value.Values.Length);
            foreach (float value in embedding.Value.Values)
                writer.Write(value);

            writer.Write(embedding.Value.Categories.Count);
            foreach (var category in embedding.Value.Categories.OrderBy(item => item.Key))
            {
                writer.Write(category.Key);
                write_string(writer, category.Value);
            }
        }

        writer.Write(sample.Metadata.Count);
        foreach (var item in sample.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            write_string(writer, item.Key);
            write_string(writer, item.Value);
        }

        write_population_results(writer, sample.Populations);
    }

    private static FlowSample read_sample(BinaryReader reader, IReadOnlyList<GateDefinition> gates)
    {
        Guid id = new Guid(reader.ReadBytes(16));
        string name = read_string(reader);
        int channel_count = reader.ReadInt32();
        var channels = new List<ChannelDefinition>(channel_count);
        for (int index = 0; index < channel_count; index++)
        {
            channels.Add(new ChannelDefinition(
                reader.ReadInt32(),
                read_string(reader),
                read_string(reader),
                reader.ReadSingle(),
                reader.ReadSingle()));
        }

        int row_count = reader.ReadInt32();
        int column_count = reader.ReadInt32();
        var raw_events = new float[row_count, column_count];
        for (int row = 0; row < row_count; row++)
        for (int column = 0; column < column_count; column++)
            raw_events[row, column] = reader.ReadSingle();

        var sample = new FlowSample(name, channels, raw_events) { Id = id };
        int embedding_count = reader.ReadInt32();
        for (int index = 0; index < embedding_count; index++)
        {
            string embedding_name = read_string(reader);
            var kind = (EmbeddingValueKind)reader.ReadInt32();
            int value_count = reader.ReadInt32();
            var values = new float[value_count];
            for (int value_index = 0; value_index < value_count; value_index++)
                values[value_index] = reader.ReadSingle();
            var embedding = new EmbeddingData { Kind = kind, Values = values };
            int category_count = reader.ReadInt32();
            for (int category_index = 0; category_index < category_count; category_index++)
                embedding.Categories[reader.ReadInt32()] = read_string(reader);
            sample.Embeddings[embedding_name] = embedding;
        }

        int metadata_count = reader.ReadInt32();
        for (int index = 0; index < metadata_count; index++)
            sample.Metadata[read_string(reader)] = read_string(reader);

        read_population_results(reader, sample.Populations, gates);
        return sample;
    }

    private static void write_population_results(BinaryWriter writer, IReadOnlyCollection<PopulationResult> populations)
    {
        writer.Write(populations.Count);
        foreach (var population in populations)
            write_population_result(writer, population);
    }

    private static void write_population_result(BinaryWriter writer, PopulationResult population)
    {
        writer.Write(population.Gate.Id.ToByteArray());
        writer.Write((int)population.Region);
        write_int_array(writer, population.EventIndices);
        writer.Write(population.EventCount);

        writer.Write(population.Statistics.Count);
        foreach (var statistic in population.Statistics)
        {
            writer.Write((int)statistic.Kind);
            write_string(writer, statistic.ChannelName);
            writer.Write(statistic.Value);
            write_string(writer, statistic.PythonDisplayName);
        }

        write_population_results(writer, population.Children);
    }

    private static void read_population_results(
        BinaryReader reader,
        ICollection<PopulationResult> target,
        IReadOnlyList<GateDefinition> gates)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            var population = read_population_result(reader, gates);
            if (population is not null)
                target.Add(population);
        }
    }

    private static PopulationResult? read_population_result(BinaryReader reader, IReadOnlyList<GateDefinition> gates)
    {
        Guid gate_id = new Guid(reader.ReadBytes(16));
        var region = (PopulationRegion)reader.ReadInt32();
        var event_indices = read_int_array(reader) ?? [];
        int event_count = reader.ReadInt32();
        var gate = find_gate(gates, gate_id);

        int statistic_count = reader.ReadInt32();
        var statistics = new List<StatisticResult>(statistic_count);
        for (int index = 0; index < statistic_count; index++)
        {
            statistics.Add(new StatisticResult
            {
                Kind = (StatisticKind)reader.ReadInt32(),
                ChannelName = read_string(reader),
                Value = reader.ReadDouble(),
                PythonDisplayName = read_string(reader)
            });
        }

        var children = new List<PopulationResult>();
        read_population_results(reader, children, gates);
        if (gate is null)
            return null;

        var population = new PopulationResult
        {
            Gate = gate,
            Region = region,
            EventIndices = event_indices,
            EventCount = event_count
        };
        foreach (var statistic in statistics)
            population.Statistics.Add(statistic);
        foreach (var child in children)
            population.Children.Add(child);
        return population;
    }

    private static GateDefinition? find_gate(IEnumerable<GateDefinition> gates, Guid id)
    {
        foreach (var gate in gates)
        {
            if (gate.Id == id)
                return gate;
            var child = find_gate(gate.Children, id);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static void write_gate(BinaryWriter writer, GateDefinition gate)
    {
        writer.Write(gate.Id.ToByteArray());
        write_string(writer, gate.Name);
        writer.Write((int)gate.Kind);
        write_string(writer, gate.XChannel);
        writer.Write(gate.YChannel is not null);
        if (gate.YChannel is not null)
            write_string(writer, gate.YChannel);
        writer.Write((int)gate.ParentPopulationRegion);

        writer.Write(gate.XMinimum);
        writer.Write(gate.XMaximum);
        write_axis_scale(writer, gate.XScale);
        writer.Write(gate.YMinimum);
        writer.Write(gate.YMaximum);
        write_axis_scale(writer, gate.YScale);

        write_string(writer, gate.PreferredXChannel);
        writer.Write(gate.PreferredYChannel is not null);
        if (gate.PreferredYChannel is not null)
            write_string(writer, gate.PreferredYChannel);
        writer.Write(gate.PreferredXMinimum);
        writer.Write(gate.PreferredXMaximum);
        write_axis_scale(writer, gate.PreferredXScale);
        writer.Write(gate.PreferredYMinimum);
        writer.Write(gate.PreferredYMaximum);
        write_axis_scale(writer, gate.PreferredYScale);

        writer.Write(gate.SamplePreferredViews.Count);
        foreach (var item in gate.SamplePreferredViews.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            write_string(writer, item.Key);
            write_gate_view_options(writer, item.Value);
        }

        writer.Write(gate.Vertices.Count);
        foreach (var vertex in gate.Vertices)
        {
            writer.Write(vertex.X);
            writer.Write(vertex.Y);
        }

        write_statistics(writer, gate.Statistics);

        writer.Write(gate.IsTreeExpanded);
        writer.Write(gate.BooleanFirstGateId.HasValue);
        if (gate.BooleanFirstGateId.HasValue)
            writer.Write(gate.BooleanFirstGateId.Value.ToByteArray());
        writer.Write((int)gate.BooleanFirstRegion);
        writer.Write(gate.BooleanSecondGateId.HasValue);
        if (gate.BooleanSecondGateId.HasValue)
            writer.Write(gate.BooleanSecondGateId.Value.ToByteArray());
        writer.Write((int)gate.BooleanSecondRegion);

        writer.Write(gate.Children.Count);
        foreach (var child in gate.Children)
            write_gate(writer, child);
    }

    private static GateDefinition read_gate(BinaryReader reader, GateDefinition? parent)
    {
        Guid id = new Guid(reader.ReadBytes(16));
        var gate = new GateDefinition
        {
            Id = id,
            Name = read_string(reader),
            Kind = (GateKind)reader.ReadInt32(),
            XChannel = read_string(reader),
            Parent = parent
        };
        if (reader.ReadBoolean())
            gate.YChannel = read_string(reader);
        gate.ParentPopulationRegion = (PopulationRegion)reader.ReadInt32();

        gate.XMinimum = reader.ReadDouble();
        gate.XMaximum = reader.ReadDouble();
        gate.XScale = read_axis_scale(reader);
        gate.YMinimum = reader.ReadDouble();
        gate.YMaximum = reader.ReadDouble();
        gate.YScale = read_axis_scale(reader);

        gate.PreferredXChannel = read_string(reader);
        if (reader.ReadBoolean())
            gate.PreferredYChannel = read_string(reader);
        gate.PreferredXMinimum = reader.ReadDouble();
        gate.PreferredXMaximum = reader.ReadDouble();
        gate.PreferredXScale = read_axis_scale(reader);
        gate.PreferredYMinimum = reader.ReadDouble();
        gate.PreferredYMaximum = reader.ReadDouble();
        gate.PreferredYScale = read_axis_scale(reader);

        int sample_view_count = reader.ReadInt32();
        for (int index = 0; index < sample_view_count; index++)
            gate.SamplePreferredViews[read_string(reader)] = read_gate_view_options(reader);

        int vertex_count = reader.ReadInt32();
        for (int index = 0; index < vertex_count; index++)
            gate.Vertices.Add(new Point(reader.ReadDouble(), reader.ReadDouble()));

        read_statistics(reader, gate.Statistics);
        gate.IsTreeExpanded = reader.ReadBoolean();
        if (reader.ReadBoolean())
            gate.BooleanFirstGateId = new Guid(reader.ReadBytes(16));
        gate.BooleanFirstRegion = (PopulationRegion)reader.ReadInt32();
        if (reader.ReadBoolean())
            gate.BooleanSecondGateId = new Guid(reader.ReadBytes(16));
        gate.BooleanSecondRegion = (PopulationRegion)reader.ReadInt32();

        int child_count = reader.ReadInt32();
        for (int index = 0; index < child_count; index++)
            gate.Children.Add(read_gate(reader, gate));

        return gate;
    }

    private static void write_gate_view_options(BinaryWriter writer, GateViewOptions view)
    {
        write_string(writer, view.XChannel);
        writer.Write(view.YChannel is not null);
        if (view.YChannel is not null)
            write_string(writer, view.YChannel);
        writer.Write(view.XMinimum);
        writer.Write(view.XMaximum);
        write_axis_scale(writer, view.XScale);
        writer.Write(view.YMinimum);
        writer.Write(view.YMaximum);
        write_axis_scale(writer, view.YScale);
    }

    private static GateViewOptions read_gate_view_options(BinaryReader reader)
    {
        var view = new GateViewOptions
        {
            XChannel = read_string(reader)
        };
        if (reader.ReadBoolean())
            view.YChannel = read_string(reader);
        view.XMinimum = reader.ReadDouble();
        view.XMaximum = reader.ReadDouble();
        view.XScale = read_axis_scale(reader);
        view.YMinimum = reader.ReadDouble();
        view.YMaximum = reader.ReadDouble();
        view.YScale = read_axis_scale(reader);
        return view;
    }

    private static void write_compensation(BinaryWriter writer, CompensationMatrix compensation)
    {
        write_string(writer, compensation.Name);
        writer.Write(compensation.ChannelNames.Count);
        foreach (string channel_name in compensation.ChannelNames)
            write_string(writer, channel_name);

        int rows = compensation.Values.GetLength(0);
        int columns = compensation.Values.GetLength(1);
        writer.Write(rows);
        writer.Write(columns);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            writer.Write(compensation.Values[row, column]);
    }

    private static CompensationMatrix read_compensation(BinaryReader reader)
    {
        string name = read_string(reader);
        int channel_count = reader.ReadInt32();
        var channel_names = new string[channel_count];
        for (int index = 0; index < channel_count; index++)
            channel_names[index] = read_string(reader);

        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        var values = new float[rows, columns];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            values[row, column] = reader.ReadSingle();

        return CompensationMatrix.Create(name, channel_names, values);
    }

    private static void write_page_layouts(BinaryWriter writer, FlowWorkspace workspace)
    {
        writer.Write(workspace.PageLayouts.Count);
        foreach (var layout in workspace.PageLayouts)
        {
            write_string(writer, layout.Name);
            write_page_elements(writer, workspace, layout);
        }
    }

    private static void read_page_layouts(BinaryReader reader, FlowWorkspace workspace)
    {
        int layout_count = reader.ReadInt32();
        for (int index = 0; index < layout_count; index++)
        {
            var layout = new PageLayout { Name = read_string(reader) };
            read_page_elements(reader, workspace, layout);
        }
    }

    private static void write_integration_jobs(BinaryWriter writer, FlowWorkspace workspace)
    {
        writer.Write(workspace.IntegrationJobs.Count);
        foreach (var job in workspace.IntegrationJobs)
        {
            writer.Write(job.Id.ToByteArray());
            write_string(writer, job.Name);
            writer.Write((int)job.Status);
            write_string(writer, job.WarningText);
            writer.Write(job.CurrentStep);

            write_logicle_parameters(writer, job.Logicle);
            write_cytonorm_options(writer, job.CytoNormOptions);
            write_string(writer, job.BatchColumnName);

            writer.Write(job.Populations.Count);
            foreach (var selection in job.Populations)
            {
                writer.Write(selection.RowKey.ToByteArray());
                writer.Write(selection.ParentKey.HasValue);
                if (selection.ParentKey.HasValue)
                    writer.Write(selection.ParentKey.Value.ToByteArray());
                writer.Write(selection.GroupId.ToByteArray());
                writer.Write(selection.SampleId.ToByteArray());
                writer.Write(selection.GateId.ToByteArray());
                writer.Write((int)selection.Region);
                write_string(writer, selection.GroupName);
                write_string(writer, selection.SampleName);
                write_string(writer, selection.PopulationName);
                writer.Write(selection.IsSelected);
                writer.Write(selection.IsEnabled);
                writer.Write(selection.IsIndeterminate);
                writer.Write(selection.Depth);
                writer.Write(selection.HasChildren);
                writer.Write(selection.IsExpanded);
                writer.Write(selection.IsPopulation);
            }

            writer.Write(job.Features.Count);
            foreach (var feature in job.Features)
            {
                writer.Write(feature.RowKey.ToByteArray());
                writer.Write(feature.ParentKey.HasValue);
                if (feature.ParentKey.HasValue)
                    writer.Write(feature.ParentKey.Value.ToByteArray());
                write_string(writer, feature.ChannelName);
                write_string(writer, feature.Label);
                writer.Write(feature.IsSelected);
                writer.Write(feature.IsEnabled);
                writer.Write(feature.IsIndeterminate);
                writer.Write(feature.IsExpanded);
                write_string(writer, feature.GroupName);
                writer.Write(feature.Depth);
                writer.Write(feature.HasChildren);
                writer.Write(feature.IsChannel);
            }

            writer.Write(job.RowMap.Count);
            foreach (var row in job.RowMap)
            {
                writer.Write(row.GroupId.ToByteArray());
                writer.Write(row.SampleId.ToByteArray());
                writer.Write(row.GateId.ToByteArray());
                writer.Write((int)row.Region);
                writer.Write(row.EventIndex);
            }

            write_float_matrix(writer, job.SourceData);
            write_int_array(writer, job.BatchIds);
            write_float_matrix(writer, job.LogicleNormalized);
            write_float_matrix(writer, job.CytoNormNormalized);
        }
    }

    private static void read_integration_jobs(BinaryReader reader, FlowWorkspace workspace)
    {
        int job_count = reader.ReadInt32();
        for (int index = 0; index < job_count; index++)
        {
            var job = new IntegrationJob
            {
                Id = new Guid(reader.ReadBytes(16)),
                Name = read_string(reader),
                Status = (IntegrationJobStatus)reader.ReadInt32(),
                WarningText = read_string(reader),
                CurrentStep = reader.ReadInt32(),
                Logicle = read_logicle_parameters(reader),
                CytoNormOptions = read_cytonorm_options(reader)
            };
            job.BatchColumnName = read_string(reader);

            int population_count = reader.ReadInt32();
            for (int item = 0; item < population_count; item++)
            {
                job.Populations.Add(new IntegrationJobPopulationSelection
                {
                    RowKey = new Guid(reader.ReadBytes(16)),
                    ParentKey = reader.ReadBoolean() ? new Guid(reader.ReadBytes(16)) : null,
                    GroupId = new Guid(reader.ReadBytes(16)),
                    SampleId = new Guid(reader.ReadBytes(16)),
                    GateId = new Guid(reader.ReadBytes(16)),
                    Region = (PopulationRegion)reader.ReadInt32(),
                    GroupName = read_string(reader),
                    SampleName = read_string(reader),
                    PopulationName = read_string(reader),
                    IsSelected = reader.ReadBoolean(),
                    IsEnabled = reader.ReadBoolean(),
                    IsIndeterminate = reader.ReadBoolean(),
                    Depth = reader.ReadInt32(),
                    HasChildren = reader.ReadBoolean(),
                    IsExpanded = reader.ReadBoolean(),
                    IsPopulation = reader.ReadBoolean()
                });
            }

            int feature_count = reader.ReadInt32();
            for (int item = 0; item < feature_count; item++)
            {
                job.Features.Add(new IntegrationJobFeatureSelection
                {
                    RowKey = new Guid(reader.ReadBytes(16)),
                    ParentKey = reader.ReadBoolean() ? new Guid(reader.ReadBytes(16)) : null,
                    ChannelName = read_string(reader),
                    Label = read_string(reader),
                    IsSelected = reader.ReadBoolean(),
                    IsEnabled = reader.ReadBoolean(),
                    IsIndeterminate = reader.ReadBoolean(),
                    IsExpanded = reader.ReadBoolean(),
                    GroupName = read_string(reader),
                    Depth = reader.ReadInt32(),
                    HasChildren = reader.ReadBoolean(),
                    IsChannel = reader.ReadBoolean()
                });
            }

            int row_count = reader.ReadInt32();
            for (int row = 0; row < row_count; row++)
            {
                job.RowMap.Add(new IntegrationJobRowMap
                {
                    GroupId = new Guid(reader.ReadBytes(16)),
                    SampleId = new Guid(reader.ReadBytes(16)),
                    GateId = new Guid(reader.ReadBytes(16)),
                    Region = (PopulationRegion)reader.ReadInt32(),
                    EventIndex = reader.ReadInt32()
                });
            }

            job.SourceData = read_float_matrix(reader);
            job.BatchIds = read_int_array(reader) ?? [];
            job.LogicleNormalized = read_float_matrix(reader);
            job.CytoNormNormalized = read_float_matrix(reader);
            workspace.IntegrationJobs.Add(job);
        }
    }

    private static void write_page_elements(BinaryWriter writer, FlowWorkspace workspace, PageLayout layout)
    {
        var serializable = layout.Elements
            .Select(element => (Element: element, Reference: create_page_reference(workspace, element)))
            .Where(item => item.Reference is not null)
            .ToArray();

        writer.Write(serializable.Length);
        foreach (var item in serializable)
        {
            var element = item.Element;
            var reference = item.Reference!.Value;
            writer.Write(reference.GroupIndex);
            writer.Write(reference.SampleIndex);
            writer.Write(reference.GatePath.Length);
            foreach (int gate_index in reference.GatePath)
                writer.Write(gate_index);
            writer.Write(reference.HasPopulation);
            writer.Write((int)reference.PopulationRegion);

            writer.Write(element.X);
            writer.Write(element.Y);
            writer.Write(element.Size);
            write_string(writer, element.Title);
            writer.Write((int)element.PlotMode);
            writer.Write(element.ShowGridlines);
            writer.Write(element.ShowOutlierPoints);
            writer.Write(element.DrawLargeDots);
            writer.Write(element.ShowTickLabels);
            writer.Write(element.UsePseudocolor);
            writer.Write(element.ShowGates);
            writer.Write(element.ShowGateAnnotations);
            writer.Write(element.ContourLevelCount);
            writer.Write(element.DensitySmoothing);
            write_axis_settings(writer, element.XAxis);
            write_axis_settings(writer, element.YAxis);
            write_string(writer, element.DotColor.ChannelName);
            writer.Write((int)element.DotColor.Palette);
            writer.Write(element.DotColor.UseLogScale);
        }
    }

    private static void read_page_elements(BinaryReader reader, FlowWorkspace workspace, PageLayout layout)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            int group_index = reader.ReadInt32();
            int sample_index = reader.ReadInt32();
            int path_length = reader.ReadInt32();
            var gate_path = new int[path_length];
            for (int path_index = 0; path_index < path_length; path_index++)
                gate_path[path_index] = reader.ReadInt32();
            bool has_population = reader.ReadBoolean();
            var population_region = (PopulationRegion)reader.ReadInt32();

            double x = reader.ReadDouble();
            double y = reader.ReadDouble();
            double size = reader.ReadDouble();
            string title = read_string(reader);
            var plot_mode = (PlotMode)reader.ReadInt32();
            bool show_gridlines = reader.ReadBoolean();
            bool show_outlier_points = reader.ReadBoolean();
            bool draw_large_dots = reader.ReadBoolean();
            bool show_tick_labels = reader.ReadBoolean();
            bool use_pseudocolor = reader.ReadBoolean();
            bool show_gates = reader.ReadBoolean();
            bool show_gate_annotations = reader.ReadBoolean();
            int contour_level_count = reader.ReadInt32();
            int density_smoothing = reader.ReadInt32();
            var x_axis = read_axis_settings(reader);
            var y_axis = read_axis_settings(reader);
            string dot_color_channel = read_string(reader);
            var dot_color_palette = (PlotColorPalette)reader.ReadInt32();
            bool dot_color_use_log_scale = reader.ReadBoolean();

            if (group_index < 0 || group_index >= workspace.Groups.Count)
                continue;

            var group = workspace.Groups[group_index];
            var sample = sample_index >= 0 && sample_index < group.Samples.Count
                ? group.Samples[sample_index]
                : null;
            var gate = resolve_gate_path(group, gate_path);
            if (gate is null)
                continue;

            var population = has_population && sample is not null
                ? find_population(sample.Populations, gate, population_region)
                : null;

            layout.Elements.Add(new PagePlotElement
            {
                Group = group,
                Sample = sample,
                Gate = gate,
                Population = population,
                XAxis = x_axis,
                YAxis = y_axis,
                DotColor = new DotColorSettings { ChannelName = dot_color_channel, Palette = dot_color_palette, UseLogScale = dot_color_use_log_scale },
                X = x,
                Y = y,
                Size = size,
                Title = title,
                PlotMode = plot_mode,
                ShowGridlines = show_gridlines,
                ShowOutlierPoints = show_outlier_points,
                DrawLargeDots = draw_large_dots,
                ShowTickLabels = show_tick_labels,
                UsePseudocolor = use_pseudocolor,
                ShowGates = show_gates,
                ShowGateAnnotations = show_gate_annotations,
                ContourLevelCount = contour_level_count,
                DensitySmoothing = density_smoothing
            });
        }
        workspace.PageLayouts.Add(layout);
    }

    private static void write_recent_file_paths(BinaryWriter writer, FlowWorkspace workspace)
    {
        writer.Write(workspace.RecentFilePaths.Count);
        foreach (string path in workspace.RecentFilePaths)
            write_string(writer, path);
    }

    private static void read_recent_file_paths(BinaryReader reader, FlowWorkspace workspace)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            string path = read_string(reader);
            if (!string.IsNullOrWhiteSpace(path))
                workspace.RecentFilePaths.Add(path);
        }
    }

    private static PageElementReference? create_page_reference(FlowWorkspace workspace, PagePlotElement element)
    {
        if (element.Group is null || element.Gate is null)
            return null;

        int group_index = workspace.Groups.IndexOf(element.Group);
        if (group_index < 0)
            return null;

        if (!try_create_gate_path(element.Group.Gates, element.Gate, [], out var gate_path))
            return null;

        int sample_index = element.Sample is null ? -1 : element.Group.Samples.IndexOf(element.Sample);
        if (element.Sample is not null && sample_index < 0)
            return null;

        return new PageElementReference(
            group_index,
            sample_index,
            gate_path,
            element.Population is not null,
            element.Population?.Region ?? PopulationRegion.Primary);
    }

    private static bool try_create_gate_path(IReadOnlyList<GateDefinition> gates, GateDefinition target, int[] prefix, out int[] path)
    {
        for (int index = 0; index < gates.Count; index++)
        {
            var gate = gates[index];
            var current = prefix.Append(index).ToArray();
            if (ReferenceEquals(gate, target))
            {
                path = current;
                return true;
            }

            if (try_create_gate_path(gate.Children, target, current, out path))
                return true;
        }

        path = [];
        return false;
    }

    private static GateDefinition? resolve_gate_path(FlowGroup group, IReadOnlyList<int> path)
    {
        if (path.Count == 0)
            return null;

        IReadOnlyList<GateDefinition> gates = group.Gates;
        GateDefinition? gate = null;
        foreach (int index in path)
        {
            if (index < 0 || index >= gates.Count)
                return null;
            gate = gates[index];
            gates = gate.Children;
        }

        return gate;
    }

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, GateDefinition gate, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate && population.Region == region)
                return population;
            var child = find_population(population.Children, gate, region);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static void write_axis_settings(BinaryWriter writer, AxisSettings axis)
    {
        write_string(writer, axis.ChannelName);
        writer.Write(axis.Minimum);
        writer.Write(axis.Maximum);
        write_axis_scale(writer, axis.Scale);
    }

    private static AxisSettings read_axis_settings(BinaryReader reader) =>
        new()
        {
            ChannelName = read_string(reader),
            Minimum = reader.ReadDouble(),
            Maximum = reader.ReadDouble(),
            Scale = read_axis_scale(reader)
        };

    private static void write_statistics(BinaryWriter writer, IReadOnlyCollection<StatisticDefinition> statistics)
    {
        writer.Write(statistics.Count);
        foreach (var statistic in statistics)
        {
            writer.Write((int)statistic.Kind);
            write_string(writer, statistic.ChannelName);
            if (statistic.Kind != StatisticKind.Python)
                continue;

            write_string(writer, statistic.PythonSource);
            write_string(writer, statistic.PythonCallableName);
            writer.Write(statistic.PythonApiVersion);
            write_string(writer, statistic.PythonDisplayName);
            write_double_array(writer, statistic.PythonParameters);
        }
    }

    private static void read_statistics(BinaryReader reader, ICollection<StatisticDefinition> statistics)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            var statistic = new StatisticDefinition
            {
                Kind = (StatisticKind)reader.ReadInt32(),
                ChannelName = read_string(reader)
            };
            if (statistic.Kind == StatisticKind.Python)
            {
                statistic.PythonSource = read_string(reader);
                statistic.PythonCallableName = read_string(reader);
                statistic.PythonApiVersion = reader.ReadInt32();
                statistic.PythonDisplayName = read_string(reader);
                statistic.PythonParameters = read_double_array(reader) ?? [];
            }
            statistics.Add(statistic);
        }
    }

    private static void write_axis_scale(BinaryWriter writer, AxisScale scale)
    {
        writer.Write((int)scale.Kind);
        writer.Write(scale.Logicle.T);
        writer.Write(scale.Logicle.W);
        writer.Write(scale.Logicle.M);
        writer.Write(scale.Logicle.A);
    }

    private static AxisScale read_axis_scale(BinaryReader reader)
    {
        return new AxisScale
        {
            Kind = (CoordinateScaleKind)reader.ReadInt32(),
            Logicle = new LogicleParameters(
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadDouble())
        };
    }

    private static void write_logicle_parameters(BinaryWriter writer, LogicleParameters parameters)
    {
        writer.Write(parameters.T);
        writer.Write(parameters.W);
        writer.Write(parameters.M);
        writer.Write(parameters.A);
    }

    private static LogicleParameters read_logicle_parameters(BinaryReader reader) =>
        new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    private static void write_cytonorm_options(BinaryWriter writer, CytoNormOptions options)
    {
        writer.Write(options.QuantileCount);
        write_double_array(writer, options.Quantiles);
        writer.Write(options.MinimumCellsPerCluster);
        writer.Write((int)options.Goal);
        writer.Write(options.GoalBatch.HasValue);
        if (options.GoalBatch.HasValue)
            writer.Write(options.GoalBatch.Value);
        write_double_array(writer, options.Limits);
    }

    private static CytoNormOptions read_cytonorm_options(BinaryReader reader)
    {
        int quantile_count = reader.ReadInt32();
        var quantiles = read_double_array(reader);
        int minimum_cells = reader.ReadInt32();
        var goal = (CytoNormGoal)reader.ReadInt32();
        int? goal_batch = reader.ReadBoolean() ? reader.ReadInt32() : null;
        var limits = read_double_array(reader);
        return new CytoNormOptions
        {
            QuantileCount = quantile_count,
            Quantiles = quantiles,
            MinimumCellsPerCluster = minimum_cells,
            Goal = goal,
            GoalBatch = goal_batch,
            Limits = limits
        };
    }

    private static void write_float_matrix(BinaryWriter writer, float[,]? matrix)
    {
        writer.Write(matrix is not null);
        if (matrix is null)
            return;
        int rows = matrix.GetLength(0);
        int columns = matrix.GetLength(1);
        writer.Write(rows);
        writer.Write(columns);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            writer.Write(matrix[row, column]);
    }

    private static float[,]? read_float_matrix(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        var matrix = new float[rows, columns];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            matrix[row, column] = reader.ReadSingle();
        return matrix;
    }

    private static void write_int_array(BinaryWriter writer, int[]? values)
    {
        writer.Write(values is not null);
        if (values is null)
            return;
        writer.Write(values.Length);
        foreach (int value in values)
            writer.Write(value);
    }

    private static int[]? read_int_array(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        int length = reader.ReadInt32();
        var values = new int[length];
        for (int index = 0; index < length; index++)
            values[index] = reader.ReadInt32();
        return values;
    }

    private static void write_double_array(BinaryWriter writer, double[]? values)
    {
        writer.Write(values is not null);
        if (values is null)
            return;
        writer.Write(values.Length);
        foreach (double value in values)
            writer.Write(value);
    }

    private static double[]? read_double_array(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        int length = reader.ReadInt32();
        var values = new double[length];
        for (int index = 0; index < length; index++)
            values[index] = reader.ReadDouble();
        return values;
    }

    private static void write_string(BinaryWriter writer, string? value) =>
        writer.Write(value ?? "");

    private static string read_string(BinaryReader reader) =>
        reader.ReadString();

    private readonly record struct PageElementReference(
        int GroupIndex,
        int SampleIndex,
        int[] GatePath,
        bool HasPopulation,
        PopulationRegion PopulationRegion);
}
