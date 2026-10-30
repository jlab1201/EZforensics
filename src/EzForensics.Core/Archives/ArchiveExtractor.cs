using System.Security.Cryptography;

namespace EzForensics.Core.Archives;

/// <summary>Result of <see cref="ArchiveExtractor.Extract"/>.</summary>
public sealed record ExtractionOutcome(IReadOnlyList<ExtractionEntry> Entries, IReadOnlyList<string> Warnings, long BytesWritten, TimestampCoverage Coverage);

/// <summary>
/// Streams every entry to disk through <see cref="EntryPathSanitizer"/>: SHA-256 and CRC-32 computed while writing, per-entry output capped at the
/// declared size (lying headers), total output capped, symlinks/hardlinks never created, times restored after each file closes and on
/// directories bottom-up at the end. The archive is only read.
/// </summary>
public static class ArchiveExtractor
{
    public static ExtractionOutcome Extract(IArchiveEngine engine, string archivePath, string? password, ArchiveInspection inspection, string destRoot,
        ArchiveLimits limits, IExtractionLayout? layout, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        layout ??= IdentityLayout.Instance;
        if (inspection.EntryCount > limits.MaxEntries)
            throw new ArchiveLimitExceededException($"The archive lists {inspection.EntryCount:N0} entries (limit {limits.MaxEntries:N0}); not extracted.");

        Directory.CreateDirectory(destRoot);
        var sanitizer = new EntryPathSanitizer(destRoot, limits.MaxPathLength, new[] { ExtractionRecord.FileName, ExtractionRecord.FileName + ".tmp" });
        var records = new List<ExtractionEntry>();
        var warnings = new List<string>();
        var dirs = new List<(string Full, ArchiveEntryInfo Entry, int RecordIndex)>();
        var written = new List<ArchiveEntryInfo>();
        long totalBytes = 0, declaredTotal = Math.Max(1, inspection.TotalUncompressedSize);
        int count = 0, entriesTotal = inspection.EntryCount;
        bool checkDeclared = inspection.Format != ArchiveFormat.GZip;
        var buffer = new byte[1 << 16];

        foreach (var (info, data) in engine.EnumerateEntries(archivePath, password, ct))
        {
            using var _ = data;
            ct.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries)
                throw new ArchiveLimitExceededException($"More than {limits.MaxEntries:N0} entries; extraction stopped.");
            progress?.Report(new ArchiveProgress(ArchivePhase.Extracting, totalBytes, declaredTotal, count - 1, entriesTotal, info.Name));

            if (info.IsSymlink)
            {
                records.Add(Skip(info, "symbolic link, hard link or special file: not created"));
                continue;
            }
            var (mapped, layoutReason) = layout.Map(info);
            if (mapped == null)
            {
                records.Add(Skip(info, layoutReason ?? "skipped by layout"));
                continue;
            }
            var s = sanitizer.Sanitize(mapped, info.IsDirectory);
            if (s.Skipped)
            {
                records.Add(Skip(info, s.Reason));
                warnings.Add($"Skipped '{info.Name}': {s.Reason}.");
                continue;
            }
            var reason = Combine(layoutReason, s.Reason);
            var full = Path.Combine(destRoot, s.RelativePath!);
            if (info.IsDirectory)
            {
                Directory.CreateDirectory(full);
                dirs.Add((full, info, records.Count));
                records.Add(new ExtractionEntry(info.Name, s.RelativePath!, 0, true, null, false, info.TimeSource.ToString(), reason));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full) || Directory.Exists(full))
            {
                // Only possible through a name Windows aliases (an 8.3 short name such as PROGRA~1): never overwrite or delete what is there.
                const string clash = "the name resolves to a file already extracted (Windows short-name alias): not extracted";
                records.Add(Skip(info, clash));
                warnings.Add($"'{info.Name}': {clash}.");
                continue;
            }
            long n = 0;
            var crc = new Crc32();
            string? lying = null;
            string sha;
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                bool created = false;
                try
                {
                    using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    created = true;
                    int r;
                    while ((r = data.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        n += r;
                        if (checkDeclared && info.Size >= 0 && n > info.Size) { lying = "output exceeded the declared size (lying header or zip bomb): entry removed"; break; }
                        if (totalBytes + n > limits.MaxTotalOutputBytes) break;
                        fs.Write(buffer, 0, r);
                        crc.Append(buffer.AsSpan(0, r));
                        sha256.AppendData(buffer, 0, r);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not IOException || IsDecodeError(ex))
                {
                    if (created) TryDelete(full);
                    records.Add(Skip(info, $"could not be decoded ({ex.GetType().Name})"));
                    warnings.Add($"'{info.Name}' could not be decoded ({ex.GetType().Name}); skipped.");
                    if (inspection.IsSolid) throw new ArchiveIngestException($"Extraction stopped: '{info.Name}' could not be decoded ({ex.GetType().Name}).", ex);
                    continue;
                }
                catch
                {
                    if (created) TryDelete(full);   // never delete a file this entry did not create
                    throw;
                }
                sha = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            }
            if (totalBytes + n > limits.MaxTotalOutputBytes)
            {
                TryDelete(full);
                throw new ArchiveLimitExceededException($"Extracted output would exceed {limits.MaxTotalOutputBytes:N0} bytes; extraction stopped (possible zip bomb).");
            }
            if (lying != null)
            {
                TryDelete(full);
                records.Add(Skip(info, lying));
                warnings.Add($"'{info.Name}': {lying}.");
                continue;
            }
            totalBytes += n;
            bool crcOk = info.Crc32 is not { } expected || expected == crc.Value;
            if (!crcOk) warnings.Add($"'{info.Name}': CRC mismatch (the extracted copy differs from what the archive declares).");
            bool restored = TimestampRestorer.ApplyFile(full, info);
            written.Add(info);
            records.Add(new ExtractionEntry(info.Name, s.RelativePath!, n, crcOk, sha, restored, info.TimeSource.ToString(), reason));
        }

        progress?.Report(new ArchiveProgress(ArchivePhase.RestoringTimes, totalBytes, declaredTotal, count, entriesTotal, null));
        var done = TimestampRestorer.ApplyDirectories(dirs.Select(d => (d.Full, d.Entry)));
        foreach (var d in dirs)
            if (done.Contains(d.Full)) records[d.RecordIndex] = records[d.RecordIndex] with { TimesRestored = true };

        return new ExtractionOutcome(records, warnings, totalBytes, TimestampRestorer.Coverage(written));
    }

    private static bool IsDecodeError(Exception ex) => ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true || ex is InvalidDataException;

    private static ExtractionEntry Skip(ArchiveEntryInfo info, string? reason) =>
        new(info.Name, "", info.Size, false, null, false, info.TimeSource.ToString(), reason);

    private static string? Combine(string? a, string? b) => a == null ? b : b == null ? a : a + "; " + b;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
