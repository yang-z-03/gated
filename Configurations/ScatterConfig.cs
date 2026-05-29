using System;
using Gated.Models;
using Gated.Preprocessing;

namespace Gated.Configurations;

public class ScatterConfig
{
    public ScatterConfig(Dimension x, Dimension y)
    {
        this.X = x;
        this.Y = y;
    }
    
    // dimension definitions
    public Dimension X { get; set; }
    public ITransform XTransform { get; set; } = new LinearTransform();
    
    public Dimension Y { get; set; }
    public ITransform YTransform { get; set; } = new LinearTransform();

    public (float, float) XRange { get; set; } = (-1, -1);
    public (float, float) YRange { get; set; } = (-1, -1);

    public PlotType Type { get; set; } = PlotType.Heatmap;
    
    // options for density and scatter
    public long MaxDisplay { get; set; } = 10000;
    
    // options for density
    public long DensityEstimate { get; set; } = 2000;
    
    // options for heatmap
    public int Resolution { get; set; } = 200;
}

public enum PlotType
{
    Scatter,
    Density,
    Heatmap
}
