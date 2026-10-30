using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace EzForensics.Core.Rita;

/// <summary>
/// Fetches one file out of a public OCI image without Docker: anonymous pull token, the platform manifest by digest (bytes
/// verified), one layer blob by digest (SHA-256 + size verified), and only the wanted file from that layer's tar (SHA-256 + size
/// verified). Used for the rita binary (<see cref="RitaRelease.Pin"/>).
/// </summary>
public static class OciBlobFetcher
{
    public const string ManifestMediaTypes =
        "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json";

    public static string TokenUrl(string registry, string repository) => $"https://{registry}/token?scope=repository:{repository}:pull";
    public static string ManifestUrl(string registry, string repository, string digest) => $"https://{registry}/v2/{repository}/manifests/{digest}";
    public static string BlobUrl(string registry, string repository, string digest) => $"https://{registry}/v2/{repository}/blobs/{digest}";

    /// <summary>"sha256:&lt;64 hex&gt;" only.</summary>
    public static bool IsSha256Digest(string? d) =>
        d != null && d.Length == 71 && d.StartsWith("sha256:", StringComparison.Ordinal) && d[7..].All(Uri.IsHexDigit);

    public static string DigestOf(ReadOnlySpan<byte> data) => "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Anonymous pull token (ghcr.io hands one out for public repositories).</summary>
    public static async Task<string> GetTokenAsync(HttpClient http, string registry, string repository, CancellationToken ct)
    {
        using var resp = await http.GetAsync(TokenUrl(registry, repository), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 } s) return s;
        throw new InvalidDataException($"{registry} returned no pull token for {repository}.");
    }

    /// <summary>Layer digests listed by a manifest (JSON); empty when none.</summary>
    public static IReadOnlyList<(string Digest, long Size)> ParseLayers(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array) return Array.Empty<(string, long)>();
        return layers.EnumerateArray()
            .Select(l => (l.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "", l.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0L))
            .Where(x => x.Item1.Length > 0).ToList();
    }

    /// <summary>Downloads the manifest <paramref name="digest"/> and checks that its bytes hash to that digest.</summary>
    public static async Task<string> GetManifestAsync(HttpClient http, string registry, string repository, string digest, string token, CancellationToken ct)
    {
        if (!IsSha256Digest(digest)) throw new ArgumentException("Not a sha256 digest: " + digest, nameof(digest));
        using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl(registry, repository, digest));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Accept", ManifestMediaTypes);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var got = DigestOf(bytes);
        if (!got.Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The manifest from {registry} hashes to {got}, not the pinned {digest}.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Downloads blob <paramref name="digest"/> to <paramref name="dest"/> (SHA-256 and size checked; a mismatch deletes it and throws).</summary>
    public static async Task DownloadBlobAsync(HttpClient http, string registry, string repository, string digest, long expectedBytes, string token,
        string dest, IProgress<double>? progress, CancellationToken ct)
    {
        if (!IsSha256Digest(digest)) throw new ArgumentException("Not a sha256 digest: " + digest, nameof(digest));
        using var req = new HttpRequestMessage(HttpMethod.Get, BlobUrl(registry, repository, digest));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var part = dest + ".part";
        long done = 0, total = resp.Content.Headers.ContentLength ?? expectedBytes;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 17];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (expectedBytes > 0 && done > expectedBytes) throw new InvalidDataException($"The layer from {registry} is larger than the pinned {expectedBytes:N0} bytes; download stopped.");
                    if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                }
            }
            var got = "sha256:" + Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!got.Equals(digest, StringComparison.OrdinalIgnoreCase) || (expectedBytes > 0 && done != expectedBytes))
                throw new InvalidDataException($"The layer from {registry} ({done:N0} bytes) hashes to {got}, not the pinned {digest} ({expectedBytes:N0} bytes); it was deleted.");
            File.Move(part, dest, overwrite: true);
        }
        catch { TryDelete(part); throw; }
    }

    /// <summary>
    /// Extracts the single entry <paramref name="entryPath"/> (leading "./" and "/" ignored) from a gzip-compressed tar layer to
    /// <paramref name="dest"/>. Returns false when the layer does not contain it. Nothing else is written.
    /// </summary>
    public static bool ExtractEntry(string layerTgz, string entryPath, string dest)
    {
        static string Norm(string p) => p.Replace('\\', '/').TrimStart('.').TrimStart('/');
        var want = Norm(entryPath);
        using var fs = File.OpenRead(layerTgz);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        TarEntry? e;
        while ((e = tar.GetNextEntry()) != null)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (!Norm(e.Name).Equals(want, StringComparison.Ordinal)) continue;
            var part = dest + ".part";
            using (var outFs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                e.DataStream?.CopyTo(outFs);
            File.Move(part, dest, overwrite: true);
            return true;
        }
        return false;
    }

    public static string Sha256File(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>True when <paramref name="file"/> is the pinned rita binary (size + SHA-256).</summary>
    public static bool VerifyBinary(string file, RitaImagePin pin)
    {
        var f = new FileInfo(file);
        return f.Exists && f.Length == pin.BinaryBytes && Sha256File(file).Equals(pin.BinarySha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads the pinned rita binary into <paramref name="dir"/> (re-uses a verified copy) and returns its path. Every step is
    /// verified against <paramref name="pin"/>; on a mismatch nothing is kept and <see cref="InvalidDataException"/> is thrown.
    /// </summary>
    public static async Task<string> FetchRitaAsync(HttpClient http, RitaImagePin pin, string dir, IProgress<double>? progress, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "rita");
        if (File.Exists(dest) && VerifyBinary(dest, pin)) { log?.Invoke("Using the verified rita binary already downloaded."); return dest; }
        log?.Invoke($"Requesting an anonymous pull token from {RitaRelease.Registry} for {RitaRelease.Repository}");
        var token = await GetTokenAsync(http, RitaRelease.Registry, RitaRelease.Repository, ct).ConfigureAwait(false);
        var manifest = await GetManifestAsync(http, RitaRelease.Registry, RitaRelease.Repository, pin.ManifestDigest, token, ct).ConfigureAwait(false);
        if (!ParseLayers(manifest).Any(l => l.Digest.Equals(pin.LayerDigest, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"The pinned manifest {pin.ManifestDigest} does not list the layer {pin.LayerDigest}.");
        var layer = Path.Combine(dir, pin.LayerDigest.Replace(':', '_') + ".tar.gz");
        try
        {
            log?.Invoke($"Downloading layer {pin.LayerDigest} ({pin.LayerBytes / 1_000_000.0:0.0} MB) of {RitaRelease.Image}:{RitaRelease.Tag} ({pin.Architecture})");
            await DownloadBlobAsync(http, RitaRelease.Registry, RitaRelease.Repository, pin.LayerDigest, pin.LayerBytes, token, layer, progress, ct).ConfigureAwait(false);
            log?.Invoke("Layer SHA-256 verified; extracting /" + pin.BinaryPath);
            if (!ExtractEntry(layer, pin.BinaryPath, dest)) throw new InvalidDataException($"The layer does not contain /{pin.BinaryPath}.");
        }
        finally { TryDelete(layer); }
        if (!VerifyBinary(dest, pin))
        {
            var got = File.Exists(dest) ? Sha256File(dest) : "missing";
            TryDelete(dest);
            throw new InvalidDataException($"The extracted rita binary hashes to {got}, not the pinned {pin.BinarySha256}; it was deleted.");
        }
        log?.Invoke($"rita {RitaRelease.Version} binary SHA-256 verified: {pin.BinarySha256}");
        return dest;
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
