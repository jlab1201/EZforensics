using System.Collections.Concurrent;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>Frozen brushes for flag colours ("#RRGGBB"), cached per colour.</summary>
public static class FlagBrushes
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Solid = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Tint = new(StringComparer.OrdinalIgnoreCase);

    private static Color Parse(string? hex)
    {
        FlagCatalog.TryParseColor(hex, out var n);
        try { return (Color)ColorConverter.ConvertFromString(n); } catch { return Colors.Gray; }
    }

    public static Brush For(string? hex) => Solid.GetOrAdd(hex ?? "", h => { var b = new SolidColorBrush(Parse(h)); b.Freeze(); return b; });

    /// <summary>Same colour at ~15 % opacity (row tint).</summary>
    public static Brush TintFor(string? hex) => Tint.GetOrAdd(hex ?? "", h =>
    {
        var c = Parse(h); c.A = 0x26;
        var b = new SolidColorBrush(c); b.Freeze(); return b;
    });
}

/// <summary>One flag in the flag filter dropdown and the Flag menu: swatch, label, count in the open file.</summary>
public sealed partial class FlagOption : ObservableObject
{
    public FlagOption(FlagDefinition def, int number)
    {
        Id = def.Id;
        _label = def.Label;
        _color = def.Color;
        Number = number;
    }

    /// <summary>Stable flag id (what is stored).</summary>
    public string Id { get; }
    /// <summary>1-based position in the flag list (Ctrl+1 .. Ctrl+9).</summary>
    public int Number { get; }
    public Brush Brush => FlagBrushes.For(Color);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Brush))] private string _color;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Display))] private string _label;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    /// <summary>The label (what menus, tooltips and the filter show).</summary>
    public string Display => Label;
}
