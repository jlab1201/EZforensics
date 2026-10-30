using System.ComponentModel;
using System.Diagnostics;
using EzForensics.Core.Catalog;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;
using EzForensics.Core.Tools;
using EzForensics.Core.Rita;
using EzForensics.Core.Zeek;

namespace EzForensics.Core.Setup;

/// <summary>Starts a process elevated (UAC prompt). Abstracted so Core stays testable and portable.</summary>
public interface IElevatedLauncher
{
    /// <summary>Runs <paramref name="exe"/> elevated and waits; returns the exit code. Throws <see cref="OperationCanceledException"/> when the user declines the prompt.</summary>
    Task<int> RunElevatedAsync(string exe, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Windows: ShellExecute with the "runas" verb.</summary>
public sealed class WindowsElevatedLauncher : IElevatedLauncher
{
    public async Task<int> RunElevatedAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Elevation is only implemented on Windows.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("The administrator prompt was declined."); }
        if (p == null) throw new InvalidOperationException($"{exe} did not start.");
        using (p)
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode;
        }
    }
}

/// <summary>Everything the real executor needs from the host (WPF app now, a CLI later).</summary>
public sealed class SetupEnvironment
{
    /// <summary>
    /// Queue for the setup processes: the host's private maintenance queue (<see cref="MaintenanceRunner.Queue"/>), never the
    /// queue the Jobs page lists. Job logs and the manifest go to <see cref="MaintenanceDir"/>.
    /// </summary>
    public required JobQueue Queue { get; init; }
    public required Func<HttpClient> CreateHttp { get; init; }
    public required AppSettings Settings { get; init; }
    public required Action SaveSettings { get; init; }
    public required IToolLocator Locator { get; init; }
    /// <summary>Folder for the setup jobs' logs / manifest (e.g. &lt;case root&gt;\_maintenance\&lt;stamp&gt;_setup).</summary>
    public required string MaintenanceDir { get; init; }
    public IElevatedLauncher Elevation { get; init; } = new WindowsElevatedLauncher();
    public IRuntimeProbe Probe { get; init; } = SystemRuntimeProbe.Instance;
    /// <summary>Called after something was installed so the host can refresh its state (tools rescan, plaso / Zeek discovery).</summary>
    public Func<SetupStepKind, Task>? AfterStep { get; init; }
    /// <summary>
    /// Test hook: these step kinds run a fake tool (e.g. EzForensics.FakeTool.exe) as a job instead of the real install, so the wizard can be
    /// exercised end to end without changing the machine. Never set in normal use (EZFORENSICS_SETUP_SIMULATE).
    /// </summary>
    public string? SimulatorExe { get; init; }
    public IReadOnlySet<SetupStepKind> Simulate { get; init; } = new HashSet<SetupStepKind>();
    /// <summary>The host already runs as Administrator: admin steps run in-process, no UAC prompt.</summary>
    public bool IsElevated { get; init; }
    /// <summary>
    /// The host's own exe, relaunched elevated with <see cref="ElevatedSetupHelper.Switch"/> to run every admin task in one UAC prompt.
    /// Null: the admin command itself is started elevated.
    /// </summary>
    public string? ElevatedHelperExe { get; init; }
    /// <summary>Arguments before the switch (the app dll when the host runs through dotnet.exe).</summary>
    public IReadOnlyList<string> ElevatedHelperPrefixArgs { get; init; } = Array.Empty<string>();
    /// <summary>Windows has a restart pending (component servicing). Replaceable for tests.</summary>
    public Func<bool> RebootPending { get; init; } = ElevatedSetupHelper.IsRebootPending;
    /// <summary>
    /// Keeps WSL's "Welcome to WSL" window from opening during the WSL steps (and closes it if it did). Null = do nothing (tests);
    /// the app passes <see cref="WslWelcomeGuard.CreateDefault"/>.
    /// </summary>
    public WslWelcomeGuard? WslWelcome { get; init; }
}

/// <summary>
/// Real step implementations, reusing the existing installers: <see cref="ToolUpdater"/> (EZ tools), JobQueue jobs (--sync, venv,
/// pip, Zeek, dotnet-install), <see cref="PrivatePython"/>, <see cref="PlasoSetup"/>, <see cref="ZeekSetup"/>.
/// </summary>
public sealed class DefaultSetupExecutor : ISetupExecutor
{
    public const string DotnetInstallScriptUrl = "https://dot.net/v1/dotnet-install.ps1";

    private readonly SetupEnvironment _env;

    public DefaultSetupExecutor(SetupEnvironment env) => _env = env;

    /// <summary>
    /// Where the dotnet-install script may end up after redirects: https on dot.net or microsoft.com, or one of their sub-domains
    /// (not "evildot.net" / "notmicrosoft.com"). The script is run with -ExecutionPolicy Bypass, so anything else is refused.
    /// </summary>
    public static bool IsTrustedDotnetScriptUri(Uri? u)
    {
        if (u == null || !u.IsAbsoluteUri || u.Scheme != Uri.UriSchemeHttps) return false;
        static bool Under(string host, string domain) =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        return Under(u.Host, "dot.net") || Under(u.Host, "microsoft.com");
    }

    public async Task<SetupStepResult> ExecuteAsync(SetupStepContext c, CancellationToken ct)
    {
        var step = c.Step;
        if (_env.SimulatorExe != null && _env.Simulate.Contains(step.Kind))
            return await SimulateAsync(c, ct).ConfigureAwait(true);
        bool wslStep = IsWslStep(step.Kind);
        if (wslStep) GuardWslWelcome(c, g => g.MarkOobeComplete() is { } line ? new[] { line } : Array.Empty<string>());
        var r = step.Kind switch
        {
            SetupStepKind.BuiltIn => SetupStepResult.Ok("Ready (part of EZ Forensics)."),
            SetupStepKind.InstallDotnet => await InstallDotnetAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.DownloadEzTools => await DownloadEzToolsAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.SyncMaps => await SyncMapsAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.ProvisionPython => await ProvisionPythonAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.CreatePlasoVenv => await CreateVenvAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.InstallPlaso => await InstallPlasoAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.InstallVolatility => await InstallVolatilityAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.InstallVolatilitySymbols => await InstallVolatilitySymbolsAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.EnableWsl => await EnableWslAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.InstallRita => await InstallRitaAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.InstallEzDistro => await InstallEzDistroAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.InstallZeekWsl => await InstallZeekWslAsync(c, ct).ConfigureAwait(true),
            SetupStepKind.ZeekGuidance => SetupStepResult.NeedsAction(step.Detail),
            _ => SetupStepResult.Fail($"Unknown step {step.Kind}."),
        };
        if (wslStep) GuardWslWelcome(c, g => g.CloseWelcomeWindows());
        if (r.State == SetupStepState.Done && _env.AfterStep != null)
        {
            try { await _env.AfterStep(step.Kind).ConfigureAwait(true); }
            catch (Exception ex) { c.Log("(refresh after the step failed: " + ex.Message + ")"); }
        }
        return r;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Steps that run wsl.exe (and can make WSL open its "Welcome to WSL" window).</summary>
    public static bool IsWslStep(SetupStepKind kind) =>
        kind is SetupStepKind.EnableWsl or SetupStepKind.InstallEzDistro or SetupStepKind.InstallZeekWsl or SetupStepKind.InstallRita;

    private void GuardWslWelcome(SetupStepContext c, Func<WslWelcomeGuard, IReadOnlyList<string>> action)
    {
        if (_env.WslWelcome is not { } guard) return;
        try { foreach (var line in action(guard)) c.Log(line); }
        catch (Exception ex) { c.Log("(WSL welcome window check failed: " + ex.Message + ")"); }
    }

    /// <summary>
    /// A running step that logged nothing for this long gets a "still working" line (<see cref="StepHeartbeat"/>). Settable for tests,
    /// like <see cref="ToolUpdater.StallTimeout"/>.
    /// </summary>
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Starts a heartbeat for the running step (dispose it when the quiet part is over).</summary>
    public static StepHeartbeat Heartbeat(SetupStepContext c, string stillText) => new(c.Step, stillText, HeartbeatInterval);

    /// <summary>Heartbeat texts of the steps whose progress comes only from log lines.</summary>
    public const string WslHeartbeat = "Still enabling WSL… Windows is installing components; this can take several minutes";
    public const string Wsl1Heartbeat = "Still enabling the Windows Subsystem for Linux component… this can take several minutes";
    public const string VenvHeartbeat = "Still creating the Python environment… this can take a minute";
    public const string PlasoPipHeartbeat = "Still installing plaso with pip… downloading and building packages can take several minutes";
    public const string VolatilityPipHeartbeat = "Still installing Volatility 3 with pip… downloading packages can take several minutes";
    public const string ZeekAptHeartbeat = "Still installing Zeek with apt… downloading and unpacking packages can take several minutes";
    public const string DistroHeartbeat = "Still setting up the Linux environment… wsl --import and the first configuration can take several minutes";

    /// <summary>Enqueues a job, streams its output into the step log, waits, and maps the outcome.</summary>
    /// <param name="heartbeat">"Still …" text logged when the job prints nothing for <see cref="HeartbeatInterval"/>; null = none.</param>
    private async Task<Job> RunJobAsync(SetupStepContext c, JobSpec spec, CancellationToken ct, string? heartbeat = null)
    {
        using var hb = heartbeat != null ? Heartbeat(c, heartbeat) : null;
        return await MaintenanceRunner.RunAsync(_env.Queue, spec, _env.MaintenanceDir, c.Log, ct).ConfigureAwait(true);
    }

    private static SetupStepResult FromJob(Job job, string ok, string failHint) => job.State switch
    {
        JobState.Succeeded => SetupStepResult.Ok(ok),
        JobState.Cancelled => new SetupStepResult(SetupStepState.Cancelled, "Cancelled."),
        JobState.TimedOut => SetupStepResult.Fail($"It took too long and was stopped. {failHint}"),
        _ => SetupStepResult.Fail($"{job.StatusReason ?? "It failed"}. {failHint}"),
    };

    private string Maint(string name) => Path.Combine(_env.MaintenanceDir, name);

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<SetupStepResult> SimulateAsync(SetupStepContext c, CancellationToken ct)
    {
        c.Log($"(simulated with {Path.GetFileName(_env.SimulatorExe)}: nothing is installed)");
        var spec = new JobSpec("setup-simulated", Path.GetFileName(_env.SimulatorExe!), new[] { "--lines", "12", "--delay", "150" },
            Maint("simulated"), "Simulated: " + c.Step.Title, "Maintenance", ExePath: _env.SimulatorExe, Timeout: TimeSpan.FromMinutes(5));
        var job = await RunJobAsync(c, spec, ct).ConfigureAwait(true);
        return FromJob(job, "Done (simulated).", "");
    }

    // ------------------------------------------------------------------ .NET 9 (private)

    private async Task<SetupStepResult> InstallDotnetAsync(SetupStepContext c, CancellationToken ct)
    {
        if (RuntimeProbe.HasSystemNet9()) return SetupStepResult.AlreadyDone("The .NET 9 runtime is already installed on this computer.");
        var dir = Path.GetFullPath(RuntimeProbe.PrivateDotnetRoot);
        if (RuntimeProbe.HasFramework(dir, "Microsoft.WindowsDesktop.App", 9) && RuntimeProbe.HasFramework(dir, "Microsoft.NETCore.App", 9))
            return SetupStepResult.AlreadyDone($"EZ Forensics' private .NET 9 runtime is already in {dir}.");
        var scriptDir = Maint("dotnet-install");
        Directory.CreateDirectory(scriptDir);
        var script = Path.Combine(scriptDir, "dotnet-install.ps1");
        c.Log($"Downloading Microsoft's install script from {DotnetInstallScriptUrl}");
        using (var http = _env.CreateHttp())
        {
            using var resp = await http.GetAsync(DotnetInstallScriptUrl, ct).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            var final = resp.RequestMessage?.RequestUri;
            if (!IsTrustedDotnetScriptUri(final))
                return SetupStepResult.Fail($"The install script came from an unexpected location ({final}); not run.");
            await File.WriteAllBytesAsync(script, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(true), ct).ConfigureAwait(true);
        }
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        // Two runs into the same folder: "windowsdesktop" brings only Microsoft.WindowsDesktop.App, the base runtime
        // (Microsoft.NETCore.App, needed by every net9 apphost) is the "dotnet" runtime. Verified with dotnet-install 9.0.20.
        var runtimes = new[] { ("dotnet", "Microsoft.NETCore.App"), ("windowsdesktop", "Microsoft.WindowsDesktop.App") };
        for (int i = 0; i < runtimes.Length; i++)
        {
            var (runtime, framework) = runtimes[i];
            if (RuntimeProbe.HasFramework(dir, framework, 9)) { c.Log($"{framework} 9 is already in {dir}."); continue; }
            var spec = new JobSpec("dotnet-install", "powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Runtime", runtime, "-Channel", "9.0",
                        "-InstallDir", dir, "-Architecture", "x64", "-NoPath" },
                scriptDir, $"Install .NET 9 runtime '{runtime}' (private, dotnet-install.ps1)", "Maintenance", ExePath: ps, Timeout: TimeSpan.FromMinutes(30));
            var job = await RunJobAsync(c, spec, ct).ConfigureAwait(true);
            if (job.State != JobState.Succeeded) return FromJob(job, "", "Check the internet connection / proxy, then click Retry.");
            c.Report((i + 1) / (double)runtimes.Length);
        }
        RuntimeProbe.Reset();
        if (!RuntimeProbe.HasPrivateNet9())
            return SetupStepResult.Fail($"dotnet-install finished but no .NET 9 runtime is in {dir}. See the log.");
        RuntimeProbe.ApplyToCurrentProcess();
        return SetupStepResult.Ok($".NET 9 Desktop Runtime installed in {dir} (used only by EZ Forensics' tools).");
    }

    // ------------------------------------------------------------------ EZ tools

    private async Task<SetupStepResult> DownloadEzToolsAsync(SetupStepContext c, CancellationToken ct)
    {
        var step = c.Step;
        int netVersion = step.Args.TryGetValue("netVersion", out var nv) && nv == "4" ? 4 : 9;
        if (netVersion == 9 && !RuntimeProbe.HasNet9())
        {
            c.Log(".NET 9 is not available: using the net4 builds.");
            netVersion = 4;
        }
        var root = _env.Settings.ToolsRoot;
        Directory.CreateDirectory(root);
        using var http = _env.CreateHttp();
        var updater = new ToolUpdater(http);
        c.Log($"Reading the EZ tools index ({ToolUpdater.IndexUrl})…");
        var all = await updater.CheckAsync(root, 0, ct).ConfigureAwait(true);
        var pick = new List<RemoteToolFile>();
        var missing = new List<string>();
        foreach (var zip in step.Zips)
        {
            var cands = all.Where(f => f.Name.Equals(zip, StringComparison.OrdinalIgnoreCase)).ToList();
            var want = cands.FirstOrDefault(f => f.IsNet9 == (netVersion == 9))
                       ?? (netVersion == 9 ? cands.FirstOrDefault() : null); // net4-only zips are fine with .NET 9 too
            if (want == null) { missing.Add(zip); continue; }
            if (want.Status != RemoteFileStatus.UpToDate) pick.Add(want);
        }
        if (missing.Count > 0) c.Log($"Not available as {(netVersion == 9 ? "net9" : "net4")} builds: {string.Join(", ", missing)}");
        if (_env.Settings.NetVersionToDownload != netVersion)
        {
            _env.Settings.NetVersionToDownload = netVersion;
            _env.SaveSettings();
        }
        if (pick.Count == 0)
        {
            _env.Locator.Rescan(root);
            return SetupStepResult.AlreadyDone("The selected tools are already up to date in " + root + ".");
        }
        long totalBytes = Math.Max(1, pick.Sum(f => Math.Max(f.Size, 1)));
        long doneBytes = 0;
        string? lastName = null;
        var progress = new Progress<UpdateProgress>(p =>
        {
            if (p.Name != lastName || p.Phase.StartsWith("Retrying", StringComparison.Ordinal))
            {
                c.Log(p.Phase.StartsWith("Retrying", StringComparison.Ordinal)
                    ? $"Network error while downloading {p.Name}; {p.Phase.ToLowerInvariant()} in a few seconds…"
                    : $"{p.Phase} {p.Name} ({p.FileIndex}/{p.FileCount})");
                lastName = p.Name;
            }
            if (p.Phase == "Done") { doneBytes += Math.Max(pick[p.FileIndex - 1].Size, 1); c.Report((double)doneBytes / totalBytes); }
            else if (p.Phase == "Downloading") c.Report((doneBytes + p.BytesReceived) / (double)totalBytes);
        });
        await updater.DownloadAsync(pick, root, progress, ct).ConfigureAwait(true);
        _env.Locator.Rescan(root);
        return SetupStepResult.Ok($"Downloaded {pick.Count} tool package(s) ({(netVersion == 9 ? "net9" : "net4")} builds, {pick.Sum(f => f.Size) / 1048576.0:0} MB) into {root}." +
                                  (missing.Count > 0 ? $" Not available for this .NET: {string.Join(", ", missing.Select(Path.GetFileNameWithoutExtension))}." : ""));
    }

    private async Task<SetupStepResult> SyncMapsAsync(SetupStepContext c, CancellationToken ct)
    {
        _env.Locator.Rescan(_env.Settings.ToolsRoot);
        var dir = Maint("sync");
        int ok = 0;
        var failed = new List<string>();
        var ids = new[] { "EvtxECmd", "RECmd", "SQLECmd" };
        for (int i = 0; i < ids.Length; i++)
        {
            var def = ToolCatalog.Find(ids[i]);
            if (def == null) continue;
            if (_env.Locator.Resolve(def.Exe, _env.Settings.PreferredBuildFlavor) == null) { c.Log($"{def.Exe} is not installed; skipped."); continue; }
            var job = await RunJobAsync(c, new JobSpec(def.Id, def.Exe, new[] { "--sync" }, dir, $"{def.DisplayName} --sync", "Maintenance"), ct).ConfigureAwait(true);
            if (job.State == JobState.Succeeded) ok++; else failed.Add($"{def.DisplayName}: {job.StatusReason}");
            c.Report((i + 1) / (double)ids.Length);
        }
        if (failed.Count > 0) return SetupStepResult.Fail("Sync failed for " + string.Join("; ", failed) + ". GitHub must be reachable (check the proxy), then click Retry.");
        return ok == 0 ? SetupStepResult.Fail("None of EvtxECmd, RECmd, SQLECmd is installed, so nothing could be synced.")
            : SetupStepResult.Ok($"Maps and batch files updated ({ok} tool(s)).");
    }

    // ------------------------------------------------------------------ plaso

    private async Task<SetupStepResult> ProvisionPythonAsync(SetupStepContext c, CancellationToken ct)
    {
        var loc = new PlasoLocator();
        if (loc.FindSupportedPython() is { } existing) return SetupStepResult.AlreadyDone($"Python {existing.Version} is available: {existing.Exe}");
        Directory.CreateDirectory(PlasoLocator.ManagedRoot);
        using var http = _env.CreateHttp();
        var exe = await PrivatePython.ProvisionAsync(http, PrivatePython.Default, loc.PrivatePythonDir, c.Log, new Progress<double>(p => c.Report(p)), ct).ConfigureAwait(true);
        return loc.FindSupportedPython() is { IsPrivate: true }
            ? SetupStepResult.Ok($"Private Python {PrivatePython.Default.Version} ready: {exe}")
            : SetupStepResult.Fail("The private Python was extracted but does not start. See the log, then click Retry.");
    }

    private async Task<SetupStepResult> CreateVenvAsync(SetupStepContext c, CancellationToken ct)
    {
        var loc = new PlasoLocator();
        var (_, venvPython, _) = loc.InstallCommands();
        if (File.Exists(venvPython)) return SetupStepResult.AlreadyDone($"The plaso environment already exists: {loc.ManagedVenv}");
        var py = loc.FindSupportedPython();
        if (py == null) return SetupStepResult.Fail("No Python 3.10-3.13 is available. Click Retry (it downloads a private Python).");
        Directory.CreateDirectory(PlasoLocator.ManagedRoot);
        var job = await RunJobAsync(c, PlasoSetup.CreateVenvStep(loc, py).ToJobSpec(Maint("plaso")), ct, VenvHeartbeat).ConfigureAwait(true);
        if (job.State != JobState.Succeeded || !File.Exists(venvPython))
        {
            PrivatePython.TryDelete(loc.ManagedVenv); // never leave a half-made venv behind
            return FromJob(job, "", "See the log, then click Retry.");
        }
        return SetupStepResult.Ok($"Created {loc.ManagedVenv} with Python {py.Version}.");
    }

    private async Task<SetupStepResult> InstallPlasoAsync(SetupStepContext c, CancellationToken ct)
    {
        var loc = new PlasoLocator();
        var job = await RunJobAsync(c, PlasoSetup.PipStep(loc, update: false).ToJobSpec(Maint("plaso")), ct, PlasoPipHeartbeat).ConfigureAwait(true);
        if (job.State != JobState.Succeeded)
            return FromJob(job, "", "pip needs PyPI (pypi.org / files.pythonhosted.org): check the internet connection and proxy (pip uses the system proxy or HTTPS_PROXY), then click Retry.");
        var d = await Task.Run(() => loc.Discover(_env.Settings.PlasoPath, includeDocker: false), ct).ConfigureAwait(true);
        if (!loc.IsManaged(d.Selected) && !string.IsNullOrWhiteSpace(_env.Settings.PlasoPath))
        {
            c.Log($"Configured plaso path '{_env.Settings.PlasoPath}' replaced by automatic detection (EZ Forensics' own copy).");
            _env.Settings.PlasoPath = null;
            _env.SaveSettings();
            d = await Task.Run(() => loc.Discover(null, includeDocker: false), ct).ConfigureAwait(true);
        }
        return d.Selected != null ? SetupStepResult.Ok($"plaso {d.Selected.Version} installed ({loc.ManagedVenv}).")
            : SetupStepResult.Fail("pip finished but plaso does not answer to --version. See the log, then click Retry.");
    }

    // ------------------------------------------------------------------ Volatility 3

    private async Task<SetupStepResult> InstallVolatilityAsync(SetupStepContext c, CancellationToken ct)
    {
        var vl = new VolatilityLocator();
        var install = VolatilitySetup.ManagedInstall(vl);
        Directory.CreateDirectory(vl.Root);
        if (!File.Exists(vl.ManagedPython))
        {
            var py = new PlasoLocator().FindSupportedPython();
            if (py == null) return SetupStepResult.Fail("No Python 3.10-3.13 is available. Click Retry (it downloads a private Python).");
            c.Log($"Creating the Volatility environment with Python {py.Version} ({py.Exe})…");
            var venvJob = await RunJobAsync(c, VolatilitySetup.CreateVenvStep(vl, py).ToJobSpec(Maint("volatility")), ct, VenvHeartbeat).ConfigureAwait(true);
            if (venvJob.State != JobState.Succeeded || !File.Exists(vl.ManagedPython))
            {
                PrivatePython.TryDelete(vl.ManagedVenv); // never leave a half-made venv behind
                return FromJob(venvJob, "", "See the log, then click Retry.");
            }
        }
        c.Report(0.1);
        var job = await RunJobAsync(c, VolatilitySetup.PipStep(vl).ToJobSpec(Maint("volatility")), ct, VolatilityPipHeartbeat).ConfigureAwait(true);
        if (job.State != JobState.Succeeded)
            return FromJob(job, "", "pip needs PyPI (pypi.org / files.pythonhosted.org): check the internet connection and proxy (pip uses the system proxy or HTTPS_PROXY), then click Retry.");
        Directory.CreateDirectory(install.SymbolsDir);
        Directory.CreateDirectory(install.CacheDir);
        var found = await Task.Run(() => vl.Discover(_env.Settings.VolatilityPath), ct).ConfigureAwait(true);
        if (found != null && !string.IsNullOrWhiteSpace(_env.Settings.VolatilityPath) &&
            !found.Exe.Equals(install.Exe, StringComparison.OrdinalIgnoreCase))
        {
            c.Log($"Configured Volatility path '{_env.Settings.VolatilityPath}' replaced by automatic detection (EZ Forensics' own copy).");
            _env.Settings.VolatilityPath = null;
            _env.SaveSettings();
            found = await Task.Run(() => vl.Discover(null), ct).ConfigureAwait(true);
        }
        return found?.Version != null ? SetupStepResult.Ok($"Volatility 3 {found.Version} installed ({vl.ManagedVenv}).")
            : SetupStepResult.Fail("pip finished but Volatility 3 does not answer. See the log, then click Retry.");
    }

    private async Task<SetupStepResult> InstallVolatilitySymbolsAsync(SetupStepContext c, CancellationToken ct)
    {
        var vl = new VolatilityLocator();
        if (VolatilitySetup.SymbolPackInstalled(vl.SymbolsDir))
            return SetupStepResult.AlreadyDone($"The Windows symbol pack is already in {vl.SymbolsDir}.");
        var dir = Maint("volatility");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "windows.zip");
        c.Log($"Downloading {VolatilitySetup.SymbolPackUrl} (≈{VolatilitySetup.SymbolPackDownloadMB} MB)…");
        try
        {
            using (var http = _env.CreateHttp())
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                await VolatilitySetup.DownloadFileAsync(http, VolatilitySetup.SymbolPackUrl, zip, new Progress<double>(p => c.Report(p * 0.8)), c.Log, ct).ConfigureAwait(true);
            }
            c.Log($"Extracting into {vl.SymbolsDir}…");
            int n = await Task.Run(() => VolatilitySetup.ExtractSymbolPack(zip, vl.SymbolsDir, new Progress<double>(p => c.Report(0.8 + p * 0.2)), ct), ct).ConfigureAwait(true);
            return SetupStepResult.Ok($"Windows symbol pack installed: {n:N0} files in {vl.SymbolsDir}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return SetupStepResult.Fail($"{ex.Message} Check the internet connection and proxy and free disk space, then click Retry.");
        }
        finally { PrivatePython.TryDelete(zip); }
    }

    // ------------------------------------------------------------------ WSL / Zeek

    /// <summary>
    /// Enables WSL with ONE elevated helper process (<see cref="ElevatedSetupHelper"/>): no prompt at all when WSL already works or
    /// EZ Forensics already runs elevated. Never restarts Windows: when a restart is needed the step waits for it and the wizard asks
    /// once at the end of the run. A declined prompt fails only this step; the distro, Zeek and RITA are then skipped, the rest installs.
    /// </summary>
    private async Task<SetupStepResult> EnableWslAsync(SetupStepContext c, CancellationToken ct)
    {
        var wsl = WslRuntime.FindWslExe(_env.Probe);
        if (wsl != null && await Task.Run(() => WslRuntime.IsEnabled(_env.Probe, wsl), ct).ConfigureAwait(true))
            return SetupStepResult.AlreadyDone("WSL is already enabled: no administrator permission needed.");

        ElevatedRunResult? result;
        using var hb = Heartbeat(c, WslHeartbeat);
        if (_env.IsElevated || _env.ElevatedHelperExe != null)
        {
            var (r, failure) = await RunElevatedTaskAsync(c, ElevatedSetupHelper.EnableWslTask, DeclinedMessage, ct).ConfigureAwait(true);
            if (failure != null) return failure;
            result = r!;
        }
        else
        {
            // No helper exe (another host): run wsl.exe itself elevated.
            var exe = wsl ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            c.Log($"Starting '{exe} --install --no-distribution' as Administrator (UAC prompt)…");
            int code;
            try { code = await _env.Elevation.RunElevatedAsync(exe, new[] { "--install", "--no-distribution" }, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SetupStepResult.Fail(DeclinedMessage); }
            c.Log($"wsl --install exited with code {code}.");
            result = new ElevatedRunResult
            {
                Ok = code is 0 or ElevatedSetupHelper.RebootRequiredExit, RestartRequired = code == ElevatedSetupHelper.RebootRequiredExit,
                Message = code is 0 or ElevatedSetupHelper.RebootRequiredExit ? "WSL was enabled." : $"wsl --install failed (exit code {code}). Virtualization may be off in the BIOS/UEFI; see docs\\SETUP.md.",
                Commands = { new ElevatedCommandLog { Command = "wsl.exe --install --no-distribution", ExitCode = code } },
            };
        }

        wsl = WslRuntime.FindWslExe(_env.Probe);
        bool usable = result.Ok && wsl != null && await Task.Run(() => WslRuntime.IsEnabled(_env.Probe, wsl), ct).ConfigureAwait(true);
        bool pending = result.Ok && _env.RebootPending();
        c.Log($"After enabling: wsl --status {(usable ? "works" : "does not work yet")}; Windows restart pending: {(pending ? "yes" : "no")}.");
        return ElevatedSetupHelper.Interpret(result, usable, pending);
    }

    /// <summary>
    /// Runs one <see cref="ElevatedSetupHelper"/> task: in-process when EZ Forensics already runs elevated, otherwise through the
    /// elevated helper exe (one UAC prompt). Returns the helper's result, or the step failure (declined prompt, no result).
    /// </summary>
    private async Task<(ElevatedRunResult? Result, SetupStepResult? Failure)> RunElevatedTaskAsync(SetupStepContext c, string task, string declinedMessage, CancellationToken ct)
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (_env.IsElevated)
        {
            c.Log("EZ Forensics runs as Administrator: no UAC prompt needed.");
            var r = await Task.Run(() => task == ElevatedSetupHelper.EnableWsl1Task
                ? ElevatedSetupHelper.EnableWsl1(ElevatedSetupHelper.RunHidden, Path.Combine(sys, "dism.exe"), c.Log)
                : ElevatedSetupHelper.EnableWsl(ElevatedSetupHelper.RunHidden, WslRuntime.FindWslExe(_env.Probe) ?? Path.Combine(sys, "wsl.exe"),
                    Path.Combine(sys, "dism.exe"), c.Log), ct).ConfigureAwait(true);
            return (r, null);
        }
        if (_env.ElevatedHelperExe is not { } helper) return (null, SetupStepResult.Fail("Administrator rights are needed, but the elevated helper is not available."));
        var dir = Maint("elevated");
        Directory.CreateDirectory(dir);
        var resultPath = Path.Combine(dir, task + ".result.json");
        var logPath = Path.Combine(dir, task + ".log");
        TryDeleteFile(resultPath); // never read a result or log lines of an earlier run
        TryDeleteFile(logPath);
        var args = _env.ElevatedHelperPrefixArgs.Concat(ElevatedSetupHelper.BuildArgs(new[] { task }, resultPath, logPath)).ToList();
        c.Log($"Asking Windows for administrator permission (UAC) once: {helper} {string.Join(" ", args)}");
        int code, forwarded;
        using (var stopTail = new CancellationTokenSource())
        {
            // The helper's log lines appear live (it writes them to the --log file, flushed per line).
            var tail = LogFileTailer.TailAsync(logPath, line => c.Log("  " + line), stopTail.Token);
            try { code = await _env.Elevation.RunElevatedAsync(helper, args, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                stopTail.Cancel();
                await tail.ConfigureAwait(true);
                c.Log("The administrator prompt was declined.");
                return (null, SetupStepResult.Fail(declinedMessage));
            }
            finally { stopTail.Cancel(); }
            forwarded = await tail.ConfigureAwait(true);
        }
        c.Log($"Elevated helper exited with code {code}.");
        var result = ElevatedSetupHelper.ReadResult(resultPath);
        if (result == null) return (null, SetupStepResult.Fail($"The elevated helper did not report a result (exit code {code}). See the log, then click Retry."));
        if (forwarded == 0) foreach (var cmd in result.Commands) c.Log($"  {cmd.Command} -> exit code {cmd.ExitCode}");
        return (result, null);
    }

    public const string Wsl1DeclinedMessage = "WSL 2 cannot run on this computer and WSL 1 needs the Windows Subsystem for Linux component, which needs " +
                                              "administrator permission. The prompt was declined: click Retry and choose Yes.";

    public const string DeclinedMessage ="The administrator prompt was declined, so WSL was not enabled. The EZ Forensics Linux environment, Zeek and RITA " +
                                          "are skipped; everything else still installs. Click Retry and choose Yes to add them.";

    private async Task<SetupStepResult> InstallEzDistroAsync(SetupStepContext c, CancellationToken ct)
    {
        using var http = _env.CreateHttp();
        http.Timeout = Timeout.InfiniteTimeSpan;
        var dir = Maint("wsl");
        var (ok, message) = await EzDistro.EnsureAsync(http, async (step, t) => await RunJobAsync(c, step.ToJobSpec(dir), t, DistroHeartbeat).ConfigureAwait(true),
            c.Log, new Progress<double>(p => c.Report(p)), _env.Probe, ct).ConfigureAwait(true);
        if (ok)
        {
            // Remember WSL 1 / 2 (WSL 1 = the import fell back because WSL 2 could not run); shown in the distro summaries.
            var info = await Task.Run(() => EzDistro.Inspect(_env.Probe, checkProvisioned: false), ct).ConfigureAwait(true);
            if (info.WslVersion is 1 or 2 && _env.Settings.EzDistroWslVersion != info.WslVersion)
            {
                _env.Settings.EzDistroWslVersion = info.WslVersion;
                _env.SaveSettings();
            }
            c.Log($"{EzDistro.Name}: WSL {(info.WslVersion is 1 or 2 ? info.WslVersion.ToString() : "version unknown")}.");
            return SetupStepResult.Ok(info.WslVersion == 1 ? message + " (WSL 1)" : message);
        }
        if (EzDistro.NeedsWsl1Component(message))
        {
            // WSL 2 cannot run here and WSL 1 is missing its Windows component (an earlier 'wsl --install' enabled only
            // VirtualMachinePlatform): enable it once (UAC), then this step continues by itself after the restart.
            c.Log("WSL 2 cannot run on this computer and WSL 1 needs the 'Windows Subsystem for Linux' component: enabling it (administrator permission).");
            ElevatedRunResult? r;
            SetupStepResult? failure;
            using (Heartbeat(c, Wsl1Heartbeat))
                (r, failure) = await RunElevatedTaskAsync(c, ElevatedSetupHelper.EnableWsl1Task, Wsl1DeclinedMessage, ct).ConfigureAwait(true);
            if (failure != null) return failure;
            if (!r!.Ok) return SetupStepResult.Fail(r.Message + " " + EzDistro.NoVirtualizationHint);
            return SetupStepResult.RestartRequired("WSL 2 cannot run on this computer (" + EzDistro.NoVirtualizationReason + "), so the Linux environment uses WSL 1. " +
                                                   "Its Windows component was just enabled: restart Windows once and setup continues by itself after you sign in again.");
        }
        return message.StartsWith("WSL is not", StringComparison.Ordinal) ? SetupStepResult.NeedsAction(message) : SetupStepResult.Fail(message);
    }

    private async Task<SetupStepResult> InstallRitaAsync(SetupStepContext c, CancellationToken ct)
    {
        using var http = _env.CreateHttp();
        http.Timeout = Timeout.InfiniteTimeSpan;
        var dir = Maint("rita");
        if (_env.Settings.EzDistroWslVersion == 1)
            c.Log($"Note: the {EzDistro.Name} distro runs on WSL 1 (WSL 2 was not available). RITA's ClickHouse database is only tested on WSL 2.");
        var (ok, message) = await RitaSetup.InstallAsync(http, async (step, t) => await RunJobAsync(c, step.ToJobSpec(dir), t).ConfigureAwait(true),
            c.Log, null, new Progress<double>(p => c.Report(p)), _env.Probe, ct).ConfigureAwait(true);
        if (ok) return SetupStepResult.Ok(message);
        return message.Contains("distro is not set up", StringComparison.Ordinal) || message.StartsWith("WSL is not", StringComparison.Ordinal)
            ? SetupStepResult.NeedsAction(message) : SetupStepResult.Fail(message);
    }

    private async Task<SetupStepResult> InstallZeekWslAsync(SetupStepContext c, CancellationToken ct)
    {
        var wsl = WslRuntime.FindWslExe(_env.Probe);
        if (wsl == null) return SetupStepResult.Fail("wsl.exe was not found.");
        var distro = c.Step.Args.TryGetValue("distro", out var d) && !string.IsNullOrEmpty(d) ? d : EzDistro.Name;
        if (distro.Equals(EzDistro.Name, StringComparison.OrdinalIgnoreCase) && !(await Task.Run(() => EzDistro.Inspect(_env.Probe), ct).ConfigureAwait(true)).Ready)
            return SetupStepResult.NeedsAction($"The EZ Forensics Linux environment ({EzDistro.Name}) is not ready yet: retry its step first, then click Retry.");
        var job = await RunJobAsync(c, ZeekSetup.WslInstallStep(wsl, distro).ToJobSpec(Maint("zeek")), ct, ZeekAptHeartbeat).ConfigureAwait(true);
        if (job.State != JobState.Succeeded) return FromJob(job, "", "See the log, then click Retry (apt continues where it stopped).");
        var state = ZeekSetupState.Load();
        if (!state.WslDistros.Contains(distro, StringComparer.OrdinalIgnoreCase)) state.WslDistros.Add(distro);
        state.Save();
        return SetupStepResult.Ok(distro.Equals(EzDistro.Name, StringComparison.OrdinalIgnoreCase)
            ? "Zeek installed in the EZ Forensics Linux environment (official packages)." : $"Zeek installed in WSL {distro} (official packages).");
    }
}

/// <summary>
/// "Still working" lines for a quiet step: when the step logged nothing for the interval, it logs
/// "&lt;text&gt; (elapsed m:ss)." so a long wsl --install, pip or apt never looks stuck. Any log line (including its own) restarts the
/// wait. Dispose it when the quiet part is over.
/// </summary>
public sealed class StepHeartbeat : IDisposable
{
    private readonly SetupStep _step;
    private readonly string _text;
    private readonly TimeSpan _interval;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Timer _timer;
    private long _lastTicks;
    private int _disposed;

    public StepHeartbeat(SetupStep step, string text, TimeSpan interval)
    {
        _step = step;
        _text = text;
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(20);
        step.LogLine += OnLine;
        var check = TimeSpan.FromTicks(Math.Clamp(_interval.Ticks / 4, TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromSeconds(1).Ticks));
        _timer = new Timer(_ => Check(), null, check, check);
    }

    /// <summary>"Still enabling WSL… (elapsed 2:40)."</summary>
    public static string Format(string text, TimeSpan elapsed) => $"{text} (elapsed {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}).";

    private void OnLine(SetupStep _, string __) => Interlocked.Exchange(ref _lastTicks, _sw.Elapsed.Ticks);

    private void Check()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var now = _sw.Elapsed;
        if (now - TimeSpan.FromTicks(Interlocked.Read(ref _lastTicks)) < _interval) return;
        Interlocked.Exchange(ref _lastTicks, now.Ticks); // the line's own LogLine may arrive later (posted to the UI thread)
        _step.Log(Format(_text, now));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        _step.LogLine -= OnLine;
    }
}