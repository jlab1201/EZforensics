using System.Diagnostics;
using System.Windows;
using EzForensics.App.ViewModels;
using EzForensics.App.Views;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.App.Services;

/// <summary>How the setup wizard was closed (OpenApp = "Open EZ Forensics" on the Done page; Cancelled = the first setup was cancelled and reverted).</summary>
public enum SetupWizardResult { Closed, OpenApp, OpenToolsAi, RestartingWindows, Cancelled }

/// <summary>Opens the setup wizard (first run, its resume after the restart, the menu's "Run setup", Tools, Settings) and the setup guide.</summary>
public static class SetupLauncher
{
    private static SetupWizardWindow? _open;

    /// <summary>Shows the wizard modally over <paramref name="owner"/>. Returns true when the user chose "Open EZ Forensics".</summary>
    public static bool Open(Window? owner)
    {
        var r = Show(owner, null);
        if (r == SetupWizardResult.RestartingWindows) Application.Current?.Shutdown(0);
        return r is SetupWizardResult.OpenApp or SetupWizardResult.OpenToolsAi;
    }

    /// <summary>
    /// The wizard on its own (the very first start): no owner, own taskbar button. The caller opens the main window afterwards
    /// (unless Windows is restarting).
    /// </summary>
    public static SetupWizardResult OpenAlone() => Show(null, null, firstSetup: true);

    /// <summary>
    /// After the Windows restart: the wizard on its own, straight on its Install page, finishing the pending steps with the same
    /// progress UI, then Done. A cancelled first setup reverts what it installed before and after the restart.
    /// </summary>
    public static SetupWizardResult OpenResume(SetupResumeState pending) =>
        Show(null, SetupWizardPolicy.FromResume(pending), pending.FirstSetup, pending.Installed);


    public static bool IsOpen => _open != null;

    /// <summary>The wizard window while it is open (owner of its message boxes).</summary>
    public static Window? OpenWindow => _open;

    private static SetupWizardResult Show(Window? owner, SetupSelection? resume, bool firstSetup = false, IEnumerable<RevertTarget>? priorInstalled = null)
    {
        if (_open != null) { _open.Activate(); return SetupWizardResult.Closed; }
        var s = AppServices.Current;
        var vm = new SetupWizardViewModel(s, resume, firstSetup, priorInstalled);
        var w = new SetupWizardWindow { DataContext = vm };
        if (owner != null && owner.IsVisible) w.Owner = owner;
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowInTaskbar = true;
        }
        _open = w;
        if (w.Owner == null)
        {
            // Opened by itself (first start, resume after the restart): come to the front, or it may sit behind other windows unnoticed
            // (a start-up launch does not get the foreground by itself).
            w.ContentRendered += (_, _) =>
            {
                try
                {
                    var started = Process.GetCurrentProcess().StartTime;
                    AppLog.Info($"Setup wizard shown {(DateTime.Now - started).TotalSeconds:0.0} s after EZ Forensics started (Windows up for {Environment.TickCount64 / 1000} s).");
                    w.Activate();
                    w.Topmost = true;
                    w.Topmost = false;
                }
                catch (Exception ex) { AppLog.Warn("Could not bring the setup wizard to the front: " + ex.Message); }
            };
        }
        var result = SetupWizardResult.Closed;
        vm.CloseRequested += r =>
        {
            result = r;
            AppLog.Info("Setup wizard closing: " + r);
            try { w.CloseFromViewModel(); }
            catch (InvalidOperationException ex)
            {
                // Close() is refused while the window is already closing or a layout pass runs: close on the next dispatcher turn.
                AppLog.Warn("Setup wizard: closing now was refused (" + ex.Message + "); closing on the next dispatcher turn.");
                w.Dispatcher.BeginInvoke(new Action(w.CloseFromViewModel));
            }
        };
        try { w.ShowDialog(); }
        finally { _open = null; }
        if (result is not (SetupWizardResult.RestartingWindows or SetupWizardResult.Cancelled)) _ = RefreshAfterSetupAsync(s);
        if (result == SetupWizardResult.OpenApp) s.Navigate<TriageViewModel>();
        else if (result == SetupWizardResult.OpenToolsAi) s.OpenToolsConfiguration(ToolsSection.Documents); // no-op while the wizard runs alone: App opens it once the main window is up
        return result;
    }

    /// <summary>
    /// After the wizard closes (finished, skipped or closed): rescan the tools folder (ToolLocator) and re-probe plaso, Volatility,
    /// Zeek and RITA, so the main window shows what was just installed without a restart of the app.
    /// </summary>
    private static async Task RefreshAfterSetupAsync(AppServices s)
    {
        async Task Try(string what, Func<Task> a)
        {
            try { await a(); } catch (Exception ex) { AppLog.Error($"Refresh after setup failed ({what})", ex); }
        }
        await Try("tools", s.RescanAsync);
        await Try("plaso", () => s.RefreshPlasoAsync());
        await Try("volatility", () => s.RefreshVolatilityAsync());
        await Try("zeek", () => s.RefreshZeekAsync());
        await Try("rita", () => s.RefreshRitaAsync());
        AppLog.Info("Setup wizard closed: tools folder rescanned and runtimes re-probed.");
    }

    /// <summary>
    /// The exe (and the arguments before any switch) that relaunches this app: the elevated setup helper and the RunOnce resume
    /// command use it. A dev build started through dotnet.exe relaunches its dll.
    /// </summary>
    public static (string? Exe, IReadOnlyList<string> Prefix) SelfLaunch()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return (null, Array.Empty<string>());
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (exe, new[] { typeof(SetupLauncher).Assembly.Location });
        return (exe, Array.Empty<string>());
    }

    /// <summary>Registers the one-time resume at the next sign-in (HKCU RunOnce). Logged; failure only means the user opens the app by hand.</summary>
    public static void RegisterResumeAfterRestart()
    {
        var (exe, prefix) = SelfLaunch();
        var cmd = SetupResumeLaunch.BuildCommand(exe, prefix.FirstOrDefault());
        if (cmd != null && SetupResumeLaunch.Register(cmd)) AppLog.Info("Setup resumes after the restart: HKCU RunOnce " + SetupResumeLaunch.ValueName + " = " + cmd);
        else AppLog.Warn("Could not register the setup resume (RunOnce); setup continues when EZ Forensics is opened again.");
        // Faster trigger: a logon task starts right at sign-in, while Explorer runs RunOnce only after its start-up delay / idle wait.
        if (exe == null) return;
        var why = SetupResumeTask.Register(exe, prefix.Append(SetupResumeLaunch.Switch).ToList());
        if (why == null) AppLog.Info("Setup resumes after the restart: logon task \"" + SetupResumeTask.TaskName + "\" registered (RunOnce stays as the fallback).");
        else AppLog.Warn("Could not register the setup resume logon task (RunOnce is used alone): " + why);
    }

    /// <summary>Removes the RunOnce value now and the logon task in the background (schtasks.exe; never blocks the UI).</summary>
    public static void UnregisterResumeAfterRestart()
    {
        SetupResumeLaunch.Unregister();
        _ = Task.Run(SetupResumeTask.Unregister);
    }

    /// <summary>
    /// A resume launch fired (logon task or RunOnce): both triggers are one-time, so the other one is removed too (Explorer may not have
    /// run RunOnce yet: it would start the app a second time minutes later). A resume that needs another restart registers them again.
    /// </summary>
    public static void ResumeLaunchStarted() => UnregisterResumeAfterRestart();

    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { AppLog.Error("Could not open " + url, ex); }
    }

    /// <summary>
    /// UI-test hook: <c>EZFORENSICS_SETUP_SIMULATE=&lt;fake tool exe&gt;</c> runs that exe as a job instead of the real install for the
    /// step kinds in <c>EZFORENSICS_SETUP_SIMULATE_STEPS</c> (comma-separated; default: everything except the EZ tools download and
    /// map sync). Logged loudly; never set in normal use. Debug builds only: a Release build ignores the variable, so it cannot be
    /// used to make the installed app run an arbitrary exe.
    /// </summary>
    public static string? SimulatorExe
    {
        get
        {
#if !DEBUG
            return null;
#else
            var exe = AppPaths.Env("SETUP_SIMULATE");
            if (exe == null || !File.Exists(exe)) return null;
            AppLog.Warn("Setup simulation is ON (EZFORENSICS_SETUP_SIMULATE): some steps run " + exe + " instead of installing.");
            return exe;
#endif
        }
    }

    public static IReadOnlySet<SetupStepKind> SimulatedKinds
    {
        get
        {
            var list = AppPaths.Env("SETUP_SIMULATE_STEPS");
            if (list == null)
                return Enum.GetValues<SetupStepKind>().Where(k => k is not (SetupStepKind.DownloadEzTools or SetupStepKind.SyncMaps or SetupStepKind.BuiltIn)).ToHashSet();
            return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Enum.TryParse<SetupStepKind>(x, true, out var k) ? (SetupStepKind?)k : null).Where(k => k != null).Select(k => k!.Value).ToHashSet();
        }
    }
}
