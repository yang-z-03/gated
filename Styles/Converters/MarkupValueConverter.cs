
using System;
using System.Globalization;
using Avalonia.Data.Converters;

public interface IMarkupExtension<out TReturn>
{
    public TReturn ProvideValue(IServiceProvider serviceProvider);
}

public interface IMarkupExtension : IMarkupExtension<object>;

public abstract class MarkupValueConverter : IMarkupExtension<IValueConverter>, IValueConverter
{
    public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    public virtual object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    public virtual IValueConverter ProvideValue(IServiceProvider _) => this;
}