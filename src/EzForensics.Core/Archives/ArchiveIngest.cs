using System.Security.Cryptography;
using EzForensics.Core.Triage;

namespace EzForensics.Core.Archives;

/// <summary>
/// Turns an archive source into extracted folder(s) for the Ingest page:
/// Hashing → Inspecting → (Password) → Extracting → RestoringTimes, into <c>&lt;case&gt;\.ezstaging\extracted\&lt;sha8&gt;\&lt;stem&gt;\</c>.
/// The archive is never modified. Passwords come only from the user (via <c>passwordProvider</c>) or from passwords the user already typed in
/// this session; no password is ever guessed, logged, persisted or put in an exception message.
/// </summary>
public static class ArchiveIngest
{
    public const string ExtractedFolder = "extracted";

    public static async Task<ArchiveIngestResult> PrepareAsync(string archivePath, string caseDir,
        Func<ArchivePasswordRequest, Task<string?>> passwordProvider, IProgress<ArchiveProgress>? progress,
        CancellationToken ct, ArchiveIngestOptions? options = null)
    {
        options ??= new ArchiveIngestOptions();
        if (options.Passwords == null) options = options with { Passwords = new ArchivePasswordSession() };
        return await PrepareCoreAsync(Path.GetFullPath(archivePath), Path.GetFullPath(caseDir), passwordProvider, progress, ct, options,
            Array.Empty<string>(), depth: 1);
    }

    /// <summary>The folder the archive with this SHA-256 / file name extracts to.</summary>
    public static string ExtractionFolder(string caseDir, string sha256, string archivePath) =>
        Path.Combine(caseDir, ".ezstaging", ExtractedFolder, sha256[..8], SanitizedStem(archivePath));

    private static async Task<ArchiveIngestResult> PrepareCoreAsync(string archivePath, string caseDir,
        Func<ArchivePasswordRequest, Task<string?>> passwordProvider, IProgress<ArchiveProgress>? progress,
        CancellationToken ct, ArchiveIngestOptions options, IReadOnlyList<string> parentChain, int depth)
    {
        var engine = options.Engine ?? new SharpCompressArchiveEngine();
        var limits = options.Limits ?? new ArchiveLimits();
        var session = options.Passwords!;
        var chain = parentChain.Append(archivePath).ToList();
        var warnings = new List<string>();
        ct.ThrowIfCancellationRequested();

        var fi = new FileInfo(archivePath);
        if (!fi.Exists) throw new ArchiveIngestException($"Archive not found: {archivePath}");
        long size0 = fi.Length;
        DateTime mtime0 = fi.LastWriteTimeUtc;

        var fmt = ArchiveFormatDetector.Detect(archivePath);
        if (fmt is ArchiveFormat.Vhd or ArchiveFormat.Vhdx)
        {
            var msg = $"'{fi.Name}' is a virtual disk ({fmt}). Mount it in Windows (needs administrator rights), then pick the mounted drive as the source.";
            var vrec = new ExtractionRecord
            {
                ArchivePath = archivePath, Format = fmt, Engine = engine.Name, EngineVersion = engine.Version, ExtractedUtc = DateTime.UtcNow,
                ArchiveSize = size0, ArchiveModifiedUtc = mtime0, Warnings = new[] { msg }, Chain = chain,
            };
            var vorigin = new ArchiveOrigin(archivePath, "", fmt, false, null, "", chain, TimestampCoverage.ModifiedOnly);
            return new ArchiveIngestResult(Array.Empty<string>(), vorigin, vrec, new[] { msg }, Array.Empty<ArchiveOrigin>());
        }
        if (!ArchiveFormatDetector.IsExtractable(fmt))
            throw new ArchiveIngestException($"'{fi.Name}' is not a recognised archive (zip, 7z, rar, tar, tar.gz, gz).");

        // 1. Hash first: the SHA-256 is the provenance and the reuse key.
        var sha = await Task.Run(() => HashFile(archivePath, size0, progress, ct), ct);
        var folder = ExtractionFolder(caseDir, sha, archivePath);

        // 2. Reuse a complete extraction of the same archive (no password prompt).
        var reused = FindReusable(caseDir, sha);
        if (reused != null)
        {
            var (rfolder, rrec) = reused.Value;
            rrec = rrec with { Chain = chain };
            var rorigin = Origin(rrec, rfolder, chain);
            return await WithNestedAsync(rfolder, rrec, rorigin, new List<string> { $"Reused the extracted copy from {rrec.ExtractedUtc:yyyy-MM-dd HH:mm} UTC." },
                caseDir, passwordProvider, progress, ct, options, chain, depth);
        }
        if (Directory.Exists(folder))
        {
            // A complete copy of a *different* archive with the same 8-hex prefix and name (a crafted SHA-256 prefix collision) is never wiped.
            if (ExtractionRecord.Load(folder) is { Complete: true } other && !string.Equals(other.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                folder = folder + "~" + sha[8..16];
            if (Directory.Exists(folder)) DeleteDirectory(folder);   // incomplete: wipe and redo
        }

        // 3. Inspect, ask for the password when needed.
        progress?.Report(new ArchiveProgress(ArchivePhase.Inspecting, 0, 0, 0, 0, null));
        var insp = await Task.Run(() => engine.Inspect(archivePath), ct);
        string? password = null;
        if (insp.IsEncrypted)
        {
            progress?.Report(new ArchiveProgress(ArchivePhase.Password, 0, 0, 0, insp.EntryCount, null));
            foreach (var known in session.Snapshot())
            {
                ct.ThrowIfCancellationRequested();
                if (await Task.Run(() => engine.VerifyPassword(archivePath, known), ct)) { password = known; break; }
            }
            bool wrong = false;
            while (password == null)
            {
                ct.ThrowIfCancellationRequested();
                var typed = await passwordProvider(new ArchivePasswordRequest(archivePath, fmt, insp.EncryptionMethod, wrong));
                if (typed == null) throw new OperationCanceledException("Password entry cancelled.");
                if (await Task.Run(() => engine.VerifyPassword(archivePath, typed), ct)) { password = typed; session.Remember(typed); }
                else wrong = true;
            }
            if (insp.HeadersEncrypted)
            {
                var pw = password;
                insp = await Task.Run(() => engine.Inspect(archivePath, pw), ct);
            }
        }

        // 4. Limits and disk space.
        if (insp.EntryCount > limits.MaxEntries)
            throw new ArchiveLimitExceededException($"The archive lists {insp.EntryCount:N0} entries (limit {limits.MaxEntries:N0}); not extracted.");
        long required = insp.TotalUncompressedSize + limits.FreeSpaceMarginBytes;
        long free = FreeSpace(caseDir, options);
        bool lowSpaceAccepted = false;
        if (free < required)
        {
            var msg = $"Extracting '{fi.Name}' needs about {Gb(required)} (contents plus 1 GB margin) but the case drive has {Gb(free)} free.";
            if (options.Confirm == null) throw new ArchiveIngestException(msg);
            if (!await options.Confirm(new ArchiveConfirmRequest(ArchiveConfirmKind.LowDiskSpace, archivePath, required, free, 0, msg)))
                throw new OperationCanceledException("Extraction cancelled (low disk space).");
            warnings.Add(msg + " Continued at the user's request.");
            lowSpaceAccepted = true;
        }
        // Zip-bomb guard for what the headers do not declare (gzip has no trustworthy size; lying sizes are cut per entry): the total
        // output stops at the free space minus the margin (a small reserve when the user chose to continue on low space).
        if (free != long.MaxValue)
        {
            long reserve = lowSpaceAccepted ? Math.Min(limits.FreeSpaceMarginBytes, 64L << 20) : limits.FreeSpaceMarginBytes;
            limits = limits with { MaxTotalOutputBytes = Math.Min(limits.MaxTotalOutputBytes, Math.Max(0, free - reserve)) };
        }
        double ratio = (double)insp.TotalUncompressedSize / Math.Max(1, size0);
        if (insp.TotalUncompressedSize > limits.PromptRatioMinBytes && ratio > limits.PromptRatio)
        {
            var msg = $"'{fi.Name}' expands {ratio:N0}:1 to {Gb(insp.TotalUncompressedSize)}. This can be a zip bomb, or simply a memory image or disk image (they compress very well).";
            if (options.Confirm != null && !await options.Confirm(new ArchiveConfirmRequest(ArchiveConfirmKind.HighCompressionRatio, archivePath, insp.TotalUncompressedSize, free, ratio, msg)))
                throw new OperationCanceledException("Extraction cancelled (compression ratio).");
            warnings.Add(msg);
        }

        // 5. Extract.
        IExtractionLayout layout = IdentityLayout.Instance;
        string? layoutName = null;
        var stem = SanitizedStem(archivePath);
        if (VelociraptorLayout.IsMatch(insp))
        {
            var pw = password;
            layout = await Task.Run(() => VelociraptorLayout.Create(engine, archivePath, pw, insp, stem), ct);
            layoutName = VelociraptorLayout.LayoutName;
        }
        else if (KapeLayout.IsMatch(insp)) layoutName = KapeLayout.Name;

        var record = new ExtractionRecord
        {
            ArchivePath = archivePath, Sha256 = sha, Format = fmt, Encrypted = insp.IsEncrypted, EncryptionMethod = insp.EncryptionMethod,
            Engine = engine.Name, EngineVersion = engine.Version, ExtractedUtc = DateTime.UtcNow, Complete = false,
            ArchiveSize = size0, ArchiveModifiedUtc = mtime0, Layout = layoutName, Chain = chain,
        };
        ExtractionOutcome outcome;
        try
        {
            record.Save(folder);
            var pw = password;
            var inspection = insp;
            outcome = await Task.Run(() => ArchiveExtractor.Extract(engine, archivePath, pw, inspection, folder, limits, layout, progress, ct), ct);
        }
        catch (Exception ex)
        {
            DeleteDirectory(folder);
            TryDeleteEmptyParent(folder);
            if (ex is OperationCanceledException or ArchiveIngestException) throw;
            throw new ArchiveIngestException($"Extracting '{fi.Name}' failed ({ex.GetType().Name}).", ex);
        }
        warnings.AddRange(outcome.Warnings);

        // 6. The archive must not have changed while we read it.
        fi.Refresh();
        if (fi.Length != size0 || fi.LastWriteTimeUtc != mtime0)
            warnings.Add($"'{fi.Name}' changed while it was being extracted (size or modified time differ). Re-hash it before relying on this copy.");

        record = record with
        {
            Complete = true, Entries = outcome.Entries, Warnings = warnings.ToList(), TimestampCoverage = outcome.Coverage,
            CopyLogPath = layoutName == KapeLayout.Name ? KapeLayout.FindCopyLog(outcome.Entries) : null,
        };
        record.Save(folder);
        var origin = Origin(record, folder, chain);
        return await WithNestedAsync(folder, record, origin, warnings, caseDir, passwordProvider, progress, ct, options, chain, depth);
    }

    /// <summary>Auto-extracts "container" archives (the only top-level entry, or within the top 2 levels when no system root was found).</summary>
    private static async Task<ArchiveIngestResult> WithNestedAsync(string folder, ExtractionRecord record, ArchiveOrigin origin, List<string> warnings,
        string caseDir, Func<ArchivePasswordRequest, Task<string?>> passwordProvider, IProgress<ArchiveProgress>? progress,
        CancellationToken ct, ArchiveIngestOptions options, IReadOnlyList<string> chain, int depth)
    {
        var limits = options.Limits ?? new ArchiveLimits();
        var roots = new List<string>();
        var origins = new List<ArchiveOrigin>();
        var (containers, sole) = depth < limits.MaxNestingDepth ? FindContainers(folder) : (new List<string>(), false);
        if (!sole) { roots.Add(folder); origins.Add(origin); }
        foreach (var c in containers)
        {
            try
            {
                var nested = await PrepareCoreAsync(c, caseDir, passwordProvider, progress, ct, options, chain, depth + 1);
                roots.AddRange(nested.ExtractedRoots);
                origins.AddRange((nested.Origins ?? new[] { nested.Origin }).Select(o => WithParent(o, origin)));
                warnings.AddRange(nested.Warnings);
            }
            catch (ArchiveIngestException ex)
            {
                warnings.Add($"Nested archive '{Path.GetFileName(c)}' was not extracted: {ex.Message}");
                if (sole) { roots.Add(folder); origins.Add(origin); }
                continue;
            }
        }
        if (roots.Count == 0) { roots.Add(folder); origins.Add(origin); }
        var primary = origins.Count == 1 ? origins[0] : origin;
        return new ArchiveIngestResult(roots, primary, record, warnings, origins);
    }

    /// <summary>Hangs <paramref name="parent"/> above the outermost origin of <paramref name="o"/>'s parent chain.</summary>
    private static ArchiveOrigin WithParent(ArchiveOrigin o, ArchiveOrigin parent) =>
        o.Parent == null ? o with { Parent = parent } : o with { Parent = WithParent(o.Parent, parent) };

    /// <summary>Container archives in an extracted folder. <c>Sole</c>: the archive is the folder's only top-level entry.</summary>
    internal static (List<string> Containers, bool Sole) FindContainers(string folder)
    {
        var top = Directory.EnumerateFileSystemEntries(folder)
            .Where(p => !Path.GetFileName(p).Equals(ExtractionRecord.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        // Only files named like archives are opened: reading an extracted file would change its restored last-access time.
        if (top.Count == 1 && File.Exists(top[0]) && ArchiveFormatDetector.HasArchiveExtension(top[0]) && ArchiveFormatDetector.IsExtractable(ArchiveFormatDetector.Detect(top[0])))
            return (new List<string> { top[0] }, true);
        if (HasSystemRoot(folder, 3)) return (new List<string>(), false);   // archives inside a system tree are evidence, never auto-extracted
        var found = new List<string>();
        foreach (var f in Directory.EnumerateFiles(folder).Concat(Directory.EnumerateDirectories(folder).SelectMany(SafeFiles)))
        {
            if (Path.GetFileName(f).Equals(ExtractionRecord.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (ArchiveFormatDetector.HasArchiveExtension(f) && ArchiveFormatDetector.IsExtractable(ArchiveFormatDetector.Detect(f))) found.Add(f);
        }
        return (found, false);
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool HasSystemRoot(string dir, int depth)
    {
        if (SourceLayoutDetector.LooksLikeSystemRoot(dir)) return true;
        if (depth == 0) return false;
        try { return Directory.EnumerateDirectories(dir).Any(d => HasSystemRoot(d, depth - 1)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static (string Folder, ExtractionRecord Record)? FindReusable(string caseDir, string sha)
    {
        var shaDir = Path.Combine(caseDir, ".ezstaging", ExtractedFolder, sha[..8]);
        if (!Directory.Exists(shaDir)) return null;
        foreach (var d in Directory.EnumerateDirectories(shaDir))
        {
            var rec = ExtractionRecord.Load(d);
            if (rec != null && rec.Complete && string.Equals(rec.Sha256, sha, StringComparison.OrdinalIgnoreCase)) return (d, rec);
        }
        return null;
    }

    private static ArchiveOrigin Origin(ExtractionRecord r, string folder, IReadOnlyList<string> chain) =>
        new(r.ArchivePath, r.Sha256, r.Format, r.Encrypted, r.EncryptionMethod, folder, chain, r.TimestampCoverage);

    private static string HashFile(string path, long size, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        using var fs = SharpCompressArchiveEngine.OpenRead(path);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long done = 0;
        int n;
        var last = DateTime.MinValue;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            h.AppendData(buf, 0, n);
            done += n;
            if (progress != null && (DateTime.UtcNow - last).TotalMilliseconds > 100)
            {
                last = DateTime.UtcNow;
                progress.Report(new ArchiveProgress(ArchivePhase.Hashing, done, size, 0, 0, null));
            }
        }
        progress?.Report(new ArchiveProgress(ArchivePhase.Hashing, done, size, 0, 0, null));
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private static long FreeSpace(string caseDir, ArchiveIngestOptions options)
    {
        if (options.FreeSpace != null) return options.FreeSpace(caseDir);
        try { return new DriveInfo(Path.GetPathRoot(caseDir)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    /// <summary>The archive file name without archive extensions, made folder-safe, at most 40 characters.</summary>
    public static string SanitizedStem(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".zip", ".7z", ".rar", ".tar", ".gz", ".vhdx", ".vhd" })
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) || c < 0x20 ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (s.Length > 40) s = s[..40].TrimEnd('.', ' ');
        if (s.Length == 0) s = "archive";
        var dot = s.IndexOf('.');
        var baseName = dot >= 0 ? s[..dot] : s;
        if (ArchivePaths.EncodeSegment(baseName) != baseName) s = "_" + s;   // reserved device name
        return s;
    }

    private static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):N1} GB";

    private static void DeleteDirectory(string dir)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    private static void TryDeleteEmptyParent(string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(folder);
            if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
