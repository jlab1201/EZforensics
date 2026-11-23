using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// One key event: what the engine picked and why, in plain words for the story view and the slides, plus Spectre's short read
/// (loaded the first time the card is opened) and the review state of its main finding.
/// </summary>
public sealed partial class MomentCardViewModel : ObservableObject
{
    public MomentCardViewModel(SpectreMoment moment, SpectreTimelineFinding? primary = null, string? severity = null, IReadOnlyList<string>? techniques = null,
        IReadOnlyDictionary<string, SpectreTimelineFinding>? byId = null)
    {
        _byId = byId;
        Moment = moment;
        Techniques = techniques ?? (string.IsNullOrWhiteSpace(primary?.Technique) ? Array.Empty<string>() : new[] { primary!.Technique! });
        PrimaryFinding = primary;
        Severity = StoryText.SeverityWord(severity ?? primary?.Severity);
    }

    private readonly IReadOnlyDictionary<string, SpectreTimelineFinding>? _byId;

    public SpectreMoment Moment { get; }
    /// <summary>The three lines every timeline card shows: title, time (UTC) and the tactic name (see <see cref="StoryText.CardLines"/>).</summary>
    public IReadOnlyList<string> CardLines => StoryText.CardLines(Moment, _byId ?? (PrimaryFinding is { } f
        ? new Dictionary<string, SpectreTimelineFinding> { [f.Id] = f, [f.FindingId] = f }.Where(kv => kv.Key.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value)
        : new Dictionary<string, SpectreTimelineFinding>()));
    public string CardTime => CardLines[1];
    public string CardTactic => CardLines[2];
    /// <summary>ATT&amp;CK techniques of the moment's findings, the main finding's first (they pick the picture, not the label).</summary>
    public IReadOnlyList<string> Techniques { get; }
    public string TechniqueText => string.Join(", ", Techniques);
    /// <summary>The techniques as Analyze shows them ("T1218 · System Binary Proxy Execution — Defense Evasion"); the stage when there are none.</summary>
    public string MappingText => Techniques.Count > 0 ? Controls.TechniquePicker.DisplayOf(TechniqueText) : Stage;
    /// <summary>The moment's main finding on the timeline (for the technical tooltip and the count); null when the engine did not list it.</summary>
    public SpectreTimelineFinding? PrimaryFinding { get; }
    public string Id => Moment.Id;
    /// <summary>The moment's number: its position in time order (1 = earliest). Spectre's importance is <see cref="Importance"/>.</summary>
    public int Number => Moment.Number;
    /// <summary>Spectre's importance rank (1 = most important); only the Importance order uses it.</summary>
    public int Importance => Moment.Rank;
    /// <summary>True on the first undated card of a time-ordered list: a "No date" divider shows above it.</summary>
    [ObservableProperty] private bool _showNoDateDivider;
    /// <summary>"You" or "Spectre": who made the item behind the card.</summary>
    [ObservableProperty] private string _originText = "";
    public string? PrimaryFindingId => Moment.PrimaryFindingId;
    public bool Pinned => Moment.Pinned;
    public string Headline => !string.IsNullOrWhiteSpace(Moment.Caption) ? Moment.Caption! : Moment.Headline;
    /// <summary>The engine's headline under an examiner caption (empty when there is no caption).</summary>
    public string EngineHeadline => !string.IsNullOrWhiteSpace(Moment.Caption) && Moment.Caption != Moment.Headline ? Moment.Headline : "";
    public string Reason => Moment.Reason ?? "";
    /// <summary>Why Spectre promoted a Spectre key event to the timeline: the "why" line on its card (empty for your own).</summary>
    public string PromotedWhy => OriginText == "Spectre" ? Moment.PromotedReason : "";
    partial void OnOriginTextChanged(string value) => OnPropertyChanged(nameof(PromotedWhy));
    public string TimeText => Moment.Time is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "No time";
    public string FindingsText => Moment.FindingIds.Count switch
    {
        0 => "",
        1 => "1 finding: " + Moment.FindingIds[0],
        var n => $"{n} findings: " + string.Join(", ", Moment.FindingIds.Take(6)) + (n > 6 ? ", …" : ""),
    };
    public string PinText => Pinned ? "Pinned by you" : "";

    // ---- plain words (story view and slides)

    /// <summary>The examiner's caption when there is one, else the engine's plain title (no paths).</summary>
    public string PlainTitle => !string.IsNullOrWhiteSpace(Moment.Caption) ? Moment.Caption!
        : Moment.PlainTitle.Length > 0 ? Moment.PlainTitle : PlainLanguage.TitleWithoutPaths(Moment.Headline);
    public string Why => Moment.Why.Length > 0 ? Moment.Why : Reason;
    public string CantTell => Moment.CantTell ?? "";
    public bool HasCantTell => CantTell.Length > 0;
    public string EvidenceLine => Moment.EvidenceLine;
    public string Phase => Moment.Phase;
    /// <summary>The plain stage on the strip ("Other activity" when unknown) and its pictogram.</summary>
    public string Stage => StoryGraphics.StageOf(Moment.Phase);
    public string StageGlyph => StoryGraphics.Icon(Stage, Techniques);
    /// <summary>Why / what we can't tell / evidence are behind "More".</summary>
    public bool HasMore => Why.Length > 0 || HasCantTell || EvidenceLine.Length > 0;
    /// <summary>"High", "Medium", ... (the word is always shown next to the colour).</summary>
    public string Severity { get; }
    public string WhenText => StoryText.When(Moment.Time, Moment.End);
    public string CountLine => StoryText.CountLine(StoryText.Count(Moment, PrimaryFinding), Moment.Time, Moment.End);

    /// <summary>The technical terms behind the plain words, for a tooltip.</summary>
    public string TechnicalText
    {
        get
        {
            var f = PrimaryFinding;
            var lines = new List<string> { "Engine headline: " + Moment.Headline };
            if (f != null)
            {
                lines.Add("Tactic: " + (string.IsNullOrWhiteSpace(f.Tactic) ? "none" : f.Tactic) + (string.IsNullOrWhiteSpace(f.Technique) ? "" : " · Technique: " + f.Technique));
                lines.Add("Rule: " + f.Title);
                if (StoryText.BaseName(f.SourceFile) is { Length: > 0 } src) lines.Add("Source file: " + src);
                lines.Add($"Finding {f.Id}" + (string.IsNullOrWhiteSpace(f.ArtifactType) ? "" : " · " + f.ArtifactType));
            }
            if (!string.IsNullOrWhiteSpace(Moment.ReasonKind)) lines.Add("Picked because: " + Moment.ReasonKind);
            return string.Join("\n", lines);
        }
    }

    // ---- review state of the main finding (set by the page)

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConfirmed), nameof(NeedsReview), nameof(StatusText), nameof(IsReviewed))] private string? _approval;
    /// <summary>Decided (confirmed or rejected): the card settles faint with a "Reviewed" tag.</summary>
    public bool IsReviewed => !NeedsReview;

    /// <summary>Bumped to play the review stamp once on the card (the view animates it; nothing shows with animations off).</summary>
    [ObservableProperty] private int _stampToken;
    /// <summary>The words on the stamp ("Confirmed", "Not a key event").</summary>
    [ObservableProperty] private string _stampText = "Confirmed";

    public void PlayStamp(string text)
    {
        StampText = text;
        StampToken++;
    }
    public bool IsConfirmed => string.Equals(Approval, SpectreFinding.Approved, StringComparison.OrdinalIgnoreCase);
    public bool NeedsReview => string.IsNullOrWhiteSpace(Approval) || string.Equals(Approval, "PENDING", StringComparison.OrdinalIgnoreCase);
    public string StatusText => IsConfirmed ? "Confirmed" : NeedsReview ? "Needs your review" : "Rejected";

    [ObservableProperty] private bool _isSelected;

    // ---- evidence peek (first rows, read on demand)

    [ObservableProperty] private bool _isPeekOpen;
    [ObservableProperty] private bool _isPeekLoading;
    /// <summary>The header row first, then the evidence rows (each a list of short cells).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPeekRows))] private IReadOnlyList<IReadOnlyList<string>> _peekRows = Array.Empty<IReadOnlyList<string>>();
    [ObservableProperty] private int _peekColumns = 1;
    /// <summary>"Security.csv · 3 of 12 rows", or why nothing can be shown.</summary>
    [ObservableProperty] private string _peekText = "";
    public bool HasPeekRows => PeekRows.Count > 0;
    internal bool PeekLoaded { get; set; }

    internal void SetPeek(EvidencePeek p)
    {
        PeekLoaded = true;
        if (p.IsEmpty)
        {
            PeekRows = Array.Empty<IReadOnlyList<string>>();
            PeekText = "No rows to show here. Show the evidence opens the file.";
            return;
        }
        PeekColumns = Math.Max(1, p.Headers.Count);
        PeekRows = new[] { p.Headers }.Concat(p.Rows).ToList();
        PeekText = $"{p.FileName} · {p.Rows.Count} of {p.TotalRows:N0} row{(p.TotalRows == 1 ? "" : "s")}";
    }
    [ObservableProperty] private bool _isReadLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRead), nameof(WhyText), nameof(WhyIsAi))] private string _readText = "";
    [ObservableProperty] private string _readCites = "";
    /// <summary>The read came from Spectre (not an error message).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WhyText), nameof(WhyIsAi))] private bool _readOk;
    /// <summary>The read was asked for (loaded or failed): it is not asked again unless the examiner refreshes it.</summary>
    public bool ReadRequested { get; set; }
    public bool HasRead => ReadText.Length > 0;

    /// <summary>"Why it matters": Spectre's AI reading when one came back, else the engine's own sentence.</summary>
    public bool WhyIsAi => ReadOk && HasRead;
    public string WhyText => WhyIsAi ? ReadText : Why;
}
