using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>Defaults for a local Ollama server (OpenAI-compatible API).</summary>
public static class OllamaDefaults
{
    public const string BaseUrl = "http://localhost:11434/v1/";
}

/// <summary>
/// OpenAI-compatible Chat Completions client (OpenAI cloud), or Ollama's native <c>/api/chat</c> when <c>contextTokens</c> is set.
/// Ollama's OpenAI endpoint cannot set the context size, and its default (4096 tokens on an 8 GB GPU) is smaller than Spectre's
/// system prompt + tools + case context: Ollama then silently drops the start of the conversation, rules included.
/// HttpClient + System.Text.Json only. Keeps the history itself; unparsable tool-call arguments become an empty object (the tool
/// then reports its own missing-argument error).
/// </summary>
public sealed class OpenAiCompatibleModelClient : IModelClient, IModelUsage, IRequestSnapshot
{
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly int _maxTokens;
    private readonly int? _numCtx;
    private readonly bool _disableThinking;
    private readonly JsonArray _messages = new();
    private int _generatedIds;

    /// <summary>Body of the last request sent (system prompt, tools, messages). The API key is a header and is never in it.</summary>
    public string? LastRequestJson { get; private set; }

    /// <summary>Share of the context window above which the oldest turns are dropped before the next examiner message.</summary>
    public const double TrimAt = 0.75;

    /// <param name="contextTokens">Set for Ollama: use the native API with this <c>num_ctx</c>. Null = OpenAI Chat Completions.</param>
    public OpenAiCompatibleModelClient(HttpClient http, Uri baseUrl, string model, string? apiKey, string systemPrompt, int maxTokens = 4096, int? contextTokens = null, bool disableThinking = false)
    {
        _disableThinking = disableThinking;
        _http = http; _model = model; _apiKey = apiKey; _maxTokens = maxTokens; _numCtx = contextTokens;
        _baseUrl = EnsureSlash(baseUrl);
        _messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
    }

    public int? LastPromptTokens { get; private set; }
    public int? ContextTokens => _numCtx;
    public string? LastTimings { get; private set; }
    public bool ToolsUnsupported { get; private set; }

    /// <summary>Shown once when the model turned out to have no tool support (Ollama: "... does not support tools").</summary>
    public const string NoToolsNotice = "This model has no built-in tool support, so Spectre asks it for tool calls in plain text. " +
        "That is slower and less reliable than a model with tool support (e.g. qwen3).";

    public Task<ModelStep> SendUserAsync(string text, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        TrimHistory();
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        return RequestAsync(tools, ct);
    }

    /// <summary>
    /// When the last request filled most of the window, drops the oldest whole turns (an examiner message and everything up to the
    /// next one) so the system prompt and the recent turns always fit. Only between turns, so tool calls keep their results.
    /// </summary>
    private void TrimHistory()
    {
        if (_numCtx is not { } ctx || LastPromptTokens is not { } used || used <= ctx * TrimAt) return;
        var users = Enumerable.Range(1, _messages.Count - 1).Where(i => (string?)_messages[i]?["role"] == "user").ToList();
        if (users.Count < 2) return;
        var keepFrom = users[users.Count / 2];   // keep the latest half of the turns
        for (int i = keepFrom - 1; i >= 1; i--) _messages.RemoveAt(i);
        LastPromptTokens = null;
    }

    public Task<ModelStep> SendToolResultsAsync(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        foreach (var (call, outcome) in results)
            _messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["tool_name"] = call.Name,   // Ollama's native API pairs results by name
                ["content"] = outcome.IsError ? "Error: " + outcome.Text : outcome.Text,
            });
        return RequestAsync(tools, ct);
    }

    /// <summary>Model ids from GET <c>models</c> (for the Options dropdown and the Test button).</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, Uri baseUrl, string? apiKey, CancellationToken ct)
    {
        var url = EnsureSlash(baseUrl);
        var json = await SendAsync(http, url, HttpMethod.Get, "models", null, apiKey, ct).ConfigureAwait(false);
        return ParseModelIds(json, url.Authority);
    }

    /// <summary>Model ids from a <c>{"data":[{"id":...}]}</c> body (OpenAI, OpenRouter, Ollama /v1) or a bare array (Together AI, where type must be "chat" when present).</summary>
    public static IReadOnlyList<string> ParseModelIds(string json, string host = "server")
    {
        var ids = new List<string>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new AssistantModelException($"{host}: the response was not valid JSON."); }
        var data = root as JsonArray ?? root?["data"] as JsonArray;   // Together AI returns a bare array
        if (data != null)
            foreach (var m in data)
                if (m is JsonObject && m["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0
                    && !(m["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type.Length > 0 && type != "chat" && root is JsonArray))
                    ids.Add(id);
        return ids;
    }

    private async Task<ModelStep> RequestAsync(IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        if (ToolsUnsupported) return await TextRequestAsync(tools, false, ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = JsonNode.Parse(_messages.ToJsonString()),   // snapshot
            ["stream"] = false,
        };
        // Thinking stays at the model's default: with "think": false qwen3:8b answered 5x faster but stopped looking in other files
        // (live flag tests 0/3), so the speed fix is on the server side (flash attention + q8 KV cache keeps 16k in 8 GB VRAM).
        if (_disableThinking && _numCtx != null) body["think"] = false;   // one-shot JSON tasks (knowledge labels) gain nothing from thinking
        if (_numCtx is { } ctx) body["options"] = new JsonObject { ["num_ctx"] = ctx, ["num_predict"] = _maxTokens };
        else body["max_tokens"] = _maxTokens;
        if (tools.Count > 0 && !ToolsUnsupported)
        {
            var arr = new JsonArray();
            foreach (var t in tools)
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.InputSchema.ToJsonString()),
                    },
                });
            body["tools"] = arr;
        }

        string json;
        try { json = await PostAsync(body, ct).ConfigureAwait(false); }
        catch (AssistantModelException ex) when (body.ContainsKey("tools") && ex.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
        {
            // some local models (e.g. Foundation-Sec-8B) have no tool template: from now on the tools go in the prompt as text
            ToolsUnsupported = true;
            return await TextRequestAsync(tools, true, ct).ConfigureAwait(false);
        }
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new AssistantModelException($"{_baseUrl.Authority}: the response was not valid JSON."); }
        var choice = _numCtx != null ? root : root?["choices"]?[0];
        var message = choice?["message"] as JsonObject
            ?? throw new AssistantModelException($"{_baseUrl.Authority}: the response had no message.");
        LastPromptTokens = (_numCtx != null ? root?["prompt_eval_count"] : root?["usage"]?["prompt_tokens"]) is JsonValue pv && pv.TryGetValue<int>(out var pt) ? pt : null;
        LastTimings = _numCtx != null ? Timings(root!) : null;

        var texts = new List<string>();
        string? content = null;
        if (message["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) && StripThinking(c) is { Length: > 0 } said) { content = said; texts.Add(said); }

        var calls = new List<ToolCall>();
        var rawCalls = new JsonArray();
        if (message["tool_calls"] is JsonArray tcs)
            foreach (var tc in tcs)
            {
                if (tc is not JsonObject tco) continue;
                var id = tco["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) { id = "call_" + (++_generatedIds); tco["id"] = id; }
                var fn = tco["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var (args, ok) = ParseArgs(fn?["arguments"]);
                calls.Add(new ToolCall(id, name, args) { ArgumentsInvalid = !ok });
                rawCalls.Add(JsonNode.Parse(tco.ToJsonString()));
            }

        var asst = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (rawCalls.Count > 0) asst["tool_calls"] = rawCalls;
        _messages.Add(asst);

        var finish = (_numCtx != null ? choice?["done_reason"] : choice?["finish_reason"])?.GetValue<string>();
        var stop = calls.Count > 0 ? ModelStopReason.ToolUse : finish switch
        {
            "stop" => ModelStopReason.EndTurn,
            "tool_calls" => ModelStopReason.ToolUse,
            "length" => ModelStopReason.MaxTokens,
            "content_filter" => ModelStopReason.Refusal,
            _ => ModelStopReason.Other,
        };
        return new ModelStep(texts, calls, stop, stop == ModelStopReason.Refusal ? "content_filter" : null);
    }

    /// <summary>
    /// <see cref="TextToolProtocol"/>: the history as one raw prompt on Ollama's <c>/api/generate</c> (no chat template, which a base
    /// model may not have), tool calls read from the reply text. The history keeps the same shape as native tool calls.
    /// </summary>
    private async Task<ModelStep> TextRequestAsync(IReadOnlyList<ToolSpec> tools, bool notice, CancellationToken ct)
    {
        var stops = new JsonArray();
        foreach (var s in TextToolProtocol.Stops) stops.Add(s);
        var body = new JsonObject
        {
            ["model"] = _model,
            ["prompt"] = TextToolProtocol.Render(_messages, tools),
            ["raw"] = true,
            ["stream"] = false,
            ["options"] = new JsonObject { ["num_ctx"] = _numCtx ?? AssistantModelFactory.OllamaContextTokens, ["num_predict"] = Math.Min(_maxTokens, TextToolProtocol.MaxReplyTokens), ["stop"] = stops },
        };
        var names = tools.Select(t => t.Name).ToList();
        var (root, said, parsed) = await GenerateAsync(body, names, ct).ConfigureAwait(false);
        // a base model that got stuck repeats an earlier answer word for word: sample once more, less greedily
        if (parsed.Count == 0 && said.Length > 0 && _messages.OfType<JsonObject>().Any(m => (string?)m["role"] == "assistant"
                && string.Equals(((m["content"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : "").Trim(), said, StringComparison.OrdinalIgnoreCase)))
        {
            body["options"]!["temperature"] = 0.9;
            body["options"]!["seed"] = Random.Shared.Next();
            (root, said, parsed) = await GenerateAsync(body, names, ct).ConfigureAwait(false);
        }
        // hitting the cap means the model ran on past its answer: keep the answer (its first paragraph), drop the ramble
        var capped = (string?)root["done_reason"] == "length";
        if (capped && parsed.Count == 0 && said.IndexOf("\n\n", StringComparison.Ordinal) is > 0 and var cut)
        {
            said = said[..cut].Trim();
            capped = false;
        }
        var texts = new List<string>();
        if (notice) texts.Add(NoToolsNotice);
        if (said.Length > 0) texts.Add(said);
        var calls = new List<ToolCall>();
        var rawCalls = new JsonArray();
        foreach (var (name, args) in parsed)
        {
            var id = "call_" + (++_generatedIds);
            calls.Add(new ToolCall(id, name, args));
            rawCalls.Add(new JsonObject { ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(args.ToJsonString()) } });
        }
        var asst = new JsonObject { ["role"] = "assistant", ["content"] = said.Length > 0 ? said : null };
        if (rawCalls.Count > 0) asst["tool_calls"] = rawCalls;
        _messages.Add(asst);

        var stop = calls.Count > 0 ? ModelStopReason.ToolUse
            : capped ? ModelStopReason.MaxTokens : ModelStopReason.EndTurn;
        return new ModelStep(texts, calls, stop, null);
    }

    /// <summary>One <c>/api/generate</c> call: the response, what the model said and the tool calls in it. Sets usage and timings.</summary>
    private async Task<(JsonNode Root, string Said, List<(string Name, JsonObject Args)> Calls)> GenerateAsync(JsonObject body, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        LastRequestJson = body.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var json = await SendAsync(_http, NativeRoot(_baseUrl), HttpMethod.Post, "api/generate", body.ToJsonString(), _apiKey, ct).ConfigureAwait(false);
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new AssistantModelException($"{_baseUrl.Authority}: the response was not valid JSON."); }
        if (root?["response"] is not JsonValue rv || !rv.TryGetValue<string>(out var reply))
            throw new AssistantModelException($"{_baseUrl.Authority}: the response had no text.");
        LastPromptTokens = root["prompt_eval_count"] is JsonValue pv && pv.TryGetValue<int>(out var pt) ? pt : null;
        LastTimings = Timings(root);
        var (said, calls) = TextToolProtocol.Parse(StripThinking(reply), names);
        return (root, TextToolProtocol.StripContextTags(said), calls);
    }

    private Task<string> PostAsync(JsonObject body, CancellationToken ct) => Snapshot(body) && _numCtx != null
        ? SendAsync(_http, NativeRoot(_baseUrl), HttpMethod.Post, "api/chat", body.ToJsonString(), _apiKey, ct)
        : SendAsync(_http, _baseUrl, HttpMethod.Post, "chat/completions", body.ToJsonString(), _apiKey, ct);

    private bool Snapshot(JsonObject body) { LastRequestJson = body.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); return true; }

    /// <summary>The arguments as an object; false when they were present but not a JSON object (missing or empty = no arguments, fine).</summary>
    private static (JsonObject Args, bool Ok) ParseArgs(JsonNode? args)
    {
        if (args is JsonObject o) return (JsonNode.Parse(o.ToJsonString())!.AsObject(), true);   // some servers (Ollama) return an object
        if (args == null) return (new JsonObject(), true);
        if (args is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (string.IsNullOrWhiteSpace(s)) return (new JsonObject(), true);
            try { if (JsonNode.Parse(s) is JsonObject parsed) return (parsed, true); }
            catch (JsonException) { }
        }
        return (new JsonObject(), false);
    }

    private static async Task<string> SendAsync(HttpClient http, Uri baseUrl, HttpMethod method, string path, string? body, string? apiKey, CancellationToken ct)
    {
        var host = baseUrl.Authority;
        using var req = new HttpRequestMessage(method, new Uri(baseUrl, path));
        if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new AssistantModelException($"{host}: HTTP {(int)resp.StatusCode} {ShortError(text, resp.ReasonPhrase)}", (int)resp.StatusCode);
            return text;
        }
        catch (AssistantModelException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AssistantModelException($"{host}: the request timed out."); }
        catch (HttpRequestException) { throw new AssistantModelException($"could not reach {host} (is Ollama running?)"); }
    }

    private static string ShortError(string body, string? fallback)
    {
        string? msg = null;
        try
        {
            var err = JsonNode.Parse(body)?["error"];
            if (err is JsonValue ev && ev.TryGetValue<string>(out var es)) msg = es;
            else if (err?["message"]?.GetValue<string>() is { } m)
                msg = (err["type"]?.GetValue<string>() is { } ty ? ty + ": " : "") + m;
        }
        catch (Exception) { }
        msg ??= fallback ?? "error";
        msg = msg.ReplaceLineEndings(" ");
        return msg.Length > 200 ? msg[..200] : msg;
    }

    /// <summary>"load 0.0s, prompt 3.4s, 17 tokens in 1.7s" from Ollama's native durations (nanoseconds); null when it sent none.</summary>
    internal static string? Timings(JsonNode root)
    {
        static double? Sec(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var ns) ? ns / 1e9 : null;
        var load = Sec(root["load_duration"]);
        var prompt = Sec(root["prompt_eval_duration"]);
        var gen = Sec(root["eval_duration"]);
        if (load == null && prompt == null && gen == null) return null;
        var tokens = root["eval_count"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(ci, "load {0:0.0}s, prompt {1:0.0}s, {2} tokens in {3:0.0}s", load ?? 0, prompt ?? 0, tokens, gen ?? 0);
    }

    /// <summary>Ollama's server root from its OpenAI base URL ("http://host:11434/v1/" becomes "http://host:11434/").</summary>
    public static Uri NativeRoot(Uri baseUrl)
    {
        var u = EnsureSlash(baseUrl).AbsoluteUri;
        return new Uri(u.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase) ? u[..^3] : u);
    }

    private static Uri EnsureSlash(Uri u) => u.AbsoluteUri.EndsWith('/') ? u : new Uri(u.AbsoluteUri + "/");

    /// <summary>
    /// Removes the reasoning some local models (qwen3, deepseek-r1) put in the reply as &lt;think&gt;…&lt;/think&gt;, and the
    /// "[after grid_sort …]" notes they write instead of calling the tool (also from the history, so they are not imitated).
    /// </summary>
    public static string StripThinking(string text)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var open = s.IndexOf("<think>", StringComparison.Ordinal);
        if (open >= 0) s = s[..open]; // cut off mid-thought (max tokens)
        s = ToolNote.Replace(s, "");
        return s.Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex ToolNote = new(
        @"^[ \t]*\[(?:after|calling|called|using|used|ran|running)\b[^\]\r\n]*\][ \t]*",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
}
