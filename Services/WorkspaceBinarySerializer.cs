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
    private const int version = 41;

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

        write_integration_jobs(writer, workspace);
        write_page_layouts(writer, workspace);
        write_recent_file_paths(writer, workspace);
        write_metadata_columns(writer, workspace);
    }

    public FlowWorkspace Load(string file_path)
    {
        try
        {
            return load(file_path, read_group_root_view: true);
        }
        catch (Exception exception) when (exception is not NotSupportedException)
        {
            try
            {
                return load(file_path, read_group_root_view: false);
            }
            catch
            {
                throw exception;
            }
        }
    }

    private static FlowWorkspace load(string file_path, bool read_group_root_view)
    {
        using var stream = new FileStream(file_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != magic)
            throw new InvalidDataException("The file is not a Gated workspace.");

        int file_version = reader.ReadInt32();
        if (file_version < 32 || file_version > version)
            throw new NotSupportedException($"Unsupported Gated workspace version: {file_version}.");

        var workspace = new FlowWorkspace { Name = read_string(reader) };
        int group_count = reader.ReadInt32();
        for (int index = 0; index < group_count; index++)
            workspace.Groups.Add(read_group(reader, read_group_root_view, file_version));

        read_integration_jobs(reader, workspace, file_version);
        read_page_layouts(reader, workspace, file_version);
        read_recent_file_paths(reader, workspace);
        read_metadata_columns(reader, workspace);

        return workspace;
    }

    private static void write_metadata_columns(BinaryWriter writer, FlowWorkspace workspace)
    {
        workspace.MetadataColumns["Group"] = MetadataColumnKind.String;
        workspace.MetadataColumns["Sample"] = MetadataColumnKind.String;
        workspace.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
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
        workspace.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
        sync_identity_metadata(workspace);
    }

    private static void sync_identity_metadata(FlowWorkspace workspace)
    {
        foreach (var group in workspace.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
            sample.Metadata[Configuration.CytometerMetadataKey] = Configuration.CytometerNameForSample(sample);
        }
    }

    private static void write_group(BinaryWriter writer, FlowGroup group)
    {
        writer.Write(group.Id.ToByteArray());
        write_string(writer, group.Name);
        write_statistics(writer, group.Statistics);
        write_gate_view_options(writer, group.RootViewOptions);
        writer.Write(group.SampleRootViewOptions.Count);
        foreach (var item in group.SampleRootViewOptions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            write_string(writer, item.Key);
            write_gate_view_options(writer, item.Value);
        }

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

    private static FlowGroup read_group(BinaryReader reader, bool read_root_view, int file_version)
    {
        Guid id = new Guid(reader.ReadBytes(16));
        var group = new FlowGroup { Id = id, Name = read_string(reader) };
        read_statistics(reader, group.Statistics, file_version);
        if (read_root_view)
        {
            group.RootViewOptions = read_gate_view_options(reader);
            if (file_version == 40)
            {
                var legacy_gate_root_view = read_gate_view_options(reader);
                if (!group.RootViewOptions.HasView && legacy_gate_root_view.HasView)
                    group.RootViewOptions = legacy_gate_root_view;
            }
            if (file_version >= 38)
            {
                int sample_root_view_count = reader.ReadInt32();
                for (int index = 0; index < sample_root_view_count; index++)
                    group.SampleRootViewOptions[read_string(reader)] = read_gate_view_options(reader);
            }
        }

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
            group.Gates.Add(read_gate(reader, parent: null, file_version));

        int sample_count = reader.ReadInt32();
        for (int index = 0; index < sample_count; index++)
            group.AddSample(read_sample(reader, group.Gates), recalculate: false);

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
        write_float_matrix_payload(writer, sample.RawEvents);

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
        var raw_events = read_float_matrix_payload(reader, row_count, column_count);

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

    private static void write_float_matrix_payload(BinaryWriter writer, float[,] values)
    {
        if (!BitConverter.IsLittleEndian)
        {
            for (int row = 0; row < values.GetLength(0); row++)
            for (int column = 0; column < values.GetLength(1); column++)
                writer.Write(values[row, column]);
            return;
        }

        var buffer = new byte[Buffer.ByteLength(values)];
        Buffer.BlockCopy(values, 0, buffer, 0, buffer.Length);
        writer.Write(buffer);
    }

    private static float[,] read_float_matrix_payload(BinaryReader reader, int row_count, int column_count)
    {
        var values = new float[row_count, column_count];
        if (!BitConverter.IsLittleEndian)
        {
            for (int row = 0; row < row_count; row++)
            for (int column = 0; column < column_count; column++)
                values[row, column] = reader.ReadSingle();
            return values;
        }

        var buffer = new byte[Buffer.ByteLength(values)];
        reader.BaseStream.ReadExactly(buffer);
        Buffer.BlockCopy(buffer, 0, values, 0, buffer.Length);
        return values;
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
        write_gate_plot_options(
            writer,
            gate.PreferredPlotMode,
            gate.PreferredShowOutlierPoints,
            gate.PreferredDrawLargeDots,
            gate.PreferredShowGridlines,
            gate.PreferredShowGateAnnotations,
            gate.PreferredShowGateAnnotationNames,
            gate.PreferredContourLevelCount,
            gate.PreferredDensitySmoothing);

        writer.Write(gate.SamplePreferredViews.Count);
        foreach (var item in gate.SamplePreferredViews.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            write_string(writer, item.Key);
            write_gate_view_options(writer, item.Value);
        }

        writer.Write(gate.PopulationNames.Count);
        foreach (var item in gate.PopulationNames.OrderBy(item => item.Key))
        {
            writer.Write((int)item.Key);
            write_string(writer, item.Value);
        }

        writer.Write(gate.PopulationPreferredViews.Count);
        foreach (var item in gate.PopulationPreferredViews.OrderBy(item => item.Key))
        {
            writer.Write((int)item.Key);
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

    private static GateDefinition read_gate(BinaryReader reader, GateDefinition? parent, int file_version)
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
        read_gate_plot_options(
            reader,
            value => gate.PreferredPlotMode = value,
            value => gate.PreferredShowOutlierPoints = value,
            value => gate.PreferredDrawLargeDots = value,
            value => gate.PreferredShowGridlines = value,
            value => gate.PreferredShowGateAnnotations = value,
            value => gate.PreferredShowGateAnnotationNames = value,
            value => gate.PreferredContourLevelCount = value,
            value => gate.PreferredDensitySmoothing = value);

        int sample_view_count = reader.ReadInt32();
        for (int index = 0; index < sample_view_count; index++)
            gate.SamplePreferredViews[read_string(reader)] = read_gate_view_options(reader);

        int population_name_count = reader.ReadInt32();
        for (int index = 0; index < population_name_count; index++)
        {
            var region = (PopulationRegion)reader.ReadInt32();
            gate.PopulationNames[region] = read_string(reader);
        }

        int population_view_count = reader.ReadInt32();
        for (int index = 0; index < population_view_count; index++)
        {
            var region = (PopulationRegion)reader.ReadInt32();
            gate.PopulationPreferredViews[region] = read_gate_view_options(reader);
        }

        int vertex_count = reader.ReadInt32();
        for (int index = 0; index < vertex_count; index++)
            gate.Vertices.Add(new Point(reader.ReadDouble(), reader.ReadDouble()));

        read_statistics(reader, gate.Statistics, file_version);
        gate.IsTreeExpanded = reader.ReadBoolean();
        if (reader.ReadBoolean())
            gate.BooleanFirstGateId = new Guid(reader.ReadBytes(16));
        gate.BooleanFirstRegion = (PopulationRegion)reader.ReadInt32();
        if (reader.ReadBoolean())
            gate.BooleanSecondGateId = new Guid(reader.ReadBytes(16));
        gate.BooleanSecondRegion = (PopulationRegion)reader.ReadInt32();

        int child_count = reader.ReadInt32();
        for (int index = 0; index < child_count; index++)
            gate.Children.Add(read_gate(reader, gate, file_version));

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
        write_gate_plot_options(
            writer,
            view.PlotMode,
            view.ShowOutlierPoints,
            view.DrawLargeDots,
            view.ShowGridlines,
            view.ShowGateAnnotations,
            view.ShowGateAnnotationNames,
            view.ContourLevelCount,
            view.DensitySmoothing);
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
        read_gate_plot_options(
            reader,
            value => view.PlotMode = value,
            value => view.ShowOutlierPoints = value,
            value => view.DrawLargeDots = value,
            value => view.ShowGridlines = value,
            value => view.ShowGateAnnotations = value,
            value => view.ShowGateAnnotationNames = value,
            value => view.ContourLevelCount = value,
            value => view.DensitySmoothing = value);
        return view;
    }

    private static void write_gate_plot_options(
        BinaryWriter writer,
        PlotMode plot_mode,
        bool show_outlier_points,
        bool draw_large_dots,
        bool show_gridlines,
        bool show_gate_annotations,
        bool show_gate_annotation_names,
        int contour_level_count,
        int density_smoothing)
    {
        writer.Write((int)plot_mode);
        writer.Write(show_outlier_points);
        writer.Write(draw_large_dots);
        writer.Write(show_gridlines);
        writer.Write(show_gate_annotations);
        writer.Write(show_gate_annotation_names);
        writer.Write(contour_level_count);
        writer.Write(density_smoothing);
    }

    private static void read_gate_plot_options(
        BinaryReader reader,
        Action<PlotMode> set_plot_mode,
        Action<bool> set_show_outlier_points,
        Action<bool> set_draw_large_dots,
        Action<bool> set_show_gridlines,
        Action<bool> set_show_gate_annotations,
        Action<bool> set_show_gate_annotation_names,
        Action<int> set_contour_level_count,
        Action<int> set_density_smoothing)
    {
        set_plot_mode((PlotMode)reader.ReadInt32());
        set_show_outlier_points(reader.ReadBoolean());
        set_draw_large_dots(reader.ReadBoolean());
        set_show_gridlines(reader.ReadBoolean());
        set_show_gate_annotations(reader.ReadBoolean());
        set_show_gate_annotation_names(reader.ReadBoolean());
        set_contour_level_count(reader.ReadInt32());
        set_density_smoothing(reader.ReadInt32());
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

    private static void read_page_layouts(BinaryReader reader, FlowWorkspace workspace, int file_version)
    {
        int layout_count = reader.ReadInt32();
        for (int index = 0; index < layout_count; index++)
        {
            var layout = new PageLayout { Name = read_string(reader) };
            read_page_elements(reader, workspace, layout, file_version);
        }
    }

    private static void write_integration_jobs(BinaryWriter writer, FlowWorkspace workspace)
    {
        writer.Write(workspace.IntegrationJobs.Count);
        foreach (var job in workspace.IntegrationJobs)
        {
            writer.Write(job.Id.ToByteArray());
            writer.Write((int)job.Kind);
            write_string(writer, job.Name);
            writer.Write((int)job.Status);
            write_string(writer, job.WarningText);
            writer.Write(job.CurrentStep);

            write_logicle_parameters(writer, job.Axis.Logicle);
            write_cytonorm_options(writer, job is IntegrationPlatform integration ? integration.CytoNormOptions : new CytoNormOptions());
            write_string(writer, job is IntegrationPlatform integration_job ? integration_job.BatchColumnName : "");
            write_platform_options(writer, job);

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
                writer.Write(selection.IsPlatformDropped);
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

            writer.Write(job.RowMap.Sources.Count);
            foreach (var source in job.RowMap.Sources)
            {
                writer.Write(source.GroupId.ToByteArray());
                writer.Write(source.SampleId.ToByteArray());
                writer.Write(source.GateId.ToByteArray());
                writer.Write((int)source.Region);
            }
            write_int_array(writer, job.RowMap.SourceIds);
            write_int_array(writer, job.RowMap.EventIndices);

            write_float_matrix(writer, job.Compensated);
            write_float_matrix(writer, job.Matrix);
            write_int_array(writer, job is IntegrationPlatform platform_integration ? platform_integration.BatchIds : []);
            write_float_matrix(writer, null);
            write_float_matrix(writer, job is MultivariatePlatform multivariate ? multivariate.Normalized : null);
            write_float_matrix(writer, job.Transformed);
            write_string(writer, "");
            write_platform_results(writer, job);
        }
    }

    private static void read_integration_jobs(BinaryReader reader, FlowWorkspace workspace, int file_version)
    {
        int job_count = reader.ReadInt32();
        for (int index = 0; index < job_count; index++)
        {
            var id = new Guid(reader.ReadBytes(16));
            var kind = (PlatformKind)reader.ReadInt32();
            var job = PlatformFactory.Create(kind);
            job.Id = id;
            job.Name = read_string(reader);
            job.Status = (IntegrationJobStatus)reader.ReadInt32();
            job.WarningText = read_string(reader);
            job.CurrentStep = reader.ReadInt32();
            job.Axis.Logicle = read_logicle_parameters(reader);
            var cytonorm_options = read_cytonorm_options(reader);
            string batch_column_name = read_string(reader);
            if (job is IntegrationPlatform integration)
            {
                integration.CytoNormOptions = cytonorm_options;
                integration.BatchColumnName = batch_column_name;
            }
            read_platform_options(reader, job, file_version);

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
                    IsPopulation = reader.ReadBoolean(),
                    IsPlatformDropped = reader.ReadBoolean()
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

            int source_count = reader.ReadInt32();
            var row_map_sources = new List<IntegrationJobRowMapSource>(source_count);
            for (int row = 0; row < source_count; row++)
            {
                row_map_sources.Add(new IntegrationJobRowMapSource
                {
                    GroupId = new Guid(reader.ReadBytes(16)),
                    SampleId = new Guid(reader.ReadBytes(16)),
                    GateId = new Guid(reader.ReadBytes(16)),
                    Region = (PopulationRegion)reader.ReadInt32()
                });
            }
            job.RowMap.Set(row_map_sources, read_int_array(reader) ?? [], read_int_array(reader) ?? []);

            job.Compensated = read_float_matrix(reader);
            job.Matrix = file_version >= 36 ? read_float_matrix(reader) : job.Compensated;
            var batch_ids = read_int_array(reader) ?? [];
            var legacy_logicle = read_float_matrix(reader);
            var normalized = read_float_matrix(reader);
            if (job is IntegrationPlatform loaded_integration)
                loaded_integration.BatchIds = batch_ids;
            if (job is MultivariatePlatform multivariate)
                multivariate.Normalized = normalized ?? legacy_logicle;
            job.Transformed = file_version >= 36 ? read_float_matrix(reader) : legacy_logicle ?? job.Compensated;
            _ = read_string(reader);
            read_platform_results(reader, job, file_version);
            if (file_version >= 35)
                workspace.IntegrationJobs.Add(job);
        }
    }

    private static void write_platform_options(BinaryWriter writer, Platform job)
    {
        writer.Write((int)job.Axis.Transform);
        writer.Write((int)(job is CellCyclePlatform cell_cycle ? cell_cycle.Model : CellCycleModelKind.WatsonPragmatic));
        writer.Write(job is not CellCyclePlatform cell_cycle_sum || cell_cycle_sum.DrawModelSum);
        writer.Write(job is not CellCyclePlatform cell_cycle_components || cell_cycle_components.DrawComponents);
        writer.Write(job is not CellCyclePlatform cell_cycle_fill || cell_cycle_fill.FillComponents);
        writer.Write(platform_smoothing(job).HalfWindow);
        writer.Write(platform_smoothing(job).Enabled);
        writer.Write(job is not ProliferationPlatform proliferation_sum || proliferation_sum.DrawModelSum);
        writer.Write(job is not ProliferationPlatform proliferation_components || proliferation_components.DrawComponents);
        writer.Write(job is ProliferationPlatform proliferation ? proliferation.MaxGenerations : 8);
        writer.Write(job is ProliferationPlatform proliferation_prominence ? proliferation_prominence.PeakProminence : 0.03);
        writer.Write((int)(job is KineticsPlatform kinetics ? kinetics.Fit : KineticsFitKind.Linear));
        writer.Write(job is KineticsPlatform kinetics_windows ? kinetics_windows.TimeWindowCount : 64);
        writer.Write(job is KineticsPlatform kinetics_change ? kinetics_change.ChangePointZ : 3.0);
        writer.Write(job is KineticsPlatform kinetics_segment ? kinetics_segment.MinSegmentWindows : 5);
        write_string(writer, job is IntensityComparisonPlatform comparison ? comparison.ReferenceSample : "");
        writer.Write(job.Axis.Minimum);
        writer.Write(job.Axis.Maximum);
        write_platform_parameters(writer, job);
    }

    private static PlatformSmoothingOptions platform_smoothing(Platform job) =>
        try_platform_smoothing(job) ?? new PlatformSmoothingOptions();

    private static PlatformSmoothingOptions? try_platform_smoothing(Platform job) =>
        job switch
        {
            UnivariatePlatform univariate => univariate.Smoothing,
            BivariatePlatform bivariate => bivariate.Smoothing,
            _ => null
        };

    private static void read_platform_options(BinaryReader reader, Platform job, int file_version)
    {
        job.Axis.Transform = (PlatformTransformationKind)reader.ReadInt32();
        var cell_cycle_model = (CellCycleModelKind)reader.ReadInt32();
        bool cell_cycle_draw_sum = reader.ReadBoolean();
        bool cell_cycle_draw_components = reader.ReadBoolean();
        bool cell_cycle_fill_components = reader.ReadBoolean();
        if (job is CellCyclePlatform cell_cycle)
        {
            cell_cycle.Model = cell_cycle_model;
            cell_cycle.DrawModelSum = cell_cycle_draw_sum;
            cell_cycle.DrawComponents = cell_cycle_draw_components;
            cell_cycle.FillComponents = cell_cycle_fill_components;
        }
        int smoothing_half_window = reader.ReadInt32();
        bool smoothing_enabled = reader.ReadBoolean();
        if (try_platform_smoothing(job) is { } smoothing)
        {
            smoothing.HalfWindow = smoothing_half_window;
            smoothing.Enabled = smoothing_enabled;
        }
        bool proliferation_draw_sum = reader.ReadBoolean();
        bool proliferation_draw_components = reader.ReadBoolean();
        int proliferation_max_generations = reader.ReadInt32();
        double proliferation_peak_prominence = reader.ReadDouble();
        if (job is ProliferationPlatform proliferation)
        {
            proliferation.DrawModelSum = proliferation_draw_sum;
            proliferation.DrawComponents = proliferation_draw_components;
            proliferation.MaxGenerations = proliferation_max_generations;
            proliferation.PeakProminence = proliferation_peak_prominence;
        }
        var kinetics_fit = (KineticsFitKind)reader.ReadInt32();
        int kinetics_windows = reader.ReadInt32();
        double kinetics_change = reader.ReadDouble();
        int kinetics_segment = reader.ReadInt32();
        if (job is KineticsPlatform kinetics)
        {
            kinetics.Fit = kinetics_fit;
            kinetics.TimeWindowCount = kinetics_windows;
            kinetics.ChangePointZ = kinetics_change;
            kinetics.MinSegmentWindows = kinetics_segment;
        }
        string reference_sample = read_string(reader);
        if (job is IntensityComparisonPlatform comparison)
            comparison.ReferenceSample = reference_sample;
        job.Axis.Minimum = reader.ReadDouble();
        job.Axis.Maximum = reader.ReadDouble();
        if (file_version >= 34)
            read_platform_parameters(reader, job);
    }

    private static void write_platform_parameters(BinaryWriter writer, Platform job)
    {
        writer.Write(job.Parameters.Count);
        foreach (var parameter in job.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
        {
            write_string(writer, parameter.Key);
            write_string(writer, Platform.ParameterToJson(parameter.Value));
        }
    }

    private static void read_platform_parameters(BinaryReader reader, Platform job)
    {
        job.Parameters.Clear();
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            string key = read_string(reader);
            string value = read_string(reader);
            job.Parameters[key] = file_version_parameter_is_json(value) ? Platform.ParameterFromJson(value) : value;
        }
    }

    private static bool file_version_parameter_is_json(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value[0] is '"' or '{' or '[' or 't' or 'f' or 'n' or '-' ||
        char.IsDigit(value[0]);

    private static void write_platform_results(BinaryWriter writer, Platform job)
    {
        writer.Write(job.ResultTables.Count);
        foreach (var table in job.ResultTables)
        {
            write_string(writer, table.Key);
            write_string(writer, table.Title);
            writer.Write(table.Columns.Length);
            foreach (string column in table.Columns)
                write_string(writer, column);
            writer.Write(table.Rows.Count);
            foreach (var row in table.Rows)
            {
                writer.Write(row.Length);
                foreach (string value in row)
                    write_string(writer, value);
            }
        }

        writer.Write(job.PlotSeries.Count);
        foreach (var series in job.PlotSeries)
        {
            write_string(writer, series.Key);
            write_string(writer, series.Title);
            write_string(writer, series.XLabel);
            write_string(writer, series.YLabel);
            write_double_array(writer, series.X);
            write_double_array(writer, series.Y);
        }

        writer.Write(job.FitCurves.Count);
        foreach (var curve in job.FitCurves)
        {
            write_string(writer, curve.Key);
            write_string(writer, curve.Title);
            write_string(writer, curve.XLabel);
            write_string(writer, curve.YLabel);
            writer.Write(curve.SourceId);
            writer.Write((int)curve.Kind);
            writer.Write((int)curve.FitTransformation);
            write_logicle_parameters(writer, curve.FitLogicle);
            writer.Write(curve.Normalizer);
            write_double_array(writer, curve.Parameters);
            write_string_array(writer, curve.ModelKeys);
            write_double_array(writer, curve.Weights);
            writer.Write(curve.Intercept);
        }

        writer.Write(job.PlatformStatistics.Count);
        foreach (var statistic in job.PlatformStatistics)
        {
            write_string(writer, statistic.Name);
            write_string(writer, statistic.Value);
        }
    }

    private static void read_platform_results(BinaryReader reader, Platform job, int file_version)
    {
        int table_count = reader.ReadInt32();
        for (int table_index = 0; table_index < table_count; table_index++)
        {
            var table = new PlatformResultTable
            {
                Key = read_string(reader),
                Title = read_string(reader),
                Columns = read_string_array(reader)
            };
            int row_count = reader.ReadInt32();
            for (int row_index = 0; row_index < row_count; row_index++)
                table.Rows.Add(read_string_array(reader));
            job.ResultTables.Add(table);
        }

        int series_count = reader.ReadInt32();
        for (int series_index = 0; series_index < series_count; series_index++)
        {
            job.PlotSeries.Add(new PlatformPlotSeries
            {
                Key = read_string(reader),
                Title = read_string(reader),
                XLabel = read_string(reader),
                YLabel = read_string(reader),
                X = read_double_array(reader) ?? [],
                Y = read_double_array(reader) ?? []
            });
        }

        int curve_count = reader.ReadInt32();
        for (int curve_index = 0; curve_index < curve_count; curve_index++)
        {
            var curve = new PlatformFitCurve
            {
                Key = read_string(reader),
                Title = read_string(reader),
                XLabel = read_string(reader),
                YLabel = read_string(reader),
                SourceId = reader.ReadInt32(),
                Kind = (PlatformFitCurveKind)reader.ReadInt32(),
                FitTransformation = (PlatformTransformationKind)reader.ReadInt32(),
                FitLogicle = read_logicle_parameters(reader),
                Normalizer = reader.ReadDouble(),
                Parameters = read_double_array(reader) ?? [],
                ModelKeys = file_version >= 36 ? read_string_array(reader) : [],
                Weights = file_version >= 36 ? read_double_array(reader) ?? [] : [],
                Intercept = file_version >= 36 ? reader.ReadDouble() : 0
            };
            job.FitCurves.Add(curve);
            if (curve.Key.Contains("component", StringComparison.OrdinalIgnoreCase) ||
                curve.Key.Contains("generation", StringComparison.OrdinalIgnoreCase))
            {
                if (!job.Components.TryGetValue(curve.Key, out var components))
                {
                    components = new List<PlatformFitCurve>();
                    job.Components[curve.Key] = components;
                }
                components.Add(curve);
            }
            else
            {
                job.Models[curve.Key] = curve;
            }
        }

        int statistic_count = reader.ReadInt32();
        for (int statistic_index = 0; statistic_index < statistic_count; statistic_index++)
        {
            job.PlatformStatistics.Add(new PlatformStatisticResult
            {
                Name = read_string(reader),
                Value = read_string(reader)
            });
        }
    }

    private static void write_page_elements(BinaryWriter writer, FlowWorkspace workspace, PageLayout layout)
    {
        var serializable = layout.Elements
            .Select(element => (Element: element, Reference: create_page_reference(workspace, element), PlatformIndex: platform_index(workspace, element)))
            .Where(item => item.Element.ElementKind != PageElementKind.FlowPlot || item.Reference is not null)
            .Where(item => item.Element.ElementKind is not (PageElementKind.PlatformPlot or PageElementKind.PlatformStatisticTable) || item.PlatformIndex >= 0)
            .ToArray();

        writer.Write(serializable.Length);
        foreach (var item in serializable)
        {
            var element = item.Element;
            writer.Write((int)element.ElementKind);
            writer.Write(element.Id.ToByteArray());
            writer.Write(element.ParentElementId.HasValue);
            if (element.ParentElementId.HasValue)
                writer.Write(element.ParentElementId.Value.ToByteArray());
            if (element.ElementKind == PageElementKind.FlowPlot)
            {
                var reference = item.Reference!.Value;
                writer.Write(reference.GroupIndex);
                writer.Write(reference.SampleIndex);
                writer.Write(reference.GatePath.Length);
                foreach (int gate_index in reference.GatePath)
                    writer.Write(gate_index);
                writer.Write(reference.HasPopulation);
                writer.Write((int)reference.PopulationRegion);
            }
            else if (element.ElementKind is PageElementKind.PlatformPlot or PageElementKind.PlatformStatisticTable)
            {
                writer.Write(item.PlatformIndex);
                write_string(writer, element is PlatformPlotElement platform_plot ? platform_plot.PlotKey : "");
            }
            else
            {
                writer.Write(0);
            }

            writer.Write(element.X);
            writer.Write(element.Y);
            writer.Write(element.Size);
            writer.Write(element.Width);
            writer.Write(element.Height);
            write_string(writer, element.Title);
            writer.Write((int)element.PlotMode);
            writer.Write(element.ShowGridlines);
            writer.Write(element.ShowOutlierPoints);
            writer.Write(element.DrawLargeDots);
            writer.Write(element.ShowTickLabels);
            writer.Write(element.UsePseudocolor);
            writer.Write(element.ShowGates);
            writer.Write(element.ShowGateAnnotations);
            writer.Write(element.ShowGateAnnotationNames);
            writer.Write(element.ContourLevelCount);
            writer.Write(element.DensitySmoothing);
            write_axis_settings(writer, element.XAxis);
            write_axis_settings(writer, element.YAxis);
            write_string(writer, element.DotColor.ChannelName);
            writer.Write((int)element.DotColor.Palette);
            writer.Write(element.DotColor.UseLogScale);
            if (element is StatisticTableElement statistic_table)
                write_statistic_table_columns(writer, workspace, statistic_table);
        }
    }

    private static void read_page_elements(BinaryReader reader, FlowWorkspace workspace, PageLayout layout, int file_version)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            var element_kind = (PageElementKind)reader.ReadInt32();
            var element_id = new Guid(reader.ReadBytes(16));
            Guid? parent_element_id = null;
            if (reader.ReadBoolean())
                parent_element_id = new Guid(reader.ReadBytes(16));

            int group_index = -1;
            int sample_index = -1;
            int[] gate_path = [];
            bool has_population = false;
            var population_region = PopulationRegion.Primary;
            int platform_index_value = -1;
            string platform_plot_key = "";
            if (element_kind == PageElementKind.FlowPlot)
            {
                group_index = reader.ReadInt32();
                sample_index = reader.ReadInt32();
                int path_length = reader.ReadInt32();
                gate_path = new int[path_length];
                for (int path_index = 0; path_index < path_length; path_index++)
                    gate_path[path_index] = reader.ReadInt32();
                has_population = reader.ReadBoolean();
                population_region = (PopulationRegion)reader.ReadInt32();
            }
            else if (element_kind is PageElementKind.PlatformPlot or PageElementKind.PlatformStatisticTable)
            {
                platform_index_value = reader.ReadInt32();
                platform_plot_key = read_string(reader);
            }
            else
            {
                _ = reader.ReadInt32();
            }

            double x = reader.ReadDouble();
            double y = reader.ReadDouble();
            double size = reader.ReadDouble();
            double width = file_version >= 33 ? reader.ReadDouble() : size;
            double height = file_version >= 33 ? reader.ReadDouble() : size;
            string title = read_string(reader);
            var plot_mode = (PlotMode)reader.ReadInt32();
            bool show_gridlines = reader.ReadBoolean();
            bool show_outlier_points = reader.ReadBoolean();
            bool draw_large_dots = reader.ReadBoolean();
            bool show_tick_labels = reader.ReadBoolean();
            bool use_pseudocolor = reader.ReadBoolean();
            bool show_gates = reader.ReadBoolean();
            bool show_gate_annotations = reader.ReadBoolean();
            bool show_gate_annotation_names = reader.ReadBoolean();
            int contour_level_count = reader.ReadInt32();
            int density_smoothing = reader.ReadInt32();
            var x_axis = read_axis_settings(reader);
            var y_axis = read_axis_settings(reader);
            string dot_color_channel = read_string(reader);
            var dot_color_palette = (PlotColorPalette)reader.ReadInt32();
            bool dot_color_use_log_scale = reader.ReadBoolean();

            PagePlotElement element;
            if (element_kind == PageElementKind.FlowPlot)
            {
                if (group_index < 0 || group_index >= workspace.Groups.Count)
                    continue;

                var group = workspace.Groups[group_index];
                var sample = sample_index >= 0 && sample_index < group.Samples.Count
                    ? group.Samples[sample_index]
                    : null;
                var gate = gate_path.Length == 0 ? null : resolve_gate_path(group, gate_path);
                if (gate_path.Length > 0 && gate is null)
                    continue;

                var population = gate is not null && has_population && sample is not null
                    ? find_population(sample.Populations, gate, population_region)
                    : null;
                element = new PagePlotElement
                {
                    Id = element_id,
                    Group = group,
                    Sample = sample,
                    Gate = gate,
                    Population = population,
                    UsesPopulation = has_population,
                    PopulationRegion = population_region,
                    XAxis = x_axis,
                    YAxis = y_axis,
                    DotColor = new DotColorSettings { ChannelName = dot_color_channel, Palette = dot_color_palette, UseLogScale = dot_color_use_log_scale }
                };
            }
            else if (element_kind == PageElementKind.PlatformPlot)
            {
                if (platform_index_value < 0 || platform_index_value >= workspace.IntegrationJobs.Count)
                    continue;
                element = new PlatformPlotElement
                {
                    Id = element_id,
                    ParentElementId = parent_element_id,
                    Platform = workspace.IntegrationJobs[platform_index_value],
                    PlotKey = platform_plot_key,
                    XAxis = x_axis,
                    YAxis = y_axis,
                    DotColor = new DotColorSettings { ChannelName = dot_color_channel, Palette = dot_color_palette, UseLogScale = dot_color_use_log_scale }
                };
            }
            else if (element_kind == PageElementKind.PlatformStatisticTable)
            {
                if (platform_index_value < 0 || platform_index_value >= workspace.IntegrationJobs.Count)
                    continue;
                element = new PlatformStatisticTableElement
                {
                    Id = element_id,
                    ParentElementId = parent_element_id,
                    Platform = workspace.IntegrationJobs[platform_index_value],
                    XAxis = x_axis,
                    YAxis = y_axis,
                    DotColor = new DotColorSettings { ChannelName = dot_color_channel, Palette = dot_color_palette, UseLogScale = dot_color_use_log_scale }
                };
            }
            else
            {
                element = new StatisticTableElement
                {
                    Id = element_id,
                    ParentElementId = parent_element_id,
                    XAxis = x_axis,
                    YAxis = y_axis,
                    DotColor = new DotColorSettings { ChannelName = dot_color_channel, Palette = dot_color_palette, UseLogScale = dot_color_use_log_scale }
                };
            }

            element.X = x;
            element.Y = y;
            element.Size = size;
            element.Width = width;
            element.Height = height;
            element.Title = title;
            element.PlotMode = plot_mode;
            element.ShowGridlines = show_gridlines;
            element.ShowOutlierPoints = show_outlier_points;
            element.DrawLargeDots = draw_large_dots;
            element.ShowTickLabels = show_tick_labels;
            element.UsePseudocolor = use_pseudocolor;
            element.ShowGates = show_gates;
            element.ShowGateAnnotations = show_gate_annotations;
            element.ShowGateAnnotationNames = show_gate_annotation_names;
            element.ContourLevelCount = contour_level_count;
            element.DensitySmoothing = density_smoothing;
            if (element is StatisticTableElement statistic_table)
                read_statistic_table_columns(reader, workspace, statistic_table);
            layout.Elements.Add(element);
        }
        workspace.PageLayouts.Add(layout);
    }

    private static void write_statistic_table_columns(BinaryWriter writer, FlowWorkspace workspace, StatisticTableElement table)
    {
        writer.Write(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            int group_index = column.Group is null ? -1 : workspace.Groups.IndexOf(column.Group);
            writer.Write(group_index);
            writer.Write(column.Gate is not null && column.Group is not null && try_create_gate_path(column.Group.Gates, column.Gate, [], out var gate_path));
            if (column.Gate is not null && column.Group is not null && try_create_gate_path(column.Group.Gates, column.Gate, [], out var path))
            {
                writer.Write(path.Length);
                foreach (int gate_index in path)
                    writer.Write(gate_index);
            }
            int statistic_index = -1;
            if (column.Statistic is not null)
            {
                if (column.Gate is not null)
                    statistic_index = column.Gate.Statistics.IndexOf(column.Statistic);
                else if (column.Group is not null)
                    statistic_index = column.Group.Statistics.IndexOf(column.Statistic);
            }
            writer.Write(statistic_index);
            write_string(writer, column.Title);
        }
    }

    private static void read_statistic_table_columns(BinaryReader reader, FlowWorkspace workspace, StatisticTableElement table)
    {
        int column_count = reader.ReadInt32();
        for (int index = 0; index < column_count; index++)
        {
            int group_index = reader.ReadInt32();
            bool has_gate = reader.ReadBoolean();
            int[] gate_path = [];
            if (has_gate)
            {
                int path_length = reader.ReadInt32();
                gate_path = new int[path_length];
                for (int path_index = 0; path_index < path_length; path_index++)
                    gate_path[path_index] = reader.ReadInt32();
            }
            int statistic_index = reader.ReadInt32();
            string title = read_string(reader);
            if (group_index < 0 || group_index >= workspace.Groups.Count)
                continue;
            var group = workspace.Groups[group_index];
            var gate = has_gate ? resolve_gate_path(group, gate_path) : null;
            var statistics = gate?.Statistics ?? group.Statistics;
            if (statistic_index < 0 || statistic_index >= statistics.Count)
                continue;
            table.Columns.Add(new StatisticTableColumn
            {
                Group = group,
                Gate = gate,
                Statistic = statistics[statistic_index],
                Title = title
            });
        }
    }

    private static int platform_index(FlowWorkspace workspace, PagePlotElement element) =>
        element switch
        {
            PlatformPlotElement plot => plot.Platform is null ? -1 : workspace.IntegrationJobs.IndexOf(plot.Platform),
            PlatformStatisticTableElement table => table.Platform is null ? -1 : workspace.IntegrationJobs.IndexOf(table.Platform),
            _ => -1
        };

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
        if (element.Group is null)
            return null;

        int group_index = workspace.Groups.IndexOf(element.Group);
        if (group_index < 0)
            return null;

        int[] gate_path = [];
        if (element.Gate is not null && !try_create_gate_path(element.Group.Gates, element.Gate, [], out gate_path))
            return null;

        int sample_index = element.Sample is null ? -1 : element.Group.Samples.IndexOf(element.Sample);
        if (element.Sample is not null && sample_index < 0)
            return null;

        return new PageElementReference(
            group_index,
            sample_index,
            gate_path,
            element.Gate is not null && element.UsesPopulation,
            element.UsesPopulation ? element.PopulationRegion : PopulationRegion.Primary);
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
            write_string(writer, statistic.DisplayName);
            if (statistic.Kind != StatisticKind.Python)
                continue;

            write_string(writer, statistic.PythonSource);
            write_string(writer, statistic.PythonCallableName);
            writer.Write(statistic.PythonApiVersion);
            write_string(writer, statistic.PythonDisplayName);
            write_string(writer, statistic.PythonParametersJson);
        }
    }

    private static void read_statistics(BinaryReader reader, ICollection<StatisticDefinition> statistics, int file_version)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            var statistic = new StatisticDefinition
            {
                Kind = (StatisticKind)reader.ReadInt32(),
                ChannelName = read_string(reader)
            };
            if (file_version >= 39)
                statistic.DisplayName = read_string(reader);
            if (statistic.Kind == StatisticKind.Python)
            {
                statistic.PythonSource = read_string(reader);
                statistic.PythonCallableName = read_string(reader);
                statistic.PythonApiVersion = reader.ReadInt32();
                statistic.PythonDisplayName = read_string(reader);
                statistic.PythonParametersJson = read_string(reader);
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
        write_float_matrix_payload(writer, matrix);
    }

    private static float[,]? read_float_matrix(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        return read_float_matrix_payload(reader, rows, columns);
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
        if (BitConverter.IsLittleEndian)
        {
            var buffer = new byte[Buffer.ByteLength(values)];
            reader.BaseStream.ReadExactly(buffer);
            Buffer.BlockCopy(buffer, 0, values, 0, buffer.Length);
            return values;
        }

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
        if (BitConverter.IsLittleEndian)
        {
            var buffer = new byte[Buffer.ByteLength(values)];
            reader.BaseStream.ReadExactly(buffer);
            Buffer.BlockCopy(buffer, 0, values, 0, buffer.Length);
            return values;
        }

        for (int index = 0; index < length; index++)
            values[index] = reader.ReadDouble();
        return values;
    }

    private static void write_string_array(BinaryWriter writer, string[]? values)
    {
        writer.Write(values?.Length ?? 0);
        if (values is null)
            return;
        foreach (string value in values)
            write_string(writer, value);
    }

    private static string[] read_string_array(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        var values = new string[length];
        for (int index = 0; index < length; index++)
            values[index] = read_string(reader);
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
