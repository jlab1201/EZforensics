using System.Text;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Commands;

/// <summary>Parsed "Additional arguments" box: the argv elements and what is wrong with them (nothing is passed on while there are errors).</summary>
public sealed record ExtraArgsParse(IReadOnlyList<string> Args, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The user's additional arguments of the commands the app builds itself (plaso, Zeek, RITA, Volatility), saved in
/// <see cref="AppSettings.ToolExtraArgs"/> by command id and used by the Tools page and Ingest alike. The builders place them before the
/// positional source / output arguments; flags the app sets itself (storage, output, input, symbols...) are rejected.
/// </summary>
public static class ExtraArgsStore
{
    public const string PlasoLog2Timeline = "plaso.log2timeline";
    public const string PlasoPsort = "plaso.psort";
    public const string Zeek = "zeek";
    public const string RitaImport = "rita.import";
    public const string VolatilityPrefix = "volatility.";

    /// <summary>Command id of one Volatility plugin (plugins.json <c>id</c>).</summary>
    public static string Volatility(string pluginId) => VolatilityPrefix + pluginId;

    /// <summary>The saved text ("" when none).</summary>
    public static string Get(AppSettings s, string id) => s.ToolExtraArgs.TryGetValue(id, out var v) ? v : "";

    /// <summary>Saves (trimmed) or removes (blank) the text of <paramref name="id"/>; the caller saves the settings.</summary>
    public static void Set(AppSettings s, string id, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) s.ToolExtraArgs.Remove(id);
        else s.ToolExtraArgs[id] = text.Trim();
    }

    /// <summary>The saved arguments of <paramref name="id"/>, or none when the saved text does not parse (an invalid value is never run).</summary>
    public static IReadOnlyList<string> Args(AppSettings s, string id)
    {
        var p = Parse(id, Get(s, id));
        return p.IsValid ? p.Args : Array.Empty<string>();
    }

    /// <summary>The saved text of <paramref name="id"/> when it parses, else null (for builders that take the text, e.g. Volatility's).</summary>
    public static string? ValidText(AppSettings s, string id)
    {
        var t = Get(s, id);
        return t.Length > 0 && Parse(id, t).IsValid ? t : null;
    }

    /// <summary>
    /// Splits on white space; <c>"..."</c> groups (also inside a word: <c>--x="a b"</c> → <c>--x=a b</c>), <c>""</c> is an empty argument.
    /// Backslashes are literal (Windows paths). <paramref name="error"/> is set for an unbalanced quote (the rest is then one argument).
    /// </summary>
    public static IReadOnlyList<string> Split(string? text, out string? error)
    {
        error = null;
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var sb = new StringBuilder();
        bool inQuote = false, any = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuote = !inQuote; any = true; continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (any) { list.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (inQuote) error = "Unbalanced quote: close the \" or remove it.";
        if (any) list.Add(sb.ToString());
        return list;
    }

    /// <summary>A flag the app sets itself: the flag and why it is not accepted.</summary>
    private sealed record Managed(string Flag, string Why);

    private const string ByApp = "set by EZ Forensics";
    private const string ByOptions = "set in Timeline options";

    private static readonly Managed[] Log2TimelineFlags =
    {
        new("--storage_file", ByApp + " (the case's storage file)"), new("--logfile", ByApp + " (the job's log file)"),
        new("--status_view", ByApp + " (the job reads the linear status)"), new("--unattended", ByOptions),
        new("--parsers", ByOptions), new("--timezone", ByOptions), new("-z", ByOptions), new("--workers", ByOptions),
        new("--vss_stores", ByOptions), new("--no_vss", ByOptions),
    };

    private static readonly Managed[] PsortFlags =
    {
        new("-o", ByApp + " (output format: Timeline options)"), new("--output_format", ByApp + " (output format: Timeline options)"),
        new("-w", ByApp + " (the timeline CSV)"), new("--write", ByApp + " (the timeline CSV)"),
        new("--output_time_zone", ByApp + " (UTC)"), new("--fields", ByApp + " (the dynamic columns)"),
        new("--logfile", ByApp + " (the job's log file)"), new("--status_view", ByApp + " (the job reads the linear status)"),
        new("--unattended", ByOptions),
    };

    private static readonly Managed[] ZeekFlags =
    {
        new("-r", ByApp + " (the capture)"), new("--readfile", ByApp + " (the capture)"),
        new("-C", ByApp + " (checksums are always ignored)"), new("--no-checksums", ByApp + " (checksums are always ignored)"),
        new("-i", "not supported (live capture)"), new("--iface", "not supported (live capture)"),
    };

    private static readonly Managed[] RitaImportFlags =
    {
        new("--config", ByApp + " (the analysis config)"), new("-c", ByApp + " (the analysis config)"), new("-config", ByApp + " (the analysis config)"),
        new("--database", ByApp + " (one database per case and host)"), new("-d", ByApp + " (one database per case and host)"), new("-database", ByApp + " (one database per case and host)"),
        new("--logs", ByApp + " (the staged Zeek logs)"), new("-l", ByApp + " (the staged Zeek logs)"), new("-logs", ByApp + " (the staged Zeek logs)"),
        new("--rebuild", ByApp + " (every import rebuilds)"), new("-r", ByApp + " (every import rebuilds)"), new("-rebuild", ByApp + " (every import rebuilds)"),
    };

    private static readonly Managed[] VolatilityFlags =
    {
        new("-f", ByApp + " (the memory image)"), new("--file", ByApp + " (the memory image)"),
        new("-r", ByApp + " (CSV output)"), new("--renderer", ByApp + " (CSV output)"),
        new("-o", ByApp + " (the job's output folder)"), new("--output-dir", ByApp + " (the job's output folder)"),
        new("-s", ByApp + " (the symbols folder)"), new("--symbol-dirs", ByApp + " (the symbols folder)"), new("--cache-path", ByApp + " (the cache folder)"),
        new("--offline", ByApp + " (the Offline option under Symbols)"), new("--single-swap-locations", ByApp + " (the pagefile option)"),
        new("-q", ByApp + " (the job reads the progress lines)"), new("--quiet", ByApp + " (the job reads the progress lines)"),
    };

    /// <summary>
    /// The flags <paramref name="id"/> manages, whether its parser accepts abbreviated long options (Python argparse) and grouped / attached
    /// short options (argparse, getopt; not RITA's urfave/cli, where <c>-rolling</c> is one flag).
    /// </summary>
    private static (Managed[] Flags, bool Abbrev, bool Grouping) FlagsOf(string id) => id switch
    {
        PlasoLog2Timeline => (Log2TimelineFlags, true, true),
        PlasoPsort => (PsortFlags, true, true),
        Zeek => (ZeekFlags, false, true),
        RitaImport => (RitaImportFlags, false, false),
        _ when id.StartsWith(VolatilityPrefix, StringComparison.Ordinal) => (VolatilityFlags, true, true),
        _ => (Array.Empty<Managed>(), false, false),
    };

    /// <summary>Splits <paramref name="text"/> and checks it against the flags <paramref name="id"/>'s command sets itself.</summary>
    public static ExtraArgsParse Parse(string id, string? text)
    {
        var args = Split(text, out var quoteError);
        var errors = new List<string>();
        if (quoteError != null) errors.Add(quoteError);
        var (flags, abbrev, grouping) = FlagsOf(id);
        foreach (var a in args)
        {
            var m = Match(a, flags, abbrev, grouping);
            if (m != null) errors.Add($"{m.Flag} is {m.Why}; remove it from the additional arguments.");
            else if (id == Zeek && a.StartsWith("LogAscii::use_json", StringComparison.OrdinalIgnoreCase))
                errors.Add("LogAscii::use_json is set by EZ Forensics (the CSV conversion reads Zeek's TSV logs); remove it.");
        }
        return new ExtraArgsParse(args, errors.Distinct(StringComparer.Ordinal).ToList());
    }

    private static Managed? Match(string arg, Managed[] flags, bool abbrev, bool grouping)
    {
        if (arg.Length < 2 || arg[0] != '-' || arg == "--") return null;
        var name = arg.Split('=', 2)[0];
        foreach (var f in flags)
        {
            bool shortFlag = f.Flag.Length == 2;
            if (string.Equals(name, f.Flag, StringComparison.Ordinal)) return f;
            // "-fimage.raw" (short flag with its value attached); "-Cx" (grouped short flags)
            if (grouping && shortFlag && !arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2 && arg[1] == f.Flag[1]) return f;
            // argparse accepts any unambiguous prefix of a long option ("--storage" = "--storage_file")
            if (abbrev && !shortFlag && name.StartsWith("--", StringComparison.Ordinal) && name.Length >= 4
                && f.Flag.StartsWith(name, StringComparison.Ordinal)) return f;
        }
        return null;
    }

    /// <summary>Display form of argv elements (quoted when they contain white space or a quote).</summary>
    public static string Join(IEnumerable<string> args) =>
        string.Join(" ", args.Select(s => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s));
}
