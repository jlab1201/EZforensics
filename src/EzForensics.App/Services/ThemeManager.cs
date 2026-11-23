using System.Windows;
using EzForensics.Core.Settings;

namespace EzForensics.App.Services;

/// <summary>Swaps the colour dictionary (Themes/Dark.xaml or Themes/Light.xaml) at runtime.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after the colour dictionary was swapped (title bars follow it, see <see cref="TitleBarTheme"/>).</summary>
    public static event Action<AppTheme>? Changed;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;
        // Assembly-qualified so it also resolves when this assembly is not the entry one (the snapshot tests).
        var uri = new Uri($"pack://application:,,,/EzForensics;component/Themes/{theme}.xaml", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };
        var merged = app.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        else
        {
            // Remove the colour dictionary declared in App.xaml (the one that is not Controls.xaml).
            var initial = merged.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                 d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)));
            if (initial != null) merged.Remove(initial);
        }
        merged.Insert(0, dict);
        _current = dict;
        Current = theme;
        Changed?.Invoke(theme);
    }
}
