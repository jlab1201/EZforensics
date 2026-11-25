using EzForensics.Core.Settings;

namespace EzForensics.Core.Setup;

/// <summary>How the app starts.</summary>
public enum StartupMode
{
    /// <summary>Main window only (setup stays available from the menu; the Ingest page shows "Tools not installed" when needed).</summary>
    MainWindow,
    /// <summary>Only the setup wizard; the main window opens when it closes (the very first start).</summary>
    WizardFirst,
    /// <summary>
    /// The setup wizard alone, on its Install page: it finishes the steps that waited for the Windows restart (distro, Zeek, RITA)
    /// with the same progress UI, then shows Done; the main window opens when it closes. Also the mode of the RunOnce
    /// <c>--resume-setup</c> launch.
    /// </summary>
    ResumeWizard,
}

/// <summary>What the resumed wizard does with the plan of the pending items.</summary>
public enum ResumeAction
{
    /// <summary>Nothing left to install: clear the pending resume.</summary>
    Finished,
    /// <summary>WSL is still not enabled (Windows was not restarted yet): ask for the restart, never re-run the admin step by itself.</summary>
    RestartWindows,
    /// <summary>Run the remaining steps (no administrator prompt needed).</summary>
    Run,
}

/// <summary>When the setup wizard opens by itself at startup, and how a setup interrupted by the restart continues.</summary>
public static class SetupWizardPolicy
{
    /// <summary>
    /// Decided before any window exists: a pending resume reopens the wizard on its Install page (before the main window); otherwise
    /// the wizard opens by itself only on the genuine first start (never again once it was finished; a cancelled first setup opens it
    /// again; the menu opens it any time).
    /// </summary>
    public static StartupMode Startup(AppSettings settings)
    {
        if (settings.SetupResume != null) return StartupMode.ResumeWizard;
        if (!settings.SetupWizardSeen && !settings.SetupWizardDontShow) return StartupMode.WizardFirst;
        return StartupMode.MainWindow;
    }

    public static SetupResumeState ToResume(SetupSelection s) => new()
    {
        Items = s.Items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList(),
        SavedUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Only what still has to run after the restart (the steps waiting for it: enabling WSL's item, the distro, Zeek, RITA), so
    /// a resumed run does not redo finished work. Null when nothing waits for a restart.
    /// </summary>
    public static SetupResumeState? PendingAfterRestart(SetupPlan plan)
    {
        var items = plan.Steps.Where(s => s.State == SetupStepState.WaitingForRestart && !string.IsNullOrEmpty(s.ItemId))
            .Select(s => s.ItemId!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        if (items.Count == 0) return null;
        return new SetupResumeState { Items = items, SavedUtc = DateTime.UtcNow };
    }

    /// <summary>
    /// A resumed setup runs by itself unless it would need the administrator prompt again (WSL still not enabled: Windows was not
    /// restarted yet), in which case the user is asked to restart Windows.
    /// </summary>
    public static ResumeAction Resume(SetupPlan plan) =>
        plan.IsEmpty ? ResumeAction.Finished : plan.NeedsAdmin ? ResumeAction.RestartWindows : ResumeAction.Run;

    /// <summary>
    /// The resumed wizard's plan: only the steps still to do (the pending items' steps, and any step they still need that is not
    /// done yet). Built-in features and already installed steps finished before the restart, so they are left out of the install
    /// list and the Done page. The runner is unaffected: such steps are never run, and no remaining step depends on them.
    /// </summary>
    public static SetupPlan PendingOnly(SetupPlan plan) => new()
    {
        Selection = plan.Selection,
        Steps = plan.Steps.Where(s => s.Kind != SetupStepKind.BuiltIn && !s.Succeeded).ToList(),
        Notes = plan.Notes,
        EzNetVersion = plan.EzNetVersion,
    };

    /// <summary>
    /// The selection of a pending resume. Licences are never carried over: a resumed selection relies on the acceptance stored in the
    /// settings. Profile = Custom (the user's own selection).
    /// </summary>
    public static SetupSelection FromResume(SetupResumeState r)
    {
        var s = new SetupSelection { Profile = SetupProfile.Custom };
        s.Items.UnionWith(r.Items);
        return s;
    }

    /// <summary>The selection a plan was built from (item ids only, order and case ignored).</summary>
    public static string SelectionKey(SetupSelection s) =>
        string.Join("|", s.Items.Select(i => i.ToLowerInvariant()).OrderBy(i => i, StringComparer.Ordinal));

    /// <summary>True when the ticks changed since the plan was built: Retry / Install must plan again instead of running the old plan.</summary>
    public static bool PlanIsStale(string? planKey, SetupSelection current) => planKey == null || planKey != SelectionKey(current);

    public const string FailureHint = "Fix it and click Retry, or Finish.";

    /// <summary>
    /// The Install page's failure box: one line per failed step ("Title: whole first line of its message", never cut: the box wraps
    /// and scrolls, and a cut message hid the log path), then <see cref="FailureHint"/>.
    /// </summary>
    public static string FailureText(IEnumerable<SetupStep> failed)
    {
        var lines = failed.Select(s =>
        {
            var title = s.Title.Replace(" (after the restart)", "");
            var why = FirstLine(s.Message);
            return why.Length > 0 ? $"{title}: {why}" : $"{title}: failed.";
        });
        return string.Join(Environment.NewLine, lines.Append(FailureHint));
    }

    private static string FirstLine(string? text) => (text ?? "").Split(LineBreaks, 2, StringSplitOptions.TrimEntries)[0];

    private static readonly char[] LineBreaks = { '\r', '\n' };
}
