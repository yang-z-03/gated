using System;
using System.Linq;
using gated.Models;

namespace gated.Reduction;

public sealed class LogicleNormalizationOptions
{
    public LogicleParameters Parameters { get; init; } = new();
    public int[]? Columns { get; init; }
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
        var selected_columns = options.Columns ?? Enumerable.Range(0, columns).ToArray();
        foreach (int column in selected_columns)
        {
            if (column < 0 || column >= columns)
                throw new ArgumentOutOfRangeException(nameof(options.Columns), "Column index is outside the input matrix.");

            for (int row = 0; row < rows; row++)
                data[row, column] = (float)transform.Transform(data[row, column]);
        }
    }
}
