using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// What the evidence panel shows on one row: Spectre's own mark (the row is in the finding's evidence) and the examiner's label and note.
/// The two are drawn differently so an analyst label never reads as Spectre's conclusion.
/// </summary>
public sealed record EvidenceRowMark(bool IsSpectre, string? Label, string? LabelBy, string? LabelAt, string? Note, string? NoteBy, string? NoteAt)
{
    public bool HasLabel => EvidenceFlagLabels.IsAnalystLabel(Label);
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool IsNotRelevant => Label == EvidenceFlagLabels.NotRelevant;
    public bool IsEmpty => !IsSpectre && !HasLabel && !HasNote;

    /// <summary>The analyst label's pictogram (Segoe Fluent Icons): check, cross, plus.</summary>
    public string LabelGlyph => Label switch
    {
        EvidenceFlagLabels.Confirmed => "",
        EvidenceFlagLabels.NotRelevant => "",
        EvidenceFlagLabels.Added => "",
        _ => "",
    };

    /// <summary>One line per mark, for the tooltip, the detail strip and screen readers.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (IsSpectre) parts.Add(EvidenceFlagLabels.Text(EvidenceFlagLabels.Evidence));
            if (HasLabel) parts.Add(EvidenceFlagLabels.Text(Label) + Who(LabelBy, LabelAt));
            if (HasNote) parts.Add("Note" + Who(NoteBy, NoteAt) + ": " + Note!.Trim());
            return string.Join("\n", parts);
        }
    }

    private static string Who(string? by, string? at)
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(by)) bits.Add(by.Trim());
        if (UtcText(at) is { } t) bits.Add(t);
        return bits.Count == 0 ? "" : " (" + string.Join(", ", bits) + ")";
    }

    /// <summary>"2026-11-07 10:00 UTC" from an ISO time; the raw text when it does not parse; null when empty.</summary>
    internal static string? UtcText(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : iso.Trim();
    }
}

/// <summary>One evidence file in the panel's picker: the case CSV, its rows behind the finding (once resolved) and its evidence system.</summary>
public sealed partial class EvidencePanelFile : ObservableObject
{
    internal EvidencePanelFile(EvidenceTarget target, string casePath, string? system)
    {
        Target = target; CasePath = casePath; System = system;
    }

    public EvidenceTarget Target { get; }
    public string Csv => Target.Csv;
    public string Name => Path.GetFileName(Target.Csv);
    /// <summary>The path the engine uses for this file (case-relative, forward slashes).</summary>
    public string CasePath { get; }
    /// <summary>The computer the file belongs to, or "Case-wide".</summary>
    public string? System { get; }

    /// <summary>Spectre's rows in the file (resolved against the CSV; explicit rows before that).</summary>
    internal SortedSet<long> SpectreRows { get; set; } = new();
    /// <summary>Rows the examiner added (engine <c>evidence[]</c> with <c>by: "analyst"</c>) that Spectre did not name.</summary>
    internal HashSet<long> AddedRows { get; } = new();
    /// <summary>The finding each Spectre row belongs to (a moment may fold several findings).</summary>
    internal Dictionary<long, string> RowFinding { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText))] private bool _isResolved;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText))] private int _spectreCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText))] private int _analystCount;
    [ObservableProperty] private bool _isCurrent;

    /// <summary>"3 Spectre rows · 1 analyst label".</summary>
    public string CountText
    {
        get
        {
            if (!IsResolved) return "counting…";
            var s = $"{SpectreCount:N0} Spectre row{(SpectreCount == 1 ? "" : "s")}";
            return AnalystCount > 0 ? s + $" · {AnalystCount:N0} analyst label{(AnalystCount == 1 ? "" : "s")}" : s;
        }
    }

    public string AutomationName => $"{Name}, {CountText}";
}

/// <summary>The panel's host (the Findings page): the engine, the case, the examiner and what to refresh after a save.</summary>
/// <summary>One row the examiner labels: the finding it belongs to, its file and its row.</summary>
internal sealed record RowTarget(string FindingId, string CasePath, long Row);

internal interface IEvidencePanelHost
{
    /// <summary>The case analysis store is available (flags can be saved).</summary>
    bool CanFlag { get; }
    string? CaseFolder { get; }
    string ExaminerName { get; }
    /// <summary>The computer of a case-relative CSV path (null when unknown).</summary>
    string? SystemOf(string casePath);
    /// <summary>The examiner's row edits of these findings (removed = not relevant, confirmed, notes).</summary>
    IReadOnlyList<SpectreEvidenceFlag> EvidenceFlagsOf(IReadOnlyCollection<string> findingIds);
    /// <summary>Applies confirmed / not_relevant / added / note / clear to rows of items; saved to the store with history.</summary>
    (bool Ok, string? Reason, IReadOnlyList<SpectreEvidenceFlag> Flags) SetRowLabels(IReadOnlyList<RowTarget> rows, string label, string? note);
    /// <summary>"New finding": a key event in the workspace, synced as an item at once (the same path as Add my analysis).</summary>
    Task<(bool Ok, string? Reason, string? Id)> CreateKeyEventAsync(string title, string severity, string casePath, IReadOnlyList<long> rows, string? note);
    /// <summary>A finding's flags changed: refresh what shows them (card evidence count, Peek).</summary>
    Task FindingFlagsChangedAsync(IReadOnlyCollection<string> findingIds);
    /// <summary>The examiner created a key event: select it.</summary>
    Task FindingCreatedAsync(string findingId);
    /// <summary>The panel's "Open in Analyze" link (the old behaviour).</summary>
    void OpenEvidenceInAnalyze(string caseDir, IReadOnlyList<EvidenceTarget> plan, string title);
}
