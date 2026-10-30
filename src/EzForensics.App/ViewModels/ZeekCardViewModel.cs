using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Zeek;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Tools card "Zeek (packet capture → logs)": status, engine, versions, one-click Install / Update / Uninstall through
/// an existing runtime (Docker Desktop or WSL), Detect again, Use existing install…, Open folder, links, log and progress.
/// </summary>
public sealed partial class ZeekCardViewModel : ObservableObject
{
    private readonly Action<string, Exception> _onError;
    private readonly Action<string, InfoSeverity> _onMessage;
    private CancellationTokenSource? _cts;
    private ZeekRelease? _latest;
    private bool _checkingLatest;
    private EzDistroInfo? _ezDistro;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private LogBuffer? _log;
    [ObservableProperty] private Job? _job;
    [ObservableProperty] private string _stepText = "";

    public ZeekCardViewModel(Action<string, Exception> onError, Action<string, InfoSeverity> onMessage)
    {
        _onError = onError;
        _onMessage = onMessage;
        S.ZeekChanged += Raise;
        S.EnsureZeekProbed();
        _ = CheckLatestAsync();
        _ = RefreshEzDistroAsync();
    }

    private static AppServices S => AppServices.Current;

    public void OnNavigatedTo()
    {
        Raise();
        if (_latest == null && !_checkingLatest) _ = CheckLatestAsync();
        _ = RefreshEzDistroAsync();
    }

    /// <summary>Existence + disk size of the private distro (does not start it).</summary>
    private async Task RefreshEzDistroAsync()
    {
        try { _ezDistro = await Task.Run(() => EzDistro.Inspect(checkProvisioned: false)); }
        catch (Exception ex) { AppLog.Error("EZ Forensics distro check failed", ex); }
        Raise();
    }

    // ------------------------------------------------------------------ state

    public ZeekDiscovery? Discovery => S.Zeek;
    public ZeekInstall? Install => S.Zeek?.Selected;
    public bool Found => Install != null;
    public ZeekSetupOption Setup => S.Zeek?.Setup ?? ZeekSetupOption.Guidance;
    public ZeekToolStatus Status => ZeekToolState.Evaluate(S.IsProbingZeek || S.Zeek == null, Install, _latest, Setup);
    public string StatusLabel => ZeekToolState.Label(Status);
    public bool UpdateAvailable => Status == ZeekToolStatus.UpdateAvailable;
    private ZeekSetupState State => ZeekSetupState.Load();
    public bool IsManaged => Install switch
    {
        { Engine: ZeekEngine.Docker } => State.DockerImagePulled != null,
        { Engine: ZeekEngine.Wsl } i => State.WslDistros.Contains(i.WslDistro ?? "", StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    public string InstallButtonText => Setup switch
    {
        ZeekSetupOption.DockerPull => $"Install  (docker pull, ≈{ZeekSetup.DockerDownloadMB} MB)",
        ZeekSetupOption.StartDockerThenPull => $"Start Docker and install  (≈{ZeekSetup.DockerDownloadMB} MB)",
        ZeekSetupOption.WslApt => $"Install in WSL {S.Zeek?.WslCandidate}  (≈{ZeekSetup.WslDownloadMB} MB)",
        ZeekSetupOption.EzDistroApt => $"Install  (≈{ZeekSetup.WslDownloadMB + (_ezDistro?.Exists == true ? 0 : EzDistro.DownloadMB)} MB)",
        _ => "Install",
    };

    public string InstallToolTip => Setup switch
    {
        ZeekSetupOption.DockerPull => $"Runs 'docker pull {ZeekLocator.DockerImage}' (the official Zeek image from Docker Hub) as a job. No admin rights. Captures are then analysed in a container with no network access.",
        ZeekSetupOption.StartDockerThenPull => $"Starts Docker Desktop (no admin rights), waits for its engine, then runs 'docker pull {ZeekLocator.DockerImage}' as a job.",
        ZeekSetupOption.WslApt => $"Inside the WSL distro {S.Zeek?.WslCandidate} (as its root user, no Windows admin rights): adds the official Zeek package repository (openSUSE Build Service, security:zeek) with its signing key and runs 'apt-get install zeek'.",
        ZeekSetupOption.EzDistroApt => $"Creates EZ Forensics' private WSL distro {EzDistro.Name} when it does not exist yet ({EzDistro.Rootfs.Display}, SHA-256 checked, 'wsl --import', no admin rights; your own distros are not touched), then installs the official Zeek packages (openSUSE Build Service, security:zeek) in it.",
        _ => "",
    };

    /// <summary>One plain-language line: what the state is and what to click.</summary>
    public string Headline => Status switch
    {
        ZeekToolStatus.Detecting => "Looking for Zeek (zeek.exe, Docker, WSL)…",
        ZeekToolStatus.Installed => $"Ready: Zeek {Install!.Version} runs through {EngineShort}. Ingest turns packet captures into Zeek logs automatically.",
        ZeekToolStatus.UpdateAvailable => Install!.Engine == ZeekEngine.Native || (Install.Engine == ZeekEngine.Wsl && !IsManaged)
            ? $"Update available: {Install.Version} → {_latest?.Version}. This Zeek was not installed by EZ Forensics: update it the way it was installed."
            : $"Update available{(Install.Engine == ZeekEngine.Docker ? $" (a newer {ZeekLocator.DockerImage} image)" : $": {Install.Version} → {_latest?.Version}")}. Click Update.",
        ZeekToolStatus.NotInstalled => Setup switch
        {
            ZeekSetupOption.DockerPull => $"Not installed. Docker is running: click Install to download the official Zeek image (≈{ZeekSetup.DockerDownloadMB} MB, about 1-2 min). Nothing else to set up.",
            ZeekSetupOption.StartDockerThenPull => $"Not installed. Docker Desktop is installed but not running: click Start Docker and install (EZ Forensics starts it, then downloads the Zeek image, ≈{ZeekSetup.DockerDownloadMB} MB).",
            ZeekSetupOption.WslApt => $"Not installed. Click Install to add Zeek to your WSL distro {S.Zeek?.WslCandidate} from the official Zeek packages (≈{ZeekSetup.WslDownloadMB} MB, a few minutes).",
            ZeekSetupOption.EzDistroApt => "Not installed. Click Install: EZ Forensics sets up its own Linux environment in WSL (if needed) and installs the official Zeek packages in it (a few minutes, no admin rights).",
            _ => "Not installed.",
        },
        _ => "Zeek needs Docker Desktop or WSL on Windows (the Zeek project publishes no Windows installer). Install one of them (needs admin rights, once), then click Detect again: EZ Forensics does the rest in one click.",
    };

    private string EngineShort => Install?.Engine switch
    {
        ZeekEngine.Docker => $"Docker ({ZeekLocator.DockerImage})",
        ZeekEngine.Wsl => Install.IsEzDistro ? "the EZ Forensics Linux environment (WSL)" : $"WSL ({Install.WslDistro})",
        _ => "zeek.exe",
    };

    public string VersionText => Install?.Version ?? "-";
    public string LatestText => _latest != null && _latest.Error == null
        ? (_latest.Version != null ? $"{_latest.Version}" + (_latest.PublishedUtc is { } p ? $"  (released {p:yyyy-MM-dd}, GitHub)" : "") : "")
          + (_latest.ImageVersion != null ? $"{(_latest.Version != null ? "   ·   " : "")}{ZeekLocator.DockerImage} = {_latest.ImageVersion}" : "")
        : _checkingLatest ? "Checking GitHub and Docker Hub…" : _latest?.Error != null ? $"Unknown ({_latest.Error}; check the proxy in Settings)" : "-";
    public string EngineText => Install == null ? "-" : $"{Install.EngineLabel} ({Install.OriginLabel})";
    public string LocationText => Install == null ? "-"
        : Install.Engine == ZeekEngine.Docker ? $"{Install.DockerImage}" + (Install.Image?.Digest is { } d ? $"  @ {d}" : "")
        : Install.Location;
    public string DockerText
    {
        get
        {
            var d = S.Zeek?.Docker;
            if (d == null) return "-";
            if (d.EngineRunning) return $"Running (engine {d.ServerVersion})" + (S.Zeek!.DockerImage != null ? $", image {ZeekLocator.DockerImage} present" : $", image {ZeekLocator.DockerImage} not pulled");
            if (d.DesktopInstalled) return "Docker Desktop installed, not running";
            return d.CliFound ? "docker CLI found, engine not running" : "Not installed";
        }
    }
    public string WslText
    {
        get
        {
            var z = S.Zeek;
            if (z == null) return "-";
            if (z.WslExe == null) return Found && Install!.Engine != ZeekEngine.Wsl ? "(not checked)" : "Not installed";
            var ez = _ezDistro is { Exists: true } e ? $"EZ Forensics distro: {e.Summary}" : "EZ Forensics distro: not installed";
            if (z.Distros.Count == 0) return Found && Install!.Engine != ZeekEngine.Wsl ? "(not checked)" : "No Linux distribution";
            var others = z.Distros.Where(d => !ZeekLocator.IsEzDistro(d)).Select(d => $"{d.Name} ({d.State}{(d.IsDefault ? ", default" : "")})").ToList();
            return ez + (others.Count > 0 ? "  ·  yours: " + string.Join(", ", others) : "");
        }
    }
    public string ConfiguredPath => string.IsNullOrWhiteSpace(S.Settings.ZeekPath) ? "(automatic)" : S.Settings.ZeekPath!;
    public string Notes => S.Zeek == null ? "" : string.Join(Environment.NewLine, S.Zeek.Notes);
    public bool HasNotes => S.Zeek is { Notes.Count: > 0 } && !Found;
    public bool ShowInstall => !Found && !S.IsProbingZeek && Setup is ZeekSetupOption.DockerPull or ZeekSetupOption.StartDockerThenPull or ZeekSetupOption.WslApt or ZeekSetupOption.EzDistroApt;
    /// <summary>Zeek runs in one of the user's own distros: offer to install it in the private distro instead.</summary>
    public bool ShowMoveToEzDistro => Found && Install!.Engine == ZeekEngine.Wsl && !Install.IsEzDistro;
    public bool ShowRemoveEzDistro => _ezDistro is { Exists: true };
    public string RemoveEzDistroToolTip => $"Delete the {EzDistro.Name} WSL distro (wsl --unregister {EzDistro.Name}){(_ezDistro?.DiskBytes is { } b ? $", {EzDistro.FormatBytes(b)}" : "")}: Zeek, RITA and their data inside it. Your cases and your own WSL distros are not touched.";
    public bool ShowUpdate => Found && Install!.Engine != ZeekEngine.Native;
    public string UpdateButtonText => UpdateAvailable ? "Update" + (_latest?.ImageVersion is { } v && Install?.Engine == ZeekEngine.Docker ? $" (pull {v})" : _latest?.Version is { } lv ? $" to {lv}" : "") : "Update";
    public bool ShowUninstall => !State.IsEmpty;
    public bool ShowGuidance => Status == ZeekToolStatus.NeedsRuntime;
    public string ManagedRoot => ZeekLocator.ManagedRoot;

    private void Raise()
    {
        foreach (var n in new[] { nameof(Discovery), nameof(Install), nameof(Found), nameof(Setup), nameof(Status), nameof(StatusLabel), nameof(UpdateAvailable),
                     nameof(IsManaged), nameof(InstallButtonText), nameof(InstallToolTip), nameof(Headline), nameof(VersionText), nameof(LatestText),
                     nameof(EngineText), nameof(LocationText), nameof(DockerText), nameof(WslText), nameof(ConfiguredPath), nameof(Notes), nameof(HasNotes),
                     nameof(ShowInstall), nameof(ShowMoveToEzDistro), nameof(ShowRemoveEzDistro), nameof(RemoveEzDistroToolTip), nameof(ShowUpdate), nameof(UpdateButtonText), nameof(ShowUninstall), nameof(ShowGuidance) })
            OnPropertyChanged(n);
        InstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        MoveToEzDistroCommand.NotifyCanExecuteChanged();
        RemoveEzDistroCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => Raise();

    /// <summary>Re-checks the latest release (the table's "Check for updates").</summary>
    public Task RefreshLatestAsync() => CheckLatestAsync();

    /// <summary>Latest Zeek on GitHub + the lts image on Docker Hub (proxy from Settings, 15 s timeout).</summary>
    private async Task CheckLatestAsync()
    {
        if (_checkingLatest) return;
        _checkingLatest = true;
        Raise();
        try
        {
            using var http = S.CreateHttpClient();
            _latest = await ZeekReleaseCheck.GetLatestAsync(http);
            if (_latest.Error != null) AppLog.Info("Zeek latest-version check: " + _latest.Error);
        }
        catch (Exception ex)
        {
            _latest = new ZeekRelease(null, null, null, null, "not reachable");
            AppLog.Error("Zeek latest-version check failed", ex);
        }
        finally
        {
            _checkingLatest = false;
            Raise();
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task DetectAsync()
    {
        try
        {
            var latest = CheckLatestAsync();
            var d = await S.RefreshZeekAsync();
            await latest;
            _onMessage(d.Selected != null ? $"Found {d.Selected.Summary}." : Headline,
                d.Selected != null ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (Exception ex) { _onError("Zeek detection failed", ex); }
    }

    [RelayCommand]
    private async Task UseExistingAsync()
    {
        var f = Dialogs.PickFile("Select zeek.exe", "zeek.exe|zeek.exe|All files|*.*", S.Settings.ZeekPath);
        if (f != null) await SetPathAsync(f);
    }

    [RelayCommand]
    private async Task ClearPathAsync() => await SetPathAsync(null);

    private async Task SetPathAsync(string? path)
    {
        try
        {
            S.Settings.ZeekPath = path;
            S.SaveSettings();
            var d = await S.RefreshZeekAsync();
            _onMessage(d.Selected != null ? $"Using {d.Selected.Summary}." : "Zeek did not answer at that location ('zeek --version' failed).",
                d.Selected != null ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (Exception ex) { _onError("Could not set the Zeek path", ex); }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var p = Install is { Engine: ZeekEngine.Native } i ? i.Location : ZeekLocator.ManagedRoot;
            if (p == ZeekLocator.ManagedRoot) Directory.CreateDirectory(p);
            ShellOps.OpenFolder(p);
        }
        catch (Exception ex) { _onError("Could not open the Zeek folder", ex); }
    }

    private bool CanRun() => !IsBusy;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private static string MaintenanceDir() => S.Maintenance.NewLogDir("zeek");

    private void Begin(string firstLine)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Log?.Dispose();
        Log = new LogBuffer();
        Log.Enqueue(firstLine);
    }

    private void End()
    {
        IsBusy = false;
        StepText = "";
        _cts?.Dispose();
        _cts = null;
        Raise();
    }

    /// <summary>One click from zero, through whatever runtime exists (see <see cref="ZeekSetupOption"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        if (IsBusy) return;
        var d = S.Zeek ?? await S.RefreshZeekAsync();
        if (d.Selected != null) { _onMessage($"Zeek is already available: {d.Selected.Summary}.", InfoSeverity.Info); return; }
        Begin("> Install Zeek");
        var ct = _cts!.Token;
        try
        {
            var state = ZeekSetupState.Load();
            switch (d.Setup)
            {
                case ZeekSetupOption.StartDockerThenPull:
                case ZeekSetupOption.DockerPull:
                {
                    var docker = d.Docker;
                    if (d.Setup == ZeekSetupOption.StartDockerThenPull)
                    {
                        StepText = "Step 1 of 2: starting Docker Desktop (this can take a minute)";
                        var log = Log!;
                        docker = await ZeekSetup.StartDockerDesktopAsync(d.Docker.DesktopExe!, log: log.Enqueue, ct: ct);
                        if (!docker.EngineRunning)
                        {
                            _onMessage("Docker Desktop did not finish starting. Open it, complete its first-run steps (sign-in is optional), then click Install again.", InfoSeverity.Warning);
                            return;
                        }
                    }
                    StepText = (d.Setup == ZeekSetupOption.StartDockerThenPull ? "Step 2 of 2: " : "") + $"docker pull {ZeekLocator.DockerImage} (≈{ZeekSetup.DockerDownloadMB} MB)";
                    var job = await RunStepAsync(ZeekSetup.DockerPullStep(docker.Cli!), ct);
                    if (job.State == JobState.Cancelled) { _onMessage("Install cancelled. Click Install to start again.", InfoSeverity.Warning); return; }
                    if (job.State != JobState.Succeeded) { _onMessage($"docker pull failed: {job.StatusReason}. Check the internet connection / Docker Desktop's proxy settings, then click Install to retry. Log below.", InfoSeverity.Error); return; }
                    state.DockerImagePulled = ZeekLocator.DockerImage;
                    state.Save();
                    break;
                }
                case ZeekSetupOption.WslApt:
                {
                    StepText = $"Installing Zeek in WSL {d.WslCandidate} (apt, official packages)";
                    var job = await RunStepAsync(ZeekSetup.WslInstallStep(d.WslExe!, d.WslCandidate!), ct);
                    if (job.State == JobState.Cancelled) { _onMessage("Install cancelled. Click Install to resume (apt continues where it stopped).", InfoSeverity.Warning); return; }
                    if (job.State != JobState.Succeeded) { _onMessage($"Installing Zeek in WSL failed: {job.StatusReason}. See the log below or the Jobs page, then click Install to retry.", InfoSeverity.Error); return; }
                    if (!state.WslDistros.Contains(d.WslCandidate!, StringComparer.OrdinalIgnoreCase)) state.WslDistros.Add(d.WslCandidate!);
                    state.Save();
                    break;
                }
                case ZeekSetupOption.EzDistroApt:
                    if (!await InstallInEzDistroAsync(ct)) return;
                    break;
                default:
                    _onMessage(Headline, InfoSeverity.Warning);
                    return;
            }
            await AutoConfigureAsync("Zeek installed");
        }
        catch (OperationCanceledException) { _onMessage("Install cancelled. Click Install to start again.", InfoSeverity.Warning); }
        catch (Exception ex)
        {
            Log?.Enqueue("ERROR: " + ex.Message);
            _onError("Install Zeek failed (click Install to retry)", ex);
        }
        finally { End(); }
    }

    /// <summary>Creates the private distro when needed, then installs Zeek in it and records it in the setup state. False (message shown) on failure.</summary>
    private async Task<bool> InstallInEzDistroAsync(CancellationToken ct)
    {
        StepText = $"Step 1 of 2: EZ Forensics Linux environment ({EzDistro.Name})";
        using (var http = S.CreateHttpClient())
        {
            http.Timeout = Timeout.InfiniteTimeSpan;
            var log = Log!;
            var (ok, message) = await EzDistro.EnsureAsync(http, (step, t) => RunStepAsync(ToZeekStep(step), t), log.Enqueue, ct: ct);
            log.Enqueue(message);
            await RefreshEzDistroAsync();
            if (ct.IsCancellationRequested) { _onMessage("Cancelled. Click again to resume.", InfoSeverity.Warning); return false; }
            if (!ok) { _onMessage(message + " See the log below.", InfoSeverity.Error); return false; }
        }
        var wsl = WslRuntime.FindWslExe(SystemRuntimeProbe.Instance)!;
        StepText = $"Step 2 of 2: installing Zeek in {EzDistro.Name} (apt, official packages)";
        var job = await RunStepAsync(ZeekSetup.WslInstallStep(wsl, EzDistro.Name), ct);
        if (job.State == JobState.Cancelled) { _onMessage("Cancelled. Click again to resume (apt continues where it stopped).", InfoSeverity.Warning); return false; }
        if (job.State != JobState.Succeeded) { _onMessage($"Installing Zeek in the EZ Forensics Linux environment failed: {job.StatusReason}. See the log below or the Jobs page, then retry.", InfoSeverity.Error); return false; }
        var state = ZeekSetupState.Load();
        if (!state.WslDistros.Contains(EzDistro.Name, StringComparer.OrdinalIgnoreCase)) state.WslDistros.Add(EzDistro.Name);
        state.Save();
        return true;
    }

    private static ZeekSetupStep ToZeekStep(RuntimeStep r) => new(r.ToolId, r.Exe, r.Args, r.Label, r.Timeout, r.Environment);

    /// <summary>Zeek found in one of the user's distros: install it in the private distro (preferred by discovery from then on).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task MoveToEzDistroAsync()
    {
        var old = Install;
        if (old is not { Engine: ZeekEngine.Wsl } || old.IsEzDistro || IsBusy) return;
        Begin($"> Move Zeek to the EZ Forensics Linux environment (from WSL {old.WslDistro})");
        try
        {
            if (!await InstallInEzDistroAsync(_cts!.Token)) return;
            var state = ZeekSetupState.Load();
            if (state.WslDistros.Contains(old.WslDistro!, StringComparer.OrdinalIgnoreCase))
            {
                var answer = EzForensics.App.Services.Dialogs.Confirm("Remove the old Zeek?", $"Zeek now runs in the EZ Forensics Linux environment. Remove the old copy from WSL {old.WslDistro}?",
                    "Remove", "Keep", details: "apt-get purge zeek and the repository EZ Forensics added; nothing else in that distro changes.", defaultNo: true);
                if (answer)
                {
                    StepText = $"Removing Zeek from WSL {old.WslDistro}";
                    var job = await RunStepAsync(ZeekSetup.WslRemoveStep(old.Exe, old.WslDistro!), _cts!.Token);
                    if (job.State == JobState.Succeeded) { state.WslDistros.Remove(old.WslDistro!); state.Save(); }
                    else Log?.Enqueue($"Removing Zeek from {old.WslDistro} failed: {job.StatusReason} (it keeps working there; remove it later with Uninstall).");
                }
            }
            await AutoConfigureAsync("Zeek moved");
        }
        catch (OperationCanceledException) { _onMessage("Cancelled.", InfoSeverity.Warning); }
        catch (Exception ex) { _onError("Moving Zeek failed", ex); }
        finally { End(); }
    }

    /// <summary>Deletes the private distro (and Zeek / RITA inside it) after a confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RemoveEzDistroAsync()
    {
        if (IsBusy) return;
        if (S.HasActiveJob("zeek", "rita", "ezdistro"))
        {
            _onMessage("A Zeek or RITA job is still running. Wait for it or cancel it on the Jobs page, then try again.", InfoSeverity.Warning);
            return;
        }
        var size = _ezDistro?.DiskBytes is { } b ? $" ({EzDistro.FormatBytes(b)})" : "";
        if (!EzForensics.App.Services.Dialogs.Confirm("Remove the Linux environment?", $"Deletes Zeek, RITA and their data{size}. Your cases and own WSL distros are not touched.", "Remove", severity: EzForensics.App.Views.EzDialogSeverity.Warning,
                details: $"wsl --unregister {EzDistro.Name}", defaultNo: true)) return;
        Begin($"> wsl --unregister {EzDistro.Name}");
        try
        {
            StepText = $"Removing the {EzDistro.Name} WSL distro";
            var (ok, message) = await EzDistro.RemoveAsync((step, t) => RunStepAsync(ToZeekStep(step), t), ct: _cts!.Token);
            Log?.Enqueue(message);
            if (ok)
            {
                var state = ZeekSetupState.Load();
                if (state.WslDistros.RemoveAll(d => d.Equals(EzDistro.Name, StringComparison.OrdinalIgnoreCase)) > 0) state.Save();
            }
            await RefreshEzDistroAsync();
            var after = await S.RefreshZeekAsync();
            _onMessage(ok ? message + (after.Selected != null ? $" Zeek now: {after.Selected.Summary}." : "") : message, ok ? InfoSeverity.Success : InfoSeverity.Error);
        }
        catch (Exception ex) { _onError("Removing the EZ Forensics Linux environment failed", ex); }
        finally { End(); }
    }


    private async Task AutoConfigureAsync(string what)
    {
        var d = await S.RefreshZeekAsync();
        if (d.Selected == null && !string.IsNullOrWhiteSpace(S.Settings.ZeekPath))
        {
            Log?.Enqueue($"Configured Zeek path '{S.Settings.ZeekPath}' does not answer; switching to automatic detection.");
            S.Settings.ZeekPath = null;
            S.SaveSettings();
            d = await S.RefreshZeekAsync();
        }
        if (_latest == null) await CheckLatestAsync();
        if (d.Selected != null)
        {
            Log?.Enqueue($"Detected: {d.Selected.Summary}");
            _onMessage($"{what}: {d.Selected.Summary}. Ingest now plans a Zeek job for every packet capture it finds.", InfoSeverity.Success);
        }
        else _onMessage("Setup finished but Zeek did not answer to 'zeek --version'. See the log, then click Detect again.", InfoSeverity.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateAsync()
    {
        var i = Install;
        if (i == null || IsBusy) return;
        ZeekSetupStep step;
        if (i.Engine == ZeekEngine.Docker) step = ZeekSetup.DockerPullStep(i.Exe, i.DockerImage ?? ZeekLocator.DockerImage);
        else if (i.Engine == ZeekEngine.Wsl && IsManaged) step = ZeekSetup.WslUpdateStep(i.Exe, i.WslDistro!);
        else
        {
            _onMessage($"Zeek at {i.Location} was not installed by EZ Forensics, so EZ Forensics does not update it. Update it the way it was installed.", InfoSeverity.Warning);
            return;
        }
        Begin($"> Update Zeek {i.Version}");
        try
        {
            StepText = step.Label;
            var job = await RunStepAsync(step, _cts!.Token);
            if (job.State == JobState.Cancelled) { _onMessage("Update cancelled.", InfoSeverity.Warning); return; }
            if (job.State != JobState.Succeeded) { _onMessage($"Update failed: {job.StatusReason}. See the log below or the Jobs page.", InfoSeverity.Error); return; }
            var before = i.Image?.Digest ?? i.Version;
            await AutoConfigureAsync("Zeek updated");
            var after = Install?.Image?.Digest ?? Install?.Version;
            if (after == before) _onMessage($"Zeek {i.Version} is already the latest version.", InfoSeverity.Success);
        }
        catch (Exception ex) { _onError("Update Zeek failed", ex); }
        finally { End(); }
    }

    /// <summary>Removes only what EZ Forensics set up: the image it pulled and/or the packages it installed in WSL.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallAsync()
    {
        var state = ZeekSetupState.Load();
        if (state.IsEmpty) { _onMessage("EZ Forensics did not install Zeek, so there is nothing to remove.", InfoSeverity.Info); return; }
        if (S.HasActiveJob("zeek"))
        {
            _onMessage("A Zeek job is still running. Wait for it or cancel it on the Jobs page, then click Uninstall again.", InfoSeverity.Warning);
            return;
        }
        var what = new List<string>();
        if (state.DockerImagePulled != null) what.Add($"docker image rm {state.DockerImagePulled}");
        what.AddRange(state.WslDistros.Select(d => $"WSL {d}: apt-get purge zeek + remove the repository EZ Forensics added"));
        if (!EzForensics.App.Services.Dialogs.Confirm("Uninstall Zeek?", "Removes what EZ Forensics installed for Zeek. Case results, Docker and WSL are not touched.", "Uninstall",
                details: string.Join("\n", what), defaultNo: true)) return;
        Begin("> Uninstall Zeek");
        try
        {
            var d = S.Zeek;
            if (state.DockerImagePulled != null)
            {
                var docker = d?.Docker.Cli ?? DockerRuntime.FindCli(SystemRuntimeProbe.Instance);
                if (docker == null || d?.Docker.EngineRunning != true)
                {
                    _onMessage("Docker is not running, so the Zeek image cannot be removed now. Start Docker Desktop and click Uninstall again.", InfoSeverity.Warning);
                    return;
                }
                StepText = $"docker image rm {state.DockerImagePulled}";
                var job = await RunStepAsync(ZeekSetup.DockerRemoveStep(docker, state.DockerImagePulled), _cts!.Token);
                bool gone = job.State == JobState.Succeeded || DockerRuntime.InspectImage(SystemRuntimeProbe.Instance, docker, state.DockerImagePulled) == null;
                if (!gone) { _onMessage($"Could not remove the image: {job.StatusReason}. See the log.", InfoSeverity.Error); return; }
                state.DockerImagePulled = null;
                state.Save();
            }
            foreach (var distro in state.WslDistros.ToList())
            {
                var wsl = d?.WslExe ?? WslRuntime.FindWslExe(SystemRuntimeProbe.Instance);
                if (wsl == null) break;
                StepText = $"Removing Zeek from WSL {distro}";
                var job = await RunStepAsync(ZeekSetup.WslRemoveStep(wsl, distro), _cts!.Token);
                if (job.State != JobState.Succeeded) { _onMessage($"Removing Zeek from WSL {distro} failed: {job.StatusReason}. See the log.", InfoSeverity.Error); return; }
                state.WslDistros.Remove(distro);
                state.Save();
            }
            var after = await S.RefreshZeekAsync();
            _onMessage("Removed what EZ Forensics installed for Zeek." + (after.Selected != null ? $" Now using {after.Selected.Summary}." : " Click Install to set it up again."), InfoSeverity.Success);
        }
        catch (Exception ex) { _onError("Uninstall Zeek failed", ex); }
        finally { End(); }
    }

    private Task<Job> RunStepAsync(ZeekSetupStep step, CancellationToken ct) =>
        ZeekSetup.RunStepAsync(S.Maintenance.Queue, step, MaintenanceDir(), job =>
        {
            Job = job;
            Log?.Enqueue($"> {step.Display}");
            job.Output += (_, line) => AppLog.Info($"[{step.Display}] {line}");
        }, ct);
}
