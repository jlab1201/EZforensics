using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Archives;
using EzForensics.Core.Network;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Triage;

/// <summary>A Disk card checkbox: the system (by root path), its tick and its (edited) host label.</summary>
public sealed record IngestSystemState(string RootPath, bool Included, string HostName);

/// <summary>A Memory / Network card checkbox: the file (by path) and its tick.</summary>
public sealed record IngestPathState(string Path, bool Included);

/// <summary>A planned row: its key (<c>RuleId|MatchedPath|Label</c>) and the tick the user wants (an excluded row: the tick it gets back).</summary>
public sealed record IngestRowTick(string Key, bool Selected);

/// <summary>One line of the "Archived evidence" card.</summary>
public sealed record IngestArchiveCard(ArchiveOrigin Origin, IReadOnlyList<string> Warnings);

/// <summary>
/// The Ingest page as the user left it (source, case, scan result, card ticks, planned rows' ticks), saved before "Restart as
/// Administrator" so the elevated instance shows the same page without scanning again. Never holds a password.
/// </summary>
public sealed record IngestSessionSnapshot
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public DateTime SavedUtc { get; init; }

    public string? Source { get; init; }
    public string CaseName { get; init; } = "";
    public string CaseFolder { get; init; } = "";
    /// <summary>The user chose the case folder (it no longer follows the case name).</summary>
    public bool CaseFolderEdited { get; init; }

    /// <summary>The scan result (null: no scan yet, only the source / case are restored).</summary>
    public TriageScanResult? Scan { get; init; }
    public PcapScanResult? Pcaps { get; init; }
    public string? PcapRoot { get; init; }
    /// <summary>The source as scanned and whether it was a single file.</summary>
    public string SummarySource { get; init; } = "";
    public bool SummaryIsFile { get; init; }

    // archived evidence
    public IReadOnlyList<string>? EffectiveRoots { get; init; }
    public IReadOnlyList<ArchiveOrigin> ArchiveOrigins { get; init; } = Array.Empty<ArchiveOrigin>();
    public string? ArchiveSource { get; init; }
    public string? ArchiveCaseDir { get; init; }
    public IReadOnlyList<IngestArchiveCard> Archives { get; init; } = Array.Empty<IngestArchiveCard>();
    public IReadOnlyList<string> EvidenceArchives { get; init; } = Array.Empty<string>();
    public bool EvidenceArchivesTruncated { get; init; }
    public IReadOnlyList<string> ContainerArchives { get; init; } = Array.Empty<string>();

    // Detected artifacts cards
    public IReadOnlyList<IngestSystemState> Systems { get; init; } = Array.Empty<IngestSystemState>();
    public IReadOnlyList<IngestPathState> MemoryImages { get; init; } = Array.Empty<IngestPathState>();
    public IReadOnlyList<IngestPathState> PcapFiles { get; init; } = Array.Empty<IngestPathState>();
    public bool IncludePagefile { get; init; }

    /// <summary>Every planned row's wanted tick.</summary>
    public IReadOnlyList<IngestRowTick> Rows { get; init; } = Array.Empty<IngestRowTick>();
}

/// <summary>Saves / takes the <see cref="IngestSessionSnapshot"/> file (<c>%LOCALAPPDATA%\EzForensicsData\ingest-session.json</c>).</summary>
public static class IngestSessionStore
{
    public const string FileName = "ingest-session.json";
    /// <summary>An older snapshot is ignored (and deleted): the evidence or the case may have changed meanwhile.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, FileName);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(IngestSessionSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    /// <summary>The snapshot, or null for invalid JSON or another version.</summary>
    public static IngestSessionSnapshot? Deserialize(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<IngestSessionSnapshot>(json, Json);
            return s is { Version: IngestSessionSnapshot.CurrentVersion } ? Relink(s) : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Every artifact's <see cref="DetectedArtifact.System"/> becomes the matching instance of <see cref="TriageScanResult.Systems"/> (by root
    /// path) again, as after a scan: JSON gives each its own copy, and records holding lists only compare equal by reference.
    /// </summary>
    private static IngestSessionSnapshot Relink(IngestSessionSnapshot s)
    {
        if (s.Scan is not { } scan || scan.Systems.Count == 0) return s;
        var byRoot = new Dictionary<string, DiscoveredSystem>(StringComparer.OrdinalIgnoreCase);
        foreach (var sys in scan.Systems) byRoot.TryAdd(sys.RootPath, sys);
        for (int i = 0; i < scan.Artifacts.Count; i++)
            if (scan.Artifacts[i].System is { } a && byRoot.TryGetValue(a.RootPath, out var same))
                scan.Artifacts[i] = scan.Artifacts[i] with { System = same };
        return s;
    }

    /// <summary>Fresh enough to restore: saved at most <paramref name="maxAge"/> (default <see cref="MaxAge"/>) ago, and not in the future.</summary>
    public static bool IsFresh(IngestSessionSnapshot snapshot, DateTime nowUtc, TimeSpan? maxAge = null)
    {
        var age = nowUtc - snapshot.SavedUtc;
        return age >= TimeSpan.FromMinutes(-5) && age <= (maxAge ?? MaxAge);
    }

    /// <summary>Writes the snapshot (temp file + move, so a reader never sees half a file).</summary>
    public static void Save(IngestSessionSnapshot snapshot, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(snapshot));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Deletes the snapshot file (no error when it is missing).</summary>
    public static void Delete(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Reads and deletes the snapshot file: the snapshot when it is valid and fresh (<see cref="IsFresh"/>), else null. The file is always
    /// deleted, so a snapshot is restored at most once.
    /// </summary>
    public static IngestSessionSnapshot? Take(DateTime nowUtc, string? path = null, TimeSpan? maxAge = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        finally { Delete(path); }
        var s = Deserialize(json);
        return s != null && IsFresh(s, nowUtc, maxAge) ? s : null;
    }
}

/// <summary>
/// Command line of the elevated relaunch that restores a saved Ingest page: <c>--restore-ingest "&lt;snapshot path&gt;"</c>. The path is passed
/// because an elevated process may run with another profile (over-the-shoulder credentials) and does not inherit the caller's environment.
/// </summary>
public static class IngestSessionLaunch
{
    public const string Switch = "--restore-ingest";
    /// <summary>How long the relaunch waits for the old instance to exit and release the single-instance mutex.</summary>
    public static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<string> Args(string snapshotPath) => new[] { Switch, snapshotPath };

    public static bool HasSwitch(IReadOnlyList<string> args) => args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The snapshot path after the switch, when it names an <see cref="IngestSessionStore.FileName"/> file (anything else is ignored: the
    /// switch never reads or deletes another file); the default path when the switch has no usable path; null without the switch.
    /// </summary>
    public static string? SnapshotPath(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase)) continue;
            var p = i + 1 < args.Count ? args[i + 1] : null;
            if (!string.IsNullOrWhiteSpace(p) && !p.StartsWith("--", StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(p), IngestSessionStore.FileName, StringComparison.OrdinalIgnoreCase))
                return p;
            return IngestSessionStore.DefaultPath;
        }
        return null;
    }
}
