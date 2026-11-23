using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace EzForensics.Core.Analysis;

/// <summary>One block of the report's walk-through: one key event in story order.</summary>
public sealed record WalkthroughStep(DateTimeOffset? Time, string Title, string Text, string Technique, string Severity);

/// <summary>
/// The case report as ONE self-contained HTML file: inline CSS, no scripts, no external URLs, A4 print layout. Everything that comes
/// from the case is untrusted evidence data: it is HTML-encoded, and Markdown text goes through Markdig with raw HTML disabled and
/// with every link turned into its plain label.
/// </summary>
public static class ReportHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>The report from the analysis store (same content as <see cref="AnalysisReport"/>), plus the walk-through.</summary>
    public static string Render(CaseAnalysis a, string caseName, DateTimeOffset now, IReadOnlyList<WalkthroughStep>? steps = null)
    {
        var events = AnalysisView.ReportKeyEvents(a);
        var appendix = AnalysisView.AppendixFindings(a);
        steps ??= StepsFromAnalysis(a);
        var sb = Head(caseName, now);
        sb.Append("<p class=\"lead\">").Append(events.Count.ToString(CultureInfo.InvariantCulture)).Append(events.Count == 1 ? " key event." : " key events.").Append("</p>\n");
        if (events.Count > 0) sb.Append("<h2>Key events</h2>\n");
        for (int n = 0; n < events.Count; n++)
        {
            var k = events[n];
            var e = KeyEventOps.Effective(a, k);
            sb.Append("<section class=\"ev\">\n<h3>").Append(n + 1).Append(". ").Append(E(e.Title)).Append("</h3>\n<p class=\"meta\">");
            sb.Append(Pill(e.Severity)).Append(" <span>").Append(E(e.Time != null ? Utc(e.Time.Value) : "No time")).Append("</span>");
            var tech = string.Join(", ", e.Mitre);
            var map = string.Join(" / ", new[] { e.Tactic, tech }.Where(s => !string.IsNullOrEmpty(s)));
            if (map.Length > 0) sb.Append(" <span>").Append(E(map)).Append("</span>");
            sb.Append(" <span>Origin: ").Append(E(AnalysisReport.OriginText(AnalysisView.OriginOf(a, k)))).Append("</span></p>\n");
            if (!string.IsNullOrWhiteSpace(k.PromotedReason)) sb.Append("<p><b>Why it matters:</b> ").Append(E(k.PromotedReason)).Append("</p>\n");
            if (!string.IsNullOrWhiteSpace(k.Notes)) sb.Append("<div class=\"notes\"><b>Notes</b>").Append(Md(k.Notes)).Append("</div>\n");
            var fs = k.FindingIds.Select(a.Find).Where(f => f != null && AnalysisView.IsActive(a, f) && AnalysisView.ReviewOf(f) != ReviewState.Rejected).Select(f => f!).ToList();
            if (fs.Count > 0)
            {
                sb.Append("<p><b>Findings</b></p>\n<ul>\n");
                foreach (var f in fs) sb.Append("<li>").Append(E(f.Title)).Append("</li>\n");
                sb.Append("</ul>\n");
            }
            sb.Append("</section>\n");
        }
        if (appendix.Count > 0)
        {
            sb.Append("<h2 class=\"newpage\">Other confirmed findings</h2>\n<ul>\n");
            foreach (var f in appendix)
                sb.Append("<li>").Append(Pill(f.Severity)).Append(' ').Append(E(f.Title)).Append(" <span class=\"muted\">(")
                  .Append(E(AnalysisReport.OriginText(AnalysisView.OriginOf(a, f)))).Append(")</span></li>\n");
            sb.Append("</ul>\n");
        }
        Walkthrough(sb, steps);
        return Tail(sb);
    }

    /// <summary>Renders Markdown text as a report page (raw HTML in it is shown as text, links become plain labels), with the walk-through when given.</summary>
    public static string RenderMarkdown(string markdown, string caseName, DateTimeOffset now, IReadOnlyList<WalkthroughStep>? steps = null)
    {
        var sb = Head(caseName, now);
        sb.Append(Md(markdown));
        Walkthrough(sb, steps ?? Array.Empty<WalkthroughStep>());
        return Tail(sb);
    }

    /// <summary>Fallback walk-through from the key events alone: time order, undated last.</summary>
    public static IReadOnlyList<WalkthroughStep> StepsFromAnalysis(CaseAnalysis a) =>
        AnalysisView.ReportKeyEvents(a).Select(k =>
        {
            var e = KeyEventOps.Effective(a, k);
            var text = !string.IsNullOrWhiteSpace(k.PromotedReason) ? k.PromotedReason : k.Notes ?? "";
            return new WalkthroughStep(e.Time, e.Title, text, string.Join(", ", e.Mitre), e.Severity);
        }).Select((s, i) => (s, i)).OrderBy(x => x.s.Time == null).ThenBy(x => x.s.Time).ThenBy(x => x.i).Select(x => x.s).ToList();

    private static void Walkthrough(StringBuilder sb, IReadOnlyList<WalkthroughStep> steps)
    {
        if (steps.Count == 0) return;
        sb.Append("<h2 class=\"newpage\">Walk-through</h2>\n<p class=\"muted\">The key events in the order they were recorded (UTC). The order is not a claim about cause.</p>\n");
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            sb.Append("<section class=\"step\">\n<h3>").Append(i + 1).Append(". ").Append(E(s.Title)).Append("</h3>\n<p class=\"meta\">")
              .Append(Pill(s.Severity)).Append(" <span>").Append(E(s.Time != null ? Utc(s.Time.Value) : "No time")).Append("</span>");
            if (!string.IsNullOrWhiteSpace(s.Technique)) sb.Append(" <span>").Append(E(s.Technique)).Append("</span>");
            sb.Append("</p>\n");
            if (!string.IsNullOrWhiteSpace(s.Text)) sb.Append(Md(s.Text));
            sb.Append("</section>\n");
        }
    }

    private static string Md(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var doc = Markdown.Parse(text, Pipeline);
        foreach (var link in doc.Descendants<LinkInline>().ToList())
        {
            link.ReplaceBy(new ContainerInline(), copyChildren: true);   // the label stays, the URL goes
        }
        foreach (var auto in doc.Descendants<AutolinkInline>().ToList()) auto.ReplaceBy(new LiteralInline(""));
        return doc.ToHtml(Pipeline);
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Utc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    private static string Pill(string? severity)
    {
        var word = severity?.Trim().ToLowerInvariant() switch
        {
            "critical" => "Critical", "high" => "High", "medium" => "Medium", "low" => "Low", _ => "Info",
        };
        return $"<span class=\"pill {word.ToLowerInvariant()}\">{word}</span>";   // fixed vocabulary: never case text
    }

    private static StringBuilder Head(string caseName, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(caseName) ? "case" : caseName.Trim();
        var sb = new StringBuilder(8192);
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>Key events report: ").Append(E(name)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append("<h1>Key events report: ").Append(E(name)).Append("</h1>\n<p class=\"muted\">Generated ").Append(E(Utc(now))).Append(" by ").Append(E(CurrentIdentity.Name)).Append("</p>\n");
        return sb;
    }

    private static string Tail(StringBuilder sb) => sb.Append("</main>\n</body>\n</html>\n").ToString();

    private const string Css = @"
:root{color-scheme:light}
*{box-sizing:border-box}
body{margin:0;background:#f4f5f7;color:#1c2330;font:15px/1.55 system-ui,-apple-system,'Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif}
main{max-width:860px;margin:24px auto;padding:32px 40px;background:#fff;border:1px solid #dde1e7}
h1{font-size:26px;margin:0 0 4px}
h2{font-size:20px;margin:32px 0 8px;padding-bottom:4px;border-bottom:1px solid #dde1e7}
h3{font-size:16px;margin:0 0 6px}
.muted{color:#5b6573}
.lead{font-size:16px}
.meta{margin:0 0 8px;color:#3a4352}
.meta span{margin-right:12px}
section.ev,section.step{margin:0 0 16px;padding:12px 16px;border:1px solid #dde1e7;border-left:4px solid #8a94a3;border-radius:4px;break-inside:avoid;page-break-inside:avoid}
.notes{margin:8px 0}
.pill{display:inline-block;padding:1px 10px;border-radius:999px;font-size:12px;font-weight:600;border:1px solid transparent}
.pill.critical{background:#7a1020;color:#fff}
.pill.high{background:#fde0e0;color:#8a1111;border-color:#e5a3a3}
.pill.medium{background:#fff0d0;color:#7a4a00;border-color:#e6c27a}
.pill.low{background:#e0f0ff;color:#0b4a7a;border-color:#9fc8ea}
.pill.info{background:#eceff3;color:#3a4352;border-color:#c9cfd8}
ul{padding-left:22px}
@page{size:A4;margin:16mm}
@media print{
body{background:#fff;font-size:11pt}
main{max-width:none;margin:0;padding:0;border:0}
.newpage{break-before:page;page-break-before:always}
.pill{-webkit-print-color-adjust:exact;print-color-adjust:exact}
}
";
}
