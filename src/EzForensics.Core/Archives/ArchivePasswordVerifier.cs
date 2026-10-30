namespace EzForensics.Core.Archives;

/// <summary>
/// Checks a user-typed password by fully decoding one entry and comparing its CRC: the smallest encrypted entry, or for a solid 7z the
/// first entry of the first solid block (decoding any later entry would decode that block anyway). No password is ever guessed.
/// </summary>
public static class ArchivePasswordVerifier
{
    public static ArchiveEntryInfo? SelectProbe(ArchiveInspection inspection)
    {
        var files = inspection.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).ToList();
        if (inspection.Format == ArchiveFormat.SevenZip && inspection.IsSolid)
            return files.Where(e => e.Size > 0).OrderBy(e => e.Index).FirstOrDefault() ?? files.FirstOrDefault();
        var enc = files.Where(e => e.IsEncrypted).ToList();
        return enc.Where(e => e.Size > 0).OrderBy(e => e.Size).ThenBy(e => e.Index).FirstOrDefault() ?? enc.FirstOrDefault();
    }

    public static bool Verify(IArchiveEngine engine, string path, string password)
    {
        ArchiveInspection insp;
        try { insp = engine.Inspect(path, password); }
        catch (ArchiveWrongPasswordException) { return false; }
        if (insp.HeadersEncrypted && insp.Entries.Count == 0) return false;
        var probe = SelectProbe(insp);
        if (probe == null) return true;   // encrypted headers decoded fine and nothing else to check
        try
        {
            using var s = engine.OpenEntry(path, probe, password);
            var crc = new Crc32();
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                crc.Append(buf.AsSpan(0, n));
                total += n;
                if (total > probe.Size && probe.Size >= 0) return false;
            }
            if (total != probe.Size) return false;
            return probe.Crc32 is not { } expected || expected == crc.Value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }   // CryptographicException, DataErrorException, ArchiveOperationException, InvalidFormatException...
    }
}
