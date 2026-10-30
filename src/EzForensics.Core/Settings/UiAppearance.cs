namespace EzForensics.Core.Settings;

/// <summary>App-wide UI zoom: 50 %–250 % in 10 % steps (Ctrl+= / Ctrl+- / Ctrl+0, Ctrl+wheel).</summary>
public static class UiZoom
{
    public const double Min = 0.5;
    public const double Max = 2.5;
    public const double Step = 0.1;
    public const double Default = 1.0;

    /// <summary>Clamps to [Min, Max] and rounds to whole percent; NaN/infinity give the default.</summary>
    public static double Clamp(double zoom) =>
        double.IsNaN(zoom) || double.IsInfinity(zoom) ? Default : Math.Round(Math.Clamp(zoom, Min, Max), 2);

    /// <summary>Next 10 % step up (an off-grid value snaps to the next grid value above it).</summary>
    public static double In(double zoom)
    {
        var z = Clamp(zoom);
        return Clamp(Math.Round(Math.Floor(z * 10 + 1e-6) / 10 + Step, 1));
    }

    /// <summary>Next 10 % step down (an off-grid value snaps to the next grid value below it).</summary>
    public static double Out(double zoom)
    {
        var z = Clamp(zoom);
        return Clamp(Math.Round(Math.Ceiling(z * 10 - 1e-6) / 10 - Step, 1));
    }

    /// <summary>"110%".</summary>
    public static string Format(double zoom) => $"{Math.Round(Clamp(zoom) * 100):0}%";
}

/// <summary>Defaults and limits for the UI and monospace font settings.</summary>
public static class UiFonts
{
    public const string DefaultUiFamily = "Segoe UI Variable Text, Segoe UI";
    public const string DefaultMonoFamily = "Cascadia Mono, Consolas, Courier New";
    public const double DefaultUiSize = 13;
    public const double DefaultMonoSize = 12;
    public const double MinSize = 8;
    public const double MaxSize = 32;

    public static double ClampSize(double size, double fallback) =>
        double.IsNaN(size) || double.IsInfinity(size) || size <= 0 ? fallback : Math.Round(Math.Clamp(size, MinSize, MaxSize), 1);

    public static string Family(string? family, string fallback) =>
        string.IsNullOrWhiteSpace(family) ? fallback : family.Trim();

    /// <summary>Results grid row height for a monospace font size (24 px at the default 12 pt; fixed so row virtualization stays cheap).</summary>
    public static double GridRowHeight(double monoSize) => Math.Ceiling(ClampSize(monoSize, DefaultMonoSize) * 2);
}
