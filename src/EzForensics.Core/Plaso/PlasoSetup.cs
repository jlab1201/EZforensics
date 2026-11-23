using EzForensics.Core.Execution;

namespace EzForensics.Core.Plaso;

/// <summary>One install/update step, run as a JobQueue job (logged, in the Jobs list on Ingest, in the run manifest).</summary>
public sealed record PlasoSetupStep(string ToolId, string Exe, IReadOnlyList<string> Args, string Label, TimeSpan Timeout, string? ExeVersion = null)
{
    public JobSpec ToJobSpec(string outputDir) =>
        new(ToolId, Path.GetFileName(Exe), Args, outputDir, Label, "Maintenance", ExePath: Exe, Timeout: Timeout, ExeVersion: ExeVersion);
}

/// <summary>
/// "Install / Update / Uninstall" of log2timeline / plaso in EZ Forensics' managed folder
/// (<c>%LOCALAPPDATA%\EzForensicsData\plaso</c>: <c>python\</c> = private Python, <c>venv\</c> = plaso).
/// </summary>
public static class PlasoSetup
{
    public const string ToolIdVenv = "plaso-venv", ToolIdPip = "plaso-pip", ToolIdDockerPull = "plaso-docker-pull";

    /// <summary>Approximate sizes measured with plaso 20260720 + Python 3.12.10 (shown on the Install button).</summary>
    public const int PackagesDownloadMB = 35, VenvDiskMB = 105, PythonDownloadMB = 14, PythonDiskMB = 37;

    /// <summary>"≈50 MB download, ≈145 MB on disk" style estimate.</summary>
    public static (int DownloadMB, int DiskMB) Estimate(bool needsPython) =>
        (PackagesDownloadMB + (needsPython ? PythonDownloadMB : 0), VenvDiskMB + (needsPython ? PythonDiskMB : 0));

    public static PlasoSetupStep CreateVenvStep(PlasoLocator loc, PythonInfo python) =>
        new(ToolIdVenv, python.Exe, loc.InstallCommands().CreateVenv, "Create plaso venv", TimeSpan.FromMinutes(15), python.Version.ToString());

    /// <summary>
    /// The plaso release a new install gets (an exact pin, not whatever PyPI serves that day): the version the command builder and sizes
    /// were checked against. Raise it after testing a newer release.
    /// </summary>
    public const string PinnedVersion = "20260720";

    /// <param name="version">Update only: the exact release to update to (e.g. the PyPI latest the user clicked); null = <see cref="PinnedVersion"/>.</param>
    public static PlasoSetupStep PipStep(PlasoLocator loc, bool update, string? version = null)
    {
        var (py, args) = loc.UpdateCommand(update ? version : null);
        var target = args[^1];
        return new(ToolIdPip, py, args, update ? $"pip install --upgrade {target} (update)" : $"pip install {target}", TimeSpan.FromMinutes(90));
    }

    public static PlasoSetupStep DockerPullStep(string docker) =>
        new(ToolIdDockerPull, docker, new[] { "pull", PlasoLocator.DockerImage }, $"docker pull {PlasoLocator.DockerImage}", TimeSpan.FromMinutes(60));

    /// <summary>Enqueues the step and waits for it; <paramref name="ct"/> cancels the job (process tree killed).</summary>
    public static async Task<Job> RunStepAsync(JobQueue queue, PlasoSetupStep step, string outputDir, Action<Job>? onJob = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var job = queue.Enqueue(step.ToJobSpec(outputDir), outputDir);
        onJob?.Invoke(job);
        using (ct.Register(job.Cancel))
            await PlasoTimelineRunner.WaitAsync(job).ConfigureAwait(false);
        return job;
    }

    /// <summary>True when a Volatility venv (which needs the private Python in plaso's folder) exists: next to the plaso folder, or in the managed Volatility root.</summary>
    public static bool VolatilityVenvExists(PlasoLocator loc)
    {
        var plasoRoot = Path.GetDirectoryName(Path.GetFullPath(loc.ManagedVenv));
        var sibling = plasoRoot == null ? null : Path.Combine(Path.GetDirectoryName(plasoRoot) ?? plasoRoot, "volatility");
        if (sibling != null && Directory.Exists(Path.Combine(sibling, "venv"))) return true;
        // The real managed install (its Volatility root can differ from the sibling folder, e.g. EZFORENSICS_VOLATILITY_ROOT).
        return string.Equals(Path.GetFullPath(loc.ManagedVenv), Path.GetFullPath(PlasoLocator.DefaultManagedVenv), StringComparison.OrdinalIgnoreCase)
               && Memory.VolatilityLocator.ManagedVenvExists();
    }

    /// <summary>
    /// Removes the managed venv and the private Python (only those two folders; the Python stays while a Volatility venv exists). Returns the folders removed.
    /// Throws <see cref="IOException"/> when a folder is in use (e.g. a plaso job is running).
    /// </summary>
    public static IReadOnlyList<string> Uninstall(PlasoLocator loc)
    {
        var removed = new List<string>();
        foreach (var dir in loc.UninstallTargets())
        {
            var full = Path.GetFullPath(dir);
            var name = Path.GetFileName(full.TrimEnd('\\', '/'));
            if (!name.Equals("venv", StringComparison.OrdinalIgnoreCase) && !name.Equals("python", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to delete an unexpected folder: {full}");
            // The Volatility venv is based on this private Python: keep it while that venv exists.
            if (name.Equals("python", StringComparison.OrdinalIgnoreCase) && VolatilityVenvExists(loc)) continue;
            foreach (var extra in new[] { full + ".tmp", full + ".download" }) PrivatePython.TryDelete(extra);
            if (!Directory.Exists(full)) continue;
            try { Directory.Delete(full, recursive: true); }
            catch (UnauthorizedAccessException ex) { throw new IOException($"Could not remove {full}: {ex.Message} Close anything that uses plaso and try again.", ex); }
            catch (IOException ex) { throw new IOException($"Could not remove {full}: {ex.Message} Close anything that uses plaso and try again.", ex); }
            removed.Add(full);
        }
        return removed;
    }
}
