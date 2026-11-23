using System.Globalization;
using System.Security.Cryptography;
using EzForensics.Core.Advisories;
using EzForensics.Core.Execution;
using EzForensics.Core.Triage;

namespace EzForensics.Core.Case;

/// <summary>How a <see cref="CsvProvenance"/> was determined.</summary>
public enum ProvenanceSource
{
    /// <summary>The file is listed in a run manifest of the case.</summary>
    Manifest,
    /// <summary>Inferred from a layout v2 path (<c>&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;.csv</c>).</summary>
    NewLayoutPath,
    /// <summary>Inferred from a layout v1 path (<c>&lt;Category&gt;\&lt;ToolId&gt;\...</c>).</summary>
    LegacyPath,
    /// <summary>Only the file name was recognised.</summary>
    FileName,
    Unknown,
}

/// <summary>Everything the case knows about one output CSV (Analyze tree Tool column, advisories, timeline Host column).</summary>
public sealed record CsvProvenance
{
    public required string Path { get; init; }
    /// <summary>Path relative to the case folder (null when the file is outside it).</summary>
    public string? RelativePath { get; init; }
    public string? ToolId { get; init; }
    /// <summary>Artifact id (<c>prefetch</c>, <c>amcache-program-entries</c>, <c>registry-plugin</c>, <c>zeek-conn</c>...).</summary>
    public string? ArtifactId { get; init; }
    public string? Host { get; init; }
    /// <summary>The file lives in the case-wide folder (<see cref="CaseIndex.CaseWideFolder"/>): results whose source host could not be identified; <see cref="Host"/> is null.</summary>
    public bool IsCaseWide { get; init; }
    public string? Category { get; init; }
    public string? RuleId { get; init; }
    public string? SystemRoot { get; init; }
    public AcquisitionKind? Acquisition { get; init; }
    public IReadOnlyList<ManifestInputFact> InputFacts { get; init; } = Array.Empty<ManifestInputFact>();
    public string? InputFactsNote { get; init; }
    /// <summary>The manifest job that produced the file (null when inferred).</summary>
    public ManifestJob? Job { get; init; }
    public string? ManifestPath { get; init; }
    /// <summary>Layout of this file's location (a mixed case has both).</summary>
    public CaseLayoutVersion Layout { get; init; }
    public ProvenanceSource Source { get; init; } = ProvenanceSource.Unknown;

    /// <summary>Archive timestamp coverage of the evidence system (ModifiedOnly, All, Mixed, DosLocal); null when the evidence did not come from an archive.</summary>
    public string? ArchiveTimestampCoverage { get; init; }
    public string? ArchivePath { get; init; }
    public string? ArchiveSha256 { get; init; }
    /// <summary>Memory image format (<c>MemoryFormat</c> name) from the memory jobs' details; null for non-memory outputs.</summary>
    public string? MemoryFormat { get; init; }
    /// <summary>Crash dump kind (<c>DumpKind</c> name); null when not a crash dump.</summary>
    public string? MemoryDumpKind { get; init; }
    /// <summary>Crash dumps: written from a running system (manifest detail <c>memory.live</c>); null = unknown / not a crash dump.</summary>
    public bool? MemoryLiveDump { get; init; }
    /// <summary>RITA outputs: hours between the first and last analysed connection (manifest detail <c>rita.dataSpanHours</c>); null = unknown / not a RITA output.</summary>
    public double? RitaDataSpanHours { get; init; }

    /// <summary><see cref="Acquisition"/> for the advisories engine.</summary>
    public EvidenceAcquisition EvidenceAcquisition => CaseIndex.ToEvidenceAcquisition(Acquisition);
    /// <summary><see cref="InputFacts"/> in the advisories engine's form (null when none were recorded).</summary>
    public IReadOnlyList<InputTimestampFact>? InputTimestampFacts =>
        InputFacts.Count == 0 ? null : InputFacts.Select(f => new InputTimestampFact(f.Path, f.CreatedUtc, f.ModifiedUtc, f.AccessedUtc)).ToList();
    /// <summary>Advisory artifact id for <see cref="ArtifactId"/> (<c>amcache-program-entries</c> → <c>amcache</c>); null when the advisories do not know it.</summary>
    public string? AdvisoryArtifactId => CaseIndex.AdvisoryArtifactId(ArtifactId);
}

/// <summary>
/// Loads every run manifest of a case (<c>EzForensics_manifest_*.json</c>; older cases <c>EzHub_manifest_*.json</c>) and answers, for any CSV of the case, which tool / artifact / host /
/// category / acquisition it came from and the input facts of its job. Files not in a manifest are inferred from their path
/// (layout v2 <c>&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;.csv</c> or layout v1 <c>&lt;Category&gt;\&lt;ToolId&gt;\...</c>) and file name.
/// Manifest paths are rebased when the case folder was moved or copied. Renames / moves / copies / deletes made in the Analyze tree
/// (<c>logs\file_operations.log</c>) are replayed in time order with the manifests, so provenance follows a renamed file and a new file that
/// reuses an old name does not inherit it. A manifest entry whose file no longer matches the recorded size (or, when the file changed after
/// the job, the recorded SHA-256) is ignored. Read-only; cheap to build (a CSV is only hashed in that ambiguous case).
/// </summary>
public sealed class CaseIndex
{
    public const string ManifestPrefix = "EzForensics_manifest_";
    /// <summary>Manifests written before the rename (EZ Hub); still read and protected in existing cases.</summary>
    public const string LegacyManifestPrefix = "EzHub_manifest_";
    /// <summary>Glob for both manifest names (<see cref="IsManifestFileName"/> narrows it to the two prefixes).</summary>
    public const string ManifestPattern = "Ez*_manifest_*.json";

    /// <summary>True for a run manifest file name, new (<c>EzForensics_manifest_*.json</c>) or legacy (<c>EzHub_manifest_*.json</c>).</summary>
    public static bool IsManifestFileName(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && (name.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(LegacyManifestPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The case's run manifests (top folder), oldest run first. Ordered by the run stamp after the prefix, so legacy and new manifests
    /// interleave by time (an alphabetical sort would put every EzForensics_ file before every EzHub_ one). Empty when the folder is missing.
    /// </summary>
    public static List<string> ManifestFiles(string caseDir)
    {
        if (!Directory.Exists(caseDir)) return new List<string>();
        return Directory.EnumerateFiles(caseDir, ManifestPattern)
            .Where(f => IsManifestFileName(Path.GetFileName(f)))
            .OrderBy(f => ManifestStamp(Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ManifestStamp(string name) =>
        name.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase) ? name[ManifestPrefix.Length..] : name[LegacyManifestPrefix.Length..];

    private sealed record Entry(string ManifestPath, RunManifest Manifest, ManifestJob Job, ManifestOutputFile File);

    /// <summary>One provenance event: a manifest job's outputs appearing, or a file operation from the log; replayed in time order.</summary>
    private sealed record Event(DateTime Utc, int Seq, Action Apply);

    private readonly Dictionary<string, bool> _verified = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, RunManifest Manifest)> _manifests = new();
    private readonly List<ManifestSource> _sources = new();

    private CaseIndex(string caseDir) { CaseDir = caseDir; }

    public string CaseDir { get; }
    /// <summary>Layout of the case: the <c>case.json</c> marker, else the manifests, else the folder structure.</summary>
    public CaseLayoutVersion LayoutVersion { get; private set; }
    /// <summary>The case has layout v1 folders (<c>&lt;case&gt;\&lt;Category&gt;\&lt;ToolId&gt;</c>); with <see cref="LayoutVersion"/> v2 the case is mixed.</summary>
    public bool HasLegacyOutputs { get; private set; }
    public IReadOnlyList<(string Path, RunManifest Manifest)> Manifests => _manifests;
    /// <summary>Evidence systems of all runs (one per system root).</summary>
    public IReadOnlyList<ManifestSource> Sources => _sources;
    /// <summary>Host folders of the case (from the manifests and from layout v2 folders).</summary>
    public IReadOnlyList<string> Hosts { get; private set; } = Array.Empty<string>();

    /// <summary>Folders that are case-level (never host folders).</summary>
    public static readonly IReadOnlySet<string> CaseLevelFolders =
        new HashSet<string>(new[] { "Timeline", "logs", "SystemInfo", OutputPlan.StagingFolderName, Assistant.AssistantConsent.FolderName }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Case-level folder for results whose source host could not be identified (captures, their RITA analysis, a super timeline of evidence of no
    /// identified system): <c>&lt;case&gt;\_Case\&lt;Category&gt;\file</c>. Every other result is under its host.
    /// It is laid out like a host folder (and a normal output folder: rename / delete allowed) but is never a host.
    /// </summary>
    public const string CaseWideFolder = "_Case";
    public static bool IsCaseWideFolder(string? name) => string.Equals(name, CaseWideFolder, StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<HashSet<string>> Categories = new(() =>
    {
        var set = new HashSet<string>(TriageRules.LoadEmbedded().Select(r => r.Category), StringComparer.OrdinalIgnoreCase)
        {
            "Persistence", "Network", "Accounts", "Logins", "ShellHistory", "Services", "ScheduledTasks", "Packages", "SystemInfo", "Memory", "Processes",
        };
        set.RemoveWhere(string.IsNullOrWhiteSpace);
        return set;
    });

    /// <summary>Category folder names EZ Forensics writes (triage rules + Persistence, Network and the native parsers' categories).</summary>
    public static bool IsKnownCategory(string? name) => name != null && Categories.Value.Contains(name);

    public static CaseIndex Load(string caseDir)
    {
        var idx = new CaseIndex(Path.GetFullPath(caseDir));
        idx.LoadManifests();
        idx.DetectLayout();
        return idx;
    }

    private void LoadManifests()
    {
        if (!Directory.Exists(CaseDir)) return;
        List<string> files;
        try { files = ManifestFiles(CaseDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EzForensics.Core.Diagnostics.CoreLog.Warn("Could not list case manifests in " + CaseDir, ex);
            return;
        }
        var events = new List<Event>();
        var origCases = new List<string>();
        foreach (var mf in files)
        {
            var m = RunManifest.TryLoad(mf);
            if (m == null) continue;
            _manifests.Add((mf, m));
            foreach (var s in m.Sources ?? new List<ManifestSource>())
                if (!_sources.Any(x => string.Equals(x.RootPath, s.RootPath, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Host, s.Host, StringComparison.OrdinalIgnoreCase)))
                    _sources.Add(s);
            var origCase = m.CaseDir ?? InferOriginalCaseDir(m);
            if (origCase != null && !origCases.Contains(origCase, StringComparer.OrdinalIgnoreCase)) origCases.Add(origCase);
            foreach (var job in m.Jobs)
            {
                var when = ParseUtc(job.EndUtc) ?? ParseUtc(job.StartUtc) ?? DateTime.MinValue;
                var (path, manifest, j) = (mf, m, job);
                events.Add(new Event(when, events.Count, () =>
                {
                    foreach (var f in j.OutputFiles)
                    {
                        if (string.IsNullOrEmpty(f.Path)) continue;
                        _byPath[Rebase(f.Path, origCase)] = new Entry(path, manifest, j, f);   // later jobs win
                    }
                }));
            }
        }
        AddFileOperationEvents(events, origCases);
        foreach (var e in events.OrderBy(e => e.Utc).ThenBy(e => e.Seq)) e.Apply();
    }

    private static DateTime? ParseUtc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>
    /// <c>logs\file_operations.log</c> lines (<c>time \t user \t op \t from \t to \t sha256</c>): RENAME / MOVE move a file's (or a folder's
    /// files') provenance to the new path, COPY / DUPLICATE give the copy the same provenance, DELETE drops it.
    /// </summary>
    private void AddFileOperationEvents(List<Event> events, IReadOnlyList<string> origCases)
    {
        var log = Path.Combine(CaseDir, CaseFileOperations.LogsFolderName, CaseFileOperations.LogFileName);
        if (!File.Exists(log)) return;
        string[] lines;
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            lines = r.ReadToEnd().Split('\n');
        }
        catch (Exception ex)  when (ex is IOException or UnauthorizedAccessException)
        {
            EzForensics.Core.Diagnostics.CoreLog.Warn("Could not read the case log " + log, ex);
            return;
        }
        foreach (var raw in lines)
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 6 || ParseUtc(parts[0]) is not { } when) continue;
            var op = parts[2].ToUpperInvariant();
            var from = RebaseAny(parts[3], origCases);
            var to = RebaseAny(parts[4], origCases);
            switch (op)
            {
                case "RENAME" or "MOVE":
                    events.Add(new Event(when, events.Count, () => Remap(from, to, keepSource: false)));
                    break;
                case "COPY" or "DUPLICATE":
                    events.Add(new Event(when, events.Count, () => Remap(from, to, keepSource: true)));
                    break;
                case "DELETE":
                    events.Add(new Event(when, events.Count, () => Remap(from, null, keepSource: false)));
                    break;
            }
        }
    }

    /// <summary>Moves (or copies, or drops when <paramref name="to"/> is null) the entries of a file or of every file under a folder.</summary>
    private void Remap(string from, string? to, bool keepSource)
    {
        if (from.Length == 0 || from == "-") return;
        var prefix = from.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var hits = _byPath.Where(kv => string.Equals(kv.Key, from, StringComparison.OrdinalIgnoreCase)
                                       || kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (key, entry) in hits)
        {
            if (!keepSource) _byPath.Remove(key);
            if (to == null || to == "-") continue;
            var target = string.Equals(key, from, StringComparison.OrdinalIgnoreCase) ? to : to.TrimEnd('\\', '/') + key[(prefix.Length - 1)..];
            _byPath[target] = entry;
        }
    }

    /// <summary>A logged absolute path, rebased onto the current case folder when it was written under an earlier location of the case.</summary>
    private string RebaseAny(string path, IReadOnlyList<string> origCases)
    {
        if (string.IsNullOrEmpty(path) || path == "-") return "-";
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return path; }
        if (RelativeToCase(full) != null) return full;
        foreach (var o in origCases)
        {
            var r = Rebase(full, o);
            if (!string.Equals(r, full, StringComparison.OrdinalIgnoreCase)) return r;
        }
        return full;
    }

    /// <summary>
    /// True when the file on disk is still the one the manifest recorded: same size (cheap); when the file was written after its job
    /// ended, the SHA-256 must match too. Entries without a recorded size / hash are trusted. Cached per path.
    /// </summary>
    private bool StillMatches(string full, Entry e)
    {
        lock (_verified)
            if (_verified.TryGetValue(full, out var known)) return known;
        bool ok = true;
        try
        {
            var fi = new FileInfo(full);
            if (fi.Exists && (e.File.Size > 0 || !string.IsNullOrEmpty(e.File.Sha256)))
            {
                if (e.File.Size > 0 && fi.Length != e.File.Size) ok = false;
                else if (!string.IsNullOrEmpty(e.File.Sha256) && e.File.Sha256!.Length == 64
                         && ParseUtc(e.Job.EndUtc) is { } end && fi.LastWriteTimeUtc > end.AddSeconds(2))
                {
                    using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                    ok = string.Equals(Convert.ToHexString(SHA256.HashData(fs)), e.File.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        lock (_verified) _verified[full] = ok;
        return ok;
    }

    /// <summary>Manifests written before <c>caseDir</c> was recorded: the case folder is the parent of the job logs' <c>logs</c> folder.</summary>
    private static string? InferOriginalCaseDir(RunManifest m)
    {
        foreach (var j in m.Jobs)
        {
            if (string.IsNullOrEmpty(j.LogPath)) continue;
            var logs = Path.GetDirectoryName(j.LogPath);
            if (logs != null && Path.GetFileName(logs).Equals("logs", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(logs);
        }
        return null;
    }

    private string Rebase(string path, string? originalCaseDir)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return path; }
        if (originalCaseDir != null)
        {
            var o = originalCaseDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(o, StringComparison.OrdinalIgnoreCase)) return Path.Combine(CaseDir, full[o.Length..]);
        }
        return full;
    }

    private void DetectLayout()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool newFolders = false;
        try
        {
            foreach (var d in Directory.Exists(CaseDir) ? Directory.EnumerateDirectories(CaseDir) : Enumerable.Empty<string>())
            {
                var name = Path.GetFileName(d);
                if (CaseLevelFolders.Contains(name)) continue;
                if (IsKnownCategory(name)) { HasLegacyOutputs = true; continue; }
                try
                {
                    if (Directory.EnumerateDirectories(d).Any(sub => IsKnownCategory(Path.GetFileName(sub)))) { newFolders = true; if (!IsCaseWideFolder(name)) hosts.Add(name); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var s in _sources) if (!string.IsNullOrEmpty(s.Host) && !IsCaseWideFolder(s.Host)) hosts.Add(s.Host);
        foreach (var (_, m) in _manifests) foreach (var j in m.Jobs) if (!string.IsNullOrEmpty(j.Host) && !IsCaseWideFolder(j.Host)) hosts.Add(j.Host!);
        Hosts = hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

        var marker = CaseLayout.ReadMarker(CaseDir);
        if (marker != null && Enum.IsDefined(typeof(CaseLayoutVersion), marker.LayoutVersion)) LayoutVersion = (CaseLayoutVersion)marker.LayoutVersion;
        else if (_manifests.Any(m => m.Manifest.LayoutVersion >= CaseLayout.CurrentVersion)) LayoutVersion = CaseLayoutVersion.HostArtifact;
        else if (newFolders) LayoutVersion = CaseLayoutVersion.HostArtifact;
        else if (HasLegacyOutputs || _manifests.Any(m => m.Manifest.Jobs.Count > 0)) LayoutVersion = CaseLayoutVersion.Legacy;
        else LayoutVersion = CaseLayoutVersion.Unknown;
    }

    /// <summary>What the case knows about <paramref name="csvPath"/> (never null; <see cref="CsvProvenance.Source"/> says how sure it is).</summary>
    public CsvProvenance Describe(string csvPath)
    {
        string full;
        try { full = Path.GetFullPath(csvPath); } catch (Exception) { full = csvPath; }
        var rel = RelativeToCase(full);
        var name = Path.GetFileName(full);
        var (pathLayout, host, category, folderTool) = FromPath(rel);
        var caseWide = pathLayout == CaseLayoutVersion.HostArtifact && host == null;

        if (_byPath.TryGetValue(full, out var e) && StillMatches(full, e))
        {
            var job = e.Job;
            var toolId = e.File.ToolId ?? job.ToolId;
            var artifact = e.File.ArtifactId ?? job.ArtifactId ?? InferArtifact(toolId, name, pathLayout);
            var h = e.File.Host ?? job.Host ?? host;
            if (caseWide || IsCaseWideFolder(h)) { h = null; caseWide = true; }
            var src = SourceFor(h, job.SystemRoot);
            var mem = MemoryDetails(e);
            return new CsvProvenance
            {
                Path = full, RelativePath = rel, ToolId = toolId, ArtifactId = artifact, Host = h, IsCaseWide = caseWide,
                Category = job.Category ?? category ?? CategoryFromOutputDir(job.OutputDir, e.Manifest),
                RuleId = job.RuleId, SystemRoot = job.SystemRoot,
                Acquisition = ParseAcquisition(job.AcquisitionKind) ?? AcquisitionForHost(h, job.SystemRoot),
                InputFacts = (IReadOnlyList<ManifestInputFact>?)job.InputFacts ?? Array.Empty<ManifestInputFact>(),
                InputFactsNote = job.InputFactsNote, Job = job, ManifestPath = e.ManifestPath,
                Layout = pathLayout, Source = ProvenanceSource.Manifest,
                ArchiveTimestampCoverage = src?.TimestampCoverage, ArchivePath = src?.ArchivePath, ArchiveSha256 = src?.ArchiveSha256,
                MemoryFormat = mem.Format, MemoryDumpKind = mem.DumpKind, MemoryLiveDump = mem.Live,
                RitaDataSpanHours = RitaSpanHours(job),
            };
        }

        if (pathLayout == CaseLayoutVersion.HostArtifact)
        {
            var id = ArtifactNaming.IdentifyNewName(name);
            return new CsvProvenance
            {
                Path = full, RelativePath = rel, ToolId = id?.ToolId, ArtifactId = id?.ArtifactId, Host = host, IsCaseWide = caseWide, Category = category,
                Acquisition = AcquisitionForHost(host, null), Layout = pathLayout, Source = ProvenanceSource.NewLayoutPath,
                ArchiveTimestampCoverage = SourceFor(host, null)?.TimestampCoverage, ArchivePath = SourceFor(host, null)?.ArchivePath,
                ArchiveSha256 = SourceFor(host, null)?.ArchiveSha256,
            };
        }
        if (pathLayout == CaseLayoutVersion.Legacy)
        {
            return new CsvProvenance
            {
                Path = full, RelativePath = rel, ToolId = folderTool, Category = category,
                ArtifactId = folderTool == null ? ArtifactNaming.IdentifyNewName(name)?.ArtifactId : ArtifactNaming.IdentifyLegacyName(folderTool, name),
                Acquisition = _sources.Count == 1 ? ParseAcquisition(_sources[0].AcquisitionKind) : null,
                Layout = pathLayout, Source = ProvenanceSource.LegacyPath,
            };
        }
        var byName = ArtifactNaming.IdentifyNewName(name);
        return new CsvProvenance
        {
            Path = full, RelativePath = rel, ToolId = byName?.ToolId, ArtifactId = byName?.ArtifactId,
            Layout = CaseLayoutVersion.Unknown, Source = byName != null ? ProvenanceSource.FileName : ProvenanceSource.Unknown,
        };
    }

    /// <summary>Host of a CSV (manifest, else the layout v2 host folder); null for layout v1 files and files outside the case.</summary>
    public string? HostOf(string csvPath) => Describe(csvPath).Host;

    private string? RelativeToCase(string full)
    {
        var root = CaseDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : null;
    }

    /// <summary>Layout of a case-relative path: v2 <c>Host\Category\file</c>; v1 <c>Category\Tool\...\file</c>.</summary>
    public static (CaseLayoutVersion Layout, string? Host, string? Category, string? ToolFolder) FromPath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return (CaseLayoutVersion.Unknown, null, null, null);
        var segs = relativePath.Split('\\', '/');
        // <case>\_Case\<Category>\file: layout v2 results across all hosts (no host)
        if (segs.Length == 3 && IsCaseWideFolder(segs[0]) && IsKnownCategory(segs[1]))
            return (CaseLayoutVersion.HostArtifact, null, segs[1], null);
        if (segs.Length == 3 && IsKnownCategory(segs[1]) && !IsKnownCategory(segs[0]) && !CaseLevelFolders.Contains(segs[0]))
            return (CaseLayoutVersion.HostArtifact, segs[0], segs[1], null);
        if (segs.Length >= 3 && IsKnownCategory(segs[0]) && !segs[0].Equals("SystemInfo", StringComparison.OrdinalIgnoreCase))
            return (CaseLayoutVersion.Legacy, null, segs[0], LegacyToolId(segs[0], segs[1]));
        return (CaseLayoutVersion.Unknown, null, null, null);
    }

    /// <summary>Tool folder of layout v1 → ToolId (<c>Persistence\EzHub</c>, the legacy name, or <c>Persistence\EzForensics</c> is the built-in collector).</summary>
    public static string LegacyToolId(string category, string folder) =>
        category.Equals("Persistence", StringComparison.OrdinalIgnoreCase)
        && (folder.Equals("EzHub", StringComparison.OrdinalIgnoreCase) || folder.Equals("EzForensics", StringComparison.OrdinalIgnoreCase))
            ? Persistence.PersistenceCollector.ToolId : folder;

    private static string? InferArtifact(string toolId, string name, CaseLayoutVersion layout) =>
        layout == CaseLayoutVersion.HostArtifact
            ? ArtifactNaming.IdentifyNewName(name)?.ArtifactId ?? ArtifactNaming.IdentifyLegacyName(toolId, name)
            : ArtifactNaming.IdentifyLegacyName(toolId, name) ?? ArtifactNaming.IdentifyNewName(name)?.ArtifactId;

    private string? CategoryFromOutputDir(string outputDir, RunManifest m)
    {
        var rel = RelativeToCase(Rebase(outputDir, m.CaseDir ?? InferOriginalCaseDir(m)));
        if (rel == null) return null;
        var (_, _, category, _) = FromPath(rel + Path.DirectorySeparatorChar + "x.csv");
        return category ?? rel.Split('\\', '/')[0];
    }

    private ManifestSource? SourceFor(string? host, string? systemRoot) =>
        _sources.FirstOrDefault(x => systemRoot != null && string.Equals(x.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
        ?? _sources.FirstOrDefault(x => host != null && string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));

    private AcquisitionKind? AcquisitionForHost(string? host, string? systemRoot)
    {
        var s = SourceFor(host, systemRoot);
        return s == null ? null : ParseAcquisition(s.AcquisitionKind);
    }

    /// <summary>
    /// Memory image format / dump kind of a memory output: the job's own details (<c>image.format</c>, <c>dump.kind</c>), else those of another
    /// job on the same image (<c>input.path</c>, normally the windows.info job). (null, null) for non-memory outputs.
    /// </summary>
    private (string? Format, string? DumpKind, bool? Live) MemoryDetails(Entry e)
    {
        string? Get(Dictionary<string, string>? d, string k) => d != null && d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        static bool? Bool(string? v) => bool.TryParse(v, out var b) ? b : null;
        var d0 = e.Job.Details;
        var fmt = Get(d0, "image.format");
        var kind = Get(d0, "dump.kind");
        var live = Bool(Get(d0, "memory.live"));
        if (fmt != null && (kind != null || Get(d0, "input.path") == null)) return (fmt, kind, live);
        var input = Get(d0, "input.path");
        if (input == null && fmt == null) return (null, null, null);
        if (input != null)
            foreach (var (_, m) in _manifests)
                foreach (var j in m.Jobs)
                {
                    if (j.Details == null || !string.Equals(Get(j.Details, "input.path"), input, StringComparison.OrdinalIgnoreCase)) continue;
                    fmt ??= Get(j.Details, "image.format");
                    kind ??= Get(j.Details, "dump.kind");
                    live ??= Bool(Get(j.Details, "memory.live"));
                }
        return (fmt, kind, live);
    }

    /// <summary>Manifest detail <c>rita.dataSpanHours</c> of a RITA job (invariant-culture number); null when absent or unparsable.</summary>
    private static double? RitaSpanHours(ManifestJob job) =>
        job.Details != null && job.Details.TryGetValue("rita.dataSpanHours", out var v)
        && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
        && !double.IsNaN(h) && h >= 0 ? h : null;

    public static AcquisitionKind? ParseAcquisition(string? value) =>
        Enum.TryParse<AcquisitionKind>(value, ignoreCase: true, out var k) ? k : null;

    /// <summary>Triage <see cref="AcquisitionKind"/> → advisories <see cref="EvidenceAcquisition"/> (null → Unknown).</summary>
    public static EvidenceAcquisition ToEvidenceAcquisition(AcquisitionKind? kind) => kind switch
    {
        AcquisitionKind.LiveVolume => EvidenceAcquisition.LiveVolume,
        AcquisitionKind.MountedImageOrRawCopy => EvidenceAcquisition.MountedImageOrRawCopy,
        AcquisitionKind.CopiedCollection => EvidenceAcquisition.CopiedCollection,
        AcquisitionKind.ExtractedArchive => EvidenceAcquisition.ExtractedArchive,
        _ => EvidenceAcquisition.Unknown,
    };

    /// <summary>Artifact id → advisory artifact id: families collapse (<c>amcache-*</c> → <c>amcache</c>, <c>jumplist-*</c> → <c>jumplist</c>);
    /// null when the advisories have no such artifact.</summary>
    public static string? AdvisoryArtifactId(string? artifactId)
    {
        if (string.IsNullOrEmpty(artifactId)) return null;
        var id = artifactId.StartsWith("amcache-", StringComparison.OrdinalIgnoreCase) ? "amcache"
               : artifactId.StartsWith("jumplist-", StringComparison.OrdinalIgnoreCase) ? "jumplist"
               : artifactId;
        return AdvisoryRuleSet.LoadEmbedded().FindArtifact(id)?.Id;
    }
}
