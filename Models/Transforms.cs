using System;

namespace gated.Models;

public sealed class LogicleTransform
{
    private const int taylor_length = 16;
    private readonly double t;
    private readonly double w_parameter;
    private readonly double m;
    private readonly double a_parameter;
    private readonly double a;
    private readonly double b;
    private readonly double c;
    private readonly double d;
    private readonly double f;
    private readonly double w;
    private readonly double x1;
    private readonly double x_taylor;
    private readonly double[] taylor;

    public LogicleTransform(LogicleParameters parameters)
    {
        t = parameters.T;
        w_parameter = parameters.W;
        m = parameters.M;
        a_parameter = parameters.A;

        w = w_parameter / (m + a_parameter);
        double x2 = a_parameter / (m + a_parameter);
        x1 = x2 + w;
        double x0 = x2 + 2 * w;
        b = (m + a_parameter) * Math.Log(10.0);
        d = solve(b, w);

        double c_a = Math.Exp(x0 * (b + d));
        double mf_a = Math.Exp(b * x1) - c_a / Math.Exp(d * x1);
        a = t / ((Math.Exp(b) - mf_a) - c_a / Math.Exp(d));
        c = c_a * a;
        f = -mf_a * a;

        x_taylor = x1 + w / 4;
        taylor = new double[taylor_length];

        double positive_coefficient = a * Math.Exp(b * x1);
        double negative_coefficient = -c / Math.Exp(d * x1);
        for (int index = 0; index < taylor_length; index++)
        {
            positive_coefficient *= b / (index + 1);
            negative_coefficient *= -d / (index + 1);
            taylor[index] = positive_coefficient + negative_coefficient;
        }

        taylor[1] = 0;
    }

    public double Transform(double data)
    {
        if (data == 0)
            return x1;

        bool negative = data < 0;
        if (negative)
            data = -data;

        double value = data < f ? x1 + data / taylor[0] : Math.Log(data / a) / b;
        double tolerance = 1e-10;

        for (int index = 0; index < 40; index++)
        {
            double ae2bx = a * Math.Exp(b * value);
            double ce2mdx = c / Math.Exp(d * value);
            double y = value < x_taylor ? series_biexp(value) - data : (ae2bx + f) - (ce2mdx + data);
            double abe2bx = b * ae2bx;
            double cde2mdx = d * ce2mdx;
            double dy = abe2bx + cde2mdx;
            double ddy = b * abe2bx - d * cde2mdx;
            double delta = y / (dy * (1 - y * ddy / (2 * dy * dy)));
            value -= delta;

            if (Math.Abs(delta) < tolerance)
                return negative ? 2 * x1 - value : value;
        }

        return negative ? 2 * x1 - value : value;
    }

    public double InverseTransform(double data)
    {
        bool negative = data < x1;
        if (negative)
            data = 2 * x1 - data;

        double inverse = data < x_taylor
            ? series_biexp(data)
            : (a * Math.Exp(b * data) + f) - c / Math.Exp(d * data);

        return negative ? -inverse : inverse;
    }

    private static double solve(double b, double w)
    {
        if (w == 0)
            return b;

        double tolerance = 2 * b * double.Epsilon;
        double d_low = 0;
        double d_high = b;
        double d_value = (d_low + d_high) / 2;
        double last_delta = d_high - d_low;
        double f_b = -2 * Math.Log(b) + w * b;
        double function_value = 2 * Math.Log(d_value) + w * d_value + f_b;
        double last_function_value = double.NaN;

        for (int index = 0; index < 40; index++)
        {
            double df = 2 / d_value + w;
            double delta;

            if (((d_value - d_high) * df - function_value) * ((d_value - d_low) * df - function_value) >= 0 ||
                Math.Abs(1.9 * function_value) > Math.Abs(last_delta * df))
            {
                delta = (d_high - d_low) / 2;
                d_value = d_low + delta;
                if (d_value == d_low)
                    return d_value;
            }
            else
            {
                delta = function_value / df;
                double previous_value = d_value;
                d_value -= delta;
                if (d_value == previous_value)
                    return d_value;
            }

            if (Math.Abs(delta) < tolerance)
                return d_value;

            last_delta = delta;
            function_value = 2 * Math.Log(d_value) + w * d_value + f_b;
            if (function_value == 0 || function_value == last_function_value)
                return d_value;

            last_function_value = function_value;
            if (function_value < 0)
                d_low = d_value;
            else d_high = d_value;
        }

        return d_value;
    }

    private double series_biexp(double value)
    {
        value -= x1;
        double sum = taylor[taylor_length - 1] * value;
        for (int index = taylor_length - 2; index >= 2; index--)
            sum = (sum + taylor[index]) * value;
        return (sum * value + taylor[0]) * value;
    }
}
