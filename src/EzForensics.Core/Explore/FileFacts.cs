using System.Security.Cryptography;
using EzForensics.Core.Sysinternals;

namespace EzForensics.Core.Explore;

/// <summary>MD5 / SHA-1 / SHA-256 (upper-case hex, like <c>ManifestWriter.Hash</c>).</summary>
public sealed record FileHashes(string Md5, string Sha1, string Sha256, long Length);

/// <summary>Built-in, managed facts about one evidence file. The file is only read, never executed or shell-opened.</summary>
/// <param name="Entropy">Shannon entropy of the whole file, 0..8 bits per byte.</param>
/// <param name="ExtensionMismatch">The content type does not fit the extension (see <see cref="MagicType.IsMismatch"/>).</param>
/// <param name="Signature">Authenticode result for PE files (offline: no revocation fetch); null for other types or when not checked.</param>
/// <param name="Streams">Named NTFS alternate data streams.</param>
public sealed record FileFacts(
    FileEntry Entry,
    FileHashes Hashes,
    double Entropy,
    MagicType Type,
    string Extension,
    bool ExtensionMismatch,
    NameFlags Name,
    SignatureCheck? Signature,
    IReadOnlyList<AdsStream> Streams,
    ZoneIdentifier? Zone)
{
    public bool IsPe => Type.Id is "pe" or "pe-dll" or "pe-driver";
    public bool HasMarkOfTheWeb => Zone != null;
}

public sealed record FileFactsOptions
{
    /// <summary>Run the Authenticode check on PE files.</summary>
    public bool VerifySignature { get; init; } = true;
    /// <summary>Verifier to use; null = <see cref="SignatureVerifiers.Default"/> (WinVerifyTrust without revocation, works offline).</summary>
    public ISignatureVerifier? Verifier { get; init; }
    public bool ReadStreams { get; init; } = true;

    public static FileFactsOptions Default { get; } = new();
}

/// <summary>Computes <see cref="FileFacts"/>: one streaming read gives the three hashes, the byte histogram and the head for magic detection.</summary>
public static class FileFactsReader
{
    private const int BufferSize = 1 << 20;

    /// <summary>Opens <paramref name="path"/> read-only, shared, and never through a reparse point (throws <see cref="IOException"/> for one).</summary>
    public static FileStream OpenEvidence(string path, int bufferSize = 81920)
    {
        var attr = File.GetAttributes(path);
        if ((attr & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{path} is a reparse point (symbolic link or junction) and is not followed.");
        if ((attr & FileAttributes.Directory) != 0) throw new IOException($"{path} is a folder.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
    }

    public static FileFacts Compute(string path, FileFactsOptions? options = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        options ??= FileFactsOptions.Default;
        var entry = FileEntry.FromPath(path) ?? throw new FileNotFoundException("File not found.", path);
        var (hashes, histogram, head) = HashAndScan(path, progress, ct);
        var type = MagicSignatures.Detect(head);
        var ext = MagicSignatures.NormExt(Path.GetExtension(entry.Name));
        var name = NameChecks.Analyze(entry.Name);
        SignatureCheck? sig = null;
        if (options.VerifySignature && type.Id is "pe" or "pe-dll" or "pe-driver")
        {
            ct.ThrowIfCancellationRequested();
            sig = (options.Verifier ?? SignatureVerifiers.Default).Verify(entry.FullPath);
        }
        IReadOnlyList<AdsStream> streams = Array.Empty<AdsStream>();
        ZoneIdentifier? zone = null;
        if (options.ReadStreams)
        {
            streams = AlternateStreams.List(entry.FullPath);
            if (streams.Any(s => s.Name.Equals(AlternateStreams.ZoneIdentifierStream, StringComparison.OrdinalIgnoreCase)))
                zone = AlternateStreams.ReadZoneIdentifier(entry.FullPath);
        }
        return new FileFacts(entry, hashes, Entropy.FromHistogram(histogram, hashes.Length), type, ext, type.IsMismatch(ext), name, sig, streams, zone);
    }

    /// <summary>Hashes only (MD5, SHA-1, SHA-256 in one pass).</summary>
    public static FileHashes Hash(string path, CancellationToken ct = default) => HashAndScan(path, null, ct).Hashes;

    private static (FileHashes Hashes, long[] Histogram, byte[] Head) HashAndScan(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hist = new long[256];
        var head = new byte[MagicSignatures.HeadSize];
        int headLen = 0;
        long total = 0;
        using var fs = OpenEvidence(path, BufferSize);
        long len = Math.Max(1, fs.Length);
        var buf = new byte[BufferSize];
        int r;
        while ((r = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var span = buf.AsSpan(0, r);
            md5.AppendData(span);
            sha1.AppendData(span);
            sha256.AppendData(span);
            foreach (var b in span) hist[b]++;
            if (headLen < head.Length)
            {
                int n = Math.Min(head.Length - headLen, r);
                span[..n].CopyTo(head.AsSpan(headLen));
                headLen += n;
            }
            total += r;
            progress?.Report(Math.Min(1, (double)total / len));
        }
        var hashes = new FileHashes(
            Convert.ToHexString(md5.GetHashAndReset()), Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha256.GetHashAndReset()), total);
        return (hashes, hist, head.AsSpan(0, headLen).ToArray());
    }
}

/// <summary>Shannon entropy in bits per byte (0 = constant, 8 = random / encrypted / compressed).</summary>
public static class Entropy
{
    public static double Of(ReadOnlySpan<byte> data)
    {
        var hist = new long[256];
        foreach (var b in data) hist[b]++;
        return FromHistogram(hist, data.Length);
    }

    public static double FromHistogram(IReadOnlyList<long> histogram, long total)
    {
        if (total <= 0) return 0;
        double h = 0;
        foreach (var c in histogram)
        {
            if (c == 0) continue;
            double p = (double)c / total;
            h -= p * Math.Log2(p);
        }
        return h;
    }
}
