using System;
using System.ComponentModel;
using System.Data;
using Avalonia.Data;
using Avalonia.Data.Core.Plugins;
using Avalonia.Utilities;

public class DataRowViewPropertyAccessorPlugin : IPropertyAccessorPlugin
{
    public bool Match(object obj, string propertyName) => obj is DataRowView row && row.Row.Table.Columns.Contains(propertyName);

    public IPropertyAccessor? Start(WeakReference<object?> reference, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(propertyName);

        if (!reference.TryGetTarget(out var instance) || instance is null)
            return null;

        return new DataRowViewPropertyAccessor(reference, propertyName);
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
            row[propertyName] = value;

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