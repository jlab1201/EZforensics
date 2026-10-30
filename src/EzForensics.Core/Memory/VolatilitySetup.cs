using System.IO.Compression;
using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;

namespace EzForensics.Core.Memory;

/// <summary>One install step, run as a JobQueue job (logged, on the Jobs page, in the run manifest).</summary>
public sealed record VolatilitySetupStep(string ToolId, string Exe, IReadOnlyList<string> Args, string Label, TimeSpan Timeout, string? ExeVersion = null)
{
    public JobSpec ToJobSpec(string outputDir) =>
        new(ToolId, Path.GetFileName(Exe), Args, outputDir, Label, "Maintenance", ExePath: Exe, Timeout: Timeout, ExeVersion: ExeVersion,
            Environment: new Dictionary<string, string> { ["PYTHONNOUSERSITE"] = "1", ["PYTHONUNBUFFERED"] = "1" });
}

/// <summary>
/// Install / Uninstall of Volatility 3 in EZ Forensics' managed folder (<c>%LOCALAPPDATA%\EzForensicsData\volatility</c>:
/// <c>venv\</c> = volatility3, <c>symbols\</c>, <c>cache\</c>). The venv is based on the same private Python that plaso uses.
/// </summary>
public static class VolatilitySetup
{
    public const string ToolIdVenv = "volatility-venv", ToolIdPip = "volatility-pip", ToolIdSymbols = "volatility-symbols";

    /// <summary>
    /// Newest stable volatility3 release on PyPI when this was written (2.28.2, uploaded 2026-09-17). NOTE: verified by a real install, it does NOT ship a windows.hibernation plugin or hiberfil layer (the plan assumed it did); hibernation files cannot be analysed with Volatility yet. It
    /// needs only pefile and Python >= 3.8.
    /// </summary>
    public const string PinnedVersion = "2.28.2";

    public const string SymbolPackUrl = "https://downloads.volatilityfoundation.org/volatility3/symbols/windows.zip";
    /// <summary>windows.zip is 839,727,133 bytes (Content-Length).</summary>
    public const int SymbolPackDownloadMB = 801, SymbolPackDiskMB = 1700;
    /// <summary>Written into the symbols folder once the pack was extracted completely.</summary>
    public const string SymbolPackMarker = ".ez-windows-symbols";

    /// <summary>The libraries of volatility3's [full] extra that matter on Windows (no pillow / leechcorepyc).</summary>
    public static readonly IReadOnlyList<string> Packages = new[] { "pefile", "capstone", "pycryptodome", "yara-python" };

    /// <summary>Sizes measured with volatility3 2.28.2 on Python 3.12.10 (shown on the Install button).</summary>
    public const int PackagesDownloadMB = 22, VenvDiskMB = 60;

    public static readonly IReadOnlyList<string> VersionProbeArgs = new[] { "-c", "import importlib.metadata as m;print(m.version('volatility3'))" };

    public static (int DownloadMB, int DiskMB) Estimate(bool needsPython) =>
        (PackagesDownloadMB + (needsPython ? PlasoSetup.PythonDownloadMB : 0), VenvDiskMB + (needsPython ? PlasoSetup.PythonDiskMB : 0));

    /// <summary>Argv (after the venv's python.exe) of the pip step: <c>-m pip install ... volatility3==&lt;pin&gt; pefile capstone pycryptodome yara-python</c>.</summary>
    public static IReadOnlyList<string> PipInstall(VolatilityInstall install) =>
        new[] { "-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "volatility3==" + PinnedVersion }.Concat(Packages).ToList();

    /// <summary>The managed install description (version unknown) for <paramref name="loc"/>.</summary>
    public static VolatilityInstall ManagedInstall(VolatilityLocator loc) =>
        new(loc.ManagedExe, loc.ManagedPython, null, loc.Root, loc.SymbolsDir, loc.CacheDir);

    public static VolatilitySetupStep CreateVenvStep(VolatilityLocator loc, PythonInfo python) =>
        new(ToolIdVenv, python.Exe, new[] { "-m", "venv", loc.ManagedVenv }, "Create Volatility venv", TimeSpan.FromMinutes(15), python.Version.ToString());

    public static VolatilitySetupStep PipStep(VolatilityLocator loc) =>
        new(ToolIdPip, loc.ManagedPython, PipInstall(ManagedInstall(loc)), $"pip install volatility3=={PinnedVersion}", TimeSpan.FromMinutes(60));

    /// <summary>Enqueues the step and waits for it; <paramref name="ct"/> cancels the job (process tree killed).</summary>
    public static async Task<Job> RunStepAsync(JobQueue queue, VolatilitySetupStep step, string outputDir, Action<Job>? onJob = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var job = queue.Enqueue(step.ToJobSpec(outputDir), outputDir);
        onJob?.Invoke(job);
        using (ct.Register(job.Cancel))
            await PlasoTimelineRunner.WaitAsync(job).ConfigureAwait(false);
        return job;
    }

    /// <summary>
    /// Removes the venv and the symbol cache. Kept: <c>symbols\</c> unless <paramref name="includeSymbols"/> (the offline pack is large
    /// and the user may have imported ISF files) and the private Python (plaso shares it). Returns the folders removed.
    /// Throws <see cref="IOException"/> when a folder is in use.
    /// </summary>
    public static IReadOnlyList<string> Uninstall(string? root = null, bool includeSymbols = false)
    {
        root = Path.GetFullPath(root ?? VolatilityLocator.ManagedRoot);
        var removed = new List<string>();
        var targets = new List<string> { Path.Combine(root, "venv"), Path.Combine(root, "cache") };
        if (includeSymbols) targets.Add(Path.Combine(root, "symbols"));
        foreach (var dir in targets)
        {
            var full = Path.GetFullPath(dir);
            var name = Path.GetFileName(full.TrimEnd('\\', '/'));
            if (!name.Equals("venv", StringComparison.OrdinalIgnoreCase) && !name.Equals("cache", StringComparison.OrdinalIgnoreCase) && !name.Equals("symbols", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to delete an unexpected folder: {full}");
            PrivatePython.TryDelete(full + ".tmp");
            if (!Directory.Exists(full)) continue;
            try { Directory.Delete(full, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not remove {full}: {ex.Message} Close anything that uses Volatility and try again.", ex);
            }
            removed.Add(full);
        }
        return removed;
    }

    // ------------------------------------------------------------------ offline symbol pack

    public static bool IsOfficialSymbolUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        u.Host.Equals("downloads.volatilityfoundation.org", StringComparison.OrdinalIgnoreCase);

    public static bool SymbolPackInstalled(string symbolsDir) => File.Exists(Path.Combine(symbolsDir, SymbolPackMarker));

    /// <summary>Streams <paramref name="url"/> into <paramref name="destFile"/> (via a .part file); progress is 0..1 when the size is known.</summary>
    public static async Task DownloadFileAsync(HttpClient http, string url, string destFile, IProgress<double>? progress, Action<string>? log, CancellationToken ct)
    {
        if (!IsOfficialSymbolUrl(url)) throw new InvalidOperationException($"Refusing to download from {url}.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destFile))!);
        var part = destFile + ".part";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var host = resp.RequestMessage?.RequestUri?.Host ?? "";
        if (!host.Equals("downloads.volatilityfoundation.org", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The download was redirected to an unexpected host ({host}); stopped.");
        long total = resp.Content.Headers.ContentLength ?? 0, done = 0, lastLog = 0;
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 17];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(done / (double)total);
                    if (done - lastLog >= 100L << 20) { lastLog = done; log?.Invoke($"Downloaded {done >> 20} MB" + (total > 0 ? $" of {total >> 20} MB" : "")); }
                }
            }
            File.Move(part, destFile, overwrite: true);
        }
        catch { PrivatePython.TryDelete(part); throw; }
    }

    /// <summary>Extracts the pack into <paramref name="symbolsDir"/> keeping its folder structure (rejects paths that escape it). Returns the file count.</summary>
    public static int ExtractSymbolPack(string zipFile, string symbolsDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(symbolsDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        int n = 0;
        using var zip = ZipFile.OpenRead(zipFile);
        int count = Math.Max(1, zip.Entries.Count), i = 0;
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || string.IsNullOrEmpty(e.Name)) continue;
            var dest = Path.GetFullPath(Path.Combine(root, name));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in the symbol pack: {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
            n++;
            if (i % 200 == 0) progress?.Report(i / (double)count);
        }
        File.WriteAllText(Path.Combine(root, SymbolPackMarker), $"{DateTime.UtcNow:O} {SymbolPackUrl}\n");
        progress?.Report(1);
        return n;
    }
}
