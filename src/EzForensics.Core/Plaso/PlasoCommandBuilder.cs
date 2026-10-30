using System.Globalization;

namespace EzForensics.Core.Plaso;

/// <summary>
/// argv for log2timeline and psort (checked against plaso 20260720 <c>--help</c>; every option exists since 2020 except
/// <c>--vss_stores none</c>, which replaces the deprecated <c>--no_vss</c>). Every value is one argv element.
/// </summary>
public static class PlasoCommandBuilder
{
    /// <summary>psort dynamic fields: plaso's default set plus macb.</summary>
    public const string DynamicFields = "datetime,timestamp_desc,macb,source,source_long,message,parser,display_name,tag";

    /// <summary>First plaso release whose log2timeline accepts <c>--vss_stores none</c> (older ones use <c>--no_vss</c>).</summary>
    public const long VssStoresNoneSince = 20220724; // approximate; --no_vss is still accepted (deprecated) by newer releases

    // Container side paths (Docker engine).
    public const string ContainerSource = "/data/source";
    public const string ContainerTimeline = "/data/timeline";

    public static PlasoInvocation BuildLog2Timeline(PlasoInstall install, PlasoRunOptions o) => BuildLog2Timeline(install, o, 0);

    /// <summary>
    /// log2timeline for the <paramref name="sourceIndex"/>-th of <see cref="PlasoRunOptions.AllSources"/>: every run writes the same storage file
    /// (later runs append a session) and its own log file (<see cref="PlasoRunOptions.Log2TimelineLogFor"/>).
    /// </summary>
    public static PlasoInvocation BuildLog2Timeline(PlasoInstall install, PlasoRunOptions o, int sourceIndex)
    {
        Validate(o);
        var sources = o.AllSources;
        if (sourceIndex < 0 || sourceIndex >= sources.Count) throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        var source = sources[sourceIndex];
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A plaso source (evidence folder or image) is empty.");
        bool sourceIsFile = File.Exists(source);
        bool docker = install.Kind == PlasoEngineKind.Docker;
        var tool = new List<string>();
        tool.Add("--status_view"); tool.Add("linear");
        if (o.Unattended) tool.Add("--unattended");
        var log = o.Log2TimelineLogFor(sourceIndex);
        tool.Add("--logfile"); tool.Add(docker ? ToContainer(o, log) : log);
        tool.Add("--storage_file"); tool.Add(docker ? ToContainer(o, o.StorageFile) : o.StorageFile);
        if (!string.IsNullOrWhiteSpace(o.Parsers)) { tool.Add("--parsers"); tool.Add(o.Parsers!.Trim()); }
        if (o.TimezoneUtc) { tool.Add("--timezone"); tool.Add("UTC"); }
        if (o.Workers is > 0) { tool.Add("--workers"); tool.Add(o.Workers.Value.ToString(CultureInfo.InvariantCulture)); }
        if (o.Vss == PlasoVssMode.All) { tool.Add("--vss_stores"); tool.Add("all"); }
        else if (install.VersionNumber is > 0 and < VssStoresNoneSince) tool.Add("--no_vss");
        else { tool.Add("--vss_stores"); tool.Add("none"); }
        if (sourceIsFile)
        {
            // Images with several partitions/volumes would otherwise prompt (or fail in unattended mode).
            tool.Add("--partitions"); tool.Add("all");
            tool.Add("--volumes"); tool.Add("all");
        }
        tool.AddRange(o.ExtraLog2TimelineArgs);   // the user's additional arguments: after ours, before the source
        tool.Add(docker ? ContainerSourcePath(source, sourceIsFile) : source);
        return Wrap(install, install.Log2Timeline, "log2timeline", tool, o, ContainerName(o, sourceIndex == 0 ? "l2t" : $"l2t{sourceIndex + 1}"), source, sourceIsFile);
    }

    public static PlasoInvocation BuildPsort(PlasoInstall install, PlasoRunOptions o)
    {
        Validate(o);
        bool docker = install.Kind == PlasoEngineKind.Docker;
        var tool = new List<string> { "--status_view", "linear" };
        if (o.Unattended) tool.Add("--unattended");
        tool.Add("--logfile"); tool.Add(docker ? ToContainer(o, o.PsortLog) : o.PsortLog);
        if (o.Format == PlasoOutputFormat.L2tCsv)
        {
            tool.Add("-o"); tool.Add("l2tcsv");
            tool.Add("--output_time_zone"); tool.Add("UTC");
        }
        else
        {
            tool.Add("-o"); tool.Add("dynamic");
            tool.Add("--fields"); tool.Add(DynamicFields);
            tool.Add("--output_time_zone"); tool.Add("UTC");
        }
        tool.AddRange(o.ExtraPsortArgs);   // the user's additional arguments: before the output file, storage file and filter
        tool.Add("-w"); tool.Add(docker ? ToContainer(o, o.OutputCsv) : o.OutputCsv);
        tool.Add(docker ? ToContainer(o, o.StorageFile) : o.StorageFile);
        var filter = BuildDateFilter(o.FromUtc, o.ToUtc);
        if (filter != null) tool.Add(filter);
        return Wrap(install, install.Psort, "psort", tool, o, ContainerName(o, "psort"), o.Source, o.SourceIsFile);
    }

    /// <summary>
    /// psort event filter for an inclusive UTC range, e.g.
    /// <c>date &gt;= DATETIME('2020-01-01T00:00:00') AND date &lt;= DATETIME('2020-01-31T23:59:59')</c>; null when unbounded.
    /// </summary>
    public static string? BuildDateFilter(DateTime? fromUtc, DateTime? toUtc)
    {
        static string D(DateTime d) => (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (fromUtc.HasValue) parts.Add($"date >= DATETIME('{D(fromUtc.Value)}')");
        if (toUtc.HasValue) parts.Add($"date <= DATETIME('{D(toUtc.Value)}')");
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    private static void Validate(PlasoRunOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Source)) throw new ArgumentException("The plaso source (evidence folder or image) is empty.");
        if (string.IsNullOrWhiteSpace(o.CaseDir)) throw new ArgumentException("The case folder is empty.");
    }

    private static string ContainerName(PlasoRunOptions o, string step) =>
        "ezforensics-plaso-" + new string(o.Stamp.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() + "-" + step;

    private static PlasoInvocation Wrap(PlasoInstall install, PlasoCommand cmd, string toolName, List<string> toolArgs, PlasoRunOptions o, string container,
        string source, bool sourceIsFile)
    {
        if (install.Kind != PlasoEngineKind.Docker)
            return new PlasoInvocation(cmd.Exe, cmd.PrefixArgs.Concat(toolArgs).ToList(), null, null);

        var srcHost = sourceIsFile ? Path.GetDirectoryName(Path.GetFullPath(source))! : Path.GetFullPath(source);
        var args = new List<string>
        {
            "run", "--rm", "--name", container, "-e", "PYTHONUNBUFFERED=1",
            "--mount", MountSpec(srcHost, ContainerSource, readOnly: true),
            "--mount", MountSpec(Path.GetFullPath(o.TimelineDir), ContainerTimeline, readOnly: false),
            install.DockerImage, toolName,
        };
        args.AddRange(toolArgs);
        return new PlasoInvocation(cmd.Exe, args, container, new[] { cmd.Exe, "stop", "--time", "5", container });
    }

    /// <summary><c>type=bind,source=...,target=...[,readonly]</c>; a source with a comma or quote is CSV-quoted.</summary>
    public static string MountSpec(string hostPath, string target, bool readOnly)
    {
        var src = "source=" + hostPath;
        if (src.IndexOfAny(new[] { ',', '"' }) >= 0) src = "\"" + src.Replace("\"", "\"\"") + "\"";
        return $"type=bind,{src},target={target}" + (readOnly ? ",readonly" : "");
    }

    private static string ContainerSourcePath(string source, bool sourceIsFile) =>
        sourceIsFile ? ContainerSource + "/" + Path.GetFileName(source) : ContainerSource;

    /// <summary>Maps a path under the case Timeline folder to the container mount.</summary>
    private static string ToContainer(PlasoRunOptions o, string hostPath)
    {
        var root = Path.GetFullPath(o.TimelineDir).TrimEnd('\\', '/');
        var full = Path.GetFullPath(hostPath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{hostPath} is not under {root}");
        return ContainerTimeline + "/" + full[(root.Length + 1)..].Replace('\\', '/');
    }

    public static string Display(string exe, IReadOnlyList<string> args)
    {
        static string Q(string s) => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        return string.Join(" ", new[] { Q(exe) }.Concat(args.Select(Q)));
    }
}
