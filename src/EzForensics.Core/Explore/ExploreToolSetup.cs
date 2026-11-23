using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using EzForensics.Core.Plaso;

namespace EzForensics.Core.Explore;

/// <summary>Written next to an installed Explore tool (<c>ezforensics-tool.json</c>): what was installed and from where.</summary>
public sealed class ExploreToolMarker
{
    public string Tool { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Url { get; set; } = "";
    public string InstalledUtc { get; set; } = "";
}

/// <summary>Downloads, verifies (size + SHA-256) and extracts an Explore tool into <c>&lt;data&gt;\Tools\Explore\&lt;tool&gt;</c>.</summary>
public static class ExploreToolSetup
{
    public const string MarkerFileName = "ezforensics-tool.json";
    private const string UserAgent = "EzForensics";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ComputeSha256(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Constant-time compare of the file's SHA-256 with the pinned hex hash.</summary>
    public static bool VerifySha256(string file, string expectedHex)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ComputeSha256(file)), expected);
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="targetDir"/>: strips <see cref="ExploreToolSpec.StripPrefix"/>,
    /// applies <see cref="ExploreToolSpec.Renames"/>, rejects entries that escape the folder and requires the entry file.
    /// </summary>
    public static int Extract(string zipPath, ExploreToolSpec spec, string targetDir, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        int n = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = e.FullName.Replace('\\', '/');
            if (spec.StripPrefix.Length > 0)
            {
                if (!name.StartsWith(spec.StripPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                name = name[spec.StripPrefix.Length..];
            }
            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (spec.Renames != null && spec.Renames.TryGetValue(name, out var renamed)) name = renamed;
            var dest = Path.GetFullPath(Path.Combine(root, name));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in the {spec.Name} zip: {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
            n++;
        }
        if (!File.Exists(Path.Combine(root, spec.EntryRelPath)))
            throw new InvalidDataException($"The {spec.Name} zip has no {spec.EntryRelPath}.");
        return n;
    }

    /// <summary>
    /// Downloads the asset to <c>&lt;targetDir&gt;.download\</c>, verifies size and SHA-256, extracts it to <c>&lt;targetDir&gt;.tmp</c>,
    /// writes the marker and renames it to <paramref name="targetDir"/>. Any failure or cancellation removes the partial download and
    /// extraction; an existing install is replaced only on success. Returns the entry file path.
    /// </summary>
    public static async Task<string> InstallAsync(HttpClient http, ExploreToolSpec spec, string targetDir,
        Action<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!ExploreToolReleases.IsOfficialUrl(spec.Url))
            throw new InvalidOperationException($"Refusing to download {spec.Name} from a non-official URL: {spec.Url}");
        targetDir = Path.GetFullPath(targetDir);
        string dlDir = targetDir + ".download", tmpDir = targetDir + ".tmp", file = Path.Combine(dlDir, spec.FileName);
        try
        {
            PrivatePython.TryDelete(tmpDir);
            Directory.CreateDirectory(dlDir);
            log?.Invoke($"Downloading {spec.Name} {spec.Version} ({spec.Size / 1048576.0:0.0} MB) from {spec.Url}");
            using (var req = new HttpRequestMessage(HttpMethod.Get, spec.Url))
            {
                req.Headers.UserAgent.ParseAdd(UserAgent);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? spec.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920];
                long got = 0; int r; double lastReport = -1;
                while ((r = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, r), ct).ConfigureAwait(false);
                    got += r;
                    if (spec.Size > 0 && got > spec.Size)
                        throw new InvalidDataException($"{spec.Name} download is larger than the pinned {spec.Size:N0} bytes.");
                    double frac = total > 0 ? Math.Min(1, (double)got / total) : 0;
                    if (frac - lastReport >= 0.01) { lastReport = frac; progress?.Report(frac * 0.8); }
                }
            }
            var size = new FileInfo(file).Length;
            if (spec.Size > 0 && size != spec.Size)
                throw new InvalidDataException($"{spec.Name} size mismatch: expected {spec.Size:N0} bytes, got {size:N0}.");
            log?.Invoke("Verifying SHA-256 against the pinned hash...");
            if (!VerifySha256(file, spec.Sha256))
                throw new InvalidDataException($"{spec.Name} SHA-256 does not match the pinned hash. The download was discarded.");
            log?.Invoke("SHA-256 OK. Extracting...");
            ct.ThrowIfCancellationRequested();
            int n = await Task.Run(() => Extract(file, spec, tmpDir, ct), ct).ConfigureAwait(false);
            WriteMarker(tmpDir, spec);
            progress?.Report(0.95);
            PrivatePython.TryDelete(targetDir);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(tmpDir, targetDir);
            log?.Invoke($"{spec.Name} {spec.Version} extracted ({n:N0} files) to {targetDir}");
            progress?.Report(1);
            return Path.Combine(targetDir, spec.EntryRelPath);
        }
        catch
        {
            PrivatePython.TryDelete(tmpDir);
            throw;
        }
        finally
        {
            PrivatePython.TryDelete(dlDir);
        }
    }

    /// <summary>Installs into the locator's folder for the tool.</summary>
    public static Task<string> InstallAsync(HttpClient http, ExploreToolSpec spec, ExploreToolLocator locator,
        Action<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default) =>
        InstallAsync(http, spec, locator.ToolDir(spec.Tool), log, progress, ct);

    /// <summary>Removes an installed tool (best effort). True when the folder is gone afterwards.</summary>
    public static bool Uninstall(ExploreToolLocator locator, ExploreTool tool)
    {
        var dir = locator.ToolDir(tool);
        PrivatePython.TryDelete(dir);
        return !Directory.Exists(dir);
    }

    private static void WriteMarker(string dir, ExploreToolSpec spec)
    {
        var m = new ExploreToolMarker
        {
            Tool = spec.Tool.ToString(),
            Version = spec.Version,
            Sha256 = spec.Sha256,
            Url = spec.Url,
            InstalledUtc = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(dir, MarkerFileName), JsonSerializer.Serialize(m, Json));
    }

    /// <summary>The marker in <paramref name="dir"/>, or null when absent / unreadable.</summary>
    public static ExploreToolMarker? ReadMarker(string dir)
    {
        try
        {
            var p = Path.Combine(dir, MarkerFileName);
            return File.Exists(p)
                ? JsonSerializer.Deserialize<ExploreToolMarker>(File.ReadAllText(p), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
