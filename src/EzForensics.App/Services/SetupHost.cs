using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.App.Services;

/// <summary>
/// What the setup wizard and the background resume after a restart share: the executor environment, the refresh after each step,
/// the restart bookkeeping (pending resume + RunOnce) and restarting Windows. Both run the same <see cref="SetupRunner"/> /
/// <see cref="DefaultSetupExecutor"/>; their processes run on the private maintenance queue (<see cref="AppServices.Maintenance"/>),
/// never in the Jobs list on Ingest, with logs under <c>logs\setup\&lt;stamp&gt;</c>.
/// </summary>
public static class SetupHost
{
    public static SetupEnvironment CreateEnvironment(AppServices s)
    {
        var (exe, prefix) = SetupLauncher.SelfLaunch();
        return new SetupEnvironment
        {
            Queue = s.Maintenance.Queue,
            CreateHttp = s.CreateHttpClient,
            Settings = s.Settings,
            SaveSettings = () => Save(s),
            Locator = s.Locator,
            MaintenanceDir = Path.Combine(AppPaths.LogsDir, "setup", AppServices.Stamp()),
            IsElevated = Elevation.IsAdministrator,
            ElevatedHelperExe = exe,
            ElevatedHelperPrefixArgs = prefix,
            AfterStep = kind => AfterStepAsync(s, kind),
            SimulatorExe = SetupLauncher.SimulatorExe,
            Simulate = SetupLauncher.SimulatedKinds,
            WslWelcome = EzForensics.Core.Runtimes.WslWelcomeGuard.CreateDefault(), // "Welcome to WSL": flag before wsl.exe, close after
        };
    }

    /// <summary>Refreshes what a finished step changed (tools rescan, plaso / Zeek / RITA / Volatility discovery).</summary>
    public static async Task AfterStepAsync(AppServices s, SetupStepKind kind)
    {
        switch (kind)
        {
            case SetupStepKind.DownloadEzTools:
            case SetupStepKind.InstallDotnet:
                await s.RescanAsync();
                break;
            case SetupStepKind.InstallPlaso:
                await s.RefreshPlasoAsync();
                break;
            case SetupStepKind.InstallZeekWsl:
                await s.RefreshZeekAsync();
                break;
            case SetupStepKind.InstallRita:
                await s.RefreshRitaAsync();
                break;
            case SetupStepKind.InstallVolatility:
                await s.RefreshVolatilityAsync();
                break;
            case SetupStepKind.InstallVolatilitySymbols:
                // the symbols folder changed (Linux ISF / offline pack): pages re-check symbols and re-plan
                s.RaiseVolatilityChanged();
                break;
        }
    }

    /// <summary>
    /// After a run: steps waiting for the restart are saved as the pending resume (only those, with <paramref name="firstSetup"/> and
    /// what the first setup installed so far, so Cancel after the restart can revert it) and a one-time RunOnce launch is registered;
    /// once a run leaves nothing pending and nothing failed, a pending resume is cleared. The wizard is not marked seen here: a pending
    /// resume reopens it by itself, and it is marked seen when it finishes.
    /// </summary>
    public static void RecordOutcome(AppServices s, SetupPlan plan, bool firstSetup = false, IReadOnlyList<RevertTarget>? installed = null)
    {
        if (SetupWizardPolicy.PendingAfterRestart(plan) is { } pending)
        {
            pending.FirstSetup = firstSetup;
            if (installed != null) pending.Installed.AddRange(installed);
            s.Settings.SetupResume = pending;
            Save(s);
            SetupLauncher.RegisterResumeAfterRestart();
            AppLog.Info("Setup continues after the restart with: " + string.Join(", ", pending.Items));
        }
        else if (s.Settings.SetupResume != null && !plan.AnyFailed) ClearResume(s);
    }

    public static void ClearResume(AppServices s)
    {
        if (s.Settings.SetupResume == null) return;
        s.Settings.SetupResume = null;
        Save(s);
        SetupLauncher.UnregisterResumeAfterRestart();
        AppLog.Info("Pending setup resume cleared.");
    }

    /// <summary>The one confirmation before a first setup is cancelled and reverted (Yes / No, No by default).</summary>
    public static bool ConfirmCancel(string question)
    {
        return EzForensics.App.Services.Dialogs.Confirm("Cancel setup?", question, "Cancel setup", "Keep going", EzForensics.App.Views.EzDialogSeverity.Warning, defaultNo: true, owner: SetupLauncher.OpenWindow);
    }

    /// <summary>Asks first, then restarts Windows immediately (<c>shutdown /r</c>). True when the restart was started.</summary>
    public static bool RestartWindows(System.Windows.Window? owner = null)
    {
        if (!EzForensics.App.Services.Dialogs.Confirm("Restart required to finish setup", "EZ Forensics needs to restart Windows to finish installing. Save your work first — setup continues automatically after you sign in.", "Restart now", "Later", owner: owner, plain: true)) return false;
        try
        {
            var shutdown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(shutdown) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/r", "/t", "0", "/c", "EZ Forensics setup: restarting to finish enabling WSL." }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("shutdown.exe did not start.");
            AppLog.Info("Setup: restarting Windows (shutdown /r /t 0).");
            if (p.WaitForExit(5000))
            {
                AppLog.Info("Setup: shutdown.exe exit code " + p.ExitCode);
                if (p.ExitCode != 0)
                {
                    EzForensics.App.Services.Dialogs.Error("Could not restart Windows", "Restart it from the Start menu.", "shutdown.exe exited with code " + p.ExitCode, owner: owner);
                    return false;
                }
            }
            else AppLog.Info("Setup: shutdown.exe still running after 5 s; assuming the restart is under way.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restart Windows", ex);
            EzForensics.App.Services.Dialogs.Error("Could not restart Windows", "Restart it from the Start menu.", ex.Message);
            return false;
        }
    }

    public static void Save(AppServices s)
    {
        try { s.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
    }
}
