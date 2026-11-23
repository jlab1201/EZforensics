using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Rita;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Zeek;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Tools card "RITA (beacons, long connections, DNS tunneling)": status / versions / data size, Install or Update (ensures
/// the EZ Forensics distro first), Uninstall, Delete RITA databases, the internal-subnets editor, the online threat intel switch and
/// the GPL-3.0 notice. Every step runs as a job with a log.
/// </summary>
public sealed partial class RitaCardViewModel : ObservableObject
{
    private readonly Action<string, Exception> _onError;
    private readonly Action<string, InfoSeverity> _onMessage;
    private CancellationTokenSource? _cts;
    private bool _loadingSubnets;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private LogBuffer? _log;
    [ObservableProperty] private Job? _job;
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private double _progress;
    /// <summary>Internal subnets being edited (one CIDR per line; commas / spaces also separate).</summary>
    [ObservableProperty] private string _subnetsText = "";

    public RitaCardViewModel(Action<string, Exception> onError, Action<string, InfoSeverity> onMessage)
    {
        _onError = onError;
        _onMessage = onMessage;
        S.RitaChanged += Raise;
        LoadSubnets();
    }

    private static AppServices S => AppServices.Current;

    public void OnNavigatedTo()
    {
        S.EnsureRitaProbed();
        Raise();
    }

    // ------------------------------------------------------------------ state

    public RitaDiscovery? Discovery => S.Rita;
    public bool Installed => S.Rita?.Installed == true;
    private bool Detecting => S.Rita == null || (S.IsProbingRita && !Installed);

    /// <summary>Chip state, same values as the Zeek card's triggers: Detecting, Installed, UpdateAvailable, NotInstalled, NeedsRuntime.</summary>
    public string Status => Detecting ? "Detecting"
        : S.Rita!.UpdateAvailable ? "UpdateAvailable"
        : Installed ? "Installed"
        : S.Rita.Setup == RitaSetupOption.NeedsWsl ? "NeedsRuntime" : "NotInstalled";

    public string StatusLabel => Status switch
    {
        "Detecting" => "Checking…",
        "Installed" => "Installed",
        "UpdateAvailable" => "Update available",
        "NeedsRuntime" => "Needs WSL",
        _ => "Not installed",
    };

    public string Headline => Status switch
    {
        "Detecting" => "Looking for RITA in the EZ Forensics Linux environment…",
        "Installed" => $"Ready: RITA {S.Rita!.RitaVersion} runs in the EZ Forensics Linux environment. Ingest plans one analysis per host after its Zeek jobs.",
        "UpdateAvailable" => $"Installed ({S.Rita!.RitaVersion}); an update to RITA {RitaRelease.Version} / ClickHouse {RitaRelease.ClickHouseVersion} is available. Click Update.",
        "NeedsRuntime" => "RITA needs WSL (it runs in EZ Forensics' own Linux environment). Enable WSL with the setup (Network bundle; administrator prompt and a restart, once), then click Detect again.",
        _ => S.Rita!.Setup == RitaSetupOption.InstallDistroThenRita
            ? $"Not installed. Click Install: EZ Forensics sets up its own Linux environment in WSL ({EzDistro.Name}, no admin rights) and installs RITA + ClickHouse in it (≈{RitaRelease.DownloadMB + EzDistro.DownloadMB} MB)."
            : $"Not installed. Click Install to add RITA {RitaRelease.Version} + ClickHouse {RitaRelease.ClickHouseVersion} to the EZ Forensics Linux environment (≈{RitaRelease.DownloadMB} MB download, about a minute).",
    };

    public string VersionText => Installed ? $"RITA {S.Rita!.RitaVersion ?? "?"}   ·   ClickHouse {S.Rita.ClickHouseVersion ?? "?"}" : "-";
    public string PinnedText => $"RITA {RitaRelease.Version} (ghcr.io/activecm/rita, pinned by digest, SHA-256 verified)   ·   ClickHouse {RitaRelease.ClickHouseVersion} (static build, SHA-512 verified)";
    public string EngineText => S.Rita == null ? "-"
        : $"EZ Forensics Linux environment (WSL distro {EzDistro.Name}): {S.Rita.Distro.Summary}";
    public string LocationText => $"{RitaSetup.Root} (program)   ·   {RitaSetup.DataDir} (databases)";
    public string DataText => S.Rita?.DataBytes is { } b ? (b < 1L << 20 ? "under 1 MB" : EzDistro.FormatBytes(b)) + " of RITA databases (kept between runs; re-runs rebuild their own database)"
        : Installed ? "No databases yet" : "-";
    public string NoteText => S.Rita?.Note ?? "";
    public bool HasNote => !string.IsNullOrEmpty(NoteText) && !Installed;

    public bool ShowInstall => !Installed && !Detecting && S.Rita!.Setup is RitaSetupOption.Install or RitaSetupOption.InstallDistroThenRita;
    public string InstallButtonText => S.Rita?.Setup == RitaSetupOption.InstallDistroThenRita
        ? $"Install  (≈{RitaRelease.DownloadMB + EzDistro.DownloadMB} MB)" : $"Install  (≈{RitaRelease.DownloadMB} MB)";
    public bool ShowUpdate => Installed;
    public string UpdateButtonText => S.Rita?.UpdateAvailable == true ? $"Update to {RitaRelease.Version}" : "Reinstall";
    public bool ShowUninstall => Installed || S.Rita?.Marker != null;
    public bool ShowDeleteDatabases => Installed;
    public bool ShowDockerNote => S.Rita?.Setup == RitaSetupOption.NeedsWsl || S.Zeek?.Selected?.Engine == ZeekEngine.Docker;
    public string DockerNote => RitaSetup.DockerNotSupportedMessage;

    /// <summary>Settings → RitaEnabled (same switch as the Ingest network card).</summary>
    public bool RitaEnabled
    {
        get => S.Settings.RitaEnabled;
        set
        {
            if (S.Settings.RitaEnabled == value) return;
            S.Settings.RitaEnabled = value;
            S.SaveSettings();
            S.RaiseRitaChanged();
        }
    }

    /// <summary>Settings → RitaOnlineThreatIntel (off by default: it downloads abuse.ch's Feodo Tracker list at every analysis).</summary>
    public bool OnlineThreatIntel
    {
        get => S.Settings.RitaOnlineThreatIntel;
        set
        {
            if (S.Settings.RitaOnlineThreatIntel == value) return;
            S.Settings.RitaOnlineThreatIntel = value;
            S.SaveSettings();
            S.RaiseRitaChanged();
        }
    }

    public string ThreatIntelText => "When on, each analysis downloads the abuse.ch Feodo Tracker IP block list (" + string.Join(", ", RitaOptions.DefaultOnlineFeeds)
        + ") from inside the EZ Forensics Linux environment and flags matching destinations. Off by default: analyses stay offline and nothing about the case leaves this computer.";

    public string LicenseNotice => RitaRelease.LicenseNotice;

    // ------------------------------------------------------------------ internal subnets editor

    private static IEnumerable<string> Tokens(string? text) =>
        (text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Entries of <see cref="SubnetsText"/> that are not a CIDR or an address.</summary>
    public IReadOnlyList<string> InvalidSubnets => Tokens(SubnetsText).Where(t => RitaOptions.NormalizeCidr(t) == null).ToList();
    public IReadOnlyList<string> ParsedSubnets => RitaOptions.NormalizeSubnets(new[] { SubnetsText });
    public bool SubnetsValid => InvalidSubnets.Count == 0 && ParsedSubnets.Count > 0;
    private IReadOnlyList<string> SavedSubnets => RitaOptions.FromSettings(S.Settings).InternalSubnets;
    public bool SubnetsChanged => !ParsedSubnets.SequenceEqual(SavedSubnets, StringComparer.OrdinalIgnoreCase) || InvalidSubnets.Count > 0;
    public string SubnetsValidation => InvalidSubnets.Count > 0 ? "Not a subnet: " + string.Join(", ", InvalidSubnets) + " (use CIDR notation, e.g. 192.168.1.0/24, or a single address)."
        : ParsedSubnets.Count == 0 ? "Enter at least one internal subnet."
        : SubnetsChanged ? $"{ParsedSubnets.Count} subnet(s); not saved yet." : $"In use: {string.Join(", ", SavedSubnets)}";
    public bool SubnetsHasError => !SubnetsValid;

    partial void OnSubnetsTextChanged(string value)
    {
        if (_loadingSubnets) return;
        RaiseSubnets();
    }

    private void RaiseSubnets()
    {
        foreach (var n in new[] { nameof(InvalidSubnets), nameof(ParsedSubnets), nameof(SubnetsValid), nameof(SubnetsChanged), nameof(SubnetsValidation), nameof(SubnetsHasError) })
            OnPropertyChanged(n);
        SaveSubnetsCommand.NotifyCanExecuteChanged();
    }

    private void LoadSubnets()
    {
        _loadingSubnets = true;
        SubnetsText = string.Join(Environment.NewLine, SavedSubnets);
        _loadingSubnets = false;
        RaiseSubnets();
    }

    private bool CanSaveSubnets() => SubnetsValid && SubnetsChanged;

    [RelayCommand(CanExecute = nameof(CanSaveSubnets))]
    private void SaveSubnets()
    {
        if (!SubnetsValid) return;
        S.Settings.RitaInternalSubnets = ParsedSubnets.ToList();
        S.SaveSettings();
        LoadSubnets();
        S.RaiseRitaChanged();
        _onMessage($"RITA internal subnets saved: {string.Join(", ", SavedSubnets)}. The next analysis uses them (no reinstall needed).", InfoSeverity.Success);
    }

    [RelayCommand]
    private void ResetSubnets()
    {
        S.Settings.RitaInternalSubnets = RitaOptions.DefaultInternalSubnets.ToList();
        S.SaveSettings();
        LoadSubnets();
        S.RaiseRitaChanged();
        _onMessage("RITA internal subnets reset to the private ranges (RFC 1918 + fd00::/8).", InfoSeverity.Info);
    }

    private void Raise()
    {
        foreach (var n in new[] { nameof(Discovery), nameof(Installed), nameof(Status), nameof(StatusLabel), nameof(Headline), nameof(VersionText), nameof(EngineText),
                     nameof(DataText), nameof(NoteText), nameof(HasNote), nameof(ShowInstall), nameof(InstallButtonText), nameof(ShowUpdate), nameof(UpdateButtonText),
                     nameof(ShowUninstall), nameof(ShowDeleteDatabases), nameof(ShowDockerNote), nameof(RitaEnabled), nameof(OnlineThreatIntel) })
            OnPropertyChanged(n);
        RaiseSubnets();
        _command?.Refresh();
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        DeleteDatabasesCommand.NotifyCanExecuteChanged();
        DetectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => Raise();

    // ------------------------------------------------------------------ commands

    private bool CanRun() => !IsBusy;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DetectAsync()
    {
        try
        {
            var d = await S.RefreshRitaAsync();
            _onMessage(d.Installed ? $"Found RITA {d.RitaVersion} (ClickHouse {d.ClickHouseVersion}) in {EzDistro.Name}." : Headline,
                d.Installed ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (Exception ex) { _onError("RITA detection failed", ex); }
    }

    private static string MaintenanceDir() => S.Maintenance.NewLogDir("rita");

    private void Begin(string firstLine)
    {
        IsBusy = true;
        Progress = 0;
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

    private static bool RitaJobsRunning() => S.HasActiveJob("rita");

    /// <summary>Install / Update / Reinstall: the EZ Forensics distro first (when missing), then the pinned RITA + ClickHouse.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        if (IsBusy) return;
        var d = S.Rita ?? await S.RefreshRitaAsync();
        if (d.Setup == RitaSetupOption.NeedsWsl) { _onMessage(Headline, InfoSeverity.Warning); return; }
        if (RitaJobsRunning()) { _onMessage("A RITA analysis is still running. Wait for it or cancel it in the Jobs list on Ingest, then try again.", InfoSeverity.Warning); return; }
        Begin(d.Installed ? "> Update RITA" : "> Install RITA");
        var ct = _cts!.Token;
        try
        {
            using var http = S.CreateHttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            var log = Log!;
            bool distro = !d.Distro.Ready;
            if (distro)
            {
                StepText = $"Step 1 of 2: EZ Forensics Linux environment ({EzDistro.Name})";
                var (ok, message) = await EzDistro.EnsureAsync(http, RunStepAsync, log.Enqueue, ct: ct);
                log.Enqueue(message);
                if (ct.IsCancellationRequested) { _onMessage("Cancelled. Click Install to resume.", InfoSeverity.Warning); return; }
                if (!ok) { _onMessage(message + " See the log below.", InfoSeverity.Error); return; }
            }
            StepText = (distro ? "Step 2 of 2: " : "") + $"RITA {RitaRelease.Version} + ClickHouse {RitaRelease.ClickHouseVersion} (download, verify, install)";
            var progress = new Progress<double>(p => Progress = p);
            var (done, msg) = await RitaSetup.InstallAsync(http, RunStepAsync, log.Enqueue, RitaOptions.FromSettings(S.Settings), progress, ct: ct);
            log.Enqueue(msg);
            var after = await S.RefreshRitaAsync();
            if (ct.IsCancellationRequested) { _onMessage("Install cancelled. Click Install to start again.", InfoSeverity.Warning); return; }
            _onMessage(done ? msg + " Ingest now plans RITA after the Zeek jobs." : msg, done && after.Installed ? InfoSeverity.Success : InfoSeverity.Error);
        }
        catch (OperationCanceledException) { _onMessage("Install cancelled. Click Install to start again.", InfoSeverity.Warning); }
        catch (Exception ex)
        {
            Log?.Enqueue("ERROR: " + ex.Message);
            _onError("Install RITA failed (click Install to retry)", ex);
        }
        finally { End(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallAsync()
    {
        if (IsBusy) return;
        if (RitaJobsRunning()) { _onMessage("A RITA analysis is still running. Wait for it or cancel it in the Jobs list on Ingest, then try again.", InfoSeverity.Warning); return; }
        if (!EzForensics.App.Services.Dialogs.Confirm("Uninstall RITA?", "Removes RITA, ClickHouse and every RITA database. Case results are kept.", "Uninstall",
                details: $"{RitaSetup.Root}\n{RitaSetup.DataDir}", defaultNo: true)) return;
        Begin("> Uninstall RITA");
        try
        {
            StepText = "Removing RITA, ClickHouse and the RITA databases";
            var (ok, message) = await RitaSetup.UninstallAsync(RunStepAsync, ct: _cts!.Token);
            Log?.Enqueue(message);
            await S.RefreshRitaAsync();
            _onMessage(message, ok ? InfoSeverity.Success : InfoSeverity.Error);
        }
        catch (Exception ex) { _onError("Uninstall RITA failed", ex); }
        finally { End(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeleteDatabasesAsync()
    {
        if (IsBusy) return;
        if (RitaJobsRunning()) { _onMessage("A RITA analysis is still running. Wait for it or cancel it in the Jobs list on Ingest, then try again.", InfoSeverity.Warning); return; }
        var size = S.Rita?.DataBytes is { } b ? $" ({(b < 1L << 20 ? "under 1 MB" : EzDistro.FormatBytes(b))})" : "";
        if (!EzForensics.App.Services.Dialogs.Confirm("Delete RITA databases?", $"Imported data only{size}. Case results stay; a later analysis re-imports.", "Delete", severity: EzForensics.App.Views.EzDialogSeverity.Warning, defaultNo: true)) return;
        Begin("> Delete RITA databases");
        try
        {
            StepText = "Deleting the RITA databases";
            var (ok, message) = await RitaSetup.DeleteDatabasesAsync(RunStepAsync, ct: _cts!.Token);
            Log?.Enqueue(message);
            await S.RefreshRitaAsync();
            _onMessage(message, ok ? InfoSeverity.Success : InfoSeverity.Error);
        }
        catch (Exception ex) { _onError("Deleting the RITA databases failed", ex); }
        finally { End(); }
    }

    private Task<Job> RunStepAsync(RuntimeStep step, CancellationToken ct) =>
        ZeekSetup.RunStepAsync(S.Maintenance.Queue, new ZeekSetupStep(step.ToolId, step.Exe, step.Args, step.Label, step.Timeout, step.Environment), MaintenanceDir(), job =>
        {
            AppServices.OnUi(() =>
            {
                Job = job;
                Log?.Enqueue($"> {step.Display}");
                job.Output += (_, line) => AppLog.Info($"[{step.Display}] {line}");
            });
        }, ct);
}
