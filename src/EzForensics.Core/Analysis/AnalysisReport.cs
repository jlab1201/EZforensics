using System.Globalization;
using System.Text;

namespace EzForensics.Core.Analysis;

/// <summary>The case report as Markdown, built from the analysis store alone (no LLM text): key events with their findings, then other confirmed findings; never rejected findings or duplicates.</summary>
public static class AnalysisReport
{
    public sealed record Result(string Markdown, int Included, int Excluded);

    public static Result Build(CaseAnalysis a, string caseName, DateTimeOffset now)
    {
        var events = AnalysisView.ReportKeyEvents(a);
        var appendix = AnalysisView.AppendixFindings(a);
        var sb = new StringBuilder();
        sb.Append("# Key events report: ").AppendLine(string.IsNullOrWhiteSpace(caseName) ? "case" : caseName.Trim());
        sb.AppendLine();
        sb.Append("Generated ").Append(Utc(now)).Append(" by ").AppendLine(CurrentIdentity.Name);
        sb.AppendLine();
        sb.Append(events.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(events.Count == 1 ? " key event." : " key events.");
        for (int n = 0; n < events.Count; n++)
        {
            var k = events[n];
            var e = KeyEventOps.Effective(a, k);
            sb.AppendLine().Append("## ").Append(n + 1).Append(". ").AppendLine(Clean(e.Title));
            sb.AppendLine();
            Line(sb, "Severity", e.Severity);
            var tech = string.Join(", ", e.Mitre);
            if (!string.IsNullOrEmpty(e.Tactic) || tech.Length > 0)
                Line(sb, "Tactic / technique", string.Join(" / ", new[] { e.Tactic, tech }.Where(s => !string.IsNullOrEmpty(s))));
            Line(sb, "Time (UTC)", e.Time != null ? Utc(e.Time.Value) : "No time");
            Line(sb, "Origin", OriginText(AnalysisView.OriginOf(a, k)));
            if (!string.IsNullOrWhiteSpace(k.PromotedReason)) Line(sb, "Why it matters", k.PromotedReason!);
            if (!string.IsNullOrWhiteSpace(k.Notes)) { sb.AppendLine().AppendLine("**Notes**").AppendLine(); sb.AppendLine(k.Notes.Trim()); }
            var fs = k.FindingIds.Select(a.Find).Where(f => f != null && AnalysisView.IsActive(a, f) && AnalysisView.ReviewOf(f) != ReviewState.Rejected).Select(f => f!).ToList();
            if (fs.Count > 0)
            {
                sb.AppendLine().AppendLine("**Findings**").AppendLine();
                foreach (var f in fs) sb.Append("- ").AppendLine(Clean(f.Title));
            }
        }
        if (appendix.Count > 0)
        {
            sb.AppendLine().AppendLine("## Other confirmed findings").AppendLine();
            foreach (var f in appendix)
                sb.Append("- ").Append(f.Severity).Append(": ").Append(Clean(f.Title)).Append(" (").Append(OriginText(AnalysisView.OriginOf(a, f))).AppendLine(")");
        }
        int rejected = a.Items.Count(i => !i.Dismissed && !i.IsHidden && AnalysisView.ReviewOf(i) == ReviewState.Rejected);
        return new Result(sb.ToString(), events.Count, rejected);
    }

    internal static string OriginText(DisplayOrigin o) => o switch { DisplayOrigin.Spectre => "Spectre", DisplayOrigin.Both => "Analyst and Spectre", _ => "Analyst" };

    private static void Line(StringBuilder sb, string k, string v) => sb.Append("- **").Append(k).Append(":** ").AppendLine(Clean(v));
    private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
    private static string Utc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
