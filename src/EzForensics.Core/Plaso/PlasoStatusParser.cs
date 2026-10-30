using System.Globalization;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Plaso;

/// <summary>
/// Incremental parser for <c>--status_view linear</c> output. log2timeline prints blocks like
/// <code>
/// Processing time: 00:01:07
/// Main (PID: 36824) status: running, event data produced: 35144, events produced: 41129, file: OS:C:\...
/// Worker_00 (PID: 127932) status: idle, event data produced: 0, file: OS:...
/// </code>
/// psort prints <c>Main (PID: n) status: exporting, events consumed: 8414</c>. Worker lines are ignored.
/// </summary>
public sealed class PlasoStatusParser
{
    private static readonly Regex TimeRx = new(@"^\s*Processing time\s*:?\s*(?<t>\d+:\d\d:\d\d)", RegexOptions.Compiled);
    private static readonly Regex MainRx = new(
        @"^\s*Main \(PID: \d+\) status: (?<s>[\w ]+?)(?:,|$)(?:.*?event data produced: (?<ed>\d+))?(?:.*?events produced: (?<ep>\d+))?(?:.*?events consumed: (?<ec>\d+))?(?:.*?file: (?<f>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex WarningsRx = new(@"Number of warnings generated while extracting events: (?<n>\d+)", RegexOptions.Compiled);

    private readonly PlasoPhase _phase;
    private string? _time;

    public PlasoStatusParser(PlasoPhase phase) => _phase = phase;

    public PlasoStatus? Last { get; private set; }

    /// <summary>Feeds one output line; returns the new status when the line changed it, otherwise null.</summary>
    public PlasoStatus? Feed(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var t = TimeRx.Match(line);
        if (t.Success) { _time = t.Groups["t"].Value; return null; }

        var w = WarningsRx.Match(line);
        if (w.Success)
            return Last = (Last ?? new PlasoStatus(_phase, null, _time, null, null, null, null)) with { Warnings = int.Parse(w.Groups["n"].Value, CultureInfo.InvariantCulture) };

        if (line.TrimStart().StartsWith("Processing completed", StringComparison.OrdinalIgnoreCase))
            return Last = (Last ?? new PlasoStatus(_phase, null, _time, null, null, null, null)) with { Status = "completed" };

        var m = MainRx.Match(line);
        if (!m.Success) return null;
        long? N(string g) => m.Groups[g].Success && long.TryParse(m.Groups[g].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        var events = N("ep") ?? N("ec");
        var file = m.Groups["f"].Success ? m.Groups["f"].Value.Trim() : null;
        if (file != null && file.StartsWith("OS:", StringComparison.Ordinal)) file = file[3..];
        return Last = new PlasoStatus(_phase, m.Groups["s"].Value.Trim(), _time, events ?? Last?.Events, N("ed"), string.IsNullOrEmpty(file) ? null : file, Last?.Warnings);
    }
}
