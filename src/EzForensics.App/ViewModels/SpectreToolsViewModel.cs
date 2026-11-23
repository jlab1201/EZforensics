using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels;

/// <summary>One tool of the Spectre engine on the Tools page: name, used by, Required / Optional, status + version, Install / Update.</summary>
public sealed partial class SpectreToolRow : ObservableObject
{
    [ObservableProperty] private string _status = "Not checked";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string _actionLabel = "Install";

    public SpectreToolRow(SpectreEngineTool tool, Func<SpectreToolRow, Task> install)
    {
        Tool = tool;
        InstallCommand = new AsyncRelayCommand(() => install(this));
    }

    public SpectreEngineTool Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;
    public string UsedBy => Tool.UsedBy;
    public string Importance => Tool.Importance;
    public string AutomationKey => Tool.Id;
    public IAsyncRelayCommand InstallCommand { get; }

    public void Apply(SpectreToolStatus? s)
    {
        IsInstalled = s?.Installed == true;
        Status = s == null ? "Not checked" : s.Text;
        ActionLabel = IsInstalled ? "Update" : "Install";
    }
}

/// <summary>
/// The "Spectre" entry of the Tools page: one row per tool the Spectre engine needs, in the private EzForensics WSL distro.
/// Check (read-only probe) on opening and with Refresh; installs only when a button is clicked.
/// </summary>
public sealed partial class SpectreToolsViewModel : ObservableObject
{
    private readonly Action<string, Exception> _onError;
    private readonly Action<string, InfoSeverity> _onMessage;
    private IReadOnlyDictionary<string, SpectreToolStatus> _status = new Dictionary<string, SpectreToolStatus>();
    private bool _probed;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _distroReady;
    [ObservableProperty] private string _headline = "Checking the EZ Forensics Linux environment…";
    [ObservableProperty] private LogBuffer? _log;

    public SpectreToolsViewModel(Action<string, Exception> onError, Action<string, InfoSeverity> onMessage)
    {
        _onError = onError;
        _onMessage = onMessage;
        foreach (var t in SpectreEngineTools.All) Rows.Add(new SpectreToolRow(t, InstallRowAsync));
    }

    private static AppServices S => AppServices.Current;

    public ObservableCollection<SpectreToolRow> Rows { get; } = new();
    public bool ShowDistroMissing => !DistroReady && !IsBusy;
    public string DistroMissingText => "The EZ Forensics Linux environment (WSL distro) is not set up yet. Set up the WSL distro first (Zeek's Install button or the setup wizard creates it), then come back here.";

    // Overview table row
    public bool Found => _status.TryGetValue("spectre", out var s) && s.Installed;
    public string StatusLabel => !_probed ? "" : !DistroReady ? "Needs WSL distro" : Found ? (Rows.All(r => r.IsInstalled || !r.Tool.Required) ? "Installed" : "Incomplete") : "Not installed";
    public string VersionText => _status.TryGetValue("spectre", out var s) && s.Version is { Length: > 0 } v ? v : "-";
    public string LocationText => Found ? SpectreEngineTools.VenvDir + " (" + EzDistro.Name + ")" : "-";
    public IAsyncRelayCommand InstallCommand => InstallAllCommand;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDistroMissing));
        InstallAllCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnDistroReadyChanged(bool value) => OnPropertyChanged(nameof(ShowDistroMissing));

    /// <summary>Probes once (first time the page or the Overview needs it).</summary>
    public void EnsureProbed()
    {
        if (!_probed && !IsBusy) _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var info = await Task.Run(() => EzDistro.Inspect());
            DistroReady = info.Ready;
            if (!info.Ready)
            {
                _status = new Dictionary<string, SpectreToolStatus>();
                Headline = info.Summary;
            }
            else
            {
                _status = await SpectreEngineTools.ProbeAsync();
                Headline = $"{EzDistro.Name}: {_status.Values.Count(s => s.Installed)} of {Rows.Count} tools installed.";
            }
            foreach (var r in Rows) r.Apply(DistroReady ? _status.GetValueOrDefault(r.Tool.Id) : null);
            foreach (var r in Rows) if (!DistroReady) r.Status = "Needs the WSL distro";
        }
        catch (Exception ex)
        {
            Headline = "Could not check the Spectre tools: " + ex.Message;
            AppLog.Error("Spectre tools probe failed", ex);
        }
        finally
        {
            _probed = true;
            IsBusy = false;
            Raise();
        }
    }

    private bool CanRun() => !IsBusy;

    /// <summary>Install / update everything: one apt run, the Spectre environment, and the optional tools that are missing.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAllAsync()
    {
        if (!DistroReady) { _onMessage(DistroMissingText, InfoSeverity.Warning); return; }
        await RunPlanAsync(wsl => SpectreEngineSetup.PlanAll(wsl, _status, SpectreEngineSetup.FindInstallSource()), "Install / update all Spectre tools");
    }

    private async Task InstallRowAsync(SpectreToolRow row)
    {
        if (IsBusy) return;
        if (!DistroReady) { _onMessage(DistroMissingText, InfoSeverity.Warning); return; }
        await RunPlanAsync(wsl => SpectreEngineSetup.PlanFor(wsl, row.Tool, SpectreEngineSetup.FindInstallSource()), $"{row.ActionLabel} {row.Name}");
    }

    private async Task RunPlanAsync(Func<string, SpectrePlan> plan, string title)
    {
        var wsl = WslRuntime.FindWslExe(SystemRuntimeProbe.Instance);
        if (wsl == null) { _onMessage("wsl.exe was not found.", InfoSeverity.Error); return; }
        var p = plan(wsl);
        if (!p.Ok) { _onMessage(p.Problem!, InfoSeverity.Warning); if (p.Steps.Count == 0) return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Log?.Dispose();
        Log = new LogBuffer();
        Log.Enqueue("> " + title);
        var ct = _cts.Token;
        try
        {
            var dir = S.Maintenance.NewLogDir("spectre");
            foreach (var step in p.Steps)
            {
                Headline = step.Label;
                var job = await SpectreEngineSetup.RunStepAsync(S.Maintenance.Queue, step, dir, j =>
                {
                    Log?.Enqueue($"> {step.Display}");
                    j.Output += (_, line) => AppLog.Info($"[{step.Display}] {line}");
                }, ct);
                if (job.State == JobState.Cancelled) { _onMessage("Cancelled. Click again to resume (apt and pip continue where they stopped).", InfoSeverity.Warning); return; }
                if (job.State != JobState.Succeeded) { _onMessage($"{step.Label} failed: {job.StatusReason}. See the Jobs list on Ingest, then retry.", InfoSeverity.Error); return; }
            }
            _onMessage(title + " finished.", InfoSeverity.Success);
        }
        catch (Exception ex) { _onError(title + " failed", ex); }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Raise()
    {
        foreach (var n in new[] { nameof(Found), nameof(StatusLabel), nameof(VersionText), nameof(LocationText) }) OnPropertyChanged(n);
        Changed?.Invoke();
    }

    /// <summary>Raised after a probe (the Overview row re-reads status / version).</summary>
    public event Action? Changed;
}
