namespace Gated.Models;

public abstract class Dimension
{
    public abstract bool IsArtificial { get; }
    public string Name { get; private set; } = string.Empty;
}

public class Channel : Dimension
{
    public override bool IsArtificial { get; } = false;
    public string Excitor { get; private set; } = string.Empty;
    public string Receiver { get; private set; } = string.Empty;
    public (int, int) Wavelength { get; private set; } = (-1, -1);
    public int Maximum { get; private set; } = 65536;
}

public class Embedding : Dimension
{
    public override bool IsArtificial { get; } = false;
}