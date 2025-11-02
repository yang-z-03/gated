namespace Gated.Models;

public abstract class Dimension
{
    public abstract bool IsArtificial { get; }
    public abstract string Name { get; set; }
    public abstract string Label { get; set; }
}

public class Channel : Dimension
{
    public Channel(string name, string label, (int, int) wavelength, int max, float gain)
    {
        this.Name = name;
        this.Label = label;
        this.Wavelength = wavelength;
        this.Maximum = max;
        this.Gain = gain;
    }
    
    public override bool IsArtificial { get; } = false;
    public override string Name { get; set; }
    public override string Label { get; set; }
    public (int, int) Wavelength { get; private set; }
    public int Maximum { get; private set; }
    public float Gain { get; private set; }
}

public class Embedding : Dimension
{
    public override bool IsArtificial { get; } = false;
    public override string Name { get; set; }
    public override string Label { get; set; }
}