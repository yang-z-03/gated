using System;

namespace Gated.Models;

public abstract class Dimension
{
    public abstract string Identifier { get; }
    public abstract string Name { get; set; }
    public abstract string Label { get; set; }
    public abstract float Maximum { get; set; }
    public static ChannelImageConverter ImageConverter = new();
}

public class Channel : Dimension
{
    public Channel(int index, string name, string label, (float, float) wavelength, float max, float gain)
    {
        this.Name = name;
        this.Label = label;
        this.Wavelength = wavelength;
        this.Maximum = max;
        this.Gain = gain;
        this.Index = index;
    }

    public override string Identifier { get; } = "channel";
    public int Index { get; set; }
    public override string Name { get; set; }
    public override string Label { get; set; }
    public (float, float) Wavelength { get; private set; }
    public override float Maximum { get; set; }
    public float Gain { get; private set; }

    public bool IsEqual(Channel? other)
    {
        return (this.Name == other?.Name)
            && (Convert.ToInt32(this.Maximum) == Convert.ToInt32(other?.Maximum))
            && (Math.Abs(this.Gain - (other?.Gain ?? 1)) <= 1e-5);
    }
}

public class Embedding : Dimension
{
    public Embedding(string name, string label)
    {
        this.Name = name;
        this.Label = label;
    }
    
    public override string Identifier { get; } = "embedding";
    public override string Name { get; set; }
    public override string Label { get; set; }
    public override float Maximum { get; set; }
}