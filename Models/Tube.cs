using System;
using System.Collections.ObjectModel;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using Gated.Configurations;
using Gated.Preprocessing;
using ScottPlot;

namespace Gated.Models;

public abstract class Population : INode
{
    public Population()
    {
        this.Subsets.CollectionChanged += (s, e) =>
        {
            if (e.OldItems != null)
            {
                foreach (var o in e.OldItems)
                    if (o is INode node)
                        if (this.children.Contains(node))
                            this.children.Remove(node);
            }
            
            if (e.NewItems != null)
            {
                foreach (var n in e.NewItems)
                    if (n is INode node)
                        if (!this.children.Contains(node))
                            this.children.Add(node);
            }
        };
    }
    
    public virtual string Identifier { get; set; } = "population";
    public abstract string Name { get; set; }
    public int ChannelCount { get; set; } = 0;
    public long EventCount { get; set; } = 0L;
    public Dictionary<int, Channel> Channels { get; set; } = new();
    public Dictionary<int, Embedding> Embeddings { get; set; } = new();
    
    public Population? Parent { get; set; } = null;
    public Tube? ParentTube { get; set; } = null;
    public Grouping? ParentGroup { get; set; } = null;
    public abstract bool IsTube { get; }
    public GatingStrategy? AssociatedGate { get; set; } = null;
    public int AssociatedGateIndex { get; set; } = 0;
    public ObservableCollection<Subset> Subsets { get; private set; } = new();
    
    public bool IsExpanded { get; set; } = false;
    private ObservableCollection<INode> children = new();
    public ObservableCollection<INode> Children
    {
        get { return children; }
    }

    public abstract Dictionary<Dimension, float[]?> GetValues(long max, params Dimension[] dimensions);
    public abstract float[]? GetValues(Dimension dimension, long[] indices);
    public Compensation? Compensation { get; set; } = null;

    public virtual void AddGate(GatingStrategy gate)
    {
        gate.AddPopulation(this);
    }

    internal static void set_ticks(IAxis axis, ITransform transform, float max)
    {
        if (transform is LinearTransform)
        {
            var origin = transform.InverseTransform(max);
            double[] thr =
            [
                50, 100, 150, 200, 250, 300, 400, 500, 600, 800,
                1000, 1500, 2000, 2500, 3000, 4000, 5000, 6000, 8000,
                10000, 15000, 20000, 25000, 30000, 40000, 50000, 60000, 80000,
                100000, 150000, 200000, 250000, 300000, 400000, 500000, 600000, 800000,
                1000000, 1500000, 2000000, 2500000, 3000000, 4000000, 5000000, 6000000, 8000000,
            ];

            string[] names =
            [
                "50", "100", "150", "200", "250", "300", "400", "500", "600", "800",
                "1000", "1500", "2000", "2500", "3k", "4k", "5k", "6k", "8k",
                "10k", "15k", "20k", "25k", "30k", "40k", "50k", "60k", "80k",
                "100k", "150k", "200k", "250k", "300k", "400k", "500k", "600k", "800k",
                "1M", "1.5M", "2M", "2.5M", "3M", "4M", "5M", "6M", "8M"
            ];

            int m = 0;
            for (int i = 0; i < thr.Length; i++)
            {
                if (max <= thr[m]) break;
                m += 1;
            }

            int from = Math.Max(0, m - 8);
            int to = Math.Min(m, thr.Length - 1);

            double[] ticks = new double[to - from + 1];
            string[] labels = new string[to - from + 1];
            for (int i = from; i <= to; i++)
            {
                ticks[i - from] = thr[i];
                labels[i - from] = names[i];
            }

            transform.Transform(ticks);
            axis.SetTicks(ticks, labels);
        }
        else if (transform is LogicleTransform)
        {
            double[] ticks = [
                2e2, 3e2, 4e2, 5e2, 6e2, 7e2, 8e2, 9e2, 1e3, 
                2e3, 3e3, 4e3, 5e3, 6e3, 7e3, 8e3, 9e3, 1e4, 
                2e4, 3e4, 4e4, 5e4, 6e4, 7e4, 8e4, 9e4, 1e5, 
                2e5, 3e5, 4e5, 5e5, 6e5, 7e5, 8e5, 9e5, 1e6, 
                2e6, 3e6, 4e6, 5e6, 6e6, 7e6, 8e6, 9e6, 1e7, 
                2e7, 3e7, 4e7, 5e7, 6e7, 7e7, 8e7, 9e7, 1e8];
            transform.Transform(ticks);
            axis.SetTicks(ticks, [
                "", "", "", "", "", "", "", "", "1k", 
                "", "", "", "", "", "", "", "", "10k", 
                "", "", "", "", "", "", "", "", "100k", 
                "", "", "", "", "", "", "", "", "1M", 
                "", "", "", "", "", "", "", "", "10M", 
                "", "", "", "", "", "", "", "", "100M"
            ]);
        }
        else axis.SetTicks(new double[]{}, new string[] {});
    }

    internal static double[,] order(int[,] hist, int size)
    {
        List<int> values = new List<int>();
        for (int i = 0; i < size; i++)
        for (int j = 0; j < size; j++)
            if (!values.Contains((hist[i, j])))
                values.Add(hist[i, j]);
        values.Sort();

        double[,] order = new double[size, size];
        for (int i = 0; i < size; i++)
        for (int j = 0; j < size; j++)
            order[i, j] = values.IndexOf(hist[i, j]);
        return order;
    }

    public ScatterConfig Display(
        IPlotControl plot, Dimension x, Dimension y, 
        ScatterConfig? config = null)
    {
        if (config == null)
            if (this.ParentGroup!.ScatterConfigs.ContainsKey(x))
                if (this.ParentGroup!.ScatterConfigs[x].ContainsKey(y))
                    config = this.ParentGroup!.ScatterConfigs[x][y];
        
        if (config == null)
            config = new ScatterConfig(x, y);

        config.X = x;
        config.Y = y;
        
        plot.Plot.Clear();
        var dict = this.GetValues(config.MaxDisplay, x!, y!);
        var xs = dict[x!]!;
        var ys = dict[y!]!;
        config.XTransform.Transform(xs);
        config.YTransform.Transform(ys);
        
        // axis limits
        // initialize range using the maximal values within the group!
        if ((!config.has_initialize_range()) || config.require_range_update)
        {
            List<float> xvals = new();
            List<float> yvals = new();
            foreach (var sample in this.ParentGroup!.Samples)
            {
                var data = sample.GetValues(sample.EventCount, x!, y!);
                xvals.AddRange(data[x!]!);
                yvals.AddRange(data[y!]!);
            }

            var xv = xvals.ToArray();
            var yv = yvals.ToArray();
            config.XTransform.Transform(xv);
            config.YTransform.Transform(yv);
            config.initialize_range(xv, yv);
        }

        config.require_range_update = false;
        
        plot.Plot.Axes.SetLimitsX(config.XRange.Item1, config.XRange.Item2);
        plot.Plot.Axes.SetLimitsY(config.YRange.Item1, config.YRange.Item2);
        
        // hide axis edge line
        plot.Plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Plot.Axes.Top.FrameLineStyle.Width = 0;
        
        // set ticks.
        // reverse transform to origin scale.
        set_ticks(plot.Plot.Axes.Left, config.YTransform, config.YRange.Item2);
        set_ticks(plot.Plot.Axes.Bottom, config.XTransform, config.XRange.Item2);
        plot.Plot.Axes.Bottom.TickLabelStyle.Rotation = -90;
        plot.Plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;
        plot.Plot.Axes.Bottom.MinimumSize = 45;
        plot.Plot.Axes.Left.MinimumSize = 45;
        
        if (config.Type == PlotType.Density)
        {
            var dictDens = this.GetValues(config.DensityEstimate, x!, y!);
            var densx = dictDens[x!]!;
            var densy = dictDens[y!]!;

            GaussianKDE kde = new(densx, densy);
            double[] density = new double[xs.Length];
            for (int j = 0; j < xs.Length; j++)
                density[j] = kde.Estimate(xs[j], ys[j]);

            double minDensity = density.Min();
            double maxDensity = density.Max();
            double spanDensity = maxDensity - minDensity;
            var colormap = new ScottPlot.Colormaps.Turbo();
            for (int j = 0; j < xs.Length; j++)
            {
                double fraction = (density[j] - minDensity) / spanDensity;
                var marker = plot.Plot.Add.Marker(xs[j], ys[j]);
                marker.Color = colormap.GetColor(fraction).WithAlpha(.8);
                marker.Size = 2;
            }
        }
        else if (config.Type == PlotType.Scatter)
        {
            var scatter = plot.Plot.Add.Markers(xs, ys);
            scatter.MarkerSize = 2;
            scatter.MarkerColor = Color.FromHex("#00000070");
        }
        else if (config.Type == PlotType.Heatmap)
        {
            int[,] histogram = new int[config.Resolution, config.Resolution];
            float xstep = (config.XRange.Item2 - config.XRange.Item1) / config.Resolution;
            float ystep = (config.YRange.Item2 - config.YRange.Item1) / config.Resolution;
            for (int i = 0; i < xs.Length; i++)
            {
                histogram[
                    config.Resolution - Math.Max(1, Math.Min(config.Resolution, Convert.ToInt32((ys[i] - config.YRange.Item1) / ystep))),
                    Math.Max(0, Math.Min(config.Resolution - 1, Convert.ToInt32((xs[i] - config.XRange.Item1) / xstep)))
                ]++;
            }
            
            double[,] o = Tube.order(histogram, config.Resolution);
            var hm1 = plot.Plot.Add.Heatmap(o);
            hm1.Colormap = new Configurations.Turbo();
            hm1.CellAlignment = Alignment.LowerLeft;
            hm1.CellWidth = xstep;
            hm1.CellHeight = ystep;
        }
        
        // plot gates at this level.
        if (this.AssociatedGate == null)
            this.ParentGroup!.Gates.Display(plot, x, y, config.XTransform, config.YTransform);
        else this.AssociatedGate.Subsets.Display(plot, x, y, config.XTransform, config.YTransform);

        plot.Refresh();

        if (!this.ParentGroup!.ScatterConfigs.ContainsKey(x))
        {
            var conf = new Dictionary<Dimension, ScatterConfig>();
            conf.Add(y, config);
            this.ParentGroup!.ScatterConfigs.Add(x, conf);
        }
        else
        {
            if (!this.ParentGroup!.ScatterConfigs[x].ContainsKey(y))
                this.ParentGroup!.ScatterConfigs[x].Add(y, config);
            else this.ParentGroup!.ScatterConfigs[x][y] = config;
        }

        return config;
    }
    
    public Channel? GetChannelByName(string channelName)
    {
        foreach (var channel in this.Channels)  
            if (channel.Value.Name == channelName) return channel.Value;
        return null;
    }
    
    public Channel? GetChannelByIndex(int channelId)
    {
        foreach (var channel in this.Channels)  
            if (channel.Value.Index == channelId) return channel.Value;
        return null;
    }

    public Dimension GetDefaultX() => GetChannelByName("FSC-A") ?? GetChannelByIndex(0)!;
    public Dimension GetDefaultY() => GetChannelByName("SSC-A") ?? GetChannelByIndex(1)!;
}

public class Tube : Population
{
    public override bool IsTube { get; } = true;
    public override string Identifier => "tube";

    public enum Specification
    {
        Fcs2   = 20,
        Fcs30  = 30,
        Fcs31  = 31,
        Fcs32  = 32
    }

    public enum StoreMode
    {
        List,
        UnivariateHistogram,
        CorrelatedHistogram
    }

    public enum DataType
    {
        UnsignedBinaryInteger,
        AsciiEncodedInteger,
        Float,
        Double
    }

    public enum ByteOrder
    {
        BigEndian,
        LittleEndian
    }

    // implement the FCS parser

    public DataType Type { get; private set; }
    public StoreMode Mode { get; private set; }
    public ByteOrder Order { get; private set; }
    public long Size { get; private set; }
    
    // the general header dictionary. conserved fields defined by the specification
    // is extracted and represent other fields in this class.
    public Dictionary<string, object> Header { get; private set; }
    public override string Name { get; set; }
    public Dictionary<string, string> Text { get; private set; }
    public Specification Version { get; private set; }
    public string Location { get; private set; }
    
    public Dictionary<Dimension, float[]> Measurements { get; internal set; } = new();

    private FileStream file_stream;
    private bool ignore_offset;
    private List<double> events;

    public Tube(
        string fcsFile,
        Grouping grouping,
        bool ignoreOffsetError = false,
        bool ignoreOffsetDiscrepancy = false,
        bool useHeaderOffsets = false,
        bool metadataOnly = false,
        int? nextDataOffset = null
    ) {
        this.ignore_offset = ignoreOffsetError;
        this.ParentTube = this;
        this.Parent = null;
        this.ParentGroup = grouping;

        this.Name = Path.GetFileNameWithoutExtension(fcsFile);
        this.Location = fcsFile;
        this.file_stream = new FileStream(fcsFile, FileMode.Open, FileAccess.Read);
        long currentOffset = nextDataOffset ?? 0;

        // get file size
        this.Size = this.file_stream.Length;
        this.file_stream.Position = currentOffset;

        this.Header = this.parse_header(currentOffset);
        switch (this.Header["version"])
        {
            case "2.0": this.Version = Version = Specification.Fcs2; break;
            case "3.0": this.Version = Version = Specification.Fcs30; break;
            case "3.1": this.Version = Version = Specification.Fcs31; break;
            case "3.2": this.Version = Version = Specification.Fcs32; break;
            default: throw new UnsupportedVersionException(
                $"Support for {this.Header["version"]} specifcation of FCS file is not implemented"
            );
        }

        // text section
        this.Text = this.parse_text(
            currentOffset,
            (int)Header["text_start"],
            (int)Header["text_stop"]
        );

        if (Text.ContainsKey("nextdata") && int.Parse(Text["nextdata"]) != 0 && nextDataOffset == null)
        {
            file_stream.Close();
            throw new MultipleDataSetsException(
                $"{this.Name} contains multiple data sets, use read_multiple_data_sets function"
            );
        }

        this.ChannelCount = int.Parse(Text["par"]);
        this.EventCount = int.Parse(Text["tot"]);
        switch (this.Text["datatype"].ToLower())
        {
            case "i": this.Type = DataType.UnsignedBinaryInteger; break;
            case "f": this.Type = DataType.Float; break;
            case "d": this.Type = DataType.Double; break;
            case "a": this.Type = DataType.AsciiEncodedInteger; break;
            default: throw new ParserException( 
                $"Illegal {this.Text["datatype"]} data type");
        }
        
        switch (this.Text["mode"].ToLower())
        {
            case "c": this.Mode = StoreMode.CorrelatedHistogram; break;
            case "u": this.Mode = StoreMode.UnivariateHistogram; break;
            case "l": this.Mode = StoreMode.List; break;
            default: throw new ParserException( 
                $"Illegal {this.Text["mode"]} mode");
        }

        var byteOrder = this.Text["byteord"];
        if (byteOrder == "1,2,3,4" || byteOrder == "1,2")
            this.Order = ByteOrder.LittleEndian;
        else if (byteOrder == "4,3,2,1" || byteOrder == "2,1")
            this.Order = ByteOrder.BigEndian;
        else
        {
            // default to system endianness
            this.Order = BitConverter.IsLittleEndian ? ByteOrder.LittleEndian : ByteOrder.BigEndian;
        }

        // determine data offsets
        int headerDataStart = (int)Header["data_start"];
        int headerDataStop = (int)Header["data_stop"];
        int dataStart, dataStop;

        if (this.Version == Specification.Fcs2)
        {
            dataStart = headerDataStart;
            dataStop = headerDataStop;
        }
        else
        {
            if (useHeaderOffsets)
            {
                dataStart = headerDataStart;
                dataStop = headerDataStop;
            }
            else
            {
                dataStart = int.Parse(Text["begindata"]);
                dataStop = int.Parse(Text["enddata"]);

                if (dataStart != headerDataStart)
                {
                    if (headerDataStart == 0 && dataStop > 99999999)
                    {
                        // large file, this is OK.
                    }
                    else if (!ignoreOffsetDiscrepancy)
                    {
                        file_stream.Close();
                        throw new DataOffsetDiscrepancyException(
                            $"{Name} has a discrepancy in the DATA start byte location: {headerDataStart} (HEADER) vs {dataStart} (TEXT)"
                        );
                    }
                }

                if (dataStop != headerDataStop)
                {
                    if (headerDataStop == 0 && dataStop > 99999999)
                    {
                        // Large file, this is OK
                    }
                    else if (!ignoreOffsetDiscrepancy)
                    {
                        file_stream.Close();
                        throw new DataOffsetDiscrepancyException(
                            $"{Name} has a discrepancy in the DATA end byte location: {headerDataStop} (HEADER) vs {dataStop} (TEXT)"
                        );
                    }
                }
            }
        }

        if (dataStop > Size)
        {
            file_stream.Close();
            throw new ParserException("FCS file indicates data section greater than file size");
        }

        this.extract_channel_metadata();

        if (!metadataOnly)
        {
            this.events = this.parse_data(
                currentOffset,
                dataStart,
                dataStop,
                this.Text
            );

            this.fill_matrix(true);
        }
        else this.events = new();

        file_stream.Close();
    }

    private byte[] read_bytes(long offset, int start, int stop)
    {
        file_stream.Position = offset + start;
        byte[] buffer = new byte[stop - start + 1];
        file_stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    private Dictionary<string, object> parse_header(long offset)
    {
        var header = new Dictionary<string, object>();

        byte[] versionBytes = read_bytes(offset, 3, 5);
        header["version"] = Encoding.ASCII.GetString(versionBytes);

        header["text_start"] = int.Parse(Encoding.ASCII.GetString(read_bytes(offset, 10, 17)));
        header["text_stop"] = int.Parse(Encoding.ASCII.GetString(read_bytes(offset, 18, 25)));
        header["data_start"] = int.Parse(Encoding.ASCII.GetString(read_bytes(offset, 26, 33)));
        header["data_stop"] = int.Parse(Encoding.ASCII.GetString(read_bytes(offset, 34, 41)));

        try
        {
            header["analysis_start"] = int.Parse(
                Encoding.ASCII.GetString(read_bytes(offset, 42, 49)));
        }
        catch
        {
            header["analysis_start"] = -1;
        }

        try
        {
            header["analysis_stop"] = int.Parse(
                Encoding.ASCII.GetString(read_bytes(offset, 50, 57)));
        }
        catch
        {
            header["analysis_stop"] = -1;
        }

        return header;
    }

    private Dictionary<string, string> parse_text(long offset, int start, int stop)
    {
        byte[] textBytes = read_bytes(offset, start, stop);

        string text;
        try
        {
            text = Encoding.UTF8.GetString(textBytes);
        }
        catch
        {
            text = Encoding.GetEncoding("ISO-8859-1").GetString(textBytes);
        }

        return parse_pairs(text);
    }

    private List<double> parse_data(long offset, int start, int stop, Dictionary<string, string> text)
    {
        if (this.Mode != StoreMode.List)
        {
            file_stream.Close();
            throw new NotImplementedException($"FCS data stored as type '{this.Mode.ToString()}' is unsupported");
        }

        if (this.Type == DataType.UnsignedBinaryInteger)
        {
            Dictionary<int, int> bitWidthByChannel = new Dictionary<int, int>();
            Dictionary<int, int> maxRangeByChannel = new Dictionary<int, int>();

            for (int i = 1; i <= ChannelCount; i++)
            {
                bitWidthByChannel[i] = int.Parse(text[$"p{i}b"]);
                int tmpMaxRange = int.Parse(text[$"p{i}r"]);
                maxRangeByChannel[i] = next_power_of_2(tmpMaxRange);
            }

            var longData = this.parse_int(offset, start, stop, bitWidthByChannel, maxRangeByChannel, this.Order == ByteOrder.LittleEndian);
            var typeConvert = new List<double>();
            foreach (long value in longData) typeConvert.Add((double)value);
            return typeConvert;
        }
        else return parse_non_integral(offset, start, stop);
    }

    private (long, int) calculate_data_count(int start, int stop, int dataTypeSize)
    {
        long dataSectSize = stop - start + 1;
        long dataMod = dataSectSize % dataTypeSize;

        if (dataMod > 0)
        {
            if (dataMod == 1 && ignore_offset)
            {
                // Warning would be appropriate here
                stop = stop - 1;
                dataSectSize = dataSectSize - 1;
            }
            else if (dataMod == 1 && !ignore_offset)
            {
                file_stream.Close();
                throw new ParserException(
                    $"FCS file {Name} reports a data offset that is off by 1. Set `ignore_offset_error=True` to force reading in this file."
                );
            }
            else
            {
                file_stream.Close();
                throw new ParserException("Unable to determine the correct byte offsets for event data");
            }
        }

        long numItems = dataSectSize / dataTypeSize;
        return (numItems, stop);
    }

    private List<long> parse_int(
        long offset,
        int start,
        int stop,
        Dictionary<int, int> bitWidthLut,
        Dictionary<int, int> maxRangeLut,
        bool isLittleEndian
    )
    {
        if (bitWidthLut.Values.All(b => b == 8 || b == 16 || b == 32))
        {
            if (bitWidthLut.Values.Distinct().Count() == 1)
            {
                int bitWidth = bitWidthLut.Values.First();
                int dataTypeSize = bitWidth / 8;
                long numItems;
                (numItems, stop) = this.calculate_data_count(start, stop, dataTypeSize);

                file_stream.Position = offset + start;
                byte[] buffer = new byte[numItems * dataTypeSize];
                file_stream.ReadExactly(buffer, 0, buffer.Length);

                List<long> result = new List<long>();

                if (bitWidth == 8)
                {
                    for (int i = 0; i < numItems; i++)
                    {
                        result.Add(buffer[i]);
                    }
                }
                else if (bitWidth == 16)
                {
                    for (int i = 0; i < numItems; i++)
                    {
                        ushort value = BitConverter.ToUInt16(buffer, i * 2);
                        if (isLittleEndian != BitConverter.IsLittleEndian)
                        {
                            value = reverse_bytes(value);
                        }

                        result.Add(value);
                    }
                }
                else if (bitWidth == 32)
                {
                    for (int i = 0; i < numItems; i++)
                    {
                        uint value = BitConverter.ToUInt32(buffer, i * 4);
                        if (isLittleEndian != BitConverter.IsLittleEndian)
                        {
                            value = reverse_bytes(value);
                        }

                        result.Add(value);
                    }
                }

                // apply bit masking if needed
                if (bitWidthLut.Any(kv => (1 << kv.Value) > maxRangeLut[kv.Key]))
                {
                    int amountDataPoints = (int)(numItems / maxRangeLut.Count);

                    for (int i = 0; i < numItems; i++)
                    {
                        int channel = (i % maxRangeLut.Count) + 1;
                        int maxRange = maxRangeLut[channel];
                        result[i] = result[i] & (maxRange - 1);
                    }
                }

                return result;
            }
            else
            {
                return read_variable_length_int(
                    bitWidthLut, maxRangeLut, offset, isLittleEndian, start, stop);
            }
        }
        else
        {
            // Non-standard bit width
            return new List<long>();
        }
    }

    private List<long> read_variable_length_int(
        Dictionary<int, int> bitWidthByChannel,
        Dictionary<int, int> maxRangeByChannel,
        long offset,
        bool isLittleEndian,
        int start,
        int stop
    )
    {
        byte[] dataBytes = read_bytes(offset, start, stop);
        List<long> result = new List<long>();
        int bytePosition = 0;

        int totalEvents = (int)((stop - start + 1) * 8 / bitWidthByChannel.Values.Sum());

        for (int eventNum = 0; eventNum < totalEvents; eventNum++)
        {
            foreach (var kvp in bitWidthByChannel.OrderBy(k => k.Key))
            {
                int channel = kvp.Key;
                int bitWidth = kvp.Value;
                int byteWidth = bitWidth / 8;

                long value = 0;
                for (int i = 0; i < byteWidth; i++)
                {
                    value |= (long)dataBytes[bytePosition + i] << (i * 8);
                }

                if (isLittleEndian != BitConverter.IsLittleEndian)
                {
                    value = reverse_bytes(value, byteWidth);
                }

                if ((1 << bitWidth) > maxRangeByChannel[channel])
                {
                    value = value % maxRangeByChannel[channel];
                }

                result.Add(value);
                bytePosition += byteWidth;
            }
        }

        return result;
    }

    private List<double> parse_non_integral(
        long offset, int start, int stop)
    {
        int dataTypeSize;
        if (this.Type == DataType.Float)
            dataTypeSize = 4; // float
        else dataTypeSize = 8;

        long numItems;
        (numItems, stop) = this.calculate_data_count(start, stop, dataTypeSize);

        file_stream.Position = offset + start;
        byte[] buffer = new byte[numItems * dataTypeSize];
        file_stream.ReadExactly(buffer, 0, buffer.Length);

        List<double> result = new List<double>();

        if (this.Type == DataType.Float)
        {
            for (int i = 0; i < numItems; i++)
            {
                float value = BitConverter.ToSingle(buffer, i * 4);
                if ((this.Order == ByteOrder.LittleEndian) != BitConverter.IsLittleEndian)
                {
                    byte[] valueBytes = BitConverter.GetBytes(value);
                    Array.Reverse(valueBytes);
                    value = BitConverter.ToSingle(valueBytes, 0);
                }

                result.Add(value);
            }
        }
        else
        {
            for (int i = 0; i < numItems; i++)
            {
                double value = BitConverter.ToDouble(buffer, i * 8);
                if ((this.Order == ByteOrder.LittleEndian) != BitConverter.IsLittleEndian)
                {
                    byte[] valueBytes = BitConverter.GetBytes(value);
                    Array.Reverse(valueBytes);
                    value = BitConverter.ToDouble(valueBytes, 0);
                }

                result.Add(value);
            }
        }

        return result;
    }

    private Dictionary<string, string> parse_pairs(string text)
    {
        if (string.IsNullOrEmpty(text)) return new Dictionary<string, string>();

        char delimiter = text[0];
        string escapedDelimiter = delimiter == '|' ? "\\|" :
            delimiter == '\\' ? "\\\\" :
            delimiter == '*' ? "\\*" :
            delimiter.ToString();

        string content = text.Substring(1, text.Length - 2).Replace("$", "");

        // Split on delimiter unless it's doubled
        string pattern = $"(?<![{escapedDelimiter}]){escapedDelimiter}(?!{escapedDelimiter})";
        string[] parts = Regex.Split(content, pattern);

        var result = new Dictionary<string, string>();
        for (int i = 0; i < parts.Length; i += 2)
        {
            if (i + 1 < parts.Length)
            {
                string key = parts[i].Replace($"{delimiter}{delimiter}", $"{delimiter}").ToLower();
                string value = parts[i + 1].Replace($"{delimiter}{delimiter}", $"{delimiter}");
                result[key] = value;
            }
        }

        return result;
    }

    private void extract_channel_metadata()
    {
        Regex pnnRegex = new Regex(@"^p(\d+)n$", RegexOptions.IgnoreCase);
        Dictionary<int, string> channelNames = new();
        
        // find all PnN values
        int index = 0;
        foreach (string key in this.Text.Keys)
        {
            Match match = pnnRegex.Match(key);
            if (match.Success)
            {
                int channelNum = int.Parse(match.Groups[1].Value);
                channelNames.Add(channelNum, this.Text[key]);
                index += 1;
            }
        }

        // Add other channel metadata
        foreach (var kvp in channelNames)
        {
            int chanNum = kvp.Key;
            var chanDict = kvp.Value;

            string pnsKey = $"p{chanNum}s";
            var label = Text.ContainsKey(pnsKey) ? Text[pnsKey] : "";

            string pneKey = $"p{chanNum}e";
            (float, float) wavelength = (0, 0);
            if (Text.ContainsKey(pneKey))
            {
                string[] pneParts = Text[pneKey].Split(',');
                double decades = double.Parse(pneParts[0]);
                double log0 = double.Parse(pneParts[1]);
                if (log0 == 0 && decades != 0) log0 = 1.0;
                wavelength = (Convert.ToSingle(decades), Convert.ToSingle(log0));
            }

            string pngKey = $"p{chanNum}g";
            var gain = Text.ContainsKey(pngKey) ? float.Parse(Text[pngKey]) : 1.0f;

            string pnrKey = $"p{chanNum}r";
            var max = float.Parse(Text[pnrKey]);
            
            this.Channels.Add(chanNum, new Channel(
                chanNum, chanDict, label, wavelength, max, gain));
        }
    }

    private void fill_matrix(bool preprocess = true)
    {
        this.Measurements.Clear();
        foreach (var channel in this.Channels)
        {
            int chanNum = channel.Key;
            int chanIdx = chanNum - 1;
            
            float[] values = new float[this.EventCount];
            for (int i = 0; i < this.EventCount; i++)
                values[i] = Convert.ToSingle(this.events[i * this.ChannelCount + chanIdx]);

            if (preprocess && Text.ContainsKey("timestep") && channel.Value.Name.ToLower().StartsWith("time"))
            {
                string timestepStr = Text["timestep"];
                float timeStep;
                if (string.IsNullOrWhiteSpace(timestepStr))
                    timeStep = 1.0f;
                else timeStep = float.Parse(timestepStr);
                for (int k = 0; k < values.Length; k++)
                    values[k] *= timeStep;
            }

            (float chanDecades, float chanLog0) = channel.Value.Wavelength;
            float chanRange = channel.Value.Maximum;
            float chanGain = channel.Value.Gain;

            if (chanDecades > 0)
                for (int i = 0; i < EventCount; i++)
                    values[i] = Convert.ToSingle(Math.Pow(10, chanDecades * values[i] / chanRange) * chanLog0);

            if (chanGain != 0)
                for (int i = 0; i < EventCount; i++)
                    values[i] /= chanGain;
            this.Measurements.Add(channel.Value, values);
        }
    }

    private static int next_power_of_2(int x)
    {
        if (x == 0) return 1;
        return (int)Math.Pow(2, Math.Ceiling(Math.Log(x) / Math.Log(2)));
    }

    private static ushort reverse_bytes(ushort value)
    {
        return (ushort)((value >> 8) | (value << 8));
    }

    private static uint reverse_bytes(uint value)
    {
        return (value >> 24) | ((value >> 8) & 0xFF00) | ((value << 8) & 0xFF0000) | (value << 24);
    }

    private static long reverse_bytes(long value, int byteWidth)
    {
        long result = 0;
        for (int i = 0; i < byteWidth; i++)
            result = (result << 8) | ((value >> (i * 8)) & 0xFF);
        return result;
    }
    
    public override Dictionary<Dimension, float[]?> GetValues(long max, params Dimension[] dimensions)
    {
        Dictionary<Dimension, float[]?> dict = new();
        
        long step = Math.Max(1L, this.EventCount / max);
        long length = Math.Min(max, this.EventCount);
        long[] indices = new long[length];
        for (int i = 0; i < length; i++)
            indices[i] = step * i;
        
        foreach (var dimension in dimensions)
            if ((this.Measurements!.ContainsKey(dimension)))
            {
                // implicitly return a copy of the data.
                if (!dict.ContainsKey(dimension))
                    dict.Add(dimension, GetValues(dimension, indices));
            }

        return dict;
    }

    public override float[]? GetValues(Dimension dimension, long[] indices)
    {
        if (this.Measurements!.ContainsKey(dimension))
        {
            var all = this.Measurements[dimension];
            float[] selected = new float[indices.Length];
            for (int id = 0; id < indices.Length; id++) selected[id] = all[indices[id]];
            return selected;
        }

        return null;
    }
}

public class Subset : Population
{
    public Subset(
        Population p, 
        long[] indices, 
        GatingStrategy? associatedGate = null, 
        int associatedGateIndex = 0, 
        string name = "Subset")
    {
        this.Name = name;
        this.AssociatedGate = associatedGate;
        this.AssociatedGateIndex = associatedGateIndex;
        
        this.Parent = p;
        this.ParentGroup = p.ParentGroup;
        this.ParentTube = p.ParentTube;
        this.EventCount = indices.Length;
        this.ChannelCount = p.ChannelCount;
        this.Channels = p.Channels;
        this.Embeddings = p.Embeddings;
        this.Compensation = p.Compensation;

        this._selection = indices;
        p.Subsets.Add(this);
    }
    
    public override bool IsTube { get; } = false;
    public override string Name { get; set; } = "Subset";
    public override string Identifier => "subset";

    private long[] _selection;
    public long[] Selection
    {
        get { return _selection;}
        set
        {
            _selection = value;
            EventCount = _selection.Length;
        }
    }

    public override Dictionary<Dimension, float[]?> GetValues(long max, params Dimension[] dimensions)
    {
        Dictionary<Dimension, float[]?> dict = new();
        
        long step = Math.Max(1L, this.EventCount / max);
        long length = Math.Min(max, this.EventCount);
        long[] indices = new long[length];
        for (int i = 0; i < length; i++)
            indices[i] = step * i;
        
        // mapping to the selection
        for (int i = 0; i < length; i++)
            indices[i] = this.Selection[indices[i]];
        
        foreach (var dimension in dimensions)
            if ((this.ParentTube!.Measurements!.ContainsKey(dimension)))
                if(!dict.ContainsKey(dimension))
                    dict.Add(dimension, GetValues(dimension, indices));

        return dict;
    }

    public override float[]? GetValues(Dimension dimension, long[] indices)
    {
        if (this.ParentTube!.Measurements!.ContainsKey(dimension))
        {
            var all = this.ParentTube!.Measurements[dimension];
            float[] selected = new float[indices.Length];
            for (int id = 0; id < indices.Length; id++) selected[id] = all[indices[id]];
            return selected;
        }

        return null;
    }
}
