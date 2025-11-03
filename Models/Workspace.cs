using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Svg.Skia;

namespace Gated.Models;

public interface INode
{
    public string Name { get; set; }
    public string Identifier { get; set; }
    public ObservableCollection<INode> Children { get; }
}

public class INodeImageConverter : IMultiValueConverter
{
    private readonly IImage bmp_workspace;
    private readonly IImage bmp_grouping;
    private readonly IImage bmp_isotype;
    private readonly IImage bmp_blank;
    private readonly IImage bmp_single;
    
    private readonly IImage bmp_tube;
    private readonly IImage bmp_subset;
    private readonly IImage bmp_gate;
    private readonly IImage bmp_statistics;
    private readonly IImage bmp_unk;

    public INodeImageConverter()
    {
        this.bmp_workspace = new SvgImage {Source = SvgSource.Load("avares://gated/Resources/workspace.svg", null)};
        this.bmp_grouping =  new SvgImage {Source = SvgSource.Load("avares://gated/Resources/grouping.svg", null)};
        this.bmp_isotype =  new SvgImage {Source = SvgSource.Load("avares://gated/Resources/controls.svg", null)};
        this.bmp_blank =  new SvgImage {Source = SvgSource.Load("avares://gated/Resources/controls.svg", null)};
        this.bmp_single =  new SvgImage {Source = SvgSource.Load("avares://gated/Resources/controls.svg", null)};
        this.bmp_tube = new SvgImage {Source = SvgSource.Load("avares://gated/Resources/tube.svg", null)};
        this.bmp_subset = new SvgImage {Source = SvgSource.Load("avares://gated/Resources/subset.svg", null)};
        this.bmp_gate =  new SvgImage {Source = SvgSource.Load("avares://gated/Resources/gate.svg", null)};
        this.bmp_statistics =  new SvgImage {Source = SvgSource.Load("avares://gated/Resources/statistics.svg", null)};
        this.bmp_unk = new SvgImage {Source = SvgSource.Load("avares://gated/Resources/unk.svg", null)};
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 2)
            if (values[0] is string name &&
                values[1] is string identifier)
            {
                switch (identifier)
                {
                    case "workspace": return this.bmp_workspace;
                    case "grouping": return this.bmp_grouping;
                    case "blank": return this.bmp_blank;
                    case "single": return this.bmp_single;
                    case "fmo": return this.bmp_single;
                    case "isotype": return this.bmp_isotype;
                    case "tube": return this.bmp_tube;
                    default: return this.bmp_unk;
                }        
            }
        
        return this.bmp_unk;
    }
}

public class ChannelImageConverter : IMultiValueConverter
{
    private readonly IImage bmp_channel;
    private readonly IImage bmp_embedding;
    private readonly IImage bmp_unk;

    public ChannelImageConverter()
    {
        this.bmp_channel = new SvgImage { Source = SvgSource.Load("avares://gated/Resources/channel.svg", null) };
        this.bmp_embedding = new SvgImage { Source = SvgSource.Load("avares://gated/Resources/embedding.svg", null) };
        this.bmp_unk = new SvgImage { Source = SvgSource.Load("avares://gated/Resources/unk.svg", null) };
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 2)
            if (values[0] is string name &&
                values[1] is string identifier)
            {
                switch (identifier)
                {
                    case "channel": return this.bmp_channel;
                    case "embedding": return this.bmp_embedding;
                    default: return this.bmp_unk;
                }        
            }
        
        return this.bmp_unk;
    }
}

public class Workspace : INode
{
    public Workspace(string name)
    {
        this.Name = name;
        this.Children.Add(new Grouping("Blank Control", new List<Tube>(), "blank"));
        this.Children.Add(new Grouping("Single Staining", new List<Tube>(), "single"));
        this.Children.Add(new Grouping("FMO Staining", new List<Tube>(), "fmo"));
        this.Children.Add(new Grouping("Isotype Control", new List<Tube>(), "isotype"));
        this.Children.Add(new Grouping("Samples", new List<Tube>()));
    }
    
    public string Name { get; set; } = "Workspace";
    public string Identifier { get; set; } = "workspace";

    public string FilePath { get; private set; } = string.Empty;
    public bool IsDirty { get; set; } = false;
    public ObservableCollection<INode> Children { get; } = new();

    public static INodeImageConverter ImageConverter = new INodeImageConverter();
}