using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Analysis;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>One finding inside a key event, as the key event editor lists it (each with Remove).</summary>
public sealed record KeyEventFindingLine(string KeyEventId, string ItemId, string Id, string Title, string Severity);

/// <summary>One row of the Key events list: effective time, severity, title, Analyst and how many findings it holds.</summary>
public sealed partial class KeyEventRowViewModel : ObservableObject
{
    public KeyEventRowViewModel(CaseAnalysis a, KeyEvent k) { KeyEvent = k; Values = KeyEventOps.Effective(a, k); Fill(a, k); }

    public KeyEvent KeyEvent { get; private set; }
    /// <summary>"Add to key event ▸": puts the selected findings into this key event (set by the page).</summary>
    public CommunityToolkit.Mvvm.Input.IRelayCommand? AddHereCommand { get; set; }
    public KeyEventValues Values { get; private set; }
    public string Id => KeyEvent.Id;
    public string Title => Values.Title;
    public string Severity => Values.Severity;
    public DateTimeOffset? Time => Values.Time;
    public string TimeText => Values.Time is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "No time";
    public string OriginText { get; private set; } = "";
    public int FindingCount => KeyEvent.FindingIds.Count;
    public string FindingCountText => FindingCount == 1 ? "1 finding" : $"{FindingCount} findings";
    public string MetaLine => string.Join(" · ", new[] { FindingCountText, TimeText }.Where(x => x.Length > 0));
    /// <summary>The mapping as Analyze shows it ("T1218 · System Binary Proxy Execution — Defense Evasion"); empty when unmapped.</summary>
    public string MappingText => Controls.TechniquePicker.DisplayOf(string.Join(", ", Values.Mitre));
    public bool HasMapping => MappingText.Length > 0;
    public bool IsEdited => KeyEvent.EditedFields.Count > 0;
    public bool IsRemovedAtSource => KeyEvent is { SourceStatus: SourceStatus.RemovedAtSource, KeptByUser: false };
    /// <summary>"Finding X is already in key event Y" for each finding a Spectre moment wanted but another key event holds; the removed-at-source chip.</summary>
    public IReadOnlyList<ItemChip> Chips { get; private set; } = Array.Empty<ItemChip>();
    public bool HasChips => Chips.Count > 0;
    public string ChipText => string.Join(" · ", Chips.Select(c => c.Text));

    /// <summary>Takes the fresh state of the same key event (after an edit), so the row keeps its identity and the selection stays.</summary>
    public void Update(CaseAnalysis a, KeyEvent k)
    {
        KeyEvent = k; Values = KeyEventOps.Effective(a, k); Fill(a, k);
        OnPropertyChanged(string.Empty);
    }

    private void Fill(CaseAnalysis a, KeyEvent k)
    {
        OriginText = FindingsViewModel.OriginWord(AnalysisView.OriginOf(a, k));
        var chips = new List<ItemChip>();
        foreach (var id in k.SkippedFindingIds)
        {
            var f = a.Find(id);
            var other = KeyEventOps.KeyEventOf(a, id);
            if (f == null || other == null || other == k) continue;
            chips.Add(new ItemChip(ItemChip.InKeyEvent, $"Finding {f.Title} is already in key event {KeyEventOps.Effective(a, other).Title}"));
        }
        if (IsRemovedAtSource) chips.Add(new ItemChip(ItemChip.Removed, "No longer reported by Spectre"));
        Chips = chips;
    }
}
