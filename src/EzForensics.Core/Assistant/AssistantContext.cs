using System.Text;

namespace EzForensics.Core.Assistant;

/// <summary>
/// Builds the context blocks put in front of each examiner message (&lt;app_state&gt;, &lt;app_features&gt;, &lt;current_view&gt;,
/// &lt;skill_help&gt;). Remembers what it last sent so unchanged blocks are not repeated: small local models have little room.
/// One instance per conversation; the chat and the live tests use the same code.
/// </summary>
public sealed class AssistantContext
{
    private string? _lastState, _lastView;
    private SpectrePage? _lastIndexPage;
    private readonly EzForensics.Core.Knowledge.IKnowledgeBase? _knowledge;

    /// <param name="knowledge">Knowledge base for the automatic &lt;knowledge&gt; block; null = none.</param>
    public AssistantContext(EzForensics.Core.Knowledge.IKnowledgeBase? knowledge = null) { _knowledge = knowledge; }

    public const int MaxKnowledgeChars = 4800;
    private static readonly System.Text.RegularExpressions.Regex AboutScreen = new(
        @"\b(?:this|that|these|those|the|my)\s+(?:file|files|row|rows|grid|table|csv|filter|flags?|selection)\b|\b(?:flag|flagged|filter|filtered)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Forget what was sent (a new conversation has seen nothing).</summary>
    public void Reset() { _lastState = null; _lastView = null; _lastIndexPage = null; }

    /// <param name="page">The page on screen.</param>
    /// <param name="state">App state text ("Page: ...", case, files); null = none.</param>
    /// <param name="view">The grid view (<see cref="GridContext.Describe"/>); null = no grid on this page.</param>
    /// <param name="text">The examiner's words.</param>
    /// <param name="log">Diagnostics (skill ids only).</param>
    /// <param name="pinnedKnowledge">A &lt;knowledge&gt; block the examiner chose (Ask Spectre in the Brain); it replaces the search.</param>
    public string Build(SpectrePage page, string? state, string? view, string text, Action<string>? log = null, string? pinnedKnowledge = null)
    {
        var header = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(state) && state != _lastState) header.Append($"<app_state>\n{state}\n</app_state>\n");
        _lastState = state;
        if (_lastIndexPage != page)
        {
            header.Append($"<app_features>\n{SpectreSkills.Index(page)}\n</app_features>\n");
            _lastIndexPage = page;
        }
        if (view != null)
        {
            header.Append(view == _lastView ? "<current_view>Same as in my previous message.</current_view>\n" : $"<current_view>\n{view}\n</current_view>\n");
            _lastView = view;
        }
        var howTo = SpectreSkills.MatchHowTo(text, page);
        if (howTo.Count > 0)
        {
            header.Append(SpectreSkills.HelpBlock(howTo));
            log?.Invoke("Spectre: attached skill help " + string.Join(", ", howTo.Select(s => s.Id)));
        }
        if (!string.IsNullOrEmpty(pinnedKnowledge))
        {
            header.Append(pinnedKnowledge);
            log?.Invoke("Spectre: attached the notes the examiner asked about");
        }
        else header.Append(KnowledgeBlock(text, howTo.Count > 0, log));
        return header.Length == 0 ? text : header + "\n" + text;
    }

    private string KnowledgeBlock(string text, bool matchedHowTo, Action<string>? log)
    {
        if (_knowledge == null || matchedHowTo || string.IsNullOrWhiteSpace(text) || SpectreSkills.IsActionRequest(text) || AboutScreen.IsMatch(text)) return "";
        try
        {
            if (_knowledge.IsEmpty) return "";
            var hits = _knowledge.Search(text, 4).Hits;
            if (hits.Count == 0 || !hits[0].Strong) return "";
            var sb = new StringBuilder("<knowledge>\n");
            var used = new List<string>();
            foreach (var h in hits)
            {
                var block = $"[{h.NoteTitle}{(string.IsNullOrWhiteSpace(h.Heading) ? "" : " › " + h.Heading)}]\n{h.Text.Trim()}\n\n";
                if (sb.Length + block.Length > MaxKnowledgeChars)
                {
                    if (used.Count > 0) break;
                    block = block[..Math.Max(0, Math.Min(block.Length, MaxKnowledgeChars - sb.Length))];
                }
                sb.Append(block);
                used.Add(h.NoteTitle);
            }
            sb.Append("</knowledge>\n");
            log?.Invoke($"Spectre: attached knowledge {used.Count} hits ({string.Join(", ", used.Distinct())})");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            log?.Invoke("Spectre: knowledge lookup failed: " + ex.Message);
            return "";
        }
    }
}
