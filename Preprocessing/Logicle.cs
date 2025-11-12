using System;

namespace Gated.Preprocessing;

public partial class Logicle
{
    private readonly double T, W, M, A;
    private readonly double a, b, c, d, f;
    private readonly double w, x0, x1, x2;
    private readonly double x_taylor;
    private readonly double[] taylor;
    private const int TAYLOR_LENGTH = 16;

    internal Logicle(
        double T = 262144.0, double W = 0.5,
        double M = 4.5, double A = 0.0)
    {
        this.T = T;
        this.W = W;
        this.M = M;
        this.A = A;

        w = W / (M + A);
        x2 = A / (M + A);
        x1 = x2 + w;
        x0 = x2 + 2 * w;
        b = (M + A) * Math.Log(10.0);
        d = solve(b, w);

        double c_a = Math.Exp(x0 * (b + d));
        double mf_a = Math.Exp(b * x1) - c_a / Math.Exp(d * x1);
        a = T / ((Math.Exp(b) - mf_a) - c_a / Math.Exp(d));
        c = c_a * a;
        f = -mf_a * a;

        x_taylor = x1 + w / 4;
        taylor = new double[TAYLOR_LENGTH];

        double posCoef = a * Math.Exp(b * x1);
        double negCoef = -c / Math.Exp(d * x1);

        for (int i = 0; i < TAYLOR_LENGTH; i++)
        {
            posCoef *= b / (i + 1);
            negCoef *= -d / (i + 1);
            taylor[i] = posCoef + negCoef;
        }

        taylor[1] = 0; // Exact result of Logicle condition
    }

    private static double solve(double b, double w)
    {
        if (w == 0) return b;

        double tolerance = 2 * b * double.Epsilon;
        double d_lo = 0;
        double d_hi = b;
        double d = (d_lo + d_hi) / 2;
        double last_delta = d_hi - d_lo;
        double f_b = -2 * Math.Log(b) + w * b;
        double f = 2 * Math.Log(d) + w * d + f_b;
        double last_f = double.NaN;

        for (int i = 0; i < 40; i++)
        {
            double df = 2 / d + w;
            double delta;

            if (((d - d_hi) * df - f) * ((d - d_lo) * df - f) >= 0 ||
                Math.Abs(1.9 * f) > Math.Abs(last_delta * df))
            {
                delta = (d_hi - d_lo) / 2;
                d = d_lo + delta;
                if (d == d_lo) return d;
            }
            else
            {
                delta = f / df;
                double t = d;
                d -= delta;
                if (d == t) return d;
            }

            if (Math.Abs(delta) < tolerance) return d;
            last_delta = delta;

            f = 2 * Math.Log(d) + w * d + f_b;
            if (f == 0 || f == last_f) return d;
            last_f = f;

            if (f < 0) d_lo = d;
            else d_hi = d;
        }

        return -1;
    }

    internal double scale(double value)
    {
        if (value == 0) return x1;

        bool negative = value < 0;
        if (negative) value = -value;

        double x;
        if (value < f)
            x = x1 + value / taylor[0];
        else
            x = Math.Log(value / a) / b;

        double tolerance = 1e-10;
        if (x > 1) tolerance = 1e-10;

        for (int i = 0; i < 40; i++)
        {
            double ae2bx = a * Math.Exp(b * x);
            double ce2mdx = c / Math.Exp(d * x);
            double y;

            if (x < x_taylor)
                y = series_biexp(x) - value;
            else
                y = (ae2bx + f) - (ce2mdx + value);

            double abe2bx = b * ae2bx;
            double cde2mdx = d * ce2mdx;
            double dy = abe2bx + cde2mdx;
            double ddy = b * abe2bx - d * cde2mdx;

            double delta = y / (dy * (1 - y * ddy / (2 * dy * dy)));
            x -= delta;

            if (Math.Abs(delta) < tolerance)
                return negative ? 2 * x1 - x : x;
        }

        return -1;
    }

    private double series_biexp(double x)
    {
        x -= x1;
        double sum = taylor[TAYLOR_LENGTH - 1] * x;
        for (int i = TAYLOR_LENGTH - 2; i >= 2; i--)
            sum = (sum + taylor[i]) * x;
        return (sum * x + taylor[0]) * x;
    }

    internal double inverse_scale(double value)
    {
        bool negative = value < x1;
        if (negative) value = 2 * x1 - value;

        double inverse;
        if (value < x_taylor)
            inverse = series_biexp(value);
        else inverse = (a * Math.Exp(b * value) + f) - c / Math.Exp(d * value);

        return negative ? -inverse : inverse;
    }
}

public class LogicleTransform(
    double t = 262144.0,
    double w = 0.5,
    double m = 4.5,
    double a = 0.0) : ITransform
{
    private Logicle logicle = new Logicle(t, w, m, a);
    private double T = t;
    private double W = w;
    private double M = m;
    private double A = a;
    
    public double Transform(double data) => this.logicle.scale(data);
    public float Transform(float data) => Convert.ToSingle(this.logicle.scale(data));

    public void Transform(double[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.Transform(data[i]);
    }
    
    public void Transform(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.Transform(data[i]);
    }
    
    public double InverseTransform(double data) => this.logicle.inverse_scale(data);
    public float InverseTransform(float data) => Convert.ToSingle(this.logicle.inverse_scale(data));

    public void InverseTransform(double[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.InverseTransform(data[i]);
    }
    
    public void InverseTransform(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.InverseTransform(data[i]);
    }
    
    public bool IsEqual(ITransform other)
    {
        if (other is LogicleTransform log)
        {
            return (
                T == log.T &&
                W == log.W &&
                M == log.M &&
                A == log.A
            );
        }

        return false;
    }
}