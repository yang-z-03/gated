using System;
using System.Collections.Generic;
using System.Linq;

namespace Gated.Preprocessing;

public class GaussianKDE
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double _hx;
    private readonly double _hy;
    private readonly double _normFactor;

    public GaussianKDE(IEnumerable<double> x, IEnumerable<double> y, double? hx = null, double? hy = null)
    {
        _x = x.ToArray();
        _y = y.ToArray();

        if (_x.Length != _y.Length)
            throw new ArgumentException("x and y must have the same number of elements");

        int n = _x.Length;

        // Calculate bandwidths using Silverman's rule if not provided
        _hx = hx ?? SilvermanBandwidth(_x);
        _hy = hy ?? SilvermanBandwidth(_y);

        // Precompute normalization factor
        _normFactor = 1.0 / (n * _hx * _hy * 2 * Math.PI);
    }

    private static double SilvermanBandwidth(double[] data)
    {
        double stdDev = StandardDeviation(data);
        double iqr = InterquartileRange(data);
        double h = 1.06 * Math.Min(stdDev, iqr / 1.34) * Math.Pow(data.Length, -0.2);
        return h + 1e-9; // Ensure non-zero
    }

    private static double StandardDeviation(double[] data)
    {
        double mean = data.Average();
        double sumSq = data.Select(d => (d - mean) * (d - mean)).Sum();
        return Math.Sqrt(sumSq / data.Length);
    }

    private static double InterquartileRange(double[] data)
    {
        double[] sorted = data.OrderBy(d => d).ToArray();
        int n = sorted.Length;
        double q1 = sorted[(int)(0.25 * n)];
        double q3 = sorted[(int)(0.75 * n)];
        return q3 - q1;
    }

    public double Estimate(double x, double y)
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