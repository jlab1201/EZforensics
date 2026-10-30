using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace EzForensics.Core.Archives;

/// <summary>
/// Default <see cref="IArchiveEngine"/>: SharpCompress (MIT) for zip / 7z / RAR (incl. ZipCrypto, AES zip, 7z AES with plain or encrypted
/// headers, RAR4/RAR5 -hp), System.Formats.Tar for tar / tar.gz (symlink types, pax atime) and GZipStream for single-file .gz.
/// Zip times come from our own central-directory parser (<see cref="ZipExtraFieldReader"/>) because SharpCompress only exposes the DOS mtime.
/// The archive is always opened FileAccess.Read + FileShare.Read; the password is passed in memory only and never appears in an exception message.
/// </summary>
public sealed class SharpCompressArchiveEngine : IArchiveEngine
{
    public string Name => "SharpCompress";
    public string Version { get; } = ReadVersion();

    public ArchiveInspection Inspect(string path, string? password = null)
    {
        var fmt = ArchiveFormatDetector.Detect(path);
        return fmt switch
        {
            ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar => InspectSc(path, fmt, password),
            ArchiveFormat.Tar or ArchiveFormat.TarGz => InspectTar(path, fmt),
            ArchiveFormat.GZip => InspectGz(path),
            _ => throw new ArchiveIngestException($"'{Path.GetFileName(path)}' is not an archive this engine can open ({fmt})."),
        };
    }

    public bool VerifyPassword(string path, string password) => ArchivePasswordVerifier.Verify(this, path, password);

    public Stream OpenEntry(string path, ArchiveEntryInfo entry, string? password)
    {
        var fmt = ArchiveFormatDetector.Detect(path);
        if (fmt is ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar)
        {
            var fs = OpenRead(path);
            IArchive? a = null;
            try
            {
                a = ArchiveFactory.OpenArchive(fs, new ReaderOptions { Password = password, LeaveStreamOpen = true });
                var list = a.Entries.ToList();
                var e = entry.Index >= 0 && entry.Index < list.Count && list[entry.Index].Key == entry.Name
                    ? list[entry.Index]
                    : list.FirstOrDefault(x => x.Key == entry.Name) ?? throw new ArchiveIngestException($"Entry not found in the archive: {entry.Name}");
                return new OwningStream(e.OpenEntryStream(), a, fs);
            }
            catch (Exception ex) when (ex is not ArchiveIngestException)
            {
                a?.Dispose();
                fs.Dispose();
                throw Wrap(ex, path, password != null);
            }
            catch
            {
                a?.Dispose();
                fs.Dispose();
                throw;
            }
        }
        if (fmt == ArchiveFormat.GZip)
        {
            var fs = OpenRead(path);
            return new OwningStream(new GZipStream(fs, CompressionMode.Decompress), fs);
        }
        if (fmt is ArchiveFormat.Tar or ArchiveFormat.TarGz)
        {
            var (reader, owners) = OpenTar(path, fmt);
            TarEntry? t;
            while ((t = reader.GetNextEntry()) != null)
                if (t.Name == entry.Name && t.DataStream != null) return new OwningStream(t.DataStream, owners.Prepend(reader).ToArray());
            reader.Dispose();
            foreach (var o in owners) o.Dispose();
            throw new ArchiveIngestException($"Entry not found in the archive: {entry.Name}");
        }
        throw new ArchiveIngestException($"'{Path.GetFileName(path)}' is not an archive this engine can open ({fmt}).");
    }

    public IEnumerable<(ArchiveEntryInfo Entry, Stream Data)> EnumerateEntries(string path, string? password, CancellationToken ct)
    {
        var fmt = ArchiveFormatDetector.Detect(path);
        return fmt switch
        {
            ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar => EnumerateSc(path, fmt, password, ct),
            ArchiveFormat.Tar or ArchiveFormat.TarGz => EnumerateTar(path, fmt, ct),
            ArchiveFormat.GZip => EnumerateGz(path),
            _ => throw new ArchiveIngestException($"'{Path.GetFileName(path)}' is not an archive this engine can open ({fmt})."),
        };
    }

    // ---------------------------------------------------------------- zip / 7z / rar

    private ArchiveInspection InspectSc(string path, ArchiveFormat fmt, string? password)
    {
        bool headersEnc = false;
        List<ArchiveEntryInfo>? entries = null;
        bool solid = false;
        if (fmt == ArchiveFormat.Rar) headersEnc = ArchiveInspector.RarHeadersEncrypted(path);
        if (!headersEnc)
        {
            // Listing never needs the password unless the headers are encrypted (a wrong zip AES password even breaks the listing).
            try { entries = ListSc(path, fmt, null, out solid); }
            catch (Exception ex) when (fmt != ArchiveFormat.Zip && IsPasswordSignal(ex)) { headersEnc = true; }
            catch (Exception ex) when (ex is not OperationCanceledException and not ArchiveIngestException) { throw Wrap(ex, path, false); }
        }
        if (headersEnc && password != null)
        {
            try { entries = ListSc(path, fmt, password, out solid); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new ArchiveWrongPasswordException(); }
        }
        entries ??= new List<ArchiveEntryInfo>();

        string? method = null;
        bool encrypted = headersEnc || entries.Any(e => e.IsEncrypted);
        if (encrypted)
        {
            method = fmt switch
            {
                ArchiveFormat.SevenZip => "AES-256",
                ArchiveFormat.Rar => ArchiveInspector.RarEncryptionMethod(path),
                _ => ZipExtraFieldReader.Read(path).FirstOrDefault(c => c.IsEncrypted)?.EncryptionMethod ?? "ZipCrypto",
            };
        }
        var total = entries.Where(e => !e.IsDirectory && e.Size > 0).Sum(e => e.Size);
        return new ArchiveInspection(path, fmt, encrypted, headersEnc, method, total, entries.Count, entries, solid);
    }

    private static List<ArchiveEntryInfo> ListSc(string path, ArchiveFormat fmt, string? password, out bool solid)
    {
        using var fs = OpenRead(path);
        using var a = ArchiveFactory.OpenArchive(fs, new ReaderOptions { Password = password, LeaveStreamOpen = true });
        var list = a.Entries.ToList();
        solid = a.IsSolid;
        var central = fmt == ArchiveFormat.Zip ? ZipExtraFieldReader.Read(fs) : null;
        var result = new List<ArchiveEntryInfo>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var key = e.Key ?? "";
            ZipCentralEntry? c = central == null ? null
                : central.Count == list.Count ? central[i]
                : central.FirstOrDefault(x => x.Name == key);
            bool isDir = e.IsDirectory;
            bool enc = e.IsEncrypted || (c?.IsEncrypted ?? false);
            bool link = !string.IsNullOrEmpty(e.LinkTarget) || (c?.IsSymlink ?? false);
            long? crc = (uint)e.Crc;
            if (isDir) crc = null;
            else if (fmt == ArchiveFormat.Zip && (c?.IsAes ?? false) && crc == 0 && e.Size > 0) crc = null;   // AE-2 stores no CRC
            else if (fmt == ArchiveFormat.Rar && enc) crc = null;                                              // RAR5 encrypted CRCs are HMAC-converted
            else if (fmt == ArchiveFormat.SevenZip && crc == 0 && e.Size > 0) crc = null;

            DateTime? m, cr, ac;
            EntryTimeSource src;
            if (fmt == ArchiveFormat.Zip)
            {
                if (c != null) (m, cr, ac, src) = c.BestTimes();
                else (m, cr, ac, src) = (ToUtc(e.LastModifiedTime), null, null, e.LastModifiedTime != null ? EntryTimeSource.DosLocal : EntryTimeSource.None);
            }
            else
            {
                m = ToUtc(e.LastModifiedTime);
                cr = ToUtc(e.CreatedTime);
                ac = ToUtc(e.LastAccessedTime);
                src = m == null ? EntryTimeSource.None : fmt == ArchiveFormat.SevenZip ? EntryTimeSource.SevenZipHeader : EntryTimeSource.RarHeader;
            }
            result.Add(new ArchiveEntryInfo(key, isDir ? 0 : e.Size, e.CompressedSize, isDir, enc, link, crc, m, cr, ac, src, i));
        }
        return result;
    }

    private IEnumerable<(ArchiveEntryInfo, Stream)> EnumerateSc(string path, ArchiveFormat fmt, string? password, CancellationToken ct)
    {
        var insp = InspectSc(path, fmt, password);
        if (insp.HeadersEncrypted && insp.Entries.Count == 0) throw new ArchiveWrongPasswordException();
        var infos = insp.Entries;
        using var fs = OpenRead(path);
        using var a = ArchiveFactory.OpenArchive(fs, new ReaderOptions { Password = password, LeaveStreamOpen = true });
        if (fmt == ArchiveFormat.SevenZip)
        {
            using var r = a.ExtractAllEntries();
            int idx = 0;
            while (r.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var key = r.Entry.Key ?? "";
                var info = idx < infos.Count && infos[idx].Name == key ? infos[idx] : infos.FirstOrDefault(x => x.Name == key) ?? infos[Math.Min(idx, infos.Count - 1)];
                idx++;
                yield return (info, info.IsDirectory || info.IsSymlink ? Stream.Null : r.OpenEntryStream());
            }
            yield break;
        }
        var list = a.Entries.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var info = i < infos.Count ? infos[i] : infos.First(x => x.Name == list[i].Key);
            yield return (info, info.IsDirectory || info.IsSymlink ? Stream.Null : list[i].OpenEntryStream());
        }
    }

    private static bool IsPasswordSignal(Exception ex) =>
        ex is SharpCompress.Common.CryptographicException
        || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
        || (ex.InnerException != null && IsPasswordSignal(ex.InnerException));

    private static Exception Wrap(Exception ex, string path, bool withPassword) =>
        withPassword && IsDecodeFailure(ex)
            ? new ArchiveWrongPasswordException()
            : new ArchiveIngestException($"Could not read '{Path.GetFileName(path)}' (damaged or unsupported archive: {ex.GetType().Name}).", ex);

    private static bool IsDecodeFailure(Exception ex) =>
        ex is SharpCompress.Common.CryptographicException or SharpCompress.Common.ArchiveOperationException
            or SharpCompress.Common.InvalidFormatException or InvalidDataException
            || ex.GetType().FullName == "SharpCompress.Compressors.LZMA.DataErrorException";   // internal type

    // ---------------------------------------------------------------- tar / tar.gz

    private static (TarReader Reader, IDisposable[] Owners) OpenTar(string path, ArchiveFormat fmt)
    {
        var fs = OpenRead(path);
        if (fmt == ArchiveFormat.TarGz)
        {
            var gz = new GZipStream(fs, CompressionMode.Decompress);
            return (new TarReader(gz, leaveOpen: true), new IDisposable[] { gz, fs });
        }
        return (new TarReader(fs, leaveOpen: true), new IDisposable[] { fs });
    }

    private static ArchiveInspection InspectTar(string path, ArchiveFormat fmt)
    {
        var (reader, owners) = OpenTar(path, fmt);
        var list = new List<ArchiveEntryInfo>();
        try
        {
            TarEntry? t;
            while ((t = reader.GetNextEntry()) != null)
            {
                if (t.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                list.Add(TarInfo(t, list.Count));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new ArchiveIngestException($"Could not read '{Path.GetFileName(path)}' (damaged tar: {ex.GetType().Name}).", ex);
        }
        finally
        {
            reader.Dispose();
            foreach (var o in owners) o.Dispose();
        }
        var total = list.Where(e => !e.IsDirectory && !e.IsSymlink).Sum(e => e.Size);
        return new ArchiveInspection(path, fmt, false, false, null, total, list.Count, list, fmt == ArchiveFormat.TarGz);
    }

    private static IEnumerable<(ArchiveEntryInfo, Stream)> EnumerateTar(string path, ArchiveFormat fmt, CancellationToken ct)
    {
        var (reader, owners) = OpenTar(path, fmt);
        try
        {
            int idx = 0;
            TarEntry? t;
            while ((t = reader.GetNextEntry()) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (t.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                var info = TarInfo(t, idx++);
                // TarReader owns the data stream (it skips the rest on the next entry): hand out a wrapper the caller may dispose.
                yield return (info, info.IsDirectory || info.IsSymlink || t.DataStream == null ? Stream.Null : new OwningStream(t.DataStream, leaveInnerOpen: true));
            }
        }
        finally
        {
            reader.Dispose();
            foreach (var o in owners) o.Dispose();
        }
    }

    private static ArchiveEntryInfo TarInfo(TarEntry t, int idx)
    {
        bool dir = t.EntryType == TarEntryType.Directory;
        bool regular = t.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
        DateTime? m = t.ModificationTime > DateTimeOffset.UnixEpoch ? t.ModificationTime.UtcDateTime : null;
        DateTime? a = null, c = null;
        if (t is PaxTarEntry p)
        {
            a = PaxTime(p.ExtendedAttributes, "atime");
            c = PaxTime(p.ExtendedAttributes, "LIBARCHIVE.creationtime");   // a real creation time; pax "ctime" is the inode change time and never used
        }
        else if (t is GnuTarEntry g && g.AccessTime > DateTimeOffset.UnixEpoch)
        {
            a = g.AccessTime.UtcDateTime;
        }
        var src = m == null ? EntryTimeSource.None : a != null || c != null ? EntryTimeSource.TarPax : EntryTimeSource.TarMtime;
        return new ArchiveEntryInfo(t.Name, dir ? 0 : t.Length, t.Length, dir, false, !dir && !regular, null, m, c, a, src, idx);
    }

    private static DateTime? PaxTime(IReadOnlyDictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var v) || !decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) || secs <= 0) return null;
        try { return DateTime.UnixEpoch.AddTicks((long)(secs * TimeSpan.TicksPerSecond)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---------------------------------------------------------------- single-file gzip

    private static ArchiveInspection InspectGz(string path)
    {
        var info = GzInfo(path);
        return new ArchiveInspection(path, ArchiveFormat.GZip, false, false, null, Math.Max(0, info.Estimate), 1, new[] { info.Entry }, false);
    }

    private static IEnumerable<(ArchiveEntryInfo, Stream)> EnumerateGz(string path)
    {
        var info = GzInfo(path).Entry;
        using var fs = OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        yield return (info, gz);
    }

    /// <summary>Name (FNAME or the archive name minus .gz), MTIME and the ISIZE estimate. Size is -1: ISIZE is only modulo 4 GiB, so it is never trusted as a cap.</summary>
    private static (ArchiveEntryInfo Entry, long Estimate) GzInfo(string path)
    {
        using var fs = OpenRead(path);
        var h = new byte[10];
        ArchiveFormatDetector.ReadFull(fs, h);
        int flg = h[3];
        uint mtime = BitConverter.ToUInt32(h, 4);
        string? fname = null;
        if ((flg & 0x04) != 0)
        {
            var x = new byte[2];
            ArchiveFormatDetector.ReadFull(fs, x);
            fs.Seek(BitConverter.ToUInt16(x, 0), SeekOrigin.Current);
        }
        if ((flg & 0x08) != 0)
        {
            var bytes = new List<byte>();
            int b;
            while ((b = fs.ReadByte()) > 0 && bytes.Count < 1024) bytes.Add((byte)b);
            fname = Encoding.Latin1.GetString(bytes.ToArray());
        }
        long estimate = 0;
        if (fs.Length >= 18)
        {
            fs.Seek(-4, SeekOrigin.End);
            var t = new byte[4];
            ArchiveFormatDetector.ReadFull(fs, t);
            estimate = BitConverter.ToUInt32(t, 0);
        }
        var name = fname is { Length: > 0 } ? Path.GetFileName(fname.Replace('\\', '/').Split('/').Last()) : null;
        if (string.IsNullOrEmpty(name))
        {
            var fn = Path.GetFileName(path);
            name = fn.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && fn.Length > 3 ? fn[..^3] : fn + ".out";
        }
        DateTime? m = mtime == 0 ? null : DateTime.UnixEpoch.AddSeconds(mtime);
        var entry = new ArchiveEntryInfo(name, -1, fs.Length, false, false, false, null, m, null, null, m == null ? EntryTimeSource.None : EntryTimeSource.GZipHeader, 0);
        return (entry, estimate);
    }

    // ---------------------------------------------------------------- helpers

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    private static DateTime? ToUtc(DateTime? d) => d switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } u => u,
        { Kind: DateTimeKind.Local } l => l.ToUniversalTime(),
        { } x => DateTime.SpecifyKind(x, DateTimeKind.Utc),
    };

    private static string ReadVersion()
    {
        var asm = typeof(IArchive).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "?";
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>Read-only stream that also disposes the archive/file objects it depends on.</summary>
    private sealed class OwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable[] _owners;
        private readonly bool _leaveInnerOpen;
        public OwningStream(Stream inner, params IDisposable[] owners) { _inner = inner; _owners = owners; }
        public OwningStream(Stream inner, bool leaveInnerOpen) { _inner = inner; _owners = Array.Empty<IDisposable>(); _leaveInnerOpen = leaveInnerOpen; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_leaveInnerOpen) _inner.Dispose();
                foreach (var o in _owners) o.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
