namespace EzForensics.Core.Memory;

/// <summary>
/// Decides which Volatility stderr lines go to the job log file. Volatility redraws <c>Progress:  12.34\t\tReading Symbol layer</c> tens of
/// thousands of times (and the <c>\r</c> redraws leave blank lines): only a changed stage text, a percentage that advanced by
/// <see cref="PercentStep"/> or more (or went backwards), the end (100) or <see cref="MinInterval"/> since the last written line are kept.
/// Every other line passes. Not thread safe on its own (the process runner serialises the callbacks).
/// </summary>
public sealed class VolatilityProgressThrottle
{
    public const double PercentStep = 5;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _now;
    private string? _stage;
    private double _percent = double.NaN;
    private DateTime _lastWrite = DateTime.MinValue;

    public VolatilityProgressThrottle(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>True for blank lines (leftovers of <c>\r</c> splitting), which are never logged.</summary>
    public static bool IsBlank(string? line) => string.IsNullOrWhiteSpace(line);

    /// <summary>True when the line should be written to the log file.</summary>
    public bool ShouldLog(string? line)
    {
        if (IsBlank(line)) return false;
        var p = VolatilityProgressParser.Parse(line);
        if (p is not double pct) return true;
        var stage = VolatilityProgressParser.Status(line) ?? "";
        var now = _now();
        bool write = _stage == null || !string.Equals(stage, _stage, StringComparison.Ordinal)
            || double.IsNaN(_percent) || pct < _percent || pct - _percent >= PercentStep
            || (pct >= 100 && _percent < 100)
            || now - _lastWrite >= MinInterval;
        if (!write) return false;
        _stage = stage; _percent = pct; _lastWrite = now;
        return true;
    }
}
