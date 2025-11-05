using System;
using System.Linq;
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

    public bool Density { get; set; } = false;
    public long MaxDisplay { get; set; } = 10000;
    public long DensityEstimate { get; set; } = 2000;

    internal bool has_initialize_range()
    {
        return (
            XRange.Item1 > 0
            || XRange.Item2 > 0
            || YRange.Item1 > 0
            || YRange.Item2 > 0
        );
    }

    internal void initialize_range(float[] x, float[] y)
    {
        XRange = (-10, x.Max());
        YRange = (-10, y.Max());
    }
}