namespace Gated.Models;

public abstract class Dimension
{
    public abstract bool IsArtificial { get; }
    public abstract string Name { get; set; }
    public abstract string Label { get; set; }
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
    
    public override bool IsArtificial { get; } = false;
    public int Index { get; set; }
    public override string Name { get; set; }
    public override string Label { get; set; }
    public (float, float) Wavelength { get; private set; }
    public float Maximum { get; private set; }
    public float Gain { get; private set; }
}

public class Embedding : Dimension
{
    public override bool IsArtificial { get; } = false;
    public override string Name { get; set; }
    public override string Label { get; set; }
}