using EzForensics.Core.Common;
using EzForensics.App.Services;
using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>What a distill run is about to do: used to word the confirmations (one question for the whole selection).</summary>
public sealed record DistillPlan(int Documents, int Sections, int PendingNotes, int PendingDocuments, int AcceptedDocuments);

/// <summary>The AI actions the Inbox and the Library share: the runner (busy, progress, cancel, errors), labelling and distilling.</summary>
public static class KnowledgeAiRun
{
    public const string NoAiHint = "Use local AI (Tools → AI)";
    private static int _runs;

    /// <summary>How many runs went through <see cref="RunAsync"/> (tests check that both view models use it).</summary>
    internal static int RunCount => Volatile.Read(ref _runs);

    /// <summary>
    /// Runs one AI action: reads the AI settings, makes the cancellation source (handed to <paramref name="setCts"/> so Cancel works),
    /// reports "title done/total" through <paramref name="setProgress"/>, and turns cancel and errors into a message. Call it on the UI thread
    /// (the progress callbacks are marshalled to it). The caller owns IsBusy. Severity is null when nothing should be announced.
    /// </summary>
    public static async Task<(string Message, InfoSeverity? Severity)> RunAsync(KnowledgeWorkspace ws, string title,
        Func<(KnowledgeCompletion? Complete, string Model)> ai, Action<string> setProgress, Action<CancellationTokenSource?> setCts,
        Func<KnowledgeCompletion, string, IProgress<(int done, int total)>, CancellationToken, Task<(string Message, bool Problem)>> work)
    {
        var (complete, model) = ai();
        if (complete == null) return ("", null);
        Interlocked.Increment(ref _runs);
        var cts = new CancellationTokenSource();
        setCts(cts);
        setProgress(title + "…");
        var progress = new Progress<(int done, int total)>(p => setProgress($"{title} {p.done}/{p.total}"));
        try
        {
            var (msg, problem) = await work(complete, model, progress, cts.Token);
            return (msg, problem ? InfoSeverity.Warning : InfoSeverity.Success);
        }
        catch (OperationCanceledException) { ws.NotifyChanged(); return ("Cancelled.", null); }
        catch (Exception ex)
        {
            AppLog.Error(title + " failed", ex);
            return (title + " failed: " + ex.Message, InfoSeverity.Error);
        }
        finally { setCts(null); cts.Dispose(); }
    }

    /// <summary>Labels the notes (summary, category, type, tags). Returns the message to show.</summary>
    public static async Task<(string Message, bool Problem)> LabelAsync(KnowledgeWorkspace ws, KnowledgeCompletion complete, string model,
        IReadOnlyList<long> ids, IProgress<(int done, int total)> progress, CancellationToken ct)
    {
        var r = await ws.UseAsync(s => new KnowledgeLabeler(complete, model).LabelAsync(s.Store, ids, progress, ct), changes: true, embed: false);
        return r.Failed == 0 ? ($"Labelled {r.Labelled}.", false) : ($"Labelled {r.Labelled}, {r.Failed} failed (use Retry AI).", true);
    }

    /// <summary>Calls the labelling of N documents takes (6 per call).</summary>
    public static int LabelCalls(int documents) => (documents + KnowledgeLabeler.BatchSize - 1) / KnowledgeLabeler.BatchSize;

    public static string LabelConfirmText(int documents, string what = "unlabelled") =>
        $"Label {Plural.Of(documents, what + " document")} (about {Plural.Of(LabelCalls(documents), "AI call")})?";

    // ---- distil

    /// <summary>Counts the sections (AI calls), pending notes and accepted notes of the documents, for the confirmations.</summary>
    public static Task<DistillPlan> PlanDistillAsync(KnowledgeWorkspace ws, IReadOnlyList<long> ids) => ws.RunAsync(s =>
    {
        int sections = 0, pendingNotes = 0, pendingDocs = 0, acceptedDocs = 0;
        foreach (var id in ids)
        {
            sections += KnowledgeDistiller.SectionCount(s.Store.NoteBody(id));
            var kids = s.Store.Children(id).Select(k => s.Store.Meta(k)?.Status).ToList();
            var pending = kids.Count(k => k == KnowledgeIntake.Pending);
            if (pending > 0) { pendingDocs++; pendingNotes += pending; }
            if (kids.Any(k => k == KnowledgeIntake.Accepted)) acceptedDocs++;
        }
        return new DistillPlan(ids.Count, sections, pendingNotes, pendingDocs, acceptedDocs);
    });

    public static string DistillConfirmText(DistillPlan p) =>
        $"Distill {Plural.Of(p.Documents, "document")} (about {Plural.Of(p.Sections, "AI call")})?" + (p.AcceptedDocuments > 0 ? $" {p.AcceptedDocuments} already {(p.AcceptedDocuments == 1 ? "has" : "have")} notes." : "");

    public static string ReplaceConfirmText(DistillPlan p) => $"Replace {Plural.Of(p.PendingNotes, "pending note")} from {Plural.Of(p.PendingDocuments, "document")}?";

    /// <summary>
    /// Distils the documents into single-idea pending notes. Asks at most two questions for the whole selection: the start
    /// ("Distill M documents (about S AI calls)?", always when <paramref name="alwaysConfirmStart"/>, else only for several documents) and,
    /// when some already have pending notes, one "Replace X pending notes from Y documents?". Declining the second skips those documents.
    /// </summary>
    public static async Task<(string Message, bool Problem)> DistillAsync(KnowledgeWorkspace ws, KnowledgeCompletion complete, string model,
        IReadOnlyList<long> ids, Func<string, bool> confirmStart, Func<string, bool> confirmReplace, bool alwaysConfirmStart,
        IProgress<(int done, int total)> progress, CancellationToken ct)
    {
        if (ids.Count == 0) return ("Nothing to distil.", false);
        var plan = await PlanDistillAsync(ws, ids);
        if ((alwaysConfirmStart || ids.Count > 1) && !confirmStart(DistillConfirmText(plan))) return ("Nothing distilled.", false);
        var replace = plan.PendingDocuments > 0 && confirmReplace(ReplaceConfirmText(plan));

        var distiller = new KnowledgeDistiller(complete, model);
        var created = 0; var failedSections = 0; var done = 0; var errors = 0; var skipped = 0;
        await ws.UseAsync(async s =>
        {
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!replace && distiller.PendingChildren(s.Store, id) > 0) { skipped++; }
                    else
                    {
                        var r = await distiller.DistillAsync(s.Store, s.Importer, id, replace, null, ct);
                        created += r.Created; failedSections += r.FailedSections;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors++; AppLog.Warn("Distill failed: " + ex.Message); }
                progress.Report((++done, ids.Count));
            }
            return 0;
        }, changes: true, embed: true);
        var msg = $"Distilled {Plural.Of(created, "note")} from {Plural.Of(ids.Count - errors - skipped, "document")}";
        if (skipped > 0) msg += $", {skipped} skipped (pending notes kept)";
        var bad = failedSections + errors;
        return bad > 0 ? ($"{msg} ({bad} section(s) or documents failed).", true) : (msg + ".", false);
    }
}
