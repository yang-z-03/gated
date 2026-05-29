using System;
using System.Collections.Generic;
using System.Linq;
using Gated.Configurations;
using Gated.Preprocessing;
using ScottPlot;

namespace Gated.Display;

public static class PlotHelper
{
    public static void SetTicks(IAxis axis, ITransform transform, float max)
    {
        if (transform is LinearTransform)
        {
            var origin = transform.InverseTransform(max);
            double[] thr =
            [
                10000, 50000, 
                100000, 150000, 200000, 250000, 300000, 400000, 500000, 600000, 800000,
                1000000, 1500000, 2000000, 2500000, 3000000, 4000000, 5000000, 6000000, 8000000,
            ];

            string[] names =
            [
                "10k", "50k",
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

    public static double[,] Order(int[,] hist, int size)
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

    public static void RenderHeatmap(Plot plot, ScatterConfig config, float[] xs, float[] ys)
    {
        int[,] histogram = new int[config.Resolution, config.Resolution];
        float xstep = (config.XRange.Item2 - config.XRange.Item1) / config.Resolution;
        float ystep = (config.YRange.Item2 - config.YRange.Item1) / config.Resolution;
        for (int i = 0; i < xs.Length; i++)
        {
            histogram[
                config.Resolution - Math.Max(1,
                    Math.Min(config.Resolution, Convert.ToInt32((ys[i] - config.YRange.Item1) / ystep))),
                Math.Max(0, Math.Min(config.Resolution - 1, Convert.ToInt32((xs[i] - config.XRange.Item1) / xstep)))
            ]++;
        }

        double[,] o = Order(histogram, config.Resolution);
        var hm1 = plot.Add.Heatmap(o);
        hm1.Colormap = new Configurations.Turbo();
        hm1.CellAlignment = Alignment.LowerLeft;
        hm1.CellWidth = xstep;
        hm1.CellHeight = ystep;
    }

    public static void StyleAxes(Plot plot, ScatterConfig config)
    {
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;

        SetTicks(plot.Axes.Left, config.YTransform, config.YRange.Item2);
        SetTicks(plot.Axes.Bottom, config.XTransform, config.XRange.Item2);
        plot.Axes.Bottom.TickLabelStyle.Rotation = -90;
        plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;
        plot.Axes.Bottom.MinimumSize = 45;
        plot.Axes.Left.MinimumSize = 45;
    }
}
