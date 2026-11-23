using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Knowledge;

namespace EzForensics.Core.Assistant;

/// <summary>Read tools over the knowledge base: kb_search, kb_get_note, kb_related.</summary>
public static class KnowledgeTools
{
    public const string Search = "kb_search";
    public const string GetNote = "kb_get_note";
    public const string RelatedTool = "kb_related";
    public const int MaxHits = 6, MaxSearchChars = 6000, MaxNoteChars = 8000, HitChars = 1200;

    public static IReadOnlyList<IAssistantTool> Create(IKnowledgeBase kb) => new IAssistantTool[] { new SearchTool(kb), new NoteTool(kb), new RelatedToolImpl(kb) };

    private static string? Str(ToolCall call, string name) => call.Input[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string Trim(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + " ...";

    private sealed class SearchTool(IKnowledgeBase kb) : IAssistantTool
    {
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(Search,
            "Search the forensics knowledge base (artifact notes, how-tos). Use it for general DFIR questions and cite the note title.", ToolKind.Read,
            new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "What to look up, e.g. 'kerberoasting detection'." },
                ["k"] = new JsonObject { ["type"] = "integer", ["description"] = "Number of hits, 1-6 (default 4)." },
            }, "query");

        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var q = Str(call, "query");
            if (string.IsNullOrWhiteSpace(q)) return Task.FromResult(new ToolOutcome("query is required.", true));
            var k = call.Input["k"] is JsonValue kv && kv.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, MaxHits) : 4;
            var r = kb.Search(q, k);
            if (r.Hits.Count == 0) return Task.FromResult(new ToolOutcome("No notes matched.", false));
            var sb = new StringBuilder();
            foreach (var h in r.Hits.Take(MaxHits))
            {
                var block = $"[{h.NoteTitle}{(string.IsNullOrWhiteSpace(h.Heading) ? "" : " › " + h.Heading)}]\n{Trim(h.Text.Trim(), HitChars)}\n\n";
                if (sb.Length + block.Length > MaxSearchChars) break;
                sb.Append(block);
            }
            if (r.Related.Count > 0) sb.Append("Related: ").Append(string.Join(", ", r.Related));
            return Task.FromResult(new ToolOutcome(Trim(sb.ToString().TrimEnd(), MaxSearchChars), false));
        }
    }

    private sealed class NoteTool(IKnowledgeBase kb) : IAssistantTool
    {
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(GetNote,
            "Read one knowledge base note in full by its title (as shown in kb_search results).", ToolKind.Read,
            new JsonObject { ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Note title." } }, "title");

        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var t = Str(call, "title");
            if (string.IsNullOrWhiteSpace(t)) return Task.FromResult(new ToolOutcome("title is required.", true));
            if (kb.GetNote(t) is { } n) return Task.FromResult(new ToolOutcome($"{n.Title}\n{Trim(n.Body.Trim(), MaxNoteChars)}", false));
            var sug = kb.Suggest(t);
            return Task.FromResult(new ToolOutcome($"No note titled '{t}'." + (sug.Count > 0 ? " Did you mean: " + string.Join(", ", sug) + "?" : " Try kb_search."), true));
        }
    }

    private sealed class RelatedToolImpl(IKnowledgeBase kb) : IAssistantTool
    {
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(RelatedTool,
            "List the knowledge base notes linked to a note.", ToolKind.Read,
            new JsonObject { ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Note title." } }, "title");

        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var t = Str(call, "title");
            if (string.IsNullOrWhiteSpace(t)) return Task.FromResult(new ToolOutcome("title is required.", true));
            if (kb.GetNote(t) is not { } n)
            {
                var sug = kb.Suggest(t);
                return Task.FromResult(new ToolOutcome($"No note titled '{t}'." + (sug.Count > 0 ? " Did you mean: " + string.Join(", ", sug) + "?" : ""), true));
            }
            var rel = kb.Related(n.Title);
            return Task.FromResult(new ToolOutcome(rel.Count == 0 ? $"{n.Title} has no linked notes." : $"Linked to {n.Title}: {string.Join(", ", rel)}", false));
        }
    }
}
