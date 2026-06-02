using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Reduction;

internal static class MatrixUtilities
{
    public static void Validate(float[,] data, string name)
    {
        if (data.GetLength(0) == 0)
            throw new ArgumentException("Input data must contain at least one observation.", name);
        if (data.GetLength(1) == 0)
            throw new ArgumentException("Input data must contain at least one variable.", name);
    }

    public static float[][] ToJagged(float[,] data)
    {
        int rows = data.GetLength(0);
        int columns = data.GetLength(1);
        var result = new float[rows][];
        for (int row = 0; row < rows; row++)
        {
            var values = new float[columns];
            for (int column = 0; column < columns; column++)
                values[column] = data[row, column];
            result[row] = values;
        }

        return result;
    }

    public static float[,] Copy(float[,] data)
    {
        var result = new float[data.GetLength(0), data.GetLength(1)];
        Array.Copy(data, result, data.Length);
        return result;
    }

    public static double[,] ToDouble(float[,] data)
    {
        int rows = data.GetLength(0);
        int columns = data.GetLength(1);
        var result = new double[rows, columns];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            result[row, column] = data[row, column];

        return result;
    }

    public static float[,] ToFloat(double[,] data)
    {
        int rows = data.GetLength(0);
        int columns = data.GetLength(1);
        var result = new float[rows, columns];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            result[row, column] = (float)data[row, column];

        return result;
    }

    public static int[] SortedUnique(int[] values) =>
        values.Distinct().OrderBy(static value => value).ToArray();

    public static double PercentileInSorted(double[] sorted_values, double quantile)
    {
        if (sorted_values.Length == 0)
            return double.NaN;
        if (quantile < 0 || quantile > 1)
            throw new ArgumentOutOfRangeException(nameof(quantile), "Quantile must be between 0 and 1.");

        double position = quantile * (sorted_values.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted_values[lower];

        double weight = position - lower;
        return sorted_values[lower] + (sorted_values[upper] - sorted_values[lower]) * weight;
    }

    public static double[] DefaultQuantiles(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Quantile count must be positive.");

        var result = new double[count];
        for (int index = 0; index < count; index++)
            result[index] = (index + 1.0) / (count + 1.0);
        return result;
    }

    public static bool HasSingleUniqueValue(double[] values)
    {
        var first = double.NaN;
        var initialized = false;
        foreach (double value in values)
        {
            if (double.IsNaN(value))
                continue;
            if (!initialized)
            {
                first = value;
                initialized = true;
                continue;
            }

            if (value != first)
                return false;
        }

        return true;
    }

    public static float SquaredEuclidean(float[,] data, int left, int right)
    {
        int columns = data.GetLength(1);
        float sum = 0;
        for (int column = 0; column < columns; column++)
        {
            float delta = data[left, column] - data[right, column];
            sum += delta * delta;
        }

        return sum;
    }
}
