using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.Views;
using EzForensics.Core.Execution;
using EzForensics.Core.Persistence;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Triage: persistence (autostart entries of the evidence). The built-in EZ Forensics job is always planned for every Windows system
/// root. All logic lives in Core (<see cref="PersistenceTriage"/>); this only wires it to the page.
/// </summary>
public sealed partial class TriageViewModel
{
    private IReadOnlyList<string> _persistenceRoots = Array.Empty<string>();

    private void InitPersistence()
    {
    }

    public bool HasPersistence => _persistenceRoots.Count > 0;

    private async Task UpdatePersistenceRootsAsync(SourceLayout layout, bool isFile)
    {
        _persistenceRoots = isFile ? Array.Empty<string>() : await Task.Run(() => PersistenceTriage.SystemRoots(layout));
        OnPropertyChanged(nameof(HasPersistence));
    }

    /// <summary>Built-in persistence job(s) for every detected Windows system (an unchecked system's rows are only excluded).</summary>
    private IReadOnlyList<PlannedJob> PlanPersistence(string caseDir)
    {
        var exe = Environment.ProcessPath;
        var roots = _persistenceRoots;
        if (roots.Count == 0 || string.IsNullOrEmpty(exe)) return Array.Empty<PlannedJob>();
        // Same host names as the artifact jobs (checked systems with the edited labels), so each host gets one <case>\<Host>\Persistence folder.
        var labelled = LabelledSystems();
        var systems = roots
            .Select(r => labelled.FirstOrDefault(s => string.Equals(s.RootPath, r, StringComparison.OrdinalIgnoreCase)) ?? ArtifactScanner.SystemFor(r, readHive: false))
            .ToList();
        return PersistenceTriage.Plan(systems, caseDir, exe);
    }

    /// <summary>Replaces the persistence rows of the plan (other rows and the user's ticks are kept).</summary>
    private void RefreshPersistencePlan()
    {
        var caseDir = CaseFolder.Trim();
        if (caseDir.Length == 0 || RefuseWhileRunning("persistence re-plan")) return;
        var keep = Planned.OrderBy(p => p.Sequence).Where(p => p.Planned.Artifact.RuleId != PersistenceTriage.RuleId).ToList();
        var ticks = WantedTicks();
        var fresh = PlanPersistence(caseDir).Select(IngestDefaults.Apply).ToList();
        var items = keep.Select(k => (k.Planned, k.WantedSelected)).ToList();
        int insertAt = items.FindIndex(i => i.Planned.Artifact.RuleId == NetworkTriage.RuleId);
        var persistence = fresh.Select(p =>
        {
            var key = p.Artifact.RuleId + "|" + p.Artifact.MatchedPath + "|" + p.Spec.Label;
            bool sel = ticks.TryGetValue(key, out var was) ? was : p.SelectedByDefault;
            return (p, sel);
        });
        if (insertAt < 0) items.AddRange(persistence); else items.InsertRange(insertAt, persistence);
        SetPlan(items);
    }
}
