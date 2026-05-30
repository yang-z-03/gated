using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using gated.Models;

namespace gated.Services;

public sealed class WorkspaceBinarySerializer
{
    private const uint magic = 0x44544731;
    private const int version = 1;

    public void Save(FlowWorkspace workspace, string file_path)
    {
        using var stream = new FileStream(file_path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(magic);
        writer.Write(version);
        write_string(writer, workspace.Name);
        writer.Write(workspace.Groups.Count);
        foreach (var group in workspace.Groups)
            write_group(writer, group);
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

        return workspace;
    }

    private static void write_group(BinaryWriter writer, FlowGroup group)
    {
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
        var group = new FlowGroup { Name = read_string(reader) };
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
            group.AddSample(read_sample(reader));

        group.RecalculateSamples();
        return group;
    }

    private static void write_sample(BinaryWriter writer, FlowSample sample)
    {
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
            writer.Write(embedding.Value.Length);
            foreach (float value in embedding.Value)
                writer.Write(value);
        }
    }

    private static FlowSample read_sample(BinaryReader reader)
    {
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

        var sample = new FlowSample(name, channels, raw_events);
        int embedding_count = reader.ReadInt32();
        for (int index = 0; index < embedding_count; index++)
        {
            string embedding_name = read_string(reader);
            int value_count = reader.ReadInt32();
            var values = new float[value_count];
            for (int value_index = 0; value_index < value_count; value_index++)
                values[value_index] = reader.ReadSingle();
            sample.Embeddings[embedding_name] = values;
        }

        return sample;
    }

    private static void write_gate(BinaryWriter writer, GateDefinition gate)
    {
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

        writer.Write(gate.Vertices.Count);
        foreach (var vertex in gate.Vertices)
        {
            writer.Write(vertex.X);
            writer.Write(vertex.Y);
        }

        write_statistics(writer, gate.Statistics);

        writer.Write(gate.Children.Count);
        foreach (var child in gate.Children)
            write_gate(writer, child);
    }

    private static GateDefinition read_gate(BinaryReader reader, GateDefinition? parent)
    {
        var gate = new GateDefinition
        {
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

        int vertex_count = reader.ReadInt32();
        for (int index = 0; index < vertex_count; index++)
            gate.Vertices.Add(new Point(reader.ReadDouble(), reader.ReadDouble()));

        read_statistics(reader, gate.Statistics);

        int child_count = reader.ReadInt32();
        for (int index = 0; index < child_count; index++)
            gate.Children.Add(read_gate(reader, gate));

        return gate;
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

    private static void write_statistics(BinaryWriter writer, IReadOnlyCollection<StatisticDefinition> statistics)
    {
        writer.Write(statistics.Count);
        foreach (var statistic in statistics)
        {
            writer.Write((int)statistic.Kind);
            write_string(writer, statistic.ChannelName);
        }
    }

    private static void read_statistics(BinaryReader reader, ICollection<StatisticDefinition> statistics)
    {
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            statistics.Add(new StatisticDefinition
            {
                Kind = (StatisticKind)reader.ReadInt32(),
                ChannelName = read_string(reader)
            });
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

    private static void write_string(BinaryWriter writer, string? value) =>
        writer.Write(value ?? "");

    private static string read_string(BinaryReader reader) =>
        reader.ReadString();
}
