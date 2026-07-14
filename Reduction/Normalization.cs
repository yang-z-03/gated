using System;
using System.Linq;
using gated.Models;

namespace gated.Reduction;

public sealed class LogicleNormalizationOptions
{
    public LogicleParameters Parameters { get; init; } = new();
    public int[]? Columns { get; init; }
    public CoordinateScaleKind[,]? Scales { get; init; }
}

public static class LogicleNormalization
{
    public static float[,] Transform(float[,] data, LogicleNormalizationOptions? options = null)
    {
        options ??= new LogicleNormalizationOptions();
        MatrixUtilities.Validate(data, nameof(data));
        var result = MatrixUtilities.Copy(data);
        TransformInPlace(result, options);
        return result;
    }

    public static void TransformInPlace(float[,] data, LogicleNormalizationOptions? options = null)
    {
        options ??= new LogicleNormalizationOptions();
        var transform = new LogicleTransform(options.Parameters);
        int rows = data.GetLength(0);
        int columns = data.GetLength(1);
        if (options.Scales is not null)
        {
            apply_metadata_aware_transform(data, options.Scales, transform);
            return;
        }
        var selected_columns = options.Columns ?? Enumerable.Range(0, columns).ToArray();
        foreach (int column in selected_columns)
        {
            if (column < 0 || column >= columns)
                throw new ArgumentOutOfRangeException(nameof(options.Columns), "Column index is outside the input matrix.");

            for (int row = 0; row < rows; row++)
                data[row, column] = (float)transform.Transform(data[row, column]);
        }
    }

    private static void apply_metadata_aware_transform(float[,] data, CoordinateScaleKind[,] scales, LogicleTransform transform)
    {
        int rows = data.GetLength(0);
        int columns = data.GetLength(1);
        if (scales.GetLength(0) != rows || scales.GetLength(1) != columns)
            throw new ArgumentException("Scale metadata dimensions must match the input matrix.", nameof(scales));

        var linear_means = new double[columns];
        var linear_stdevs = new double[columns];
        for (int column = 0; column < columns; column++)
        {
            double sum = 0;
            int count = 0;
            for (int row = 0; row < rows; row++)
            {
                if (scales[row, column] != CoordinateScaleKind.Linear)
                    continue;
                double value = data[row, column];
                if (!double.IsFinite(value))
                    continue;
                sum += value;
                count++;
            }

            if (count == 0)
            {
                linear_stdevs[column] = 1;
                continue;
            }

            double mean = sum / count;
            double variance = 0;
            for (int row = 0; row < rows; row++)
            {
                if (scales[row, column] != CoordinateScaleKind.Linear)
                    continue;
                double value = data[row, column];
                if (!double.IsFinite(value))
                    continue;
                double delta = value - mean;
                variance += delta * delta;
            }

            linear_means[column] = mean;
            linear_stdevs[column] = count > 1 ? Math.Sqrt(variance / (count - 1)) : 1;
            if (!double.IsFinite(linear_stdevs[column]) || linear_stdevs[column] <= 0)
                linear_stdevs[column] = 1;
        }

        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            double value = data[row, column];
            if (!double.IsFinite(value))
            {
                data[row, column] = float.NaN;
                continue;
            }

            data[row, column] = scales[row, column] switch
            {
                CoordinateScaleKind.Linear => (float)((value - linear_means[column]) / linear_stdevs[column]),
                CoordinateScaleKind.Logarithmic => (float)(Math.Sign(value) * Math.Log10(1.0 + Math.Abs(value))),
                CoordinateScaleKind.Arcsinh => (float)Math.Asinh(value / 5.0),
                _ => (float)transform.Transform(value)
            };
        }
    }
}
