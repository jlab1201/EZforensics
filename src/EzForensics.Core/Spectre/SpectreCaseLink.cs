using System.Text.Json;
using EzForensics.Core.Assistant;

namespace EzForensics.Core.Spectre;

/// <summary>
/// Which Spectre engine case belongs to an EZ Forensics case, so reopening the case resumes the review.
/// Stored as <c>&lt;case&gt;\.ezassistant\spectre.json</c> (hidden case-level folder).
/// </summary>
/// <param name="LastPhase">Highest analysis phase that finished (null = none yet).</param>
/// <param name="Files">The files handed to the engine with their hashes (null for links saved before this was recorded); see <see cref="EvidenceChanged"/>.</param>
public sealed record SpectreCaseLink(string EngineCaseId, int? LastPhase, DateTime OpenedAt, IReadOnlyList<SpectreHandoffFile>? Files = null)
{
    public const string FileName = "spectre.json";

    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string PathFor(string caseDir) => Path.Combine(caseDir, AssistantConsent.FolderName, FileName);

    /// <summary>The saved link, or null when there is none or the file is unreadable.</summary>
    public static SpectreCaseLink? Load(string? caseDir)
    {
        if (string.IsNullOrEmpty(caseDir)) return null;
        try
        {
            var p = PathFor(caseDir);
            if (!File.Exists(p)) return null;
            var l = JsonSerializer.Deserialize<SpectreCaseLink>(File.ReadAllText(p), Opts);
            return l != null && !string.IsNullOrWhiteSpace(l.EngineCaseId) ? l : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes the link (creates the hidden <c>.ezassistant</c> folder). A write failure is ignored: the case just is not resumed.</summary>
    public static void Save(string caseDir, SpectreCaseLink link)
    {
        try
        {
            var dir = Path.Combine(caseDir, AssistantConsent.FolderName);
            Directory.CreateDirectory(dir);
            try { File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.Hidden); } catch (Exception) { /* best effort */ }
            File.WriteAllText(PathFor(caseDir), JsonSerializer.Serialize(link, Opts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Records that analysis reached <paramref name="phase"/> (never lowers the stored phase).</summary>
    public static void RecordPhase(string caseDir, int phase)
    {
        var l = Load(caseDir);
        if (l == null) return;
        Save(caseDir, l with { LastPhase = Math.Max(l.LastPhase ?? 0, phase) });
    }

    /// <summary>
    /// Has the case's evidence changed since it was handed to the engine? Cheap first: a file whose size and modified time match is unchanged;
    /// only a file with the same size but a new time is hashed. Logs, run manifests and hidden folders do not count as evidence.
    /// Known is false when there is no link or it has no file list (then nothing can be said).
    /// </summary>
    public static EvidenceCheck EvidenceChanged(string? caseDir, CancellationToken ct = default)
    {
        var link = Load(caseDir);
        if (link?.Files == null || string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) return EvidenceCheck.Unknown;
        var root = Path.GetFullPath(caseDir);
        var stored = link.Files.Where(f => !IsBookkeeping(f.Path)).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>(); var modified = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var f in SpectreCaseHandoff.Enumerate(root))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (IsBookkeeping(rel)) continue;
            if (++n > SpectreCaseHandoff.MaxFiles) break;   // the handoff never went past this many files either
            seen.Add(rel);
            if (!stored.TryGetValue(rel, out var old)) { added.Add(rel); continue; }
            try
            {
                var info = new FileInfo(f);
                if (info.Length != old.Size) { modified.Add(rel); continue; }
                if (info.LastWriteTimeUtc.Ticks == old.ModifiedUtcTicks) continue;
                if (!string.Equals(SpectreCaseHandoff.Sha256(f), old.Sha256, StringComparison.OrdinalIgnoreCase)) modified.Add(rel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { modified.Add(rel); }
        }
        var removed = stored.Keys.Where(k => !seen.Contains(k)).ToList();
        return new EvidenceCheck(true, added.Count + modified.Count + removed.Count > 0, added, removed, modified);
    }

    private static bool IsBookkeeping(string relPath) =>
        relPath.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relPath).StartsWith("EzForensics_manifest_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A file as handed to the engine. <see cref="ModifiedUtcTicks"/> is the file's UTC last-write time in ticks.</summary>
public sealed record SpectreHandoffFile(string Path, string Sha256, long Size, long ModifiedUtcTicks);

/// <summary>Result of <see cref="SpectreCaseLink.EvidenceChanged"/>.</summary>
public sealed record EvidenceCheck(bool Known, bool Changed, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Modified)
{
    public static EvidenceCheck Unknown { get; } = new(false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}
