using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.SystemInfo;

/// <summary>A host folder of the case with the computer names and IPv4 addresses its Computer details recorded.</summary>
public sealed record KnownHost(string Host, IReadOnlyList<string> Names, IReadOnlyList<string> IpAddresses);

/// <summary>Reads and writes <c>&lt;case&gt;\SystemInfo\ComputerDetails.json</c> and <c>ComputerDetails.csv</c> (Category, Field, Value, Source).</summary>
public static class ComputerDetailsStore
{
    public const string FolderName = "SystemInfo";
    public const string JsonName = "ComputerDetails.json";
    public const string CsvName = "ComputerDetails.csv";
    /// <summary>Tool id of the built-in job (Ingest job list, run manifest).</summary>
    public const string ToolId = "ezforensics-computer-details";
    /// <summary>The id older run manifests (EZ Hub era) carry; read as <see cref="ToolId"/>.</summary>
    public const string LegacyToolId = "ezhub-computer-details";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Legacy (layout v1) folder <c>&lt;case&gt;\SystemInfo</c>.</summary>
    public static string FolderFor(string caseDir) => Path.Combine(caseDir, FolderName);
    public static string JsonPathFor(string caseDir) => Path.Combine(FolderFor(caseDir), JsonName);

    /// <summary>
    /// The CSV the Analyze page opens: the legacy <c>&lt;case&gt;\SystemInfo\ComputerDetails.csv</c> when it exists, else the first host's CSV
    /// (layout v2), else the legacy path (which may not exist).
    /// </summary>
    public static string CsvPathFor(string caseDir)
    {
        var legacy = Path.Combine(FolderFor(caseDir), CsvName);
        if (File.Exists(legacy)) return legacy;
        return HostFolders(caseDir).Select(h => Path.Combine(h, FolderName, CsvName)).FirstOrDefault(File.Exists) ?? legacy;
    }

    /// <summary>Layout v2: <c>&lt;case&gt;\&lt;Host&gt;\SystemInfo</c> (host folder name as the triage planner derives it).</summary>
    public static string HostFolderFor(string caseDir, string host) =>
        Path.Combine(caseDir, Triage.TriagePlanner.HostFolder(host), FolderName);
    public static string HostJsonPathFor(string caseDir, string host) => Path.Combine(HostFolderFor(caseDir, host), JsonName);
    public static string HostCsvPathFor(string caseDir, string host) => Path.Combine(HostFolderFor(caseDir, host), CsvName);

    /// <summary>Top-level folders of the case that hold a <c>SystemInfo\ComputerDetails.json</c> (layout v2 hosts).</summary>
    private static List<string> HostFolders(string caseDir)
    {
        try
        {
            if (!Directory.Exists(caseDir)) return new List<string>();
            return Directory.EnumerateDirectories(caseDir)
                .Where(d => !string.Equals(Path.GetFileName(d), FolderName, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(Path.Combine(d, FolderName, JsonName)))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<string>(); }
    }

    /// <summary>Writes both files into <paramref name="outDir"/> atomically (temp file, then rename). Returns the two paths.</summary>
    public static (string Json, string Csv) Write(ComputerDetailsReport report, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var json = Path.Combine(outDir, JsonName);
        var csv = Path.Combine(outDir, CsvName);
        WriteAtomic(json, JsonSerializer.Serialize(report, Json));
        WriteAtomic(csv, ToCsv(report));
        return (json, csv);
    }

    public static string ToCsv(ComputerDetailsReport report)
    {
        var sb = new StringBuilder();
        bool multi = report.Systems.Count > 1;
        sb.Append(multi ? "System,Category,Field,Value,Source\r\n" : "Category,Field,Value,Source\r\n");
        foreach (var s in report.Systems)
            foreach (var f in s.Fields)
            {
                if (multi) { Cell(sb, s.Title); sb.Append(','); }
                Cell(sb, f.Category); sb.Append(',');
                Cell(sb, f.Field); sb.Append(',');
                Cell(sb, f.Value); sb.Append(',');
                Cell(sb, f.Source);
                sb.Append("\r\n");
            }
        return sb.ToString();
    }

    private static void Cell(StringBuilder sb, string v)
    {
        if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
        else sb.Append(v);
    }

    /// <summary>
    /// Loads the case's report; null when none exists or none can be read. Merges the legacy <c>&lt;case&gt;\SystemInfo\ComputerDetails.json</c>
    /// with every host's <c>&lt;case&gt;\&lt;Host&gt;\SystemInfo\ComputerDetails.json</c> (layout v2): one report whose <see cref="ComputerDetailsReport.Systems"/>
    /// hold all systems, each with its <see cref="SystemDetails.Host"/> (empty for legacy reports).
    /// </summary>
    public static ComputerDetailsReport? Load(string caseDir)
    {
        var parts = new List<(ComputerDetailsReport Report, string Host)>();
        if (ReadReport(JsonPathFor(caseDir)) is { } legacy) parts.Add((legacy, ""));
        foreach (var d in HostFolders(caseDir))
            if (ReadReport(Path.Combine(d, FolderName, JsonName)) is { } r) parts.Add((r, Path.GetFileName(d)));
        if (parts.Count == 0) return null;
        if (parts.Count == 1 && parts[0].Host.Length == 0) return parts[0].Report;
        var merged = new ComputerDetailsReport
        {
            Source = parts.Select(p => p.Report.Source).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "",
            GeneratedUtc = parts.Max(p => p.Report.GeneratedUtc),
            Generator = parts[0].Report.Generator,
        };
        foreach (var (r, host) in parts)
        {
            foreach (var s in r.Systems)
            {
                if (string.IsNullOrEmpty(s.Host)) s.Host = host;
                merged.Systems.Add(s);
            }
            foreach (var n in r.Notes) if (!merged.Notes.Contains(n)) merged.Notes.Add(n);
        }
        return merged;
    }

    /// <summary>
    /// Hosts of an earlier ingest (layout v2 <c>&lt;case&gt;\&lt;Host&gt;\SystemInfo\ComputerDetails.json</c>) with their computer names and the IPv4
    /// addresses of their "Network: …" interface fields ("IP address (static)" / "IP address (DHCP)"). Empty when the case has none.
    /// </summary>
    public static IReadOnlyList<KnownHost> KnownHosts(string caseDir)
    {
        var list = new List<KnownHost>();
        foreach (var d in HostFolders(caseDir))
        {
            if (ReadReport(Path.Combine(d, FolderName, JsonName)) is not { } r) continue;
            var ips = new List<string>();
            var names = new List<string>();
            foreach (var s in r.Systems)
            {
                if (!string.IsNullOrWhiteSpace(s.Title)) names.Add(s.Title.Trim());
                foreach (var f in s.Fields)
                {
                    if (f.Category == ComputerDetailsCollector.CatSystem && f.Field is "Computer name" or "Host name (TCP/IP)" &&
                        f.Value != ComputerDetailsCollector.Unknown) names.Add(f.Value.Trim());
                    if (!f.Category.StartsWith(ComputerDetailsCollector.CatNetwork + ":", StringComparison.Ordinal) ||
                        !f.Field.StartsWith("IP address", StringComparison.Ordinal)) continue;
                    foreach (var ip in f.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (System.Net.IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            InterfaceAddresses.UsableIp(ip)) ips.Add(ip);
                }
            }
            list.Add(new KnownHost(Path.GetFileName(d), names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ips.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return list;
    }

    /// <summary>Same as <see cref="Load"/> (name used by the Analyze page).</summary>
    public static ComputerDetailsReport? TryLoad(string caseDir) => Load(caseDir);

    private static ComputerDetailsReport? ReadReport(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ComputerDetailsReport>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// First folder of <see cref="FindSourcesFromManifests"/> (the evidence source recorded in the case's newest run manifest that still
    /// exists), else null.
    /// </summary>
    public static string? FindSourceFromManifests(string caseDir) => FindSourcesFromManifests(caseDir).FirstOrDefault();

    /// <summary>
    /// Evidence folders of the case's newest run manifest (<c>EzForensics_manifest_*.json</c>, or the older <c>EzHub_manifest_*.json</c>) that still exist: its <c>sourceRoots</c> (the
    /// folders the jobs read, e.g. the extracted roots of archived evidence), else its <c>sourcePath</c> when that is a folder. A
    /// <c>sourcePath</c> that is a file (an archive or an image) is never returned. Empty when none is found.
    /// </summary>
    public static IReadOnlyList<string> FindSourcesFromManifests(string caseDir)
    {
        foreach (var m in ManifestsNewestFirst(caseDir))
        {
            var roots = (m.SourceRoots ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count > 0) return roots;
            if (m.SourceRoots is not { Count: > 0 } && m.SourcePath is { Length: > 0 } s && Directory.Exists(s)) return new[] { s };
        }
        return Array.Empty<string>();
    }

    /// <summary>A system a run processed, as recorded in a run manifest's <c>sources</c> (<see cref="Execution.ManifestSource"/>).</summary>
    public sealed record ManifestSystem(string RootPath, string Host);

    /// <summary>
    /// The systems recorded in the case's run manifests (<c>sources[].rootPath</c> + <c>host</c>) whose system root still exists, newest
    /// manifest first, one per host. These are the exact roots the jobs read (never a broader parent folder). Empty when none.
    /// </summary>
    public static IReadOnlyList<ManifestSystem> FindSystemsFromManifests(string caseDir)
    {
        var list = new List<ManifestSystem>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ManifestsNewestFirst(caseDir))
            foreach (var src in m.Sources ?? new List<Execution.ManifestSource>())
            {
                if (string.IsNullOrWhiteSpace(src.RootPath) || string.IsNullOrWhiteSpace(src.Host) || !Directory.Exists(src.RootPath)) continue;
                if (hosts.Add(src.Host)) list.Add(new ManifestSystem(src.RootPath, src.Host));
            }
        return list;
    }

    private static IEnumerable<Execution.RunManifest> ManifestsNewestFirst(string caseDir)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(caseDir)) yield break;
            files = Case.CaseIndex.ManifestFiles(caseDir);
            files.Reverse();   // newest first
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in files)
            if (Execution.RunManifest.TryLoad(f) is { } m) yield return m;
    }

    /// <summary>
    /// Command-line entry used by the built-in job (<c>EzForensics.exe --computer-details --source &lt;dir&gt; --out &lt;dir&gt;</c>):
    /// collects, writes both files and returns the exit code (0 = at least one system, 2 = no hives found, 1 = error).
    /// </summary>
    public static int RunCommandLine(IReadOnlyList<string> args, TextWriter output, IHiveOpener? opener = null)
    {
        string? source = null, outDir = null, host = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--source" && i + 1 < args.Count) source = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Count) outDir = args[++i];
            else if (args[i] == "--host" && i + 1 < args.Count) host = args[++i];
        }
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(outDir))
        {
            output.WriteLine("usage: --computer-details --source <triage source> --out <SystemInfo folder> [--host <case host folder>]");
            return 1;
        }
        try
        {
            output.WriteLine($"Computer details: offline registry (read-only) from {source}");
            var collector = new ComputerDetailsCollector(opener);
            // Layout v2: --out is <case>\<Host>\SystemInfo, so the host's memory outputs (<case>\<Host>\Memory) refine the memory facts.
            if (!string.IsNullOrWhiteSpace(host) &&
                Path.GetDirectoryName(Path.GetFullPath(outDir).TrimEnd('\\', '/')) is { } hostDir && Path.GetDirectoryName(hostDir) is { } caseDir)
                collector.Providers.Add(new MemoryDetailsProvider(caseDir, host.Trim()));
            var report = collector.Collect(source, l => output.WriteLine(l));
            if (!string.IsNullOrWhiteSpace(host)) foreach (var sys in report.Systems) sys.Host = host.Trim();
            var (json, csv) = Write(report, outDir);
            foreach (var s in report.Systems)
            {
                output.WriteLine($"{s.Title}: {s.Summary} ({s.Fields.Count} fields)");
                foreach (var n in s.Notes) output.WriteLine("  note: " + n);
            }
            foreach (var n in report.Notes) output.WriteLine("note: " + n);
            output.WriteLine("Wrote " + json);
            output.WriteLine("Wrote " + csv);
            return report.Systems.Count > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            output.WriteLine("Computer details could not be collected: " + ex.Message);
            return 1;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
