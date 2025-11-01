namespace Gated.Models;

public abstract class Dimension
{
    public abstract bool IsArtificial { get; }
}

public class Channel : Dimension
{
    public override bool IsArtificial
    {
        get => false;
    }
}

public class Embedding : Dimension
{
    public override bool IsArtificial
    {
        get => true;
    }
}