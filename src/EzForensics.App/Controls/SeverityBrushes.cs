using System.Windows.Media;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>Severity (rank 3+ / 2 / 1 / other) to the themed Error / Warning / Info / Neutral brush, shared by the OnRender controls.</summary>
internal static class SeverityBrushes
{
    /// <param name="res">Resource lookup with fallback, e.g. the control's own <c>Res(key, fallback)</c>.</param>
    public static Brush Of(int rank, Func<string, Brush, Brush> res) => rank switch
    {
        >= 3 => res("Brush.Error", Brushes.IndianRed),
        2 => res("Brush.Warning", Brushes.Orange),
        1 => res("Brush.Info", Brushes.SteelBlue),
        _ => res("Brush.Neutral", Brushes.Gray),
    };

    public static Brush Of(string? severity, Func<string, Brush, Brush> res) => Of(SpectreFinding.Rank(severity), res);
}
