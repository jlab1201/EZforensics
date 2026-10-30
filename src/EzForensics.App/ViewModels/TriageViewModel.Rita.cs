using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Execution;
using EzForensics.Core.Rita;
using EzForensics.Core.Triage;
using EzForensics.Core.Zeek;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Ingest network card, RITA part: one RITA analysis per Zeek host folder (the case-wide _Case folder of captures with no identified host included), planned
/// after the Zeek rows and queued after the Zeek jobs of the run (each RITA job waits for them).
/// </summary>
public sealed partial class TriageViewModel
{
    private string? _ritaPlanCaseDir;

    private void InitRita()
    {
        S.RitaChanged += OnRitaChanged;
    }

    /// <summary>Settings → RitaEnabled (also the Tools card): adds / removes the RITA rows of the current plan.</summary>
    public bool RitaEnabled
    {
        get => S.Settings.RitaEnabled;
        set
        {
            if (S.Settings.RitaEnabled == value) return;
            S.Settings.RitaEnabled = value;
            S.SaveSettings();
            S.RaiseRitaChanged();
            if (value && S.Rita == null) _ = EnsureRitaProbedAsync();
        }
    }

    public bool RitaReady => S.Rita?.Installed == true;
    private bool RitaProbing => S.IsProbingRita && S.Rita == null;

    private async Task EnsureRitaProbedAsync()
    {
        if (S.Rita != null || !S.Settings.RitaEnabled) return;
        try { await S.RefreshRitaAsync(); }
        catch (Exception ex) { AppLog.Error("RITA discovery failed", ex); }
    }

    private void RaiseRita()
    {
        foreach (var n in new[] { nameof(RitaEnabled), nameof(RitaReady) })
            OnPropertyChanged(n);
        RefreshAvailability();
    }

    private void OnRitaChanged()
    {
        RaiseRita();
        RefreshRitaRows();
    }

    /// <summary>Zeek options of the Ingest jobs: the additional arguments saved on the Tools page (RITA's come with <see cref="RitaOptions.FromSettings"/>).</summary>
    private ZeekRunOptions IngestZeekOptions() => new() { ExtraArgs = ExtraArgsStore.Args(S.Settings, ExtraArgsStore.Zeek) };

    /// <summary>RITA rows for the planned Zeek rows (none when RITA is off).</summary>
    private IReadOnlyList<PlannedJob> PlanRita(IReadOnlyList<PlannedJob> zeekPlan, string caseDir)
    {
        _ritaPlanCaseDir = caseDir;
        return S.Settings.RitaEnabled ? NetworkTriage.PlanRita(zeekPlan, caseDir, S.Rita?.Selected) : Array.Empty<PlannedJob>();
    }

    /// <summary>
    /// Re-plans the RITA rows of the current plan (RITA switched on / off, installed after the scan). A row keeps the user's tick unless
    /// RITA's install state changed (then the new default applies: ticked when installed).
    /// </summary>
    private void RefreshRitaRows()
    {
        if (_ritaPlanCaseDir == null || Planned.Count == 0 || RefuseWhileRunning("RITA re-plan")) return;
        // the plan order (the list itself is in the live run order)
        var plan = Planned.OrderBy(p => p.Sequence).ToList();
        var zeek = plan.Where(p => p.Planned.Artifact.RuleId == NetworkTriage.RuleId).Select(p => p.Planned).ToList();
        var old = plan.Where(p => NetworkTriage.IsRita(p.Planned)).ToList();
        if (zeek.Count == 0 && old.Count == 0) return;
        var rows = PlanRita(zeek, _ritaPlanCaseDir).Select(IngestDefaults.Apply).ToList();
        var oldByKey = old.GroupBy(o => o.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var o in old)
        {
            o.PropertyChanged -= OnItemChanged;
            Planned.Remove(o);
            plan.Remove(o);
        }
        // After the last Zeek row, else before the first super timeline row (they are always the last ones).
        int at = plan.Count;
        for (int i = 0; i < plan.Count; i++)
            if (plan[i].IsMacbTimeline || plan[i].IsSupertimeline) { at = i; break; }
        for (int i = plan.Count - 1; i >= 0; i--)
            if (plan[i].Planned.Artifact.RuleId == NetworkTriage.RuleId) { at = i + 1; break; }
        foreach (var r in rows)
        {
            var item = new PlannedJobItem(r);
            if (oldByKey.TryGetValue(item.Key, out var prev)) item.IsSelected = prev.WantedSelected;
            item.PropertyChanged += OnItemChanged;
            plan.Insert(at++, item);
            Planned.Add(item);
        }
        AfterPlanChanged(plan);
        RefreshAvailability();
    }

    /// <summary>
    /// Queues the ticked RITA rows after this run's Zeek jobs (<paramref name="zeekRun"/>); returns notes for the rows that were skipped.
    /// </summary>
    private List<string> EnqueueRita(IReadOnlyList<PlannedJobItem> rita, IReadOnlyList<(PlannedJob Planned, Job Job)> zeekRun, string caseDir)
    {
        var notes = new List<string>();
        if (rita.Count == 0) return notes;
        var install = S.Rita?.Selected;
        if (install == null)
        {
            notes.Add($"{rita.Count} RITA job(s) skipped because RITA is not set up yet (set it up in Tools, then run them again).");
            foreach (var r in rita) r.NotQueuedReason = "Not queued: RITA is not set up (Tools).";
            return notes;
        }
        var options = RitaOptions.FromSettings(S.Settings);
        int noZeek = 0;
        foreach (var row in rita)
        {
            var r = row.Planned;
            var jobs = NetworkTriage.ZeekJobsFor(r, zeekRun);
            if (jobs.Count == 0) { noZeek++; row.NotQueuedReason = "Not queued: its Zeek jobs are not part of this run."; continue; }
            try
            {
                TrackRunJob(row, S.Queue.Enqueue(NetworkTriage.ResolveRita(r, install, caseDir, jobs, options), caseDir));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                notes.Add($"{r.Spec.Label}: {ex.Message}");
                row.NotQueuedReason = "Not queued: " + ex.Message;
            }
        }
        if (noZeek > 0) notes.Add($"{noZeek} RITA job(s) skipped: their Zeek jobs are not part of this run (tick the captures' Zeek jobs too).");
        return notes;
    }
}
