using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Plaso;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using EzForensics.Core.Zeek;

namespace EzForensics.App.ViewModels;

public enum SetupPage { Welcome, SystemCheck, Bundles, Summary, Install, Done }

public enum CheckLevel { Ok, Info, Warning }

/// <summary>One line of the system check page: a short value, the explanation behind its "?".</summary>
public sealed record CheckRow(string Id, string Label, string Value, CheckLevel Level, string? Help = null)
{
    /// <summary>The Segoe Fluent Icons / MDL2 glyph of the status slot: one character for every level, never text.</summary>
    public string Glyph => GlyphFor(Level);

    public static string GlyphFor(CheckLevel level) => level switch
    {
        CheckLevel.Ok => "\uE73E",      // CheckMark
        CheckLevel.Warning => "\uE7BA", // Warning
        _ => "\uE946",                  // Info (also any future level)
    };
}

/// <summary>A check box for one catalog item.</summary>
public sealed partial class SetupItemViewModel : ObservableObject
{
    private readonly SetupWizardViewModel _owner;

    public SetupItemViewModel(SetupWizardViewModel owner, SetupBundleViewModel bundle, SetupItem item)
    {
        _owner = owner;
        Bundle = bundle;
        Item = item;
    }

    public SetupItem Item { get; }
    public SetupBundleViewModel Bundle { get; }
    public string Id => Item.Id;
    public string Title => _owner.TitleFor(Item);
    public string Description => Item.Description;
    /// <summary>Not selectable: built in, coming soon, or already on this computer (.NET 9 installed system-wide).</summary>
    public bool IsEnabled => Item.Selectable && !_owner.IsAlreadyOnMachine(Item);

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        Bundle.Refresh();
        _owner.OnItemToggled(this);
    }

    public string SizeText => _owner.SizeTextFor(Item);
    /// <summary>A few words next to the title ("Already installed"); the explanation is in <see cref="Help"/>.</summary>
    public string? Note => _owner.NoteFor(Item);
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public string Help => _owner.HelpFor(Item);

    public void RaiseInfo()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(IsEnabled));
    }
}

/// <summary>A bundle check box (tri-state: some items) with its items listed below it.</summary>
public sealed partial class SetupBundleViewModel : ObservableObject
{
    private bool _updating;

    public SetupBundleViewModel(SetupWizardViewModel owner, SetupBundle bundle)
    {
        Owner = owner;
        Bundle = bundle;
        // hidden items (the WSL distro) are not shown: the planner adds them when Zeek or RITA needs them
        foreach (var i in bundle.Items.Where(i => !i.Hidden)) Items.Add(new SetupItemViewModel(owner, this, i));
        // Windows artifacts (the big one) is open by default so its ticks are visible; the others show a one-line summary.
        _isExpanded = bundle.Id == SetupIds.BundleArtifacts;
    }

    public SetupWizardViewModel Owner { get; }
    public SetupBundle Bundle { get; }
    public string Id => Bundle.Id;
    public string Title => Bundle.Title;
    public string Description => Bundle.Description;
    public bool ComingSoon => Bundle.ComingSoon;
    public bool IsEnabled => !Bundle.ComingSoon;
    public ObservableCollection<SetupItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    /// <summary>True = every selectable item, null = some, false = none (items not selectable here do not count).</summary>
    public bool? IsChecked
    {
        get
        {
            var sel = Items.Where(i => i.IsEnabled).ToList();
            if (sel.Count == 0) return false;
            int on = sel.Count(i => i.IsChecked);
            return on == 0 ? false : on == sel.Count ? true : null;
        }
        set
        {
            if (_updating || Bundle.ComingSoon) return;
            _updating = true;
            try
            {
                // Clicking a partly ticked bundle ticks it fully; ticking selects the default items (all when none is default).
                bool on = value != false;
                var targets = Items.Where(i => i.IsEnabled).ToList();
                var defaults = targets.Where(i => i.Item.DefaultOn).ToList();
                foreach (var i in targets) i.IsChecked = on && (IsChecked == null || defaults.Count == 0 || defaults.Contains(i));
            }
            finally { _updating = false; }
            Refresh();
        }
    }

    public string SizeText => Owner.BundleSizeText(this);

    /// <summary>Collapsed bundles: which items are ticked ("Zeek, RITA"), so the selection is always visible.</summary>
    public string ChildSummary
    {
        get
        {
            var on = Items.Where(i => i.IsChecked && i.IsEnabled).Select(i => i.Title).ToList();
            return on.Count == 0 ? "Nothing selected" : string.Join(" · ", on);
        }
    }

    public void Refresh()
    {
        if (_updating) return;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ChildSummary));
    }
}

/// <summary>
/// The setup wizard: Welcome, System check, Choose bundles, Summary (with the one third-party licence acceptance), Install, Done.
/// All decisions live in Core (<see cref="SetupCatalog"/>, <see cref="SetupPlanner"/>, <see cref="SetupRunner"/>,
/// <see cref="DefaultSetupExecutor"/>); this view model only binds them to the window. Explanations sit behind "?" help tips
/// (<see cref="Controls.HelpTip"/>), so the visible text stays one line per thing.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly SetupCatalog _catalog = SetupCatalog.Default;
    private readonly SetupSelection? _resume;
    /// <summary>Tests (README screenshots): a fake machine probe. Given, the system check never starts a process or touches the network.</summary>
    private readonly EzForensics.Core.Runtimes.IRuntimeProbe? _probe;
    private bool _applyingProfile;
    private CancellationTokenSource? _cts;
    private SetupRunner? _runner;
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _runWatch = new();
    private System.Windows.Threading.DispatcherTimer? _timer;
    /// <summary>What existed before this wizard's first step (first setup only: Cancel removes only what is new since).</summary>
    private SetupSnapshot? _snapshot;
    /// <summary>What the first setup installed before the Windows restart (from the saved resume).</summary>
    private readonly List<RevertTarget> _priorInstalled = new();
    private Task? _runTask;
    /// <summary>Keeps <see cref="Steps"/> in live order while the install runs (never rebuilt, only moved).</summary>
    private readonly SetupStepOrder _order;

    /// <param name="firstSetup">The wizard of the first start (or its resume after the restart): Cancel removes what it installed.</param>
    /// <param name="priorInstalled">First setup resumed after the restart: what it installed before the restart.</param>
    public SetupWizardViewModel(AppServices s, SetupSelection? resume = null, bool firstSetup = false, IEnumerable<RevertTarget>? priorInstalled = null, EzForensics.Core.Runtimes.IRuntimeProbe? probe = null)
    {
        _s = s;
        _probe = probe;
        _order = new SetupStepOrder(Steps);
        _resume = resume;
        FirstSetup = firstSetup;
        if (priorInstalled != null) _priorInstalled.AddRange(priorInstalled);
        foreach (var b in _catalog.Bundles) Bundles.Add(new SetupBundleViewModel(this, b));
        _licensesAccepted = s.Settings.ThirdPartyLicensesAccepted;
        if (resume != null)
        {
            // Reopened after the Windows restart: the pending items (the user's own selection); the install starts after the system check.
            ApplySelection(resume);
            _profile = SetupProfile.Custom;
            _page = SetupPage.Install; // opens on the progress page; the run starts once this computer is checked
            _currentText = "Checking this computer… (the first start of WSL after enabling it can take a few minutes)";
        }
        _ = RunSystemCheckAsync();
    }

    /// <summary>Raised when the window should close (OpenApp = show the main window's Ingest page; RestartingWindows = Windows is restarting).</summary>
    public event Action<SetupWizardResult>? CloseRequested;

    public ObservableCollection<SetupBundleViewModel> Bundles { get; } = new();
    public ObservableCollection<CheckRow> Checks { get; } = new();
    public ObservableCollection<SetupStep> Steps { get; } = new();
    public ObservableCollection<string> PlanNotes { get; } = new();

    public string Version => typeof(SetupWizardViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public string DataRoot => AppPaths.DataRoot;
    public string DataRootHelp => "Settings, downloaded tools and logs live here. The folder is kept when EZ Forensics is updated or uninstalled.";
    public string ToolsRoot => _s.Settings.ToolsRoot;
    /// <summary>Reopened after the Windows restart: straight to the Install page, finishing the pending steps (no choices to make).</summary>
    public bool IsResume => _resume != null;
    /// <summary>
    /// The wizard of the first start (or its resume after the restart). Cancel (button or X, any page but Done) then asks once, stops a
    /// running install and removes what this setup installed (<see cref="SetupRevert"/>); the wizard opens again at the next start.
    /// Otherwise Cancel just closes the wizard.
    /// </summary>
    public bool FirstSetup { get; }

    // ------------------------------------------------------------------ navigation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageName), nameof(PageNumber), nameof(CanGoBack), nameof(NextText), nameof(ShowNext), nameof(ShowBack), nameof(ShowCancel), nameof(ShowOpenApp))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private SetupPage _page = SetupPage.Welcome;

    public string PageName => Page.ToString();
    public int PageNumber => (int)Page + 1;
    public bool ShowBack => Page is not (SetupPage.Welcome or SetupPage.Install or SetupPage.Done);
    /// <summary>On Install, Next ("Finish") appears once a run ended with problems, to reach the Done page.</summary>
    public bool ShowNext => Page switch
    {
        SetupPage.Done => false,
        SetupPage.Install => !IsRunning && _runFinished && !IsReverting,
        _ => true,
    };
    /// <summary>Footer Cancel: every page but Done. A later wizard shows it only while nothing runs (its Install page has its own Cancel).</summary>
    public bool ShowCancel => Page != SetupPage.Done && !IsReverting && (FirstSetup || !IsRunning);
    /// <summary>The Install page's own Cancel (stops the run, finished steps kept, Retry continues): later wizards only.</summary>
    public bool ShowStopInstall => IsRunning && !FirstSetup;
    /// <summary>Done page footer: "Open EZ Forensics" takes the place of Cancel.</summary>
    public bool ShowOpenApp => Page == SetupPage.Done;
    public bool CanGoBack => ShowBack && !IsRunning;
    public string NextText => Page switch
    {
        SetupPage.Welcome => "Get started",
        SetupPage.Summary => Plan is { IsEmpty: true } ? "Finish" : "Install",
        SetupPage.Install => "Finish",
        _ => "Next",
    };

    private bool CanNext() => Page switch
    {
        SetupPage.SystemCheck => !IsChecking,
        SetupPage.Bundles => Bundles.SelectMany(b => b.Items).Any(i => i.IsChecked && i.IsEnabled),
        SetupPage.Summary => Plan != null && (Plan.IsEmpty || LicensesAccepted),
        SetupPage.Install => !IsRunning,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        switch (Page)
        {
            case SetupPage.Welcome: Page = SetupPage.SystemCheck; break;
            case SetupPage.SystemCheck: Page = SetupPage.Bundles; break;
            case SetupPage.Bundles: GoSummary(); break;
            case SetupPage.Summary:
                if (Plan is { IsEmpty: true }) { Finish(); return; }
                _ = InstallAsync();
                break;
            case SetupPage.Install:
                if (Plan != null) ShowDone(Plan);
                break;
        }
    }

    private bool CanBack() => CanGoBack;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        Page = Page switch
        {
            SetupPage.SystemCheck => SetupPage.Welcome,
            SetupPage.Bundles => SetupPage.SystemCheck,
            SetupPage.Summary => SetupPage.Bundles,
            _ => Page,
        };
    }

    /// <summary>Finish / X on a later wizard: remember the wizard was seen, so it never opens by itself again. A running install must be cancelled first.</summary>
    public bool TryClose()
    {
        if (IsRunning || IsReverting) return false;
        _s.Settings.SetupWizardSeen = true;
        try { SaveSettings(); }
        catch (Exception ex) { AppLog.Warn("Setup: saving the wizard-seen flag failed: " + ex.Message); } // never keeps the wizard open
        return true;
    }

    /// <summary>
    /// The window's X. Returns true when the window may close now. First setup (not on Done): the same as Cancel (asks first, reverts,
    /// then closes itself). Done page: like "Open EZ Forensics" without navigating.
    /// </summary>
    public bool OnWindowClosing()
    {
        if (IsReverting) return false;
        if (FirstSetup && Page != SetupPage.Done)
        {
            _ = CancelSetupAsync();
            return false;
        }
        return TryClose();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCancel), nameof(ShowNext), nameof(CanGoBack), nameof(CanRetry))]
    [NotifyCanExecuteChangedFor(nameof(CancelSetupCommand), nameof(RetryCommand), nameof(NextCommand), nameof(BackCommand))]
    private bool _isReverting;

    public const string CancelQuestion = "Tools installed so far will be removed.";

    private bool CanCancelSetup() => !IsReverting;

    /// <summary>
    /// Footer Cancel. First setup: one confirmation, then stop the run, remove what this setup installed, clear the pending resume and
    /// close (the app stays installed; the wizard is not marked seen, so it opens again next time). Later wizards: just close.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelSetup))]
    private async Task CancelSetupAsync()
    {
        if (IsReverting) return;
        if (!FirstSetup)
        {
            if (IsRunning) return; // its Install page's own Cancel stops the run first
            if (TryClose()) CloseRequested?.Invoke(SetupWizardResult.Closed);
            return;
        }
        if (!SetupHost.ConfirmCancel(CancelQuestion)) return;
        IsReverting = true;
        try
        {
            AppLog.Info("First setup cancelled by the user: stopping and removing what it installed.");
            if (IsRunning)
            {
                CurrentText = "Stopping setup…";
                _cts?.Cancel();
                if (_runTask != null) try { await _runTask; } catch (Exception ex) { AppLog.Warn("Setup run ended with " + ex.Message); }
            }
            await RevertAsync();
            SetupHost.ClearResume(_s);
            SetupLauncher.UnregisterResumeAfterRestart(); // also when no resume was saved (RunOnce only)
            _s.Settings.SetupWizardSeen = false; // opens again at the next start
            SaveSettings();
        }
        catch (Exception ex) { AppLog.Error("Reverting the cancelled setup failed", ex); }
        finally { IsReverting = false; }
        CloseRequested?.Invoke(SetupWizardResult.Cancelled);
    }

    /// <summary>Removes what this first setup installed (before and after the restart). Shown on the Install page with its log.</summary>
    private async Task RevertAsync()
    {
        var steps = Plan?.Steps ?? Array.Empty<SetupStep>();
        var targets = SetupRevert.Merge(_priorInstalled, _snapshot != null ? SetupRevert.Targets(steps, RevertPaths, _snapshot) : null);
        AppLog.Info("Setup revert targets: " + (targets.Count == 0 ? "none" : string.Join("; ", targets)));
        if (targets.Count == 0) return;
        Page = SetupPage.Install;
        FailureText = null;
        CurrentText = "Removing what setup installed…";
        OverallProgress = 0;
        AppendLog("=== Cancel: removing what this setup installed ===");
        var dir = Path.Combine(AppPaths.LogsDir, "setup", AppServices.Stamp() + "_revert");
        var r = await SetupRevert.RunAsync(targets, AppPaths.DataRoot,
            ct => EzForensics.Core.Runtimes.EzDistro.RemoveAsync((step, t) => EzForensics.Core.Execution.MaintenanceRunner.RunAsync(_s.Maintenance.Queue, step.ToJobSpec(dir), dir, AppendLog, t), ct: ct),
            AppendLog);
        OverallProgress = 1;
        AppLog.Info($"Setup revert: removed {r.Removed.Count} ({string.Join("; ", r.Removed)}); failed {r.Failed.Count} ({string.Join("; ", r.Failed)})");
        if (r.Failed.Count > 0)
            EzForensics.App.Services.Dialogs.Warn("Some files were left behind", "Close programs using them and delete them by hand, or run setup again.", string.Join("\n", r.Failed.Take(8)));
        try { await _s.RescanAsync(); } catch (Exception ex) { AppLog.Error("Rescan after the setup revert failed", ex); }
    }

    private SetupRevertPaths RevertPaths => SetupRevertPaths.Current(_s.Settings);

    /// <summary>"Open EZ Forensics" on the Done page.</summary>
    [RelayCommand] private void OpenApp() => Finish();

    private void Finish()
    {
        AppLog.Info($"Setup: Finish clicked (page={Page}, running={IsRunning}, reverting={IsReverting}, checking={IsChecking}, resume={IsResume}, listener={CloseRequested != null}).");
        if (!TryClose())
        {
            AppLog.Warn($"Setup: Finish ignored (running={IsRunning}, reverting={IsReverting}).");
            return;
        }
        CloseRequested?.Invoke(SetupWizardResult.OpenApp);
    }

    /// <summary>"Restart now" on the Done page (only when a step waits for the restart). Asks first; Windows restarts immediately.</summary>
    [RelayCommand]
    private void RestartNow()
    {
        if (IsRunning || IsReverting) return;
        // Ask first (owned by the wizard); the pending resume is already registered, so it is re-registered here defensively.
        if (_s.Settings.SetupResume != null) SetupLauncher.RegisterResumeAfterRestart();
        if (SetupHost.RestartWindows(SetupLauncher.OpenWindow)) CloseRequested?.Invoke(SetupWizardResult.RestartingWindows);
    }


    // ------------------------------------------------------------------ system check

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isChecking;

    public SystemCheckResult? Machine { get; private set; }

    [RelayCommand]
    private async Task RunSystemCheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        Checks.Clear();
        Checks.Add(new CheckRow("Checking", "Checking this computer", "Windows, disk, WSL, .NET, Python, internet…", CheckLevel.Info));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var http = _probe == null ? _s.CreateHttpClient() : null;
            var r = await SystemCheck.RunAsync(http, _s.Settings, null, _probe);
            Machine = r;
            Checks.Clear();
            foreach (var row in BuildRows(r)) Checks.Add(row);
            if (_resume == null && Profile != SetupProfile.Custom) ApplyProfile(Profile);
            else if (r.HasNet9) foreach (var i in AllItems.Where(i => IsAlreadyOnMachine(i.Item))) i.IsChecked = true; // shown as installed
            foreach (var i in AllItems) i.RaiseInfo();
            foreach (var b in Bundles) b.Refresh();
            OnPropertyChanged(nameof(Machine));
            NextCommand.NotifyCanExecuteChanged();
            AppLog.Info($"Setup system check ({watch.Elapsed.TotalSeconds:0.0} s): " + string.Join("; ", Checks.Select(c => c.Label + "=" + c.Value)));
        }
        catch (Exception ex)
        {
            AppLog.Error("System check failed", ex);
            Checks.Clear();
            Checks.Add(new CheckRow("Error", "System check", "Failed: " + ex.Message, CheckLevel.Warning));
        }
        finally
        {
            IsChecking = false;
            // After the restart: straight to the Install page with the pending items.
            if (_resume != null && Page == SetupPage.Install && Plan == null)
            {
                if (Machine != null) _ = ResumeInstallAsync();
                else Page = SetupPage.SystemCheck; // the check failed: shown, with Retry; Next leads on as usual
            }
        }
    }

    private static IEnumerable<CheckRow> BuildRows(SystemCheckResult r)
    {
        yield return new CheckRow("Windows", "Windows", r.OsName + (r.Is64Bit ? ", 64-bit" : ", 32-bit (not supported)"),
            r.Is64Bit && r.OsBuild >= 17763 ? CheckLevel.Ok : CheckLevel.Warning,
            r.Is64Bit && r.OsBuild >= 17763 ? null : "EZ Forensics needs 64-bit Windows 10 1809 (build 17763) or later.");
        var free = r.FreeDiskBytes is { } f ? $"{f / 1073741824.0:0.0} GB free" : "unknown";
        yield return new CheckRow("Disk", "Free disk space", free, r.DiskLow(4096) ? CheckLevel.Warning : CheckLevel.Ok,
            $"On {Path.GetPathRoot(r.DataRoot)} (data folder {r.DataRoot}). The recommended set needs a few GB, plus room for cases.");
        yield return new CheckRow("Admin", "Administrator", r.IsAdmin ? "Yes" : "No (not needed)", CheckLevel.Info,
            r.IsAdmin ? "EZ Forensics runs elevated: enabling WSL needs no prompt."
                : "Not needed for the minimal setup. Enabling WSL (Network: Zeek, RITA) asks for administrator permission once (UAC).");
        yield return new CheckRow("Wsl", "WSL", r.Wsl switch
        {
            WslStatus.Enabled => "Enabled",
            WslStatus.NotEnabled => "Not enabled (setup can enable it)",
            WslStatus.NotAvailable => "Not available",
            _ => "Unknown",
        }, r.Wsl == WslStatus.Enabled ? CheckLevel.Ok : CheckLevel.Info, r.Wsl switch
        {
            WslStatus.Enabled => (r.WslDistros.Count > 0 ? "Distros: " + string.Join(", ", r.WslDistros) : "No Linux distro yet") +
                                 $". EZ Forensics Linux environment: {(r.EzDistroState?.Summary ?? "not checked").ToLowerInvariant()}.",
            WslStatus.NotEnabled => "Only needed for Network (Zeek, RITA). Setup enables it with one administrator prompt and one restart.",
            WslStatus.NotAvailable => "This Windows has no WSL, so Network (Zeek, RITA) cannot be installed.",
            _ => null,
        });
        yield return new CheckRow("Net9", ".NET 9 and 10 runtimes", r.HasSystemNet9 ? "Installed" : r.HasPrivateNet9 ? "Private copy" : "Not installed (setup adds a private copy)",
            r.HasNet9 ? CheckLevel.Ok : CheckLevel.Info,
            r.HasNet9 ? "The net9 builds of the EZ tools can run."
                : "Setup installs private .NET 9 and 10 runtimes into EZ Forensics' data folder (no admin, nothing else changes) for " +
                  "the net9 tool builds. Without it, the net4 builds are used (they need only the .NET Framework in Windows).");
        var ai = LocalAiAdvisor.Recommend(r);
        yield return new CheckRow("Ai", "AI assistant", ai.Choice switch
        {
            LocalAiChoice.Local => $"Local AI: {ai.Model}",
            LocalAiChoice.LocalSlow => "Cloud AI recommended",
            _ => "Cloud AI recommended",
        }, ai.RecommendLocal ? CheckLevel.Ok : CheckLevel.Info,
            $"{ai.Reason} ({r.TotalRamBytes / 1073741824.0:0} GB RAM)" +
            (r.Ollama.Running ? $" Ollama is running ({r.Ollama.Models.Count} model(s))." : r.Ollama.Installed ? " Ollama is installed but not running." : ""));
        yield return new CheckRow("Python", "Python for plaso", r.Python ?? "None (a private copy is downloaded)", r.Python != null ? CheckLevel.Ok : CheckLevel.Warning,
            r.Python != null ? null : $"No Python 3.10-3.13 was found: a private Python {PrivatePython.Default.Version} (python.org's official package, SHA-512 checked) is downloaded for plaso and Volatility. No admin, no PATH changes.");
        yield return new CheckRow("Internet", "Internet", r.InternetReachable switch
        {
            true => "Reachable",
            false => "Not reachable",
            _ => "Unknown",
        }, r.InternetReachable == false ? CheckLevel.Warning : CheckLevel.Ok,
            r.InternetReachable == false ? $"{r.InternetError}. Check the proxy in Options." : "download.ericzimmermanstools.com answered.");
        var have = new List<string>();
        if (r.EzToolsInstalled) have.Add($"{r.InstalledZips.Count} EZ tools");
        if (r.PlasoVersion != null) have.Add("plaso " + r.PlasoVersion);
        if (r.EzDistroReady) have.Add("EZ Forensics Linux environment");
        if (r.ZeekSummary != null) have.Add("Zeek");
        yield return new CheckRow("Installed", "Already installed", have.Count > 0 ? string.Join(", ", have) : "Nothing yet", CheckLevel.Info);
    }

    // ------------------------------------------------------------------ bundles

    [ObservableProperty] private SetupProfile _profile = SetupProfile.Everything;

    /// <summary>Profile radios. Changing a tick by hand selects neither (Profile = Custom); there is no "Custom" radio.</summary>
    public bool IsMinimal { get => Profile == SetupProfile.Minimal; set { if (value) ApplyProfile(SetupProfile.Minimal); } }
    public bool IsEverything { get => Profile == SetupProfile.Everything; set { if (value) ApplyProfile(SetupProfile.Everything); } }

    partial void OnProfileChanged(SetupProfile value)
    {
        OnPropertyChanged(nameof(IsMinimal));
        OnPropertyChanged(nameof(IsEverything));
    }

    private IEnumerable<SetupItemViewModel> AllItems => Bundles.SelectMany(b => b.Items);

    public void ApplyProfile(SetupProfile p)
    {
        ApplySelection(SetupSelection.ForProfile(p, _catalog, Machine));
        Profile = p;
    }

    private void ApplySelection(SetupSelection sel)
    {
        _applyingProfile = true;
        try
        {
            // built-in features are always there; so is a runtime the machine already has (shown ticked, not selectable)
            foreach (var i in AllItems)
                i.IsChecked = i.Item.BuiltIn || IsAlreadyOnMachine(i.Item) || (i.IsEnabled && sel.Has(i.Id));
        }
        finally { _applyingProfile = false; }
        foreach (var b in Bundles) b.Refresh();
        NextCommand.NotifyCanExecuteChanged();
    }

    internal void OnItemToggled(SetupItemViewModel item)
    {
        if (!_applyingProfile)
        {
            Profile = SetupProfile.Custom; // the user's own ticks: no profile radio stays selected
            // A net9-only item on a machine without .NET 9 brings the private runtime along (shown right away).
            if (item.IsChecked && item.Item.RequiresNet9 && Machine is { HasNet9: false })
            {
                var dotnet = AllItems.FirstOrDefault(i => i.Id == SetupIds.Dotnet9);
                if (dotnet != null && !dotnet.IsChecked) dotnet.IsChecked = true;
            }
            if (item.IsChecked && item.Item.Requires.Count > 0)
                foreach (var r in AllItems.Where(i => item.Item.Requires.Contains(i.Id) && !i.IsChecked)) r.IsChecked = true;
        }
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The private .NET 9 runtime is pointless when the machine already has .NET 9: shown as installed, not selectable.</summary>
    internal bool IsAlreadyOnMachine(SetupItem item) => item.Id == SetupIds.Dotnet9 && Machine is { HasNet9: true };

    internal string SizeTextFor(SetupItem i)
    {
        if (i.ComingSoon) return "coming soon";
        if (i.BuiltIn) return "built in";
        if (IsAlreadyOnMachine(i)) return "installed";
        var (dl, _) = Size(i);
        return $"≈{SetupPlan.FormatMB(dl)}";
    }

    /// <summary>"Local AI (Ollama + qwen3:8b, ~5 GB download)" once the PC is measured.</summary>
    internal string TitleFor(SetupItem i)
    {
        if (i.Id != SetupIds.LocalAi || Machine == null) return i.Title;
        var (model, gb) = LocalAiAdvisor.ModelToInstall(LocalAiAdvisor.Recommend(Machine));
        return $"Local AI (Ollama + {model}, ~{gb + (Machine.Ollama.Installed ? 0 : 1.3):0.#} GB download)";
    }

    private (int Download, int Disk) Size(SetupItem i) => i.Id switch
    {
        SetupIds.LocalAi when Machine != null => LocalAiSize(Machine),
        SetupIds.Plaso => PlasoSetup.Estimate(needsPython: Machine?.Python == null),
        SetupIds.Zeek => (ZeekSetup.WslDownloadMB, ZeekSetup.WslDiskMB),
        _ => (i.DownloadMB, i.DiskMB),
    };

    private static (int, int) LocalAiSize(SystemCheckResult r)
    {
        var (_, gb) = LocalAiAdvisor.ModelToInstall(LocalAiAdvisor.Recommend(r));
        int dl = (int)(gb * 1024) + (r.Ollama.Installed ? 0 : 1300);
        return (dl, dl * 2);
    }

    internal string? NoteFor(SetupItem i)
    {
        var r = Machine;
        if (r == null) return null;
        return i.Id switch
        {
            SetupIds.LocalAi when !LocalAiAdvisor.Recommend(r).RecommendLocal => "Your PC is better suited to a cloud AI: set it up later in Options → AI",
            SetupIds.LocalAi when r.Ollama.Running => "Ollama is already running",
            SetupIds.Dotnet9 when r.HasNet9 => "Already installed",
            SetupIds.Plaso when r.PlasoVersion != null => "Already installed",
            SetupIds.Zeek when r.ZeekSummary != null => "Already available",
            SetupIds.Rita when r.RitaVersion != null && r.RitaUpdateAvailable => $"Update to {Core.Rita.RitaRelease.Version}",
            SetupIds.Rita when r.RitaVersion != null => "Already installed",
            SetupIds.Zeek or SetupIds.Rita when r.Wsl == WslStatus.NotEnabled => NetworkAdminNote,
            _ => null,
        };
    }

    /// <summary>Network items when WSL still has to be enabled (one UAC prompt, one restart).</summary>
    internal const string NetworkAdminNote = "Admin prompt + restart";

    /// <summary>The "?" of an item: its description plus what it means on this computer, in one plain sentence each.</summary>
    internal string HelpFor(SetupItem i)
    {
        var r = Machine;
        var (dl, disk) = Size(i);
        var extra = r == null ? null : i.Id switch
        {
            SetupIds.LocalAi => LocalAiAdvisor.Recommend(r).Reason,
            SetupIds.Dotnet9 when r.HasNet9 => r.HasSystemNet9 ? ".NET 9 and 10 are already installed on this computer: not needed." : "EZ Forensics' private copy is already installed.",
            SetupIds.Plaso when r.PlasoVersion != null => $"plaso {r.PlasoVersion} is already installed.",
            SetupIds.Plaso when r.Python == null => $"No Python found: a private Python {PrivatePython.Default.Version} is downloaded first.",
            SetupIds.Zeek when r.ZeekSummary != null => "Zeek is already available on this computer.",
            SetupIds.Rita when r.RitaVersion != null => $"RITA {r.RitaVersion} is already installed.",
            SetupIds.Zeek or SetupIds.Rita when r.Wsl == WslStatus.NotEnabled => "Setup enables WSL with one admin prompt, then Windows restarts once.",
            _ => null,
        };
        var size = i.BuiltIn || i.ComingSoon ? "" : $"{Environment.NewLine}≈{SetupPlan.FormatMB(dl)} download, ≈{SetupPlan.FormatMB(disk)} on disk.";
        return i.Description + (extra != null ? Environment.NewLine + extra : "") + size;
    }

    internal string BundleSizeText(SetupBundleViewModel b)
    {
        if (b.ComingSoon) return "coming soon";
        var on = b.Items.Where(i => i.IsChecked && i.IsEnabled && !i.Item.BuiltIn).ToList();
        if (on.Count == 0) return b.Items.Any(i => i.IsChecked) ? "built in" : "";
        return $"≈{SetupPlan.FormatMB(on.Sum(i => Size(i.Item).Download))}";
    }

    private SetupSelection CurrentSelection()
    {
        var s = new SetupSelection { Profile = Profile };
        s.Items.UnionWith(AllItems.Where(i => i.IsChecked && !IsAlreadyOnMachine(i.Item)).Select(i => i.Id));
        // A resumed setup may name the hidden WSL distro item (no check box): kept as is.
        if (_resume != null) s.Items.UnionWith(_resume.Items.Where(id => _catalog.Find(id) is { Hidden: true }));
        // Install is only possible with the licence box on the Summary page ticked.
        return s;
    }

    // ------------------------------------------------------------------ licences (one acceptance, on the Summary page)

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _licensesAccepted;

    [ObservableProperty] private bool _showLicenseList;

    public IReadOnlyList<ThirdPartyLicense> Licenses => ThirdPartyLicenses.All;
    public string LicensesHelp => "EZ Forensics itself is released under the MIT License. It downloads these tools from their publishers during setup (nothing is bundled):" + Environment.NewLine + ThirdPartyLicenses.Text;

    [RelayCommand] private void ToggleLicenseList() => ShowLicenseList = !ShowLicenseList;

    [RelayCommand] private void OpenLicense(ThirdPartyLicense? l) { if (l != null) SetupLauncher.OpenUrl(l.Url); }

    /// <summary>
    /// The user ticked the box and clicked Install: recorded with the date.
    /// </summary>
    private void RecordLicenseAcceptance()
    {
        if (!LicensesAccepted) return;
        if (!_s.Settings.ThirdPartyLicensesAccepted)
        {
            _s.Settings.ThirdPartyLicensesAccepted = true;
            _s.Settings.ThirdPartyLicensesAcceptedUtc = DateTime.UtcNow;
            SaveSettings();
            AppLog.Info("Third-party licence terms accepted by the user (setup wizard) at " + _s.Settings.ThirdPartyLicensesAcceptedUtc.Value.ToString("o"));
        }
    }

    // ------------------------------------------------------------------ summary

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText), nameof(AdminText), nameof(AdminHelp), nameof(HasAdminText), nameof(DiskWarning), nameof(DiskHelp),
        nameof(HasDiskWarning), nameof(NextText), nameof(NotesHelp), nameof(HasNotes))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private SetupPlan? _plan;

    public string TotalText => Plan == null ? "" : Plan.IsEmpty ? "Everything you selected is already installed."
        : $"Download ≈{SetupPlan.FormatMB(Plan.TotalDownloadMB)} · ≈{SetupPlan.FormatMB(Plan.TotalDiskMB)} on disk · {SetupPlan.FormatDuration(Plan.EstimatedDuration)}";
    public bool HasAdminText => Plan is { NeedsAdmin: true };
    public string AdminText => Plan is not { NeedsAdmin: true } ? ""
        : Machine is { IsAdmin: true } ? "WSL is enabled without a prompt (running as administrator)."
        : "Windows asks once for administrator permission (to enable WSL)" + (Plan.NeedsRestart ? ", then one restart." : ".");
    public string AdminHelp => Plan is not { NeedsAdmin: true } ? ""
        : (Machine is { IsAdmin: true } ? "" :
              "The prompt appears right after you click Install; everything else installs without admin. If your account is not an administrator, " +
              "the prompt asks for an administrator's name and password; if you decline, everything except the Linux environment, Zeek and RITA still installs. ") +
          (Plan.NeedsRestart ? "If Windows must restart, setup asks once, at the end. After you sign in again, setup opens by itself and finishes the Linux environment, Zeek and RITA (no further prompt)." : "");
    public string? DiskWarning => Plan != null && Machine != null && Machine.DiskLow(Plan.TotalDiskMB) ? "Low disk space" : null;
    public string? DiskHelp => DiskWarning == null ? null
        : $"{Machine!.FreeDiskBytes / 1073741824.0:0.0} GB free, ≈{SetupPlan.FormatMB(Plan!.TotalDiskMB)} needed plus room for cases.";
    public bool HasDiskWarning => DiskWarning != null;
    public bool HasNotes => PlanNotes.Count > 0;
    public string NotesHelp => string.Join(Environment.NewLine, PlanNotes.Select(n => "• " + n)) +
                               (Plan is { IsEmpty: false } ? $"{Environment.NewLine}Time estimated at about 30 Mbit/s. Tools folder: {ToolsRoot}" : "");

    /// <summary>The selection the current <see cref="Plan"/> was built from (items only), so Retry never reuses a stale plan.</summary>
    private string? _planSelectionKey;

    private void GoSummary()
    {
        BuildPlan();
        Page = SetupPage.Summary;
    }

    private void BuildPlan()
    {
        var sys = Machine ?? new SystemCheckResult();
        var selection = CurrentSelection();
        var plan = SetupPlanner.Plan(_catalog, selection, sys);
        // After the restart: only what is still pending (no built-in or already installed rows from before the restart).
        if (_resume != null) plan = SetupWizardPolicy.PendingOnly(plan);
        _planSelectionKey = SetupWizardPolicy.SelectionKey(selection);
        PlanNotes.Clear();
        foreach (var n in plan.Notes) PlanNotes.Add(n);
        Steps.Clear();
        foreach (var st in plan.Steps) Steps.Add(st);
        _order.Reset();
        Plan = plan;
        AppLog.Info("Setup plan:\n" + plan.ToText());
    }

    // ------------------------------------------------------------------ install

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanRetry), nameof(ShowNext), nameof(ShowCancel), nameof(ShowStopInstall))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(CancelInstallCommand), nameof(RetryCommand), nameof(NextCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _currentText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _logExpanded = true;
    [ObservableProperty] private string _elapsedText = "00:00";
    /// <summary>The step that is running now (it is also the top row of the install list).</summary>
    [ObservableProperty] private SetupStep? _runningStep;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private string? _failureText;

    private bool _runFinished;

    public bool HasFailure => !string.IsNullOrEmpty(FailureText);
    public bool CanRetry => !IsRunning && !IsReverting && Plan is { AnyFailed: true };

    // Done page
    public string DoneHeadline { get; private set; } = "";
    public ObservableCollection<string> DoneInstalled { get; } = new();
    public ObservableCollection<string> DoneAfterRestart { get; } = new();
    public ObservableCollection<string> DoneFailed { get; } = new();
    /// <summary>The Done page offers "Restart now" (a step waits for the restart).</summary>
    public bool ShowRestart { get; private set; }
    public string TotalTimeText => "Total time " + SetupPlan.FormatElapsed(_runWatch.Elapsed);

    private async Task InstallAsync()
    {
        if (Plan == null || IsRunning) return;
        RecordLicenseAcceptance();
        Page = SetupPage.Install;
        await (_runTask = RunPlanAsync());
    }

    /// <summary>
    /// The resumed setup after the restart: plans the pending items and runs them on the Install page. Nothing left: Done. WSL still not
    /// enabled (Windows was not restarted): Done with "Restart now", never a second administrator prompt by itself.
    /// </summary>
    private async Task ResumeInstallAsync()
    {
        BuildPlan();
        var plan = Plan!;
        switch (SetupWizardPolicy.Resume(plan))
        {
            case ResumeAction.Finished:
                AppLog.Info("Setup resume: nothing left to install.");
                SetupHost.ClearResume(_s);
                ShowDone(plan);
                return;
            case ResumeAction.RestartWindows:
                AppLog.Info("Setup resume: WSL is still not enabled; asking for the restart.");
                SetupLauncher.RegisterResumeAfterRestart();
                ShowDone(plan, restartPending: true);
                return;
        }
        AppLog.Info("Setup resume after the restart (wizard): " + string.Join(", ", plan.Selection.Items));
        Page = SetupPage.Install;
        await (_runTask = RunPlanAsync());
    }

    private bool CanCancelInstall() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelInstall))]
    private void CancelInstall()
    {
        _cts?.Cancel();
        CurrentText = "Cancelling…";
    }

    private bool CanRetryInstall() => CanRetry;

    [RelayCommand(CanExecute = nameof(CanRetryInstall))]
    private async Task RetryAsync()
    {
        Page = SetupPage.Install;
        await (_runTask = RunPlanAsync());
    }

    private async Task RunPlanAsync()
    {
        if (Plan == null || SetupWizardPolicy.PlanIsStale(_planSelectionKey, CurrentSelection()))
        {
            AppLog.Info("Setup: the selection changed since the plan was built; planning again.");
            BuildPlan();
        }
        var plan = Plan!;
        IsRunning = true;
        _runFinished = false;
        FailureText = null;
        _cts = new CancellationTokenSource();
        foreach (var st in plan.Steps)
        {
            st.LogLine -= OnStepLog;
            st.LogLine += OnStepLog;
            st.PropertyChanged -= OnStepChanged;
            st.PropertyChanged += OnStepChanged;
        }
        AppendLog($"=== EZ Forensics setup {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        if (FirstSetup && _snapshot == null)
        {
            // Before the first step: what is already there is never removed by Cancel.
            _snapshot = SetupSnapshot.Take(RevertPaths, ezDistroExists: Machine == null || (Machine.EzDistroState?.Exists ?? false));
            AppLog.Info($"Setup snapshot taken ({_snapshot.Existing.Count} existing paths, distro existed: {_snapshot.EzDistroExisted}).");
        }
        _runner = new SetupRunner(new DefaultSetupExecutor(SetupHost.CreateEnvironment(_s)));
        StartClock();
        try
        {
            await _runner.RunAsync(plan, new Progress<SetupRunProgress>(p =>
            {
                OverallProgress = p.Fraction;
                CurrentText = p.CurrentStep != null ? $"{p.CurrentStep} ({Math.Min(p.StepsFinished + 1, p.StepsTotal)} of {p.StepsTotal})" : $"{p.StepsFinished} of {p.StepsTotal} steps finished";
            }), _cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("Setup run failed", ex);
            FailureText = "Setup stopped unexpectedly: " + ex.Message;
        }
        finally
        {
            StopClock();
            RunningStep = null;
            _runFinished = true;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
        await AfterRunAsync(plan);
    }

    private void StartClock()
    {
        _runWatch.Start();
        ElapsedText = SetupPlan.FormatElapsed(_runWatch.Elapsed);
        _timer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnClockTick;
        _timer.Tick += OnClockTick;
        _timer.Start();
    }

    private void StopClock()
    {
        _runWatch.Stop();
        _timer?.Stop();
        ElapsedText = SetupPlan.FormatElapsed(_runWatch.Elapsed); // frozen at the end
        OnPropertyChanged(nameof(TotalTimeText));
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        ElapsedText = SetupPlan.FormatElapsed(_runWatch.Elapsed);
        _runner?.Tick(); // steps without output still move the overall bar
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SetupStep.State) || sender is not SetupStep st) return;
        if (st.State == SetupStepState.Running) RunningStep = st;
        // Live order of the install list (running, queued, finished newest last): moves only, on the UI thread.
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null || ui.CheckAccess()) _order.Update();
        else ui.BeginInvoke(new Action(_order.Update));
    }

    private async Task AfterRunAsync(SetupPlan plan)
    {
        SetupHost.RecordOutcome(_s, plan, FirstSetup,
            FirstSetup ? SetupRevert.Merge(_priorInstalled, _snapshot != null ? SetupRevert.Targets(plan.Steps, RevertPaths, _snapshot) : null) : null);
        try { await _s.RescanAsync(); } catch (Exception ex) { AppLog.Error("Rescan after setup failed", ex); }

        var failed = plan.Steps.Where(s => s.State is SetupStepState.Failed or SetupStepState.ActionNeeded).ToList();
        AppLog.Info($"Setup finished in {SetupPlan.FormatElapsed(_runWatch.Elapsed)}: " + string.Join("; ", plan.Steps.Select(s => $"{s.Id}={s.State}")));
        OnPropertyChanged(nameof(CanRetry));
        RetryCommand.NotifyCanExecuteChanged();
        if (plan.AllSucceeded || (plan.WaitingForRestart && failed.Count == 0)) ShowDone(plan);
        else if (plan.Steps.Any(s => s.State == SetupStepState.Cancelled))
            FailureText = "Setup was cancelled. Retry continues where it stopped; Finish shows what was installed.";
        else if (failed.Count > 0)
            FailureText = SetupWizardPolicy.FailureText(failed);
    }

    /// <summary>Done page: what is installed, what finishes after the restart, what failed.</summary>
    private void ShowDone(SetupPlan plan, bool restartPending = false)
    {
        static string Clean(SetupStep s) => s.Title.Replace(" (after the restart)", "");
        DoneInstalled.Clear();
        DoneAfterRestart.Clear();
        DoneFailed.Clear();
        foreach (var s in plan.Steps.Where(s => s.Kind != SetupStepKind.BuiltIn))
        {
            if (s.State == SetupStepState.Done) DoneInstalled.Add(Clean(s));
            else if (s.State == SetupStepState.AlreadyInstalled) DoneInstalled.Add(Clean(s) + " (already installed)");
            else if (s.State == SetupStepState.WaitingForRestart || restartPending) DoneAfterRestart.Add(Clean(s));
            else DoneFailed.Add($"{Clean(s)}: {s.Message}");
        }
        ShowRestart = plan.WaitingForRestart || restartPending;
        DoneHeadline = DoneFailed.Count > 0 ? "Setup finished with problems." : ShowRestart ? "Restart Windows to finish." : "EZ Forensics is ready.";
        OnPropertyChanged(nameof(DoneHeadline));
        OnPropertyChanged(nameof(ShowRestart));
        OnPropertyChanged(nameof(TotalTimeText));
        Page = SetupPage.Done;
    }

    private void OnStepLog(SetupStep step, string line) => AppendLog(line);

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > 400_000) _log.Remove(0, _log.Length - 300_000);
        LogText = _log.ToString();
    }

    private void SaveSettings() => SetupHost.Save(_s);
}
