using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Archives;

/// <summary>One entry of <see cref="ExtractionRecord"/>.</summary>
/// <param name="ExtractedPath">Relative to the extracted folder; "" when skipped.</param>
/// <param name="TimeSource">The <see cref="EntryTimeSource"/> name.</param>
/// <param name="Reason">Why it was skipped (link, traversal, bomb) or renamed (percent-encoded, ~2 duplicate, hash-shortened).</param>
public sealed record ExtractionEntry(string OriginalName, string ExtractedPath, long Size, bool CrcOk, string? Sha256,
    bool TimesRestored, string? TimeSource, string? Reason);

/// <summary>
/// <c>.ezextract.json</c> in the extracted folder (camelCase, enums as text). Same SHA-256 + Complete = true → the folder is reused without a
/// password prompt; otherwise it is wiped and extracted again. Never holds a password (only <see cref="Encrypted"/> and the method).
/// </summary>
public sealed record ExtractionRecord
{
    public const string FileName = ".ezextract.json";

    public int Version { get; init; } = 1;
    public string ArchivePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public ArchiveFormat Format { get; init; }
    public bool Encrypted { get; init; }
    public string? EncryptionMethod { get; init; }
    public string Engine { get; init; } = "";
    public string EngineVersion { get; init; } = "";
    public DateTime ExtractedUtc { get; init; }
    public bool Complete { get; init; }
    public long ArchiveSize { get; init; }
    public DateTime ArchiveModifiedUtc { get; init; }
    public TimestampCoverage TimestampCoverage { get; init; }
    /// <summary>"kape" | "velociraptor" | null.</summary>
    public string? Layout { get; init; }
    public string? CopyLogPath { get; init; }
    public IReadOnlyList<ExtractionEntry> Entries { get; init; } = Array.Empty<ExtractionEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Appended: outer → inner archive paths (nested archives); [ArchivePath] otherwise.</summary>
    public IReadOnlyList<string>? Chain { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ExtractionRecord? Load(string folder)
    {
        var p = Path.Combine(folder, FileName);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<ExtractionRecord>(File.ReadAllText(p), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var p = Path.Combine(folder, FileName);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, p, overwrite: true);
    }
}
