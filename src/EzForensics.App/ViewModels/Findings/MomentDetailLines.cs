using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// The hover text of a key event on the Timeline and the Attack flow: what the card does not already show (the card has the title, the time and
/// the tactic). Only fields that have a value are listed, so the text stays at six lines or so. Wording states what the data shows and never
/// claims that one event caused another.
/// </summary>
public static class MomentDetailLines
{
    public const int MaxLineChars = 160;

    /// <summary>"Confirmed", "Rejected" or "Needs your review" for an approval value (null or PENDING needs a review).</summary>
    public static string StatusWord(string? approval) =>
        string.Equals(approval, SpectreFinding.Approved, StringComparison.OrdinalIgnoreCase) ? "Confirmed"
        : string.Equals(approval, SpectreFinding.Rejected, StringComparison.OrdinalIgnoreCase) ? "Rejected"
        : "Needs your review";

    /// <param name="approval">The review state when the page knows it (it wins over the timeline's); else the main finding's.</param>
    public static IReadOnlyList<string> Build(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId, string? approval = null)
    {
        var f = StoryText.Primary(m, byId);
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var v = value.Trim();
            if (v.Length > MaxLineChars) v = v[..(MaxLineChars - 1)] + "…";
            lines.Add(label.Length == 0 ? v : label + ": " + v);
        }

        Add("", m.Why.Length > 0 ? m.Why : m.Reason);
        Add("What we can't tell", m.CantTell);

        var techniques = StoryGraphics.MomentTechniques(m, byId);
        if (techniques.Count == 0 && !string.IsNullOrWhiteSpace(f?.Technique)) techniques = new[] { f!.Technique! };
        if (techniques.Count > 0)
        {
            var t = techniques[0];
            var name = TechniqueCatalog.Name(t);
            Add("Technique", name == null ? t : $"{t} {name}");
        }

        var status = StatusWord(approval ?? f?.Approval);
        Add("", $"{StoryText.SeverityWord(StoryText.Severity(m, byId))} severity · {status}");
        Add("Evidence", m.EvidenceLine.Length > 0 ? m.EvidenceLine : BuildSource(f));
        Add("Computer", f?.Host);

        int count = m.FindingIds.Count;
        var idText = f == null ? "" : (f.FindingId.Length > 0 ? f.FindingId : f.Id);
        if (idText.Length > 0 || count > 1)
            Add("", (idText.Length > 0 ? "Finding " + idText : "") + (idText.Length > 0 && count > 1 ? " · " : "") + (count > 1 ? $"{count} findings in this event" : ""));
        return lines;
    }

    private static string BuildSource(SpectreTimelineFinding? f)
    {
        if (f == null) return "";
        var file = StoryText.BaseName(f.SourceFile);
        if (file.Length > 0 && !string.IsNullOrWhiteSpace(f.ArtifactType)) return $"{f.ArtifactType} ({file})";
        return file.Length > 0 ? file : f.ArtifactType ?? "";
    }
}
