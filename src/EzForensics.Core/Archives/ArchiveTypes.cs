namespace EzForensics.Core.Archives;

/// <summary>Where an entry's timestamps came from. Appended values: <see cref="RarHeader"/>, <see cref="GZipHeader"/>.</summary>
public enum EntryTimeSource { None, DosLocal, ExtendedTimestamp /* 0x5455 */, NtfsExtra /* 0x000a */, SevenZipHeader, TarPax, TarMtime, RarHeader, GZipHeader }

/// <summary>One archive entry as listed (nothing extracted).</summary>
/// <param name="Name">The name as stored in the archive.</param>
/// <param name="IsSymlink">A symbolic link, hard link or special file (device, FIFO): never created on disk.</param>
/// <param name="Crc32">The stored CRC-32, or null when the archive has none or it cannot be checked (tar, AES zip AE-2, encrypted RAR).</param>
/// <param name="Index">Position in the archive's own listing order (appended; -1 when unknown).</param>
public sealed record ArchiveEntryInfo(string Name, long Size, long CompressedSize, bool IsDirectory, bool IsEncrypted, bool IsSymlink, long? Crc32,
    DateTime? ModifiedUtc, DateTime? CreatedUtc, DateTime? AccessedUtc, EntryTimeSource TimeSource, int Index = -1);

/// <summary>What an archive holds, read without extracting. <see cref="Entries"/> is empty when the headers are encrypted and no (correct) password was given.</summary>
public sealed record ArchiveInspection(string Path, ArchiveFormat Format, bool IsEncrypted, bool HeadersEncrypted, string? EncryptionMethod,
    long TotalUncompressedSize, int EntryCount, IReadOnlyList<ArchiveEntryInfo> Entries, bool IsSolid);

/// <summary>Archive reading backend. Implementations open the archive read-only (FileAccess.Read, FileShare.Read) and never put a password in an exception message.</summary>
public interface IArchiveEngine
{
    string Name { get; }
    string Version { get; }
    /// <summary>Lists entries without extracting. Encrypted headers without a password give HeadersEncrypted = true (never throws for that);
    /// a wrong password for encrypted headers throws <see cref="ArchiveWrongPasswordException"/>.</summary>
    ArchiveInspection Inspect(string path, string? password = null);
    /// <summary>Fully decodes the smallest encrypted entry (first entry of the first solid block for 7z) and checks its CRC.</summary>
    bool VerifyPassword(string path, string password);
    /// <summary>Forward-only stream of the decoded entry; the caller disposes it.</summary>
    Stream OpenEntry(string path, ArchiveEntryInfo entry, string? password);
    /// <summary>All entries in archive order (solid archives in one sequential pass). Each stream is valid until the next item is requested.</summary>
    IEnumerable<(ArchiveEntryInfo Entry, Stream Data)> EnumerateEntries(string path, string? password, CancellationToken ct);
}

/// <summary>Zip-bomb and path limits (injectable for tests).</summary>
public sealed record ArchiveLimits
{
    public int MaxEntries { get; init; } = 1_000_000;
    public long FreeSpaceMarginBytes { get; init; } = 1L << 30;
    public double PromptRatio { get; init; } = 200;
    public long PromptRatioMinBytes { get; init; } = 1L << 30;
    public int MaxNestingDepth { get; init; } = 2;
    public int MaxPathLength { get; init; } = 240;
    /// <summary>Appended: hard cap on the bytes written for one archive (besides the free-space check).</summary>
    public long MaxTotalOutputBytes { get; init; } = long.MaxValue;
}

public enum ArchivePhase { Hashing, Inspecting, Password, Extracting, RestoringTimes, Scanning }

public sealed record ArchiveProgress(ArchivePhase Phase, long BytesDone, long BytesTotal, int EntriesDone, int EntriesTotal, string? CurrentEntry);

/// <summary>Asks the user for a password. <paramref name="WrongPassword"/>: the previous attempt failed ("Incorrect password").</summary>
public sealed record ArchivePasswordRequest(string ArchivePath, ArchiveFormat Format, string? EncryptionMethod, bool WrongPassword);

/// <summary>Appended: why the ingest asks the user to confirm before extracting.</summary>
public enum ArchiveConfirmKind { LowDiskSpace, HighCompressionRatio }

/// <summary>Appended: a Continue / Cancel question (Cancel = <c>false</c> → OperationCanceledException; the UI may then offer "Change case folder").</summary>
public sealed record ArchiveConfirmRequest(ArchiveConfirmKind Kind, string ArchivePath, long RequiredBytes, long AvailableBytes, double Ratio, string Message);

/// <summary>Passwords the user typed in this app session (memory only, never persisted or logged). Nested and later archives try them first.</summary>
public sealed class ArchivePasswordSession
{
    private readonly List<string> _passwords = new();
    private readonly object _lock = new();
    public IReadOnlyList<string> Snapshot() { lock (_lock) return _passwords.ToArray(); }
    public void Remember(string password) { lock (_lock) if (!_passwords.Contains(password)) _passwords.Insert(0, password); }
    public void Clear() { lock (_lock) _passwords.Clear(); }
    public override string ToString() => "ArchivePasswordSession";   // never prints the passwords
}

/// <summary>Options for <see cref="ArchiveIngest.PrepareAsync"/>.</summary>
public sealed record ArchiveIngestOptions
{
    public IArchiveEngine? Engine { get; init; }
    public ArchiveLimits? Limits { get; init; }
    /// <summary>Keep the extracted copy after the run (AppSettings.KeepExtractedEvidence); the app deletes it when false.</summary>
    public bool Keep { get; init; } = true;
    /// <summary>Continue/Cancel questions (low disk space, very high compression ratio). Null: low space fails, high ratio continues with a warning.</summary>
    public Func<ArchiveConfirmRequest, Task<bool>>? Confirm { get; init; }
    /// <summary>Free bytes on the drive holding the case folder (tests inject this). Null: DriveInfo.</summary>
    public Func<string, long>? FreeSpace { get; init; }
    /// <summary>Session password memory; one is created per call when null (shared with nested archives).</summary>
    public ArchivePasswordSession? Passwords { get; init; }
}

/// <param name="Origins">Appended: one origin per extracted root (outer and nested archives), same order as <paramref name="ExtractedRoots"/>.</param>
public sealed record ArchiveIngestResult(IReadOnlyList<string> ExtractedRoots, ArchiveOrigin Origin, ExtractionRecord Record, IReadOnlyList<string> Warnings,
    IReadOnlyList<ArchiveOrigin>? Origins = null);

/// <summary>Archive ingest failure. Messages never contain a password.</summary>
public class ArchiveIngestException : Exception
{
    public ArchiveIngestException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The password is wrong (or the encrypted data is damaged).</summary>
public sealed class ArchiveWrongPasswordException : ArchiveIngestException
{
    public ArchiveWrongPasswordException() : base("Incorrect password, or the archive is damaged.") { }
}

/// <summary>A zip-bomb guard tripped (too many entries, too much output).</summary>
public sealed class ArchiveLimitExceededException : ArchiveIngestException
{
    public ArchiveLimitExceededException(string message) : base(message) { }
}
