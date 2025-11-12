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

    public PlotType Type { get; set; } = PlotType.Heatmap;
    
    // options for density and scatter
    public long MaxDisplay { get; set; } = 10000;
    
    // options for density
    public long DensityEstimate { get; set; } = 2000;
    
    // options for heatmap
    public int RangeEstimateResolution { get; set; } = 100;
    public int Resolution { get; set; } = 200;

    internal bool require_range_update = true;
    internal bool has_initialize_range()
    {
        return (
            XRange.Item1 > 0
            || XRange.Item2 > 0
            || YRange.Item1 > 0
            || YRange.Item2 > 0
        );
    }
    
    // given transformed range.
    // when changing transform, the range should be forced a refresh.
    internal void initialize_range(float[] x, float[] y)
    {
        float xmax = x.Max();
        float ymax = y.Max();
        float xmin = x.Min();
        float ymin = y.Min();

        float x_step = (xmax - xmin) / this.RangeEstimateResolution;
        float y_step = (ymax - ymin) / this.RangeEstimateResolution;
        int[] x_hist = new int[this.RangeEstimateResolution + 1];
        int[] y_hist = new int[this.RangeEstimateResolution + 1];
        for (int i = 0; i < x.Length; i++)
        {
            x_hist[Convert.ToInt32((x[i] - xmin) / x_step)]++;
            y_hist[Convert.ToInt32((y[i] - ymin) / y_step)]++;
        }

        int threshold = Convert.ToInt32(x.Length * 0.99);
        int xcum = 0;
        float x_range = 1;
        for (int i = 0; i < this.RangeEstimateResolution; i++)
        {
            xcum += x_hist[i];
            if (xcum > threshold)
            {
                x_range = xmin + x_step * (i + 1);
                break;
            }
        }
        
        int ycum = 0;
        float y_range = 1;
        for (int i = 0; i < this.RangeEstimateResolution; i++)
        {
            ycum += y_hist[i];
            if (ycum > threshold)
            {
                y_range = ymin + y_step * (i + 1);
                break;
            }
        }

        this.XRange = (0, x_range);
        this.YRange = (0, y_range);
    }
}

public enum PlotType
{
    Scatter,
    Density,
    Heatmap
}