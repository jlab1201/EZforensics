using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>
/// Tool calls for models without native tool support (Ollama: "... does not support tools", e.g. Foundation-Sec-8B, a base model).
/// The whole conversation is rendered as one plain-text transcript with the tools listed in it, sent raw (no chat template), and
/// the reply is read for <c>&lt;tool_call&gt;{"name":..,"arguments":{..}}&lt;/tool_call&gt;</c>. Stop sequences end the reply at the
/// call or before the model writes the examiner's next line, so a base model does not ramble on.
/// </summary>
public static class TextToolProtocol
{
    public const string Open = "<tool_call>", Close = "</tool_call>";
    private const string User = "Examiner:", Assistant = "Spectre:", Result = "Tool result";

    /// <summary>Where the reply must end: after a call, or before the model starts writing the next turn itself.</summary>
    public static readonly string[] Stops =
    {
        Close, "\n" + User, "\n" + Result, "\n" + Assistant, "\nQuestion:", "\nAnswer:", "\nUser:", "\nHuman:", "\n\n\n",
        "<|eot_id|>", "<|end_of_text|>",
    };

    /// <summary>
    /// Most tokens one reply may use. A reply is one tool call or a few short lines; a base model that misses every stop would
    /// otherwise write until the chat limit (8192 tokens is close to an hour on a CPU-bound 8B model).
    /// </summary>
    public const int MaxReplyTokens = 400;

    private static readonly System.Text.RegularExpressions.Regex ContextBlock = new(
        @"<(app_state|app_features|current_view|skill_help|knowledge)\b[^>]*>(.*?)</\1>\s*",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The examiner's words without the context blocks, and the blocks (tag, whole block text) in order.</summary>
    public static (string Question, List<(string Tag, string Block)> Blocks) SplitContext(string content)
    {
        var blocks = new List<(string, string)>();
        var question = ContextBlock.Replace(content, m => { blocks.Add((m.Groups[1].Value, m.Value.Trim())); return ""; }).Trim();
        return (question, blocks);
    }

    /// <summary>"&lt;current_view&gt;Yes.&lt;/current_view&gt;" (a base model copying the message shape) becomes "Yes.".</summary>
    public static string StripContextTags(string said) =>
        System.Text.RegularExpressions.Regex.Replace(said, @"</?(app_state|app_features|current_view|skill_help|knowledge)\b[^>]*>", "").Trim();

    private static string Content(JsonObject o) => (o["content"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c.Trim() : "";

    /// <summary>
    /// The transcript ending in "Spectre:", for the model to continue. A base model copies whatever the transcript shows, so the
    /// context blocks sent with the examiner's messages (&lt;app_state&gt;, &lt;current_view&gt;, ...) are taken out of the dialogue and
    /// shown once, latest version, just before the last question; and an answer that repeats an earlier one is left out, so one
    /// stuck reply does not become the pattern for every next one.
    /// </summary>
    public static string Render(JsonArray messages, IReadOnlyList<ToolSpec> tools)
    {
        var lastUser = -1;
        for (int i = 0; i < messages.Count; i++)
            if (messages[i] is JsonObject u && (string?)u["role"] == "user") lastUser = i;
        var latest = new Dictionary<string, string>();   // tag -> latest block (skill help: the last question's only)
        for (int i = 0; i <= lastUser; i++)
        {
            if (messages[i] is not JsonObject u || (string?)u["role"] != "user") continue;
            latest.Remove("skill_help");
            var help = new StringBuilder();
            foreach (var (tag, block) in SplitContext(Content(u)).Blocks)
            {
                if (block.Contains("Same as in my previous message.", StringComparison.Ordinal)) continue;
                if (tag == "skill_help") help.Append(block).Append('\n');
                else latest[tag] = block;
            }
            if (help.Length > 0) latest["skill_help"] = help.ToString().Trim();
        }

        var sb = new StringBuilder();
        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject o) continue;
            var content = Content(o);
            switch ((string?)o["role"])
            {
                case "system":
                    sb.Append(content).Append("\n\n");
                    if (tools.Count > 0) sb.Append(ToolsBlock(tools)).Append('\n');
                    sb.Append("The conversation:\n\n");
                    break;
                case "user":
                    if (i == lastUser && latest.Count > 0)
                    {
                        sb.Append("(What the examiner's screen shows now:)\n");
                        foreach (var tag in new[] { "app_state", "app_features", "current_view", "skill_help" })
                            if (latest.TryGetValue(tag, out var b)) sb.Append(b).Append('\n');
                        sb.Append('\n');
                    }
                    sb.Append(User).Append(' ').Append(SplitContext(content).Question).Append("\n\n");
                    break;
                case "assistant":
                    if (o["tool_calls"] is not JsonArray && content.Length > 0 && !said.Add(content)) break;   // a repeated answer
                    sb.Append(Assistant);
                    if (content.Length > 0) sb.Append(' ').Append(content);
                    if (o["tool_calls"] is JsonArray calls)
                        foreach (var call in calls)
                        {
                            var fn = call?["function"];
                            var args = fn?["arguments"] is JsonObject a ? a.ToJsonString() : "{}";
                            sb.Append(' ').Append(Open).Append("{\"name\":").Append(JsonSerializer.Serialize((string?)fn?["name"] ?? ""))
                              .Append(",\"arguments\":").Append(args).Append('}').Append(Close);
                        }
                    sb.Append("\n\n");
                    break;
                case "tool":
                    sb.Append(Result).Append(" (").Append((string?)o["tool_name"] ?? "tool").Append("): ").Append(content).Append("\n\n");
                    break;
            }
        }
        return sb.Append(Assistant).ToString();
    }

    /// <summary>How to call a tool, one line per tool, and a worked example.</summary>
    public static string ToolsBlock(IReadOnlyList<ToolSpec> tools)
    {
        var sb = new StringBuilder("## Tools\n").Append(PromptStore.Section("text-tools", "tools_intro")).Append("\n\n");
        foreach (var t in tools)
        {
            sb.Append("- ").Append(Signature(t)).Append(": ").Append(FirstSentence(t.Description)).Append('\n');
            foreach (var arg in ArgumentNotes(t)) sb.Append("    ").Append(arg).Append('\n');
        }
        if (tools.Any(t => t.Name == AssistantGridTools.ApplyFilter))
            sb.Append('\n').Append(PromptStore.Section("text-tools", "filter_syntax_label")).Append(' ').Append(AssistantGrid.FilterSyntaxHelp).Append('\n')
              .Append('\n').Append(PromptStore.Section("text-tools", "example_filter")).Append('\n');
        if (tools.Any(t => t.Name == AssistantCaseTools.Query))
            sb.Append('\n').Append(PromptStore.Section("text-tools", "example_query")).Append('\n');
        sb.Append('\n').Append(PromptStore.Section("text-tools", "example_smalltalk")).Append('\n');
        return sb.ToString();
    }

    /// <summary>"filter: Filter text, e.g. [EventId] = '4688'" for every argument that has a description (cut at 200 characters).</summary>
    private static IEnumerable<string> ArgumentNotes(ToolSpec t)
    {
        if (t.InputSchema["properties"] is not JsonObject props) yield break;
        foreach (var (name, p) in props)
            if (p?["description"] is JsonValue dv && dv.TryGetValue<string>(out var d) && d.Trim() is { Length: > 0 } text)
                yield return name + ": " + (text.Length > 200 ? text[..200] + "…" : text).ReplaceLineEndings(" ");
    }

    /// <summary><c>grid_sort(column: string, descending?: boolean)</c>; enums as <c>"a"|"b"</c>.</summary>
    public static string Signature(ToolSpec t)
    {
        var required = (t.InputSchema["required"] as JsonArray)?.Select(r => (string?)r).ToHashSet() ?? new HashSet<string?>();
        var parts = new List<string>();
        if (t.InputSchema["properties"] is JsonObject props)
            foreach (var (name, p) in props)
            {
                var type = p?["enum"] is JsonArray e ? string.Join("|", e.Select(v => v?.ToJsonString()))
                    : p?["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts
                    : p?["type"] is JsonArray ta ? string.Join("|", ta.Select(v => (string?)v)) : "any";
                if (type == "array" && p?["items"]?["type"] is JsonValue iv && iv.TryGetValue<string>(out var it)) type = it + "[]";
                parts.Add(name + (required.Contains(name) ? "" : "?") + ": " + type);
            }
        return t.Name + "(" + string.Join(", ", parts) + ")";
    }

    private static string FirstSentence(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        var dot = s.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 && dot < 240 ? s[..(dot + 1)] : s.Length > 240 ? s[..240] + "…" : s;
    }

    /// <summary>
    /// Splits a raw reply into what the model said and the tool calls it made. Takes <c>&lt;tool_call&gt;</c> JSON (closed or cut off by
    /// the stop sequence), or a bare JSON object naming an offered tool. "arguments" or "parameters"; arguments that are a JSON
    /// schema (the model copied the tool's definition) count as none. Unknown tool names are kept: the session reports them.
    /// </summary>
    public static (string Said, List<(string Name, JsonObject Args)> Calls) Parse(string reply, IReadOnlyCollection<string> toolNames)
    {
        var calls = new List<(string, JsonObject)>();
        var said = new StringBuilder();
        var rest = reply;
        while (true)
        {
            var at = rest.IndexOf(Open, StringComparison.Ordinal);
            if (at < 0) break;
            said.Append(rest[..at]);
            rest = rest[(at + Open.Length)..];
            if (FirstObject(rest) is { } obj && AsCall(obj.Node) is { } call) calls.Add(call);
            var end = rest.IndexOf(Close, StringComparison.Ordinal);
            rest = end >= 0 ? rest[(end + Close.Length)..] : (FirstObject(rest) is { } o ? rest[o.End..] : "");
        }
        said.Append(rest);
        if (calls.Count == 0 && FirstObject(reply) is { } bare && AsCall(bare.Node) is { } c && toolNames.Contains(c.Item1))
            return (reply[..bare.Start].Trim(), new() { c });
        return (said.ToString().Trim(), calls);
    }

    private static (string, JsonObject)? AsCall(JsonObject o)
    {
        var fn = o["function"] as JsonObject ?? o;
        if ((fn["name"] as JsonValue)?.TryGetValue<string>(out var name) != true || string.IsNullOrWhiteSpace(name)) return null;
        var args = fn["arguments"] ?? fn["parameters"];
        if (args is JsonValue v && v.TryGetValue<string>(out var s))
            try { args = JsonNode.Parse(s); } catch (JsonException) { args = null; }
        var obj = args as JsonObject;
        if (obj is { } a && (a["type"] as JsonValue)?.TryGetValue<string>(out var ty) == true && ty == "object" && a.ContainsKey("properties")) obj = null;   // a copied schema
        return (name.Trim(), obj == null ? new JsonObject() : JsonNode.Parse(obj.ToJsonString())!.AsObject());
    }

    /// <summary>The first balanced <c>{...}</c> in <paramref name="s"/> that parses as a JSON object.</summary>
    private static (JsonObject Node, int Start, int End)? FirstObject(string s)
    {
        for (int start = s.IndexOf('{'); start >= 0; start = s.IndexOf('{', start + 1))
        {
            int depth = 0; bool str = false, esc = false;
            for (int i = start; i < s.Length; i++)
            {
                var ch = s[i];
                if (str) { if (esc) esc = false; else if (ch == '\\') esc = true; else if (ch == '"') str = false; continue; }
                if (ch == '"') str = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    try { if (JsonNode.Parse(s[start..(i + 1)]) is JsonObject o) return (o, start, i + 1); }
                    catch (JsonException) { }
                    break;
                }
            }
        }
        return null;
    }
}
