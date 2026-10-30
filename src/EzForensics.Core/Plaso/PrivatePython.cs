using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace EzForensics.Core.Plaso;

/// <summary>
/// A pinned build of python.org's official NuGet package (<c>python</c> on nuget.org, authored by the Python Software
/// Foundation): a full portable CPython with venv and a bundled pip wheel. Extracting its <c>tools\</c> folder gives a
/// private interpreter: no installer, no admin rights, no PATH or registry changes.
/// </summary>
/// <param name="Sha512Base64">nuget.org's <c>packageHash</c> (SHA-512, base64) from the package's catalog entry.</param>
public sealed record PrivatePythonSpec(string Version, string Url, string Sha512Base64, long Size)
{
    public string FileName => $"python.{Version}.nupkg";
}

/// <summary>Downloads, verifies and extracts EZ Forensics' private Python (<c>%LOCALAPPDATA%\EzForensicsData\plaso\python</c>).</summary>
public static class PrivatePython
{
    public const string NuGetFlatContainer = "https://api.nuget.org/v3-flatcontainer/python/";

    /// <summary>
    /// CPython 3.12.10: the last 3.12 release with Windows binaries (3.12 is security-only since), the version plaso
    /// 20260720 was verified with on Windows, and every plaso dependency ships cp312 win_amd64 wheels (yara-python has no
    /// 3.14 wheel, so 3.14 would need a C compiler). Hash and size from nuget.org's catalog entry for python 3.12.10.
    /// </summary>
    public static readonly PrivatePythonSpec Default = new(
        "3.12.10",
        PackageUrl("3.12.10"),
        "u9pNz2iKlCEbYtUJaKkbOPMF0LjR7NkCafdKhvigpPzrt8oWKgdTpHaR6z3wyWQAm9PYGUxv0Zr66NX9AeHMDw==",
        14_515_433);

    public static string DefaultDir => Path.Combine(PlasoLocator.ManagedRoot, "python");

    public static string PythonExe(string dir) => Path.Combine(dir, "python.exe");

    /// <summary>Official nuget.org download URL of the python package (lower-case id and version, as the v3 API requires).</summary>
    public static string PackageUrl(string version) =>
        $"{NuGetFlatContainer}{version.ToLowerInvariant()}/python.{version.ToLowerInvariant()}.nupkg";

    /// <summary>Only https downloads from nuget.org or python.org are accepted.</summary>
    public static bool IsOfficialUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.nuget.org", StringComparison.OrdinalIgnoreCase)
         || u.Host.Equals("www.python.org", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("python.org", StringComparison.OrdinalIgnoreCase));

    public static string ComputeSha512Base64(Stream s)
    {
        using var sha = SHA512.Create();
        return Convert.ToBase64String(sha.ComputeHash(s));
    }

    public static bool VerifySha512(string file, string expectedBase64)
    {
        using var fs = File.OpenRead(file);
        var actual = Convert.FromBase64String(ComputeSha512Base64(fs));
        byte[] expected;
        try { expected = Convert.FromBase64String(expectedBase64); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Extracts the package's <c>tools/</c> entries into <paramref name="targetDir"/> (rejects paths that escape it).</summary>
    public static int ExtractTools(string nupkg, string targetDir, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        int n = 0;
        using var zip = ZipFile.OpenRead(nupkg);
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = e.FullName.Replace('\\', '/');
            if (!name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) || name.EndsWith('/')) continue;
            var dest = Path.GetFullPath(Path.Combine(root, name[6..]));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in the Python package: {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
            n++;
        }
        if (!File.Exists(PythonExe(targetDir))) throw new InvalidDataException("The Python package has no tools/python.exe.");
        return n;
    }

    /// <summary>
    /// Downloads the package to <c>&lt;targetDir&gt;.download\</c>, verifies size and SHA-512, extracts it to
    /// <c>&lt;targetDir&gt;.tmp</c> and renames that to <paramref name="targetDir"/>. Any failure or cancellation removes
    /// the partial download and extraction (an existing <paramref name="targetDir"/> is replaced only on success).
    /// Returns the path of python.exe.
    /// </summary>
    public static async Task<string> ProvisionAsync(HttpClient http, PrivatePythonSpec spec, string targetDir,
        Action<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!IsOfficialUrl(spec.Url)) throw new InvalidOperationException($"Refusing to download Python from a non-official URL: {spec.Url}");
        targetDir = Path.GetFullPath(targetDir);
        string dlDir = targetDir + ".download", tmpDir = targetDir + ".tmp", file = Path.Combine(dlDir, spec.FileName);
        try
        {
            TryDelete(tmpDir);
            Directory.CreateDirectory(dlDir);
            log?.Invoke($"Downloading Python {spec.Version} (python.org's NuGet package, {spec.Size / 1048576.0:0.0} MB) from {spec.Url}");
            using (var resp = await http.GetAsync(spec.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
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
                    double frac = total > 0 ? Math.Min(1, (double)got / total) : 0;
                    if (frac - lastReport >= 0.01) { lastReport = frac; progress?.Report(frac * 0.8); }
                }
            }
            var size = new FileInfo(file).Length;
            if (spec.Size > 0 && size != spec.Size)
                throw new InvalidDataException($"Python package size mismatch: expected {spec.Size:N0} bytes, got {size:N0}.");
            log?.Invoke("Verifying SHA-512 against nuget.org's published package hash…");
            if (!VerifySha512(file, spec.Sha512Base64))
                throw new InvalidDataException("Python package SHA-512 does not match the published nuget.org hash. The download was discarded.");
            log?.Invoke("SHA-512 OK. Extracting…");
            ct.ThrowIfCancellationRequested();
            int n = await Task.Run(() => ExtractTools(file, tmpDir, ct), ct).ConfigureAwait(false);
            progress?.Report(0.95);
            TryDelete(targetDir);
            Directory.Move(tmpDir, targetDir);
            log?.Invoke($"Python {spec.Version} extracted ({n:N0} files) to {targetDir}");
            progress?.Report(1);
            return PythonExe(targetDir);
        }
        catch
        {
            TryDelete(tmpDir);
            throw;
        }
        finally
        {
            TryDelete(dlDir);
        }
    }

    /// <summary>Best-effort recursive delete (ignores in-use / access errors).</summary>
    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
