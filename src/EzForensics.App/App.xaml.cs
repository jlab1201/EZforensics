using System.Windows;
using System.Windows.Threading;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.App;

public partial class App : Application
{
    private ShellViewModel? _shell;
    private SingleInstanceGuard? _single;
    /// <summary>The Ingest page saved before Restart as Administrator, shown again once the main window is open (null: none).</summary>
    private EzForensics.Core.Triage.IngestSessionSnapshot? _pendingIngest;

    /// <summary>Set by Velopack's OnFirstRun hook (first start right after the installer).</summary>
    public static bool IsFirstRunAfterInstall { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Built-in headless step run as a queued job (logged, hashed into the run manifest): no window.
        if (e.Args.Length > 0 && e.Args[0] == ComputerDetailsJob.Switch)
        {
            int code;
            using (var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true })
                code = EzForensics.Core.SystemInfo.ComputerDetailsStore.RunCommandLine(e.Args.Skip(1).ToList(), stdout);
            Shutdown(code);
            return;
        }
        // Built-in persistence collector (offline autostart entries), same headless pattern.
        if (e.Args.Length > 0 && e.Args[0] == EzForensics.Core.Persistence.PersistenceCollector.Switch)
        {
            int code;
            using (var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true })
                code = EzForensics.Core.Persistence.PersistenceCollector.RunCommandLine(e.Args.Skip(1).ToList(), stdout);
            Shutdown(code);
            return;
        }
        // Built-in process correlator (Processes\Processes.csv + Processes_Activity.csv of one host), same headless pattern.
        if (e.Args.Length > 0 && e.Args[0] == EzForensics.Core.Processes.ProcessCorrelator.Switch)
        {
            int code;
            using (var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true })
                code = EzForensics.Core.Processes.ProcessCorrelator.RunCommandLine(e.Args.Skip(1).ToList(), stdout);
            Shutdown(code);
            return;
        }
        // Elevated setup helper (relaunched by the setup wizard with UAC): every admin task in one process, no window.
        if (e.Args.Length > 0 && e.Args[0] == ElevatedSetupHelper.Switch)
        {
            int code;
            try
            {
                AppLog.Info($"Elevated setup helper started (admin={Elevation.IsAdministrator}): {string.Join(" ", e.Args)}");
                code = ElevatedSetupHelper.RunCommandLine(e.Args.Skip(1).ToList(), AppLog.Info);
                AppLog.Info($"Elevated setup helper finished with exit code {code}.");
            }
            catch (Exception ex) { AppLog.Error("Elevated setup helper failed", ex); code = 1; }
            Shutdown(code);
            return;
        }
        // One window per user: a second launch (RunOnce --resume-setup after the restart, a shortcut click) brings the running one forward.
        // Restart as Administrator (--restore-ingest): the old instance still holds the mutex while it exits, so wait for it instead of
        // handing over to it (it would close and nothing would open).
        bool restoreLaunch = EzForensics.Core.Triage.IngestSessionLaunch.HasSwitch(e.Args);
        _single = new SingleInstanceGuard();
        if (!(restoreLaunch ? _single.TryBecomeFirst(EzForensics.Core.Triage.IngestSessionLaunch.MutexWait) : _single.TryBecomeFirst()))
        {
            _single.SignalExisting();
            if (_single.OwnedByElevatedInstance)
                MessageBox.Show("EZ Forensics is already running as Administrator. Switch to its window (Windows does not let this launch bring it forward).",
                    "EZ Forensics", MessageBoxButton.OK, MessageBoxImage.None);
            _single.Dispose();
            _single = null;
            Shutdown(0);
            return;
        }
        _single.StartListening(() => Dispatcher.BeginInvoke(new Action(BringToFront)));
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            AppLog.Error("Unhandled exception (terminating: " + a.IsTerminating + ")", a.ExceptionObject as Exception);
            if (a.IsTerminating) FlushUserState(); // the process is going down: keep flags and the Analyze view
        };
        SessionEnding += (_, a) =>
        {
            AppLog.Info("Windows session ending (" + a.ReasonSessionEnding + "): saving flags and the Analyze view");
            FlushUserState();
        };
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            AppLog.Error("Unobserved task exception", a.Exception);
            a.SetObserved();
        };

        AppLog.Info($"EZ Forensics {typeof(App).Assembly.GetName().Version?.ToString(3)} starting (admin={Elevation.IsAdministrator}, pid={Environment.ProcessId}, exe={Environment.ProcessPath}, firstRun={IsFirstRunAfterInstall})");
        try
        {
            // First start after EZ Hub: copy settings.json (and flag / stats fallbacks) from %LOCALAPPDATA%\EzForensics. Never moves or deletes
            // anything; plaso venvs keep being used where they are (the tools folder is moved below). Skipped for a custom settings file / data root.
            if (AppPaths.Env("SETTINGS") == null && !AppPaths.IsDataRootOverridden)
            {
                var m = DataMigration.Run();
                if (m.Migrated) AppLog.Info($"Migrated from EZ Hub ({AppPaths.LegacyRoot} -> {AppPaths.DataRoot}): {string.Join(", ", m.Copied)}");
                foreach (var n in m.Notes) AppLog.Warn(n);
            }
            // EZFORENSICS_SETTINGS=<path> (or the old EZHUB_SETTINGS) uses another settings file (portable use, UI tests).
            var store = new JsonSettingsStore(AppPaths.SettingsPath);
            var settings = store.Load();
            if (AppPaths.Env("SETTINGS") == null && !AppPaths.IsDataRootOverridden)
            {
                // settings carried over from EZ Hub still point at %LOCALAPPDATA%\EzHub\Tools: move that folder into EzForensicsData once
                var (newRoot, note) = DataMigration.MoveLegacyTools(settings.ToolsRoot);
                if (note != null) (newRoot != null ? (Action<string>)AppLog.Info : AppLog.Warn)(note);
                if (newRoot != null) { settings.ToolsRoot = newRoot; store.Save(settings); }
            }
            // EZ Forensics' private .NET 9 runtime (only when the machine has none): child processes inherit DOTNET_ROOT.
            var dotnetRoot = EzForensics.Core.Tools.RuntimeProbe.ApplyToCurrentProcess();
            if (dotnetRoot != null) AppLog.Info("Using the private .NET runtime for the EZ tools: DOTNET_ROOT=" + dotnetRoot);
            TitleBarTheme.Register();
            ThemeManager.Apply(settings.Theme);
            Appearance.Initialize(settings); // UI zoom + fonts (dynamic resources)

            // Manual composition (no DI container needed for a handful of singletons).
            AppServices.Current = new AppServices(store, settings);
            _shell = new ShellViewModel(AppServices.Current);
            var mode = SetupWizardPolicy.Startup(settings);
            if (SetupResumeLaunch.HasSwitch(e.Args))
            {
                AppLog.Info($"Started with {SetupResumeLaunch.Switch} (pending setup: {settings.SetupResume != null}); " + ResumeTiming(settings.SetupResume));
                SetupLauncher.ResumeLaunchStarted();
            }
            else if (mode == StartupMode.ResumeWizard) AppLog.Info("Pending setup, opened by hand; " + ResumeTiming(settings.SetupResume));
            _ = _shell.InitializeAsync();
            if (restoreLaunch) TakeSavedIngest(e.Args);
            if (mode is StartupMode.WizardFirst or StartupMode.ResumeWizard)
            {
                // The very first start, or after the restart (opened by hand or by the RunOnce --resume-setup launch): only the setup
                // wizard (after the restart it opens on its Install page and finishes the remaining steps); the main window opens when it
                // closes. A second launch meanwhile brings the wizard forward (single instance).
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var resume = mode == StartupMode.ResumeWizard ? settings.SetupResume : null;
                Dispatcher.BeginInvoke(new Action(() => ShowWizardThenMain(resume)), DispatcherPriority.Loaded);
            }
            else ShowMainWindow();
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            ShowStartupError(ex);
            Shutdown(1);
        }
    }

    /// <summary>For the log: how long after the setup asked for the restart, and after Windows started, this launch came.</summary>
    private static string ResumeTiming(SetupResumeState? resume)
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var sinceSaved = resume != null && resume.SavedUtc != default ? DateTime.UtcNow - resume.SavedUtc : (TimeSpan?)null;
        return $"Windows up for {up.TotalSeconds:0} s" + (sinceSaved is { } s ? $", {s.TotalMinutes:0.0} min after setup saved the pending steps" : "");
    }

    /// <summary>Startup failure: the themed dialog, or the stock MessageBox only when the theme resources are not loaded (nothing to style with).</summary>
    private static void ShowStartupError(Exception ex)
    {
        try
        {
            if (Current?.TryFindResource("Brush.Window") != null)
            {
                EzForensics.App.Services.Dialogs.Error("EZ Forensics could not start", ex.Message, ex.Message + "\n\nLog: " + AppLog.LogPath);
                return;
            }
        }
        catch { /* fall through to the stock box */ }
        MessageBox.Show("EZ Forensics could not start:\n\n" + ex.Message + "\n\nDetails: " + AppLog.LogPath, "EZ Forensics", MessageBoxButton.OK, MessageBoxImage.None);
    }

    /// <summary>Another launch asked for this instance: show the main window (or the setup wizard while it is open).</summary>
    private void BringToFront()
    {
        try
        {
            var win = Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w.Owner == null) ?? MainWindow;
            if (win == null) return;
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            win.Topmost = true; // Activate alone only flashes the taskbar button when another app has the foreground
            win.Topmost = false;
            win.Focus();
        }
        catch (Exception ex) { AppLog.Warn("Could not bring the window to the front: " + ex.Message); }
    }

    private void ShowMainWindow()
    {
        var win = new MainWindow { DataContext = _shell };
        MainWindow = win;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        win.Show();
        win.Activate();
        if (_pendingIngest is { } snapshot && _shell != null)
        {
            _pendingIngest = null;
            _shell.Selected = _shell.Items.FirstOrDefault(i => i.Page == _shell.Triage) ?? _shell.Selected;
            _shell.ClearBackHistory();
            _ = _shell.Triage.RestoreSessionAsync(snapshot);
        }
    }

    /// <summary>Reads (and deletes) the Ingest page saved before Restart as Administrator; an expired or unreadable one is dropped.</summary>
    private void TakeSavedIngest(IReadOnlyList<string> args)
    {
        try
        {
            var path = EzForensics.Core.Triage.IngestSessionLaunch.SnapshotPath(args) ?? EzForensics.Core.Triage.IngestSessionStore.DefaultPath;
            _pendingIngest = EzForensics.Core.Triage.IngestSessionStore.Take(DateTime.UtcNow, path);
            AppLog.Info(_pendingIngest != null ? "Restoring the Ingest page saved before the restart as Administrator: " + path
                                               : "No fresh Ingest session to restore (missing, expired or unreadable): " + path);
        }
        catch (Exception ex) { AppLog.Error("Could not read the saved Ingest session", ex); }
    }

    private void ShowWizardThenMain(SetupResumeState? resume)
    {
        var result = SetupWizardResult.Closed;
        try
        {
            if (resume != null)
            {
                AppLog.Info("Pending setup after the restart: opening the setup wizard on its Install page (wizard only): " + string.Join(", ", resume.Items));
                result = SetupLauncher.OpenResume(resume);
            }
            else
            {
                AppLog.Info("First start: opening the setup wizard (wizard only).");
                result = SetupLauncher.OpenAlone();
            }
        }
        catch (Exception ex) { AppLog.Error("Could not open the setup wizard", ex); }
        if (result == SetupWizardResult.RestartingWindows)
        {
            AppLog.Info("Windows is restarting to finish setup: not opening the main window.");
            Shutdown(0);
            return;
        }
        if (result == SetupWizardResult.Cancelled)
        {
            // The first setup was cancelled and reverted: the app stays installed and the wizard opens again at the next start.
            AppLog.Info("First setup cancelled: exiting without opening the main window.");
            Shutdown(0);
            return;
        }
        try { ShowMainWindow(); }
        catch (Exception ex)
        {
            AppLog.Error("Could not open the main window", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Current?.Queue.CancelAll(); } catch { }
        FlushUserState();
        try { _single?.Dispose(); } catch { }
        AppLog.Info("EZ Forensics exiting");
        base.OnExit(e);
    }

    /// <summary>Writes pending row flags and the Analyze session state now (exit, Windows logoff / shutdown, crash). Never throws.</summary>
    private void FlushUserState()
    {
        try { _shell?.Results.FlushFlags(); } catch { }
        try { _shell?.Results.SaveSessionNow(); } catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        FlushUserState(); // the app keeps running, but a later crash must not lose what is already flagged
        try { _shell?.ReportUnhandled(e.Exception); } catch { }
    }
}
