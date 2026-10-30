using System.Globalization;
using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Triage;

namespace EzForensics.Core.SystemInfo;

/// <summary>
/// Facts from a memory image of a host: the Volatility <c>windows.info</c> output (<c>&lt;case&gt;\&lt;Host&gt;\Memory\Memory_Info*.csv</c>, Variable/Value rows),
/// the run manifest's info-job details (image format, dump kind, SHA-256) and, for Windows crash dumps, the header's physical page count
/// (re-read from the image when it still exists). Replaces the "Installed memory" placeholder and adds a "Memory image" category.
/// </summary>
public sealed class MemoryDetailsProvider : IComputerDetailsProvider
{
    public const string CatMemoryImage = "Memory image";
    public const string InstalledMemoryField = "Installed memory";
    private const string VolatilityToolId = "Volatility3";

    private readonly string _caseDir;
    private readonly string _host;

    public MemoryDetailsProvider(string caseDir, string host)
    {
        _caseDir = caseDir;
        _host = host ?? "";
    }

    /// <summary>What was found for a host's memory image.</summary>
    public sealed record MemoryFacts(
        string? CsvPath, IReadOnlyDictionary<string, string> Info, string? ImagePath, string? Format, string? DumpKind, string? Sha256,
        long? PhysicalPages, string? VolatilityVersion)
    {
        public long? UsableBytes => PhysicalPages is > 0 ? PhysicalPages * 4096 : null;
    }

    public static string MemoryFolder(string caseDir, string host) => Path.Combine(caseDir, TriagePlanner.HostFolder(host), "Memory");

    public void Contribute(SystemDetails system, string systemRoot, CancellationToken ct)
    {
        var facts = Read(_caseDir, _host, ct);
        if (facts == null) return;
        Apply(system, facts);
    }

    /// <summary>Reads the host's windows.info CSV and manifest details; null when the host has no memory info output.</summary>
    public static MemoryFacts? Read(string caseDir, string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseDir) || string.IsNullOrWhiteSpace(host)) return null;
        var dir = MemoryFolder(caseDir, host);
        string? csv = null;
        try
        {
            if (Directory.Exists(dir))
                csv = Directory.EnumerateFiles(dir, "Memory_Info*.csv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (csv == null) return null;
        ct.ThrowIfCancellationRequested();
        var info = ReadInfoCsv(csv);
        if (info.Count == 0) return null;

        Dictionary<string, string>? d = null;
        var hostFolder = TriagePlanner.HostFolder(host);
        foreach (var f in ManifestFiles(caseDir))
        {
            var m = RunManifest.TryLoad(f);
            if (m == null) continue;
            foreach (var j in m.Jobs)
            {
                if (j.Details == null || !string.Equals(j.ToolId, VolatilityToolId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!j.Details.TryGetValue("volatility.plugin", out var plug) || !plug.Equals("windows.info", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(j.Host) && !string.Equals(TriagePlanner.HostFolder(j.Host), hostFolder, StringComparison.OrdinalIgnoreCase)) continue;
                d = j.Details;   // later manifests / jobs win
            }
        }
        string? G(string k) => d != null && d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var image = G("input.path");
        long? pages = null;
        if (image != null)
        {
            try { pages = MemoryImageDetector.Detect(image)?.PhysicalPages; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new MemoryFacts(csv, info, image, G("image.format"), G("dump.kind"), G("input.sha256"), pages, G("volatility.version"));
    }

    private static List<string> ManifestFiles(string caseDir)
    {
        try
        {
            return Case.CaseIndex.ManifestFiles(caseDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<string>(); }
    }

    /// <summary>Variable/Value rows of a windows.info CSV (the Volatility header <c>TreeDepth,Variable,Value</c> or just <c>Variable,Value</c>).</summary>
    public static Dictionary<string, string> ReadInfoCsv(string path)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, new UTF8Encoding(false), true);
            int vi = -1, li = -1;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var cells = SplitCsvLine(line);
                if (vi < 0)
                {
                    var v = cells.FindIndex(c => c.Equals("Variable", StringComparison.OrdinalIgnoreCase));
                    var l = cells.FindIndex(c => c.Equals("Value", StringComparison.OrdinalIgnoreCase));
                    if (v >= 0 && l >= 0) { vi = v; li = l; }
                    continue;
                }
                if (cells.Count > Math.Max(vi, li) && cells[vi].Length > 0) res[cells[vi]] = cells[li];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return res;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    /// <summary>"≈8 GB usable (from memory image)".</summary>
    public static string FormatUsable(long bytes)
    {
        double gb = bytes / (1024.0 * 1024 * 1024);
        var s = gb >= 10 ? Math.Round(gb).ToString("0", CultureInfo.InvariantCulture) : Math.Round(gb, 1).ToString("0.#", CultureInfo.InvariantCulture);
        return $"≈{s} GB usable (from memory image)";
    }

    public static void Apply(SystemDetails system, MemoryFacts f)
    {
        const string src = "Volatility windows.info";
        if (f.UsableBytes is { } bytes)
        {
            var value = FormatUsable(bytes);
            const string srcText = "Crash dump header PhysicalMemoryBlock NumberOfPages x 4 KiB (usable RAM, not the installed DIMM size)";
            int i = system.Fields.FindIndex(x => x.Category == ComputerDetailsCollector.CatMemory && x.Field == InstalledMemoryField);
            if (i >= 0) system.Fields[i] = system.Fields[i] with { Value = value, Source = srcText };
            else system.Fields.Add(new DetailField(ComputerDetailsCollector.CatMemory, InstalledMemoryField, value, srcText));
        }

        void Add(string field, string? value, string source = src)
        {
            if (!string.IsNullOrWhiteSpace(value)) system.Fields.Add(new DetailField(CatMemoryImage, field, value!, source));
        }
        string? I(string k) => f.Info.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) && v != "N/A" ? v : null;

        Add("Image path", f.ImagePath, "Run manifest (input.path)");
        Add("Image format", f.Format, "Run manifest (image.format)");
        Add("Dump kind", f.DumpKind, "Run manifest (dump.kind)");
        Add("SHA-256", f.Sha256, "Run manifest (input.sha256)");
        var t = I("SystemTime");
        Add("Acquisition system time (UTC)", t == null ? null : t.EndsWith("UTC", StringComparison.OrdinalIgnoreCase) || t.EndsWith('Z') ? t : t + " UTC", "windows.info SystemTime");
        Add("Kernel build", I("NtBuildLab") ?? BuildFromVersion(f), "windows.info NtBuildLab");
        Add("Processors", I("KeNumberProcessors"), "windows.info KeNumberProcessors");
        Add("64-bit", I("Is64Bit"), "windows.info Is64Bit");
        Add("Kernel base", I("Kernel Base"), "windows.info Kernel Base");
        Add("DTB", I("DTB"), "windows.info DTB");
        Add("System root", I("NtSystemRoot"), "windows.info NtSystemRoot");
        Add("Product type", I("NtProductType"), "windows.info NtProductType");
        Add("Volatility version", f.VolatilityVersion, "Run manifest (volatility.version)");
    }

    private static string? BuildFromVersion(MemoryFacts f) =>
        f.Info.TryGetValue("NtMajorVersion", out var maj) && f.Info.TryGetValue("NtMinorVersion", out var min) ? $"NT {maj}.{min}" : null;

    /// <summary>
    /// For a host that has a memory image but no registry-based details: writes <c>&lt;case&gt;\&lt;Host&gt;\SystemInfo\ComputerDetails.json/.csv</c> from the
    /// memory facts alone. Returns the JSON path, or null when the host already has registry-based details or has no windows.info output.
    /// Safe to call from a job PostProcess (idempotent; refreshes an earlier memory-only report).
    /// </summary>
    public static string? WriteForMemoryOnlyHost(string caseDir, string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseDir) || string.IsNullOrWhiteSpace(host)) return null;
        var jsonPath = ComputerDetailsStore.HostJsonPathFor(caseDir, host);
        if (File.Exists(jsonPath) && !IsMemoryOnlyReport(jsonPath)) return null;   // registry details win
        var facts = Read(caseDir, host, ct);
        if (facts == null) return null;

        var hostFolder = TriagePlanner.HostFolder(host);
        var sys = new SystemDetails { SystemRoot = "", Title = hostFolder, Host = hostFolder };
        string? I(string k) => facts.Info.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        if (I("NtMajorVersion") is { } maj && I("NtMinorVersion") is { } min)
            sys.Fields.Add(new DetailField(ComputerDetailsCollector.CatOs, "Kernel version", $"NT {maj}.{min}" + (I("NtBuildLab") is { } bl ? $" ({bl})" : ""), "Volatility windows.info"));
        sys.Fields.Add(new DetailField(ComputerDetailsCollector.CatMemory, InstalledMemoryField, ComputerDetailsCollector.MemoryUnknown, "No crash dump header available for this image"));
        Apply(sys, facts);
        sys.Summary = $"{hostFolder} · memory image only" + (I("NtBuildLab") is { } b ? " · " + b : "");
        sys.Notes.Add(MemoryOnlyNote);

        var report = new ComputerDetailsReport { Source = facts.ImagePath ?? "" };
        report.Systems.Add(sys);
        ComputerDetailsStore.Write(report, ComputerDetailsStore.HostFolderFor(caseDir, host));
        return jsonPath;
    }

    private const string MemoryOnlyNote = "Computer details come from a memory image only; no registry hives were processed for this host.";

    private static bool IsMemoryOnlyReport(string jsonPath)
    {
        try { return File.ReadAllText(jsonPath).Contains("memory image only", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
