using System.Text;
using EzForensics.Core.Assistant;

namespace EzForensics.App.ViewModels;

/// <summary>What Spectre sees and may propose on the Ingest page: the evidence, the case folder, the planned jobs, and pressing Start.</summary>
public sealed partial class TriageViewModel
{
    private const int AssistantMaxPlanned = 30;

    public override SpectrePage AssistantPage => SpectrePage.Ingest;

    public override string? AssistantState() => OnUi(() =>
    {
        var sb = new StringBuilder();
        sb.Append("Evidence: ").Append(string.IsNullOrWhiteSpace(Source) ? "none chosen" : Source);
        sb.Append("\nCase folder: ").Append(string.IsNullOrWhiteSpace(CaseFolder) ? "(not set)" : CaseFolder);
        if (Planned.Count > 0)
        {
            sb.Append($"\nPlanned jobs ({Planned.Count(p => p.IsSelected)} of {Planned.Count} ticked; [x] = ticked):");
            foreach (var p in Planned.Take(AssistantMaxPlanned))
                sb.Append("\n[").Append(p.IsSelected ? 'x' : ' ').Append("] ").Append(p.Label).Append(p.RequiresAdmin ? " (needs Administrator)" : "")
                  .Append(p.Status.Length > 0 ? " - " + p.Status : "");
            if (Planned.Count > AssistantMaxPlanned) sb.Append($"\n+{Planned.Count - AssistantMaxPlanned} more");
        }
        if (IsStopMode) sb.Append("\nAn ingest is running now (Start is a Stop button).");
        if (Jobs.Rows.Count > 0) sb.Append("\n").Append(Jobs.AssistantState());   // the Jobs list, numbered for jobs_retry / jobs_cancel
        return sb.ToString();
    });

    public override IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host)
    {
        foreach (var t in base.AssistantTools(host)) yield return t;
        foreach (var t in Jobs.AssistantTools(host)) yield return t;
        yield return SpectrePageTools.Create(SpectrePageTools.IngestSelectSourceSpec, host,
            (id, input) =>
            {
                var path = NormalizeSourcePath(input["path"]?.ToString())?.Trim() ?? "";
                var before = OnUi(() => Source);
                return new PageActionProposal(id, "Scan the evidence", path, async () =>
                {
                    if (OnUi(() => IsStopMode)) return "An ingest is running; wait for it or stop it first.";
                    OnUi(() => { Source = path; return 0; });
                    if (!OnUi(() => ScanCommand.CanExecute(null))) return "The evidence cannot be scanned now (an ingest is running or the plan is locked).";
                    await OnUi(() => ScanCommand.ExecuteAsync(null));   // same as the Scan button: detects artifacts and plans the jobs
                    return null;
                }, Undo: () => { OnUi(() => { Source = before; return 0; }); return Task.FromResult<string?>(null); },
                AfterApply: () => "Scan result (jobs are NOT started):\n" + AssistantState());
            },
            input => input["path"]?.ToString() is { Length: > 0 } p && NormalizeSourcePath(p)?.Trim() is { } clean && (File.Exists(clean) || Directory.Exists(clean))
                ? null : $"No such file or folder: {input["path"]}. Ask the examiner for the exact path.");
        yield return SpectrePageTools.Create(SpectrePageTools.IngestStartSpec, host,
            (id, _) => OnUi(() => new PageActionProposal(id, $"Start the ingest ({Planned.Count(p => p.IsSelected)} jobs)", Source ?? "", () =>
            {
                if (IsStopMode) return Task.FromResult<string?>("An ingest is already running.");
                if (!RunSelectedCommand.CanExecute(null)) return Task.FromResult<string?>("Nothing to start: choose evidence and tick at least one planned job.");
                RunSelectedCommand.Execute(null);   // same as the Start button, including its Administrator prompt
                return Task.FromResult<string?>(null);
            }, AutoAllowed: SpectrePageTools.AsksToStart(host.Question))),   // auto-mode presses Start only when the examiner's own words asked for it
            _ => OnUi(() => IsStopMode ? "An ingest is already running." : !RunSelectedCommand.CanExecute(null) ? "Nothing to start yet: no evidence chosen or no planned job ticked. Tell the examiner what is missing." : null));
    }
}
