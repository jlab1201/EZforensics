using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;

namespace EzForensics.App.ViewModels;

/// <summary>Operating-system folder of the Volatility symbols directory an imported ISF file goes into.</summary>
public sealed record IsfTarget(string Folder, string Label);

/// <summary>
/// Tools card "Volatility 3 (memory images)": status and version, one-click Install / Update (venv on the private Python shared with
/// plaso, pip install of the pinned volatility3), Uninstall (symbols kept unless asked), the optional offline Windows symbol pack,
/// Import ISF… (copies .json / .json.xz into <c>symbols\{windows|linux|mac}</c>) and the offline-mode switch (<c>--offline</c>).
/// </summary>
public sealed partial class VolatilityCardViewModel : ObservableObject
{
    private readonly Action<string, Exception> _onError;
    private readonly Action<string, InfoSeverity> _onMessage;
    private CancellationTokenSource? _cts;
    private PlasoRelease? _latest;
    private bool _checkingLatest;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private LogBuffer? _log;
    [ObservableProperty] private Job? _job;
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProgressIndeterminate))] private bool _progressDeterminate;
    public bool ProgressIndeterminate => !ProgressDeterminate;
    [ObservableProperty] private double _progress;
    /// <summary>Uninstall keeps the symbols folder (the offline pack is large; imported ISF files live there too).</summary>
    [ObservableProperty] private bool _keepSymbolsOnUninstall = true;
    [ObservableProperty] private IsfTarget _isfTarget;

    public VolatilityCardViewModel(Action<string, Exception> onError, Action<string, InfoSeverity> onMessage)
    {
        _onError = onError;
        _onMessage = onMessage;
        _isfTarget = IsfTargets[0];
        S.VolatilityChanged += Raise;
        S.EnsureVolatilityProbed();
    }

    private static AppServices S => AppServices.Current;

    public IReadOnlyList<IsfTarget> IsfTargets { get; } = new[]
    {
        new IsfTarget("windows", "Windows"), new IsfTarget("linux", "Linux"), new IsfTarget("mac", "macOS"),
    };

    public void OnNavigatedTo()
    {
        Raise();
        if (_latest == null && !_checkingLatest) _ = CheckLatestAsync();
    }

    // ------------------------------------------------------------------ state

    private static VolatilityLocator NewLocator() => new();

    public VolatilityInstall? Install => S.Volatility;
    public bool Found => Install != null;
    public bool IsDetecting => S.IsProbingVolatility || !S.VolatilityProbed;
    public bool IsManaged => Install != null && Install.Exe.Equals(NewLocator().ManagedExe, StringComparison.OrdinalIgnoreCase);
    public bool UpdateAvailable => IsManaged && VolatilityReleaseCheck.IsNewer(Install!.Version, VolatilitySetup.PinnedVersion);
    /// <summary>"Detecting", "Installed", "UpdateAvailable" or "NotInstalled" (drives the chip colour).</summary>
    public string Status => IsDetecting && !Found ? "Detecting" : !Found ? "NotInstalled" : UpdateAvailable ? "UpdateAvailable" : "Installed";
    public string StatusLabel => Status switch
    {
        "Detecting" => "Detecting…",
        "NotInstalled" => "Not installed",
        "UpdateAvailable" => "Update available",
        _ => "Installed",
    };

    private static (int DownloadMB, int DiskMB) Estimate => VolatilitySetup.Estimate(needsPython: !File.Exists(NewLocator().ManagedPython) && new PlasoLocator().FindSupportedPython() == null);

    public string Headline => Status switch
    {
        "Detecting" => "Looking for Volatility 3 (vol.exe)…",
        "NotInstalled" => $"Not installed. Click Install (≈{VolatilitySetup.PackagesDownloadMB} MB, plus a private Python if none is found; about 1-3 min). Ingest then analyses the memory images it finds.",
        "UpdateAvailable" => $"Update available: {Install!.Version} → {VolatilitySetup.PinnedVersion} (the version EZ Forensics is tested with). Click Update.",
        _ => $"Ready: Volatility 3 {Install!.Version}. Ingest runs one job per selected plugin for every memory image; CSVs go to <case>\\<host>\\Memory." +
             (S.Settings.VolatilityOffline ? " Offline mode: symbols come only from the symbols folder." : ""),
    };

    public string InstallButtonText => $"Install  (≈{Estimate.DownloadMB} MB)";
    public string VersionText => Install?.Version ?? "-";
    public string PinnedText => VolatilitySetup.PinnedVersion;
    public string LatestText => _latest != null ? $"{_latest.Version}" + (_latest.UploadedUtc is { } p ? $"  (released {p:yyyy-MM-dd}, PyPI)" : "  (PyPI)")
        : _checkingLatest ? "Checking PyPI…" : "-";
    public string LocationText => Install?.Exe ?? "-";
    public string SymbolsDir => Install?.SymbolsDir ?? NewLocator().SymbolsDir;
    public string SymbolsText
    {
        get
        {
            var dir = SymbolsDir;
            var parts = new List<string>();
            if (VolatilitySetup.SymbolPackInstalled(dir)) parts.Add("Windows symbol pack installed");
            foreach (var t in IsfTargets)
            {
                int n = CountFiles(Path.Combine(dir, t.Folder));
                if (n > 0 && !(t.Folder == "windows" && VolatilitySetup.SymbolPackInstalled(dir))) parts.Add($"{n} {t.Label} ISF file{(n == 1 ? "" : "s")}");
            }
            return parts.Count == 0 ? "None yet (Windows symbols are downloaded automatically when online)" : string.Join("  ·  ", parts);
        }
    }
    public bool SymbolPackInstalled => VolatilitySetup.SymbolPackInstalled(SymbolsDir);
    public string ConfiguredPath => string.IsNullOrWhiteSpace(S.Settings.VolatilityPath) ? "(automatic)" : S.Settings.VolatilityPath!;
    public bool ShowInstall => !Found && !IsDetecting;
    public bool ShowUpdate => IsManaged;
    public string UpdateButtonText => UpdateAvailable ? $"Update to {VolatilitySetup.PinnedVersion}" : "Repair / reinstall";
    public bool ShowUninstall => VolatilityLocator.ManagedVenvExists() || Directory.Exists(Path.Combine(VolatilityLocator.ManagedRoot, "cache"));
    public bool ShowSymbolPack => Found && !SymbolPackInstalled;
    public string SymbolPackButtonText => $"Download Windows symbol pack  (≈{VolatilitySetup.SymbolPackDownloadMB} MB)";

    /// <summary>Volatility runs with <c>--offline</c> (symbols only from the local folder). Saved in the settings.</summary>
    public bool Offline
    {
        get => S.Settings.VolatilityOffline;
        set
        {
            if (S.Settings.VolatilityOffline == value) return;
            S.Settings.VolatilityOffline = value;
            try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
            OnPropertyChanged();
            S.RaiseVolatilityChanged();
        }
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(100_000).Count() : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void Raise()
    {
        foreach (var n in new[] { nameof(Install), nameof(Found), nameof(IsDetecting), nameof(IsManaged), nameof(UpdateAvailable), nameof(Status), nameof(StatusLabel),
                     nameof(Headline), nameof(InstallButtonText), nameof(VersionText), nameof(LatestText), nameof(LocationText), nameof(SymbolsDir), nameof(SymbolsText),
                     nameof(SymbolPackInstalled), nameof(ConfiguredPath), nameof(ShowInstall), nameof(ShowUpdate), nameof(UpdateButtonText), nameof(ShowUninstall),
                     nameof(ShowSymbolPack), nameof(Offline) })
            OnPropertyChanged(n);
        _ = RefreshPluginProbeAsync();
        _command?.Refresh();
        InstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        InstallSymbolPackCommand.NotifyCanExecuteChanged();
        ImportIsfCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => Raise();

    /// <summary>Re-checks the latest release (the table's "Check for updates").</summary>
    public Task RefreshLatestAsync() => CheckLatestAsync();

    private async Task CheckLatestAsync()
    {
        if (_checkingLatest) return;
        _checkingLatest = true;
        Raise();
        try
        {
            using var http = S.CreateHttpClient();
            _latest = await VolatilityReleaseCheck.GetLatestAsync(http);
        }
        catch (Exception ex) { AppLog.Info("Volatility latest-version check: " + ex.Message); }
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
            var d = await S.RefreshVolatilityAsync();
            await latest;
            _onMessage(d != null ? $"Found Volatility 3 {d.Version}: {d.Exe}." : "Volatility 3 was not found. Click Install to set it up in one step.",
                d != null ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (Exception ex) { _onError("Volatility detection failed", ex); }
    }

    [RelayCommand]
    private async Task UseExistingAsync()
    {
        var f = Dialogs.PickFile("Select vol.exe (Volatility 3, in the Scripts folder of its Python environment)", "vol.exe|vol*.exe|All files|*.*", S.Settings.VolatilityPath);
        if (f != null) await SetPathAsync(f);
    }

    [RelayCommand]
    private async Task ClearPathAsync() => await SetPathAsync(null);

    private async Task SetPathAsync(string? path)
    {
        try
        {
            S.Settings.VolatilityPath = path;
            S.SaveSettings();
            var d = await S.RefreshVolatilityAsync();
            _onMessage(d != null ? $"Using Volatility 3 {d.Version}: {d.Exe}." : "Volatility 3 did not answer at that location (its Python must have the volatility3 package).",
                d != null ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (Exception ex) { _onError("Could not set the Volatility path", ex); }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var root = Install?.Root ?? VolatilityLocator.ManagedRoot;
            Directory.CreateDirectory(root);
            ShellOps.OpenFolder(root);
        }
        catch (Exception ex) { _onError("Could not open the Volatility folder", ex); }
    }

    [RelayCommand]
    private void OpenSymbolsFolder()
    {
        try
        {
            Directory.CreateDirectory(SymbolsDir);
            ShellOps.OpenFolder(SymbolsDir);
        }
        catch (Exception ex) { _onError("Could not open the symbols folder", ex); }
    }

    private bool CanRun() => !IsBusy;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private static string MaintenanceDir() => S.Maintenance.NewLogDir("volatility");

    private void Begin(string firstLine)
    {
        IsBusy = true;
        ProgressDeterminate = false;
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
        ProgressDeterminate = false;
        _cts?.Dispose();
        _cts = null;
        Raise();
    }

    /// <summary>
    /// One click from zero: (1) a Python 3.10-3.13 (the private one plaso uses, else an existing one, else python.org's package is downloaded),
    /// (2) the venv <c>volatility\venv</c>, (3) pip install of the pinned volatility3, (4) detect it and make it the one in use.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        if (IsBusy) return;
        Begin($"> Install Volatility 3 {VolatilitySetup.PinnedVersion}");
        await InstallCoreAsync("Volatility 3 installed");
    }

    /// <summary>Re-runs pip in the managed venv (installs the pinned version, repairs missing packages).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateAsync()
    {
        if (IsBusy) return;
        if (!IsManaged)
        {
            _onMessage($"Volatility at {Install?.Exe} was not installed by EZ Forensics, so EZ Forensics does not update it. Update it the way it was installed, or click Install for EZ Forensics' own copy.", InfoSeverity.Warning);
            return;
        }
        Begin($"> Update Volatility 3 {Install?.Version} → {VolatilitySetup.PinnedVersion}");
        await InstallCoreAsync("Volatility 3 updated");
    }

    private async Task InstallCoreAsync(string what)
    {
        var ct = _cts!.Token;
        var vl = NewLocator();
        var outDir = MaintenanceDir();
        if (S.HasActiveJob("volatility"))
        {
            _onMessage("A Volatility job is still running. Wait for it or cancel it in the Jobs list on Ingest, then try again.", InfoSeverity.Warning);
            End();
            return;
        }
        try
        {
            Directory.CreateDirectory(vl.Root);
            bool venvCreated = false;
            if (!File.Exists(vl.ManagedPython))
            {
                StepText = "Step 1 of 3: Python";
                var plaso = new PlasoLocator();
                var py = await Task.Run(() => plaso.FindSupportedPython(), ct);
                if (py == null)
                {
                    ProgressDeterminate = true;
                    using var http = S.CreateHttpClient();
                    var log = Log!;
                    Directory.CreateDirectory(PlasoLocator.ManagedRoot);
                    await PrivatePython.ProvisionAsync(http, PrivatePython.Default, plaso.PrivatePythonDir, log.Enqueue, new Progress<double>(p => Progress = p), ct);
                    ProgressDeterminate = false;
                    py = await Task.Run(() => plaso.FindSupportedPython(), ct);
                    if (py == null)
                    {
                        _onMessage("The private Python was extracted but does not start. See the log; click Install to try again.", InfoSeverity.Error);
                        return;
                    }
                }
                else Log!.Enqueue($"Using {py.Origin} Python {py.Version}: {py.Exe}");

                StepText = "Step 2 of 3: creating the Volatility environment";
                var venvJob = await RunStepAsync(VolatilitySetup.CreateVenvStep(vl, py), outDir, ct);
                if (venvJob.State != JobState.Succeeded || !File.Exists(vl.ManagedPython))
                {
                    await Task.Run(() => PrivatePython.TryDelete(vl.ManagedVenv));
                    _onMessage(venvJob.State == JobState.Cancelled ? "Install cancelled. Nothing was left behind; click Install to start again."
                        : $"Creating the environment failed: {venvJob.StatusReason}. See the log below, then click Install to retry.", InfoSeverity.Warning);
                    return;
                }
                venvCreated = true;
            }
            else Log!.Enqueue($"Environment already exists: {vl.ManagedVenv}");

            StepText = $"Step 3 of 3: pip install volatility3=={VolatilitySetup.PinnedVersion} (downloads ≈{VolatilitySetup.PackagesDownloadMB} MB)";
            var pipJob = await RunStepAsync(VolatilitySetup.PipStep(vl), outDir, ct);
            if (pipJob.State == JobState.Cancelled || ct.IsCancellationRequested)
            {
                if (venvCreated) await Task.Run(() => PrivatePython.TryDelete(vl.ManagedVenv));
                _onMessage("Install cancelled. Click Install to start again.", InfoSeverity.Warning);
                return;
            }
            if (pipJob.State != JobState.Succeeded)
            {
                _onMessage($"pip install volatility3 failed: {pipJob.StatusReason}. pip needs pypi.org / files.pythonhosted.org: check the internet connection and proxy, then click Install to retry. Log below.", InfoSeverity.Error);
                return;
            }
            Directory.CreateDirectory(vl.SymbolsDir);
            Directory.CreateDirectory(vl.CacheDir);
            await AutoConfigureAsync(what);
        }
        catch (OperationCanceledException) { _onMessage("Install cancelled. Click Install to start again.", InfoSeverity.Warning); }
        catch (Exception ex)
        {
            Log?.Enqueue("ERROR: " + ex.Message);
            _onError("Install Volatility 3 failed (click Install to retry)", ex);
        }
        finally { End(); }
    }

    private async Task AutoConfigureAsync(string what)
    {
        var d = await S.RefreshVolatilityAsync();
        var managed = NewLocator().ManagedExe;
        if (d != null && !d.Exe.Equals(managed, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(S.Settings.VolatilityPath))
        {
            Log?.Enqueue($"Configured Volatility path '{S.Settings.VolatilityPath}' replaced by automatic detection (EZ Forensics' own copy).");
            S.Settings.VolatilityPath = null;
            S.SaveSettings();
            d = await S.RefreshVolatilityAsync();
        }
        if (d != null)
        {
            Log?.Enqueue($"Detected: Volatility 3 {d.Version} ({d.Exe})");
            _onMessage($"{what}: version {d.Version}. Ingest now plans Volatility jobs for every memory image it finds.", InfoSeverity.Success);
        }
        else _onMessage("pip finished but Volatility 3 does not answer. See the log, then click Detect again.", InfoSeverity.Warning);
    }

    /// <summary>Downloads the official Windows symbol pack (windows.zip) into the symbols folder, for offline analysis.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallSymbolPackAsync()
    {
        if (IsBusy) return;
        var dir = SymbolsDir;
        if (VolatilitySetup.SymbolPackInstalled(dir)) { _onMessage($"The Windows symbol pack is already in {dir}.", InfoSeverity.Info); return; }
        Begin($"> Download the Windows symbol pack ({VolatilitySetup.SymbolPackUrl})");
        var ct = _cts!.Token;
        var tmpDir = MaintenanceDir();
        var zip = Path.Combine(tmpDir, "windows.zip");
        try
        {
            Directory.CreateDirectory(tmpDir);
            ProgressDeterminate = true;
            StepText = $"Downloading windows.zip (≈{VolatilitySetup.SymbolPackDownloadMB} MB)";
            var log = Log!;
            string sha;
            using (var http = S.CreateHttpClient())
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                sha = await VolatilitySetup.DownloadFileAsync(http, VolatilitySetup.SymbolPackUrl, zip, new Progress<double>(p => Progress = p * 0.8), log.Enqueue, ct);
            }
            StepText = $"Extracting into {dir}";
            int n = await Task.Run(() => VolatilitySetup.ExtractSymbolPack(zip, dir, new Progress<double>(p => Progress = 0.8 + p * 0.2), ct, sha), ct);
            log.Enqueue($"Extracted {n:N0} files into {dir}");
            _onMessage($"Windows symbol pack installed ({n:N0} files). Memory images can now be analysed offline.", InfoSeverity.Success);
            S.RaiseVolatilityChanged();
        }
        catch (OperationCanceledException) { _onMessage("Symbol pack download cancelled.", InfoSeverity.Warning); }
        catch (Exception ex)
        {
            Log?.Enqueue("ERROR: " + ex.Message);
            _onError("Downloading the symbol pack failed (check the internet connection, proxy and free disk space)", ex);
        }
        finally
        {
            PrivatePython.TryDelete(zip);
            End();
        }
    }

    /// <summary>Copies ISF files (.json / .json.xz) chosen by the user into <c>symbols\&lt;os&gt;</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ImportIsf()
    {
        var files = Dialogs.PickFiles($"Select {IsfTarget.Label} symbol files (ISF: .json or .json.xz)", "Volatility symbol files (ISF)|*.json;*.json.xz;*.xz|All files|*.*");
        if (files == null || files.Count == 0) return;
        try
        {
            var dest = Path.Combine(SymbolsDir, IsfTarget.Folder);
            Directory.CreateDirectory(dest);
            var skipped = new List<string>();
            int copied = 0;
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".json.xz", StringComparison.OrdinalIgnoreCase)) { skipped.Add(name); continue; }
                File.Copy(f, Path.Combine(dest, name), overwrite: true);
                copied++;
            }
            Raise();
            S.RaiseVolatilityChanged();
            _onMessage(copied == 0 ? "Nothing imported: ISF files end in .json or .json.xz."
                : $"Imported {copied} {IsfTarget.Label} symbol file{(copied == 1 ? "" : "s")} into {dest}." + (skipped.Count > 0 ? $" Skipped (not .json / .json.xz): {string.Join(", ", skipped)}." : "")
                  + (IsfTarget.Folder == "linux" ? " Scan the source again on the Ingest page to plan Linux memory images." : ""),
                copied == 0 ? InfoSeverity.Warning : InfoSeverity.Success);
        }
        catch (Exception ex) { _onError("Import ISF failed", ex); }
    }

    /// <summary>Removes EZ Forensics' Volatility environment and symbol cache (and the symbols folder when not kept). Never the private Python (plaso shares it).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallAsync()
    {
        var root = VolatilityLocator.ManagedRoot;
        var targets = new List<string> { Path.Combine(root, "venv"), Path.Combine(root, "cache") };
        if (!KeepSymbolsOnUninstall) targets.Add(Path.Combine(root, "symbols"));
        targets = targets.Where(Directory.Exists).ToList();
        if (targets.Count == 0) { _onMessage("EZ Forensics has no Volatility environment to remove.", InfoSeverity.Info); return; }
        if (S.HasActiveJob("volatility"))
        {
            _onMessage("A Volatility job is still running. Wait for it or cancel it in the Jobs list on Ingest, then click Uninstall again.", InfoSeverity.Warning);
            return;
        }
        if (!EzForensics.App.Services.Dialogs.Confirm("Uninstall Volatility 3?", KeepSymbolsOnUninstall ? "Removes EZ Forensics' copy. The symbols folder is kept." : "Removes EZ Forensics' copy and its symbols folder.",
                "Uninstall", details: string.Join("\n", targets), defaultNo: true)) return;
        IsBusy = true;
        try
        {
            var keep = KeepSymbolsOnUninstall;
            var removed = await Task.Run(() => VolatilitySetup.Uninstall(root, includeSymbols: !keep));
            if (!string.IsNullOrWhiteSpace(S.Settings.VolatilityPath) && S.Settings.VolatilityPath!.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                S.Settings.VolatilityPath = null;
                S.SaveSettings();
            }
            var d = await S.RefreshVolatilityAsync();
            _onMessage((removed.Count > 0 ? $"Removed {string.Join(" and ", removed)}." : "Nothing to remove.") +
                       (d != null ? $" Now using Volatility 3 {d.Version} ({d.Exe})." : " Click Install to set Volatility up again."), InfoSeverity.Success);
        }
        catch (Exception ex) { _onError("Uninstall Volatility 3 failed", ex); }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    private Task<Job> RunStepAsync(VolatilitySetupStep step, string outDir, CancellationToken ct) =>
        VolatilitySetup.RunStepAsync(S.Maintenance.Queue, step, outDir, job =>
        {
            Job = job;
            Log?.Enqueue($"> {step.Label}");
            job.Output += (_, line) => AppLog.Info($"[{step.Label}] {line}");
        }, ct);
}
