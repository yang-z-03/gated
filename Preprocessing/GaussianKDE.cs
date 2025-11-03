using System;
using System.Collections.Generic;
using System.Linq;

namespace Gated.Preprocessing;

public class GaussianKDE
{
    private readonly float[] _x;
    private readonly float[] _y;
    private readonly double _hx;
    private readonly double _hy;
    private readonly double _normFactor;

    public GaussianKDE(
        IEnumerable<float> x, IEnumerable<float> y, 
        double? hx = null, double? hy = null)
    {
        _x = x.ToArray();
        _y = y.ToArray();

        if (_x.Length != _y.Length)
            throw new ArgumentException("x and y must have the same number of elements");

        int n = _x.Length;

        // calculate bandwidths using silverman's rule if not provided
        _hx = hx ?? silverman_bandwidth(_x);
        _hy = hy ?? silverman_bandwidth(_y);

        // precompute normalization factor
        _normFactor = 1.0 / (n * _hx * _hy * 2 * Math.PI);
    }

    private static double silverman_bandwidth(float[] data)
    {
        double stdDev = sd(data);
        double iqrange = iqr(data);
        double h = 1.06 * Math.Min(stdDev, iqrange / 1.34) * Math.Pow(data.Length, -0.2);
        return h + 1e-9; // Ensure non-zero
    }

    private static double sd(float[] data)
    {
        double mean = data.Average();
        double sumSq = data.Select(d => (d - mean) * (d - mean)).Sum();
        return Math.Sqrt(sumSq / data.Length);
    }

    private static double iqr(float[] data)
    {
        float[] sorted = data.OrderBy(d => d).ToArray();
        int n = sorted.Length;
        double q1 = sorted[(int)(0.25 * n)];
        double q3 = sorted[(int)(0.75 * n)];
        return q3 - q1;
    }

    public double Estimate(float x, float y)
    {
        double sum = 0.0;
        for (int i = 0; i < _x.Length; i++)
        {
            double dx = (x - _x[i]) / _hx;
            double dy = (y - _y[i]) / _hy;
            sum += Math.Exp(-0.5 * (dx * dx + dy * dy));
        }
        return _normFactor * sum;
    }
}