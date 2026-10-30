namespace EzForensics.Core.Memory;

/// <summary>
/// A usable Volatility 3 installation. <paramref name="Exe"/> is <c>&lt;venv&gt;\Scripts\vol.exe</c>; <paramref name="Root"/> is the
/// component root (<c>symbols\</c> and <c>cache\</c> live next to the venv, whatever install was found).
/// </summary>
public sealed record VolatilityInstall(string Exe, string Python, string? Version, string Root, string SymbolsDir, string CacheDir);
