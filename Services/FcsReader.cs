using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Runtime.InteropServices;
using gated.Models;

namespace gated.Services;

public sealed class FcsReader
{
    private enum DataType
    {
        UnsignedBinaryInteger,
        Float,
        Double
    }

    private enum ByteOrder
    {
        LittleEndian,
        BigEndian
    }

    public FlowSample Read(string file_path)
    {
        using var file_stream = new FileStream(file_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = parse_header(file_stream, 0);
        var text = parse_text(file_stream, (int)header["text_start"], (int)header["text_stop"]);

        int channel_count = int.Parse(text["par"]);
        int event_count = int.Parse(text["tot"]);
        int data_start = resolve_data_offset(text, header, "begindata", "data_start");
        int data_stop = resolve_data_offset(text, header, "enddata", "data_stop");
        var data_type = parse_data_type(text["datatype"]);
        var byte_order = parse_byte_order(text.TryGetValue("byteord", out string? order) ? order : "");
        var channels = extract_channels(text, channel_count);
        var values = read_data(file_stream, data_start, data_stop, channel_count, event_count, data_type, byte_order, text);
        string sample_name = Path.GetFileNameWithoutExtension(file_path);
        var sample = new FlowSample(sample_name, channels, values);
        if (try_parse_spillover(text, sample_name, out var spillover))
            sample.DefaultCompensation = spillover;
        return sample;
    }

    private static Dictionary<string, object> parse_header(FileStream file_stream, long offset)
    {
        var header = new Dictionary<string, object>
        {
            ["version"] = Encoding.ASCII.GetString(read_bytes(file_stream, offset, 3, 5)).Trim(),
            ["text_start"] = parse_header_int(file_stream, offset, 10, 17),
            ["text_stop"] = parse_header_int(file_stream, offset, 18, 25),
            ["data_start"] = parse_header_int(file_stream, offset, 26, 33),
            ["data_stop"] = parse_header_int(file_stream, offset, 34, 41),
            ["analysis_start"] = parse_header_int(file_stream, offset, 42, 49, -1),
            ["analysis_stop"] = parse_header_int(file_stream, offset, 50, 57, -1)
        };

        return header;
    }

    private static int parse_header_int(FileStream file_stream, long offset, int start, int stop, int fallback = 0)
    {
        string raw = Encoding.ASCII.GetString(read_bytes(file_stream, offset, start, stop)).Trim();
        return int.TryParse(raw, out int value) ? value : fallback;
    }

    private static byte[] read_bytes(FileStream file_stream, long offset, int start, int stop)
    {
        file_stream.Position = offset + start;
        var buffer = new byte[stop - start + 1];
        file_stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    private static Dictionary<string, string> parse_text(FileStream file_stream, int start, int stop)
    {
        var bytes = read_bytes(file_stream, 0, start, stop);
        string text = Encoding.UTF8.GetString(bytes);
        if (string.IsNullOrEmpty(text))
            return new Dictionary<string, string>();

        char delimiter = text[0];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = split_escaped(text[1..], delimiter);
        for (int index = 0; index + 1 < parts.Count; index += 2)
        {
            string key = parts[index].TrimStart('$').ToLowerInvariant();
            string value = parts[index + 1];
            if (!string.IsNullOrWhiteSpace(key))
                result[key] = value;
        }

        return result;
    }

    private static List<string> split_escaped(string text, char delimiter)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == delimiter)
            {
                if (index + 1 < text.Length && text[index + 1] == delimiter)
                {
                    builder.Append(delimiter);
                    index++;
                }
                else
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                }
            }
            else builder.Append(character);
        }

        if (builder.Length > 0)
            parts.Add(builder.ToString());

        return parts;
    }

    private static int resolve_data_offset(Dictionary<string, string> text, Dictionary<string, object> header, string text_key, string header_key)
    {
        if (text.TryGetValue(text_key, out string? text_value) && int.TryParse(text_value, out int parsed) && parsed > 0)
            return parsed;

        return Convert.ToInt32(header[header_key]);
    }

    private static DataType parse_data_type(string data_type) =>
        data_type.Trim().ToLowerInvariant() switch
        {
            "i" => DataType.UnsignedBinaryInteger,
            "f" => DataType.Float,
            "d" => DataType.Double,
            _ => throw new InvalidDataException($"Unsupported FCS data type '{data_type}'.")
        };

    private static ByteOrder parse_byte_order(string byte_order)
    {
        if (byte_order is "4,3,2,1" or "2,1")
            return ByteOrder.BigEndian;

        return ByteOrder.LittleEndian;
    }

    private static IReadOnlyList<ChannelDefinition> extract_channels(Dictionary<string, string> text, int channel_count)
    {
        var channels = new List<ChannelDefinition>();
        for (int channel = 1; channel <= channel_count; channel++)
        {
            string name = text.TryGetValue($"p{channel}n", out string? name_value) ? name_value : $"P{channel}";
            string label = text.TryGetValue($"p{channel}s", out string? label_value) ? label_value : "";
            float maximum = text.TryGetValue($"p{channel}r", out string? maximum_value) && float.TryParse(maximum_value, out float parsed_maximum)
                ? parsed_maximum
                : 262144.0f;
            float gain = text.TryGetValue($"p{channel}g", out string? gain_value) && float.TryParse(gain_value, out float parsed_gain)
                ? parsed_gain
                : 1.0f;

            channels.Add(new ChannelDefinition(channel - 1, name, label, maximum, gain));
        }

        return channels;
    }

    private static bool try_parse_spillover(Dictionary<string, string> text, string sample_name, out CompensationMatrix matrix)
    {
        matrix = new CompensationMatrix();
        text.TryGetValue("spillover", out string? spillover_text);
        if (string.IsNullOrWhiteSpace(spillover_text)) text.TryGetValue("spill", out spillover_text);
        if (string.IsNullOrWhiteSpace(spillover_text)) text.TryGetValue("comp", out spillover_text);
        if (string.IsNullOrWhiteSpace(spillover_text))
            return false;

        var parts = spillover_text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size <= 0)
            return false;
        if (parts.Length < 1 + size + size * size)
            return false;

        var channel_names = parts.Skip(1).Take(size).ToArray();
        var values = new float[size, size];
        int value_offset = 1 + size;
        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
        {
            string token = parts[value_offset + row * size + column];
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                return false;
            values[row, column] = value;
        }

        matrix = CompensationMatrix.Create($"Compensation matrix ({sample_name})", channel_names, values);
        return true;
    }

    private static float[,] read_data(
        FileStream file_stream,
        int data_start,
        int data_stop,
        int channel_count,
        int event_count,
        DataType data_type,
        ByteOrder byte_order,
        Dictionary<string, string> text)
    {
        file_stream.Position = data_start;
        var data = new float[event_count, channel_count];
        int expected_value_count = event_count * channel_count;

        if (data_type == DataType.UnsignedBinaryInteger)
        {
            int bit_width = int.Parse(text["p1b"]);
            int byte_width = bit_width / 8;
            var buffer = new byte[expected_value_count * byte_width];
            file_stream.ReadExactly(buffer, 0, buffer.Length);
            for (int index = 0; index < expected_value_count; index++)
                data[index / channel_count, index % channel_count] = read_integer(buffer, index * byte_width, byte_width, byte_order);
            return data;
        }

        int value_width = data_type == DataType.Float ? 4 : 8;
        int byte_count = expected_value_count * value_width;
        if (data_stop >= data_start && data_stop - data_start + 1 < byte_count)
            throw new InvalidDataException("FCS DATA segment is shorter than the declared event matrix.");

        if (data_type == DataType.Float && byte_order == ByteOrder.LittleEndian == BitConverter.IsLittleEndian)
        {
            read_float_matrix(file_stream, data);
            return data;
        }

        var bytes = new byte[byte_count];
        file_stream.ReadExactly(bytes, 0, bytes.Length);

        if (data_type == DataType.Double && byte_order == ByteOrder.LittleEndian == BitConverter.IsLittleEndian)
        {
            var doubles = MemoryMarshal.Cast<byte, double>(bytes);
            for (int index = 0; index < expected_value_count; index++)
                data[index / channel_count, index % channel_count] = Convert.ToSingle(doubles[index]);
            return data;
        }

        for (int index = 0; index < expected_value_count; index++)
        {
            int offset = index * value_width;
            data[index / channel_count, index % channel_count] = data_type == DataType.Float
                ? read_float(bytes, offset, byte_order)
                : Convert.ToSingle(read_double(bytes, offset, byte_order));
        }
        return data;
    }

    private static void read_float_matrix(FileStream file_stream, float[,] data)
    {
        if (data.Length == 0)
            return;

        var values = MemoryMarshal.CreateSpan(ref data[0, 0], data.Length);
        file_stream.ReadExactly(MemoryMarshal.AsBytes(values));
    }

    private static float read_integer(byte[] buffer, int offset, int byte_width, ByteOrder byte_order)
    {
        Span<byte> value_bytes = stackalloc byte[4];
        for (int index = 0; index < byte_width && index < 4; index++)
            value_bytes[index] = buffer[offset + index];
        if (byte_order == ByteOrder.BigEndian == BitConverter.IsLittleEndian)
            value_bytes[..byte_width].Reverse();

        return byte_width switch
        {
            1 => buffer[offset],
            2 => BitConverter.ToUInt16(value_bytes),
            4 => BitConverter.ToUInt32(value_bytes),
            _ => 0
        };
    }

    private static float read_float(byte[] buffer, int offset, ByteOrder byte_order)
    {
        Span<byte> value_bytes = stackalloc byte[4];
        buffer.AsSpan(offset, 4).CopyTo(value_bytes);
        if (byte_order == ByteOrder.BigEndian == BitConverter.IsLittleEndian)
            value_bytes.Reverse();
        return BitConverter.ToSingle(value_bytes);
    }

    private static double read_double(byte[] buffer, int offset, ByteOrder byte_order)
    {
        Span<byte> value_bytes = stackalloc byte[8];
        buffer.AsSpan(offset, 8).CopyTo(value_bytes);
        if (byte_order == ByteOrder.BigEndian == BitConverter.IsLittleEndian)
            value_bytes.Reverse();
        return BitConverter.ToDouble(value_bytes);
    }
}

public static class SampleFactory
{
    public static FlowGroup CreateDemoGroup()
    {
        var channels = new[]
        {
            new ChannelDefinition(0, "FSC-A", "", 262144, 1),
            new ChannelDefinition(1, "SSC-A", "", 262144, 1)
        };

        var group = new FlowGroup { Name = "Samples" };
        group.AddSample(CreateGeneratedSample("ct-1", channels, 56860, 11));
        group.AddSample(CreateGeneratedSample("ct-2", channels, 45732, 17));
        group.AddSample(CreateGeneratedSample("ct-3", channels, 48167, 23));

        var gate = new GateDefinition
        {
            Name = "Gate 1",
            Kind = GateKind.Polygon,
            XChannel = "FSC-A",
            YChannel = "SSC-A"
        };
        gate.Vertices.Add(new Avalonia.Point(12000, 8000));
        gate.Vertices.Add(new Avalonia.Point(28000, 135000));
        gate.Vertices.Add(new Avalonia.Point(90000, 170000));
        gate.Vertices.Add(new Avalonia.Point(130000, 160000));
        gate.Vertices.Add(new Avalonia.Point(150000, 55000));
        gate.Vertices.Add(new Avalonia.Point(80000, 22000));
        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = "FSC-A" });
        gate.Statistics.Add(new StatisticDefinition { Kind = StatisticKind.FrequencyOfParent, ChannelName = "FSC-A" });
        group.Gates.Add(gate);
        group.RecalculateSamples();
        return group;
    }

    public static FlowSample CreateGeneratedSample(string name, IReadOnlyList<ChannelDefinition> channels, int event_count, int seed)
    {
        var random = new Random(seed);
        var data = new float[event_count, channels.Count];
        for (int row = 0; row < event_count; row++)
        {
            double cloud = random.NextDouble();
            double radial = Math.Sqrt(-2 * Math.Log(Math.Max(0.0001, random.NextDouble())));
            double angle = random.NextDouble() * Math.PI * 2;
            double fsc = 25000 + radial * Math.Cos(angle) * 18000 + cloud * 80000;
            double ssc = 14000 + radial * Math.Sin(angle) * 23000 + cloud * 105000;
            data[row, 0] = Convert.ToSingle(Math.Clamp(fsc, 0, 262144));
            data[row, 1] = Convert.ToSingle(Math.Clamp(ssc, 0, 262144));
        }

        return new FlowSample(name, channels, data);
    }
}
