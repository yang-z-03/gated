using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Reduction;

public sealed class CytoNormSpline
{
    private readonly double[] x;
    private readonly double[] y;
    private readonly double[] m;
    private readonly bool identity;

    private CytoNormSpline(double[] x, double[] y, double[] m, bool identity)
    {
        this.x = x;
        this.y = y;
        this.m = m;
        this.identity = identity;
    }

    public static CytoNormSpline Identity { get; } = new([], [], [], true);

    public static CytoNormSpline Fit(double[] current_distribution, double[] goal_distribution, double[]? limits = null)
    {
        if (current_distribution.Length != goal_distribution.Length)
            throw new ArgumentException("Current and goal distributions must have equal length.");

        if (limits is { Length: > 0 })
        {
            current_distribution = current_distribution.Concat(limits).ToArray();
            goal_distribution = goal_distribution.Concat(limits).ToArray();
        }

        var (clean_x, clean_y) = regularize(current_distribution, goal_distribution);
        if (clean_x.Length < 2 ||
            MatrixUtilities.HasSingleUniqueValue(clean_x) ||
            MatrixUtilities.HasSingleUniqueValue(clean_y))
            return Identity;

        return new CytoNormSpline(clean_x, clean_y, select_interpolants(clean_x, clean_y), false);
    }

    public double Transform(double value)
    {
        if (identity)
            return value;

        if (value <= x[0])
            return y[0] + m[0] * (value - x[0]);
        if (value >= x[^1])
            return y[^1] + m[^1] * (value - x[^1]);

        int interval = Array.BinarySearch(x, value);
        if (interval >= 0)
            return y[interval];

        interval = ~interval - 1;
        double h = x[interval + 1] - x[interval];
        double t = (value - x[interval]) / h;
        double t2 = t * t;
        double t3 = t2 * t;
        double h00 = 2 * t3 - 3 * t2 + 1;
        double h10 = t3 - 2 * t2 + t;
        double h01 = -2 * t3 + 3 * t2;
        double h11 = t3 - t2;
        return h00 * y[interval] +
               h10 * h * m[interval] +
               h01 * y[interval + 1] +
               h11 * h * m[interval + 1];
    }

    public void TransformInPlace(float[,] data, int row, int channel)
    {
        data[row, channel] = (float)Transform(data[row, channel]);
    }

    private static (double[] X, double[] Y) regularize(double[] source_x, double[] source_y)
    {
        var groups = new SortedDictionary<double, List<double>>();
        for (int index = 0; index < source_x.Length; index++)
        {
            double x_value = source_x[index];
            double y_value = source_y[index];
            if (double.IsNaN(x_value) || double.IsNaN(y_value))
                continue;

            if (!groups.TryGetValue(x_value, out var values))
            {
                values = [];
                groups[x_value] = values;
            }

            values.Add(y_value);
        }

        var x = new double[groups.Count];
        var y = new double[groups.Count];
        int write = 0;
        foreach (var group in groups)
        {
            x[write] = group.Key;
            y[write] = group.Value.Sum() / group.Value.Count;
            write++;
        }

        return (x, y);
    }

    private static double[] select_interpolants(double[] x, double[] y)
    {
        int n = x.Length;
        var slopes = new double[n - 1];
        for (int index = 0; index < n - 1; index++)
            slopes[index] = (y[index + 1] - y[index]) / (x[index + 1] - x[index]);

        var m = new double[n];
        m[0] = slopes[0];
        m[^1] = slopes[^1];
        for (int index = 1; index < n - 1; index++)
            m[index] = (slopes[index - 1] + slopes[index]) / 2.0;

        for (int index = 0; index < n - 1; index++)
        {
            double slope = slopes[index];
            int next = index + 1;
            if (slope == 0)
            {
                m[index] = 0;
                m[next] = 0;
                continue;
            }

            double alpha = m[index] / slope;
            double beta = m[next] / slope;
            double a2b3 = 2 * alpha + beta - 3;
            double ab23 = alpha + 2 * beta - 3;
            if (a2b3 > 0 &&
                ab23 > 0 &&
                alpha * (a2b3 + ab23) < a2b3 * a2b3)
            {
                double tau_s = 3 * slope / Math.Sqrt(alpha * alpha + beta * beta);
                m[index] = tau_s * alpha;
                m[next] = tau_s * beta;
            }
        }

        return m;
    }
}
