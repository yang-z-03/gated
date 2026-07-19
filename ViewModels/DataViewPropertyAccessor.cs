using System;
using System.ComponentModel;
using System.Globalization;
using System.Data;
using Avalonia.Data;
using Avalonia.Data.Core.Plugins;
using Avalonia.Utilities;

public class DataRowViewPropertyAccessorPlugin : IPropertyAccessorPlugin
{
    private const string ordinal_property_prefix = "__gated_data_column_";

    public static string BindingPropertyName(DataColumn column) =>
        $"{ordinal_property_prefix}{column.Ordinal.ToString(CultureInfo.InvariantCulture)}";

    public bool Match(object obj, string propertyName) =>
        obj is DataRowView row && try_resolve_column_name(row, propertyName, out _);

    public IPropertyAccessor? Start(WeakReference<object?> reference, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(propertyName);

        if (!reference.TryGetTarget(out var instance) || instance is not DataRowView row ||
            !try_resolve_column_name(row, propertyName, out string column_name))
            return null;

        return new DataRowViewPropertyAccessor(reference, column_name);
    }

    private static bool try_resolve_column_name(DataRowView row, string property_name, out string column_name)
    {
        column_name = "";
        if (property_name.StartsWith(ordinal_property_prefix, StringComparison.Ordinal) &&
            int.TryParse(property_name[ordinal_property_prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) &&
            ordinal >= 0 && ordinal < row.Row.Table.Columns.Count)
        {
            column_name = row.Row.Table.Columns[ordinal].ColumnName;
            return true;
        }

        if (!row.Row.Table.Columns.Contains(property_name))
            return false;

        column_name = property_name;
        return true;
    }
}

public class DataRowViewPropertyAccessor : PropertyAccessorBase, IWeakEventSubscriber<PropertyChangedEventArgs>
{
    private readonly WeakReference<object?> reference;
    private readonly string propertyName;
    private bool eventRaised;

    public DataRowViewPropertyAccessor(WeakReference<object?> reference, string propertyName)
    {
        this.reference = reference;
        this.propertyName = propertyName;
    }

    public override Type? PropertyType => GetReferenceTarget()?.Row?.Table?.Columns?[propertyName]?.DataType;

    public override object? Value => GetReferenceTarget()?[propertyName];

    public void OnEvent(object? sender, WeakEvent ev, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == propertyName)
        {
            eventRaised = true;
            SendCurrentValue();
        }
    }

    public override bool SetValue(object? value, BindingPriority priority)
    {
        eventRaised = false;

        var row = GetReferenceTarget();
        if(row is not null)
            row[propertyName] = convert_value(row, value);

        if (!eventRaised)
        {
            SendCurrentValue();
        }
        return true;
    }

    protected override void SubscribeCore()
    {
        if (GetReferenceTarget() is INotifyPropertyChanged inpc)
            WeakEvents.ThreadSafePropertyChanged.Subscribe(inpc, this);

        SendCurrentValue();
    }

    protected override void UnsubscribeCore()
    {
        if (GetReferenceTarget() is INotifyPropertyChanged inpc)
            WeakEvents.ThreadSafePropertyChanged.Unsubscribe(inpc, this);
    }

    private DataRowView? GetReferenceTarget()
    {
        reference.TryGetTarget(out var target);
        return target as DataRowView;
    }

    private object convert_value(DataRowView row, object? value)
    {
        var column = row.Row.Table.Columns[propertyName];
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
                    : row[propertyName];
            if (column.DataType == typeof(double))
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double double_value)
                    ? double_value
                    : row[propertyName];
            if (column.DataType == typeof(float))
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float float_value)
                    ? float_value
                    : row[propertyName];
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
            return row[propertyName];
        }
    }

    private void SendCurrentValue()
    {
        try
        {
            var value = Value;
            PublishValue(value);
        }
        catch
        {
            // ignored
        }
    }
}
