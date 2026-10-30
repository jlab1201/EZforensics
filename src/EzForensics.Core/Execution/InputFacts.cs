using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Execution;

/// <summary>File-system facts of one input file, captured before the tool ran (see <see cref="InputFacts"/>).</summary>
public sealed class ManifestInputFact
{
    public string Path { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public DateTime? AccessedUtc { get; set; }
    public long? Size { get; set; }
    /// <summary>SHA-256 of the content; null when not hashed (too big, per-job cap reached, live volume, unreadable).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }
}

/// <summary>
/// Records the inputs of a job before the tool starts. Reading the metadata (FileInfo) never changes a file's times. Hashing reads the
/// content, which on a live volume can update last-access times, so it is skipped there unless asked for. Limits: at most
/// <see cref="MaxFiles"/> files per job, files up to <see cref="MaxHashFileBytes"/> are hashed, at most <see cref="MaxHashBytesPerJob"/>
/// hashed per job; whatever was left out is explained in the note.
/// </summary>
public static class InputFacts
{
    public const int MaxFiles = 5_000;
    public const long MaxHashFileBytes = 64L * 1024 * 1024;
    public const long MaxHashBytesPerJob = 512L * 1024 * 1024;

    public const string LiveVolumeNote =
        "Live volume: file times were read from the file-system metadata (this does not change them); content hashes were skipped because reading file content on a live volume can update last-access times.";

    public const string LiveVolumeHashedNote =
        "Live volume: file times were read from the file-system metadata before hashing (this does not change them); content hashes were computed as requested, and reading file content on a live volume may have updated last-access times afterwards.";

    /// <param name="hashInputs">The job asks for content hashes (<see cref="JobProvenance.HashInputs"/>).</param>
    /// <param name="isLiveVolume">The inputs are on the live system volume. The note always says so (hashed or not).</param>
    /// <param name="hashOnLiveVolume">Hash even on a live volume (<see cref="JobProvenance.HashOnLiveVolume"/>).</param>
    public static (List<ManifestInputFact> Facts, string? Note) Collect(IReadOnlyList<InputSelector> inputs, bool hashInputs, bool isLiveVolume,
        bool hashOnLiveVolume = false, CancellationToken ct = default)
    {
        var facts = new List<ManifestInputFact>();
        var notes = new List<string>();
        if (inputs.Count == 0) return (facts, null);
        bool doHash = hashInputs && (!isLiveVolume || hashOnLiveVolume);
        if (isLiveVolume) notes.Add(doHash ? LiveVolumeHashedNote : LiveVolumeNote);

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool truncated = false;
        int missing = 0;
        foreach (var sel in inputs)
        {
            foreach (var f in Expand(sel, ref missing))
            {
                if (!seen.Add(f)) continue;
                if (files.Count >= MaxFiles) { truncated = true; break; }
                files.Add(f);
            }
            if (truncated) break;
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);

        long hashed = 0;
        int tooBig = 0, capped = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var fact = new ManifestInputFact { Path = f };
            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists) { missing++; continue; }
                fact.CreatedUtc = fi.CreationTimeUtc;
                fact.ModifiedUtc = fi.LastWriteTimeUtc;
                fact.AccessedUtc = fi.LastAccessTimeUtc;
                fact.Size = fi.Length;
                if (doHash)
                {
                    if (fi.Length > MaxHashFileBytes) tooBig++;
                    else if (hashed + fi.Length > MaxHashBytesPerJob) capped++;
                    else
                    {
                        fact.Sha256 = Sha256(f);
                        hashed += fi.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            facts.Add(fact);
        }

        if (truncated) notes.Add($"Only the first {MaxFiles} input files were recorded.");
        if (tooBig > 0) notes.Add($"{tooBig} input file(s) larger than {MaxHashFileBytes / (1024 * 1024)} MB were not hashed.");
        if (capped > 0) notes.Add($"{capped} input file(s) were not hashed: the per-job hashing limit ({MaxHashBytesPerJob / (1024 * 1024)} MB) was reached.");
        if (missing > 0) notes.Add($"{missing} input path(s) could not be read (missing or access denied).");
        return (facts, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private static IEnumerable<string> Expand(InputSelector sel, ref int missing)
    {
        try
        {
            if (File.Exists(sel.Path)) return new[] { sel.Path };
            if (Directory.Exists(sel.Path))
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true, AttributesToSkip = 0, MatchCasing = MatchCasing.CaseInsensitive,
                    MatchType = MatchType.Win32, RecurseSubdirectories = sel.Recurse,
                };
                return Directory.EnumerateFiles(sel.Path, string.IsNullOrEmpty(sel.Mask) ? "*" : sel.Mask, opts).Take(MaxFiles + 1).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        missing++;
        return Array.Empty<string>();
    }

    private static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
