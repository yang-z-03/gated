using System;
using System.ComponentModel;
using System.Globalization;
using System.Data;
using System.Linq;

public sealed class DataRowViewAdapter
{
    public DataRowViewAdapter(DataRowView row_view)
    {
        ArgumentNullException.ThrowIfNull(row_view);
        Cells = Enumerable.Range(0, row_view.Row.Table.Columns.Count)
            .Select(ordinal => new DataRowCellAdapter(row_view.Row, ordinal))
            .ToArray();
    }

    public DataRowCellAdapter[] Cells { get; }
}

public sealed class DataRowCellAdapter : INotifyPropertyChanged
{
    private readonly DataRow row;
    private readonly int ordinal;

    public DataRowCellAdapter(DataRow row, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(row);
        this.row = row;
        this.ordinal = ordinal;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? Value
    {
        get => ordinal >= 0 && ordinal < row.Table.Columns.Count ? row[ordinal] : null;
        set
        {
            if (ordinal < 0 || ordinal >= row.Table.Columns.Count)
                return;
            row[ordinal] = convert_value(ordinal, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    private object convert_value(int ordinal, object? value)
    {
        var column = row.Table.Columns[ordinal];
        if (column is null)
            return value ?? DBNull.Value;

        if (value is null || value == DBNull.Value)
            return DBNull.Value;

        if (value is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DBNull.Value;
            if (column.DataType == typeof(int))
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int int_value)
                    ? int_value
                    : row[ordinal];
            if (column.DataType == typeof(double))
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double double_value)
                    ? double_value
                    : row[ordinal];
            if (column.DataType == typeof(float))
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float float_value)
                    ? float_value
                    : row[ordinal];
            return text;
        }

        if (column.DataType.IsInstanceOfType(value))
            return value;

        try
        {
            return Convert.ChangeType(value, column.DataType, CultureInfo.InvariantCulture) ?? DBNull.Value;
        }
        catch
        {
            return row[ordinal];
        }
    }
}
