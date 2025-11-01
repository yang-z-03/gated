
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Gated.Styles.Locales;
namespace Gated;

public class Theme : Avalonia.Styling.Styles
{
    private static readonly Dictionary<CultureInfo, ResourceDictionary> _localeToResource = new()
    {
        { new CultureInfo("en-us"), new English() }
    };

    private static readonly ResourceDictionary _defaultResource = new English();

    private CultureInfo? _locale;

    public Theme(IServiceProvider? provider = null)
    {
        AvaloniaXamlLoader.Load(provider, this);
    }

    public CultureInfo? Locale
    {
        get => _locale;
        set
        {
            try
            {
                if (TryGetLocaleResource(value, out var resource) && resource is not null)
                {
                    _locale = value;
                    foreach (var kv in resource) Resources[kv.Key] = kv.Value;
                }
                else
                {
                    _locale = new CultureInfo("en-us");
                    foreach (var kv in _defaultResource) Resources[kv.Key] = kv.Value;
                }
            }
            catch
            {
                _locale = CultureInfo.InvariantCulture;
            }
        }
    }

    private static bool TryGetLocaleResource(CultureInfo? locale, out ResourceDictionary? resourceDictionary)
    {
        if (Equals(locale, CultureInfo.InvariantCulture))
        {
            resourceDictionary = _defaultResource;
            return true;
        }

        if (locale is null)
        {
            resourceDictionary = _defaultResource;
            return false;
        }

        if (_localeToResource.TryGetValue(locale, out var resource))
        {
            resourceDictionary = resource;
            return true;
        }

        resourceDictionary = _defaultResource;
        return false;
    }

    public static void OverrideLocaleResources(Application application, CultureInfo? culture)
    {
        if (culture is null) return;
        if (!_localeToResource.TryGetValue(culture, out var resources)) return;
        foreach (var kv in resources)
        {
            application.Resources[kv.Key] = kv.Value;
        }
    }

    public static void OverrideLocaleResources(StyledElement element, CultureInfo? culture)
    {
        if (culture is null) return;
        if (!_localeToResource.TryGetValue(culture, out var resources)) return;
        foreach (var kv in resources)
        {
            element.Resources[kv.Key] = kv.Value;
        }
    }
}