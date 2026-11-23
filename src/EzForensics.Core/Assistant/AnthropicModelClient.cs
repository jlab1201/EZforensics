using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace EzForensics.Core.Assistant;

public static class AnthropicDefaults
{
    /// <summary>Used when the model setting is empty.</summary>
    public const string Model = "claude-opus-5-5";
}

/// <summary>
/// Anthropic Messages client (non-streaming), one manual step per call: the agent loop is <see cref="AssistantSession"/>'s.
/// Keeps the append-only history itself, including thinking blocks (signature preserved) and tool_use blocks, because the API
/// rejects edited history. Adaptive thinking; no budget_tokens, sampling params, prefill or forced tool_choice.
/// The key is only sent in the x-api-key header; errors are rethrown as <see cref="AssistantModelException"/> without it.
/// </summary>
public sealed class AnthropicModelClient : IModelClient, IRequestSnapshot
{
    private readonly IAnthropicClient _client;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _system;
    private readonly int _maxTokens;
    private readonly Effort _effort;
    private readonly List<MessageParam> _history = new();

    /// <summary>The last request as JSON (system, tools, messages). The API key is a header and is never in it.</summary>
    public string? LastRequestJson { get; private set; }

    /// <param name="baseUrl">Override for tests; null uses api.anthropic.com.</param>
    public AnthropicModelClient(string apiKey, string? model, string systemPrompt, int maxTokens = 16000, Effort? effort = null, HttpClient? http = null, string? baseUrl = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? AnthropicDefaults.Model : model.Trim();
        _system = systemPrompt; _maxTokens = maxTokens; _effort = effort ?? Effort.Medium;
        _client = new AnthropicClient { ApiKey = apiKey };
        if (http != null || !string.IsNullOrEmpty(baseUrl))
            _client = _client.WithOptions(o => o with
            {
                HttpClient = http ?? o.HttpClient,
                BaseUrl = string.IsNullOrEmpty(baseUrl) ? o.BaseUrl : baseUrl,
            });
    }

    public Task<ModelStep> SendUserAsync(string text, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        _history.Add(new UserMessageParam { Content = text });
        return RequestAsync(tools, ct);
    }

    public Task<ModelStep> SendToolResultsAsync(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var (call, outcome) in results)
            blocks.Add(new ToolResultBlockParam { ToolUseID = call.Id, Content = outcome.Text, IsError = outcome.IsError });
        _history.Add(new UserMessageParam { Content = blocks });
        return RequestAsync(tools, ct);
    }

    private string Snapshot(MessageCreateParams p, IReadOnlyList<ToolSpec> tools)
    {
        var o = new JsonObject { ["model"] = _model, ["max_tokens"] = _maxTokens, ["system"] = _system };
        var arr = new JsonArray();
        foreach (var t in tools)
            arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(t.InputSchema.ToJsonString()) });
        o["tools"] = arr;
        var msgs = new JsonArray();
        foreach (var m in _history)
        {
            try { msgs.Add(JsonNode.Parse(JsonSerializer.Serialize(m, m.GetType()))); }
            catch (Exception) { msgs.Add("(message could not be shown)"); }
        }
        o["messages"] = msgs;
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<ModelStep> RequestAsync(IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = _maxTokens,
            System = _system,
            Messages = _history.ToList(),   // snapshot
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = _effort },
        };
        if (tools.Count > 0) p = p with { Tools = tools.Select(ToTool).Select(t => (ToolUnion)t).ToList() };

        LastRequestJson = Snapshot(p, tools);
        Message response;
        try { response = await _client.Messages.Create(p, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (AnthropicRateLimitException) { throw new AssistantModelException("Rate limited. Try again shortly."); }
        catch (Anthropic5xxException) { throw new AssistantModelException("Anthropic service error. Try again shortly."); }
        catch (AnthropicApiException ex)
        {
            var status = (int)ex.StatusCode;
            throw new AssistantModelException(status switch
            {
                401 => "Anthropic rejected the API key.",
                403 => "The API key is not allowed to use this model.",
                404 => "Anthropic does not know this model. Pick another in Options → AI.",
                400 or 413 or 422 => "Anthropic rejected the request.",
                _ => $"Anthropic request failed (HTTP {status}).",
            });
        }
        catch (HttpRequestException) { throw new AssistantModelException("could not reach api.anthropic.com"); }
        catch (Exception ex) when (ex is not AssistantModelException)
        {
            throw new AssistantModelException($"The Anthropic request failed ({ex.GetType().Name}).");
        }

        var texts = new List<string>();
        var calls = new List<ToolCall>();
        var echo = new List<ContentBlockParam>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? t) && t != null)
            {
                texts.Add(t.Text);
                echo.Add(new TextBlockParam { Text = t.Text });
            }
            else if (block.TryPickThinking(out ThinkingBlock? th) && th != null)
                echo.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt) && rt != null)
                echo.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            else if (block.TryPickToolUse(out ToolUseBlock? tu) && tu != null)
            {
                var input = new JsonObject();
                foreach (var kv in tu.Input) input[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
                calls.Add(new ToolCall(tu.ID, tu.Name, input));
                echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
            }
        }
        if (echo.Count > 0) _history.Add(new AssistantMessageParam { Content = echo });

        var reason = response.StopReason?.Raw();
        var stop = calls.Count > 0 ? ModelStopReason.ToolUse : reason switch
        {
            "end_turn" or "stop_sequence" => ModelStopReason.EndTurn,
            "tool_use" => ModelStopReason.ToolUse,
            "max_tokens" => ModelStopReason.MaxTokens,
            "refusal" => ModelStopReason.Refusal,
            _ => ModelStopReason.Other,
        };
        string? category = null;
        if (stop == ModelStopReason.Refusal) category = response.StopDetails?.Category?.Raw() ?? "refusal";
        return new ModelStep(texts, calls, stop, category);
    }

    private static Tool ToTool(ToolSpec s)
    {
        var props = new Dictionary<string, JsonElement>();
        List<string>? required = null;
        if (s.InputSchema["properties"] is JsonObject po)
            foreach (var kv in po)
                props[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
        if (s.InputSchema["required"] is JsonArray ra)
            required = ra.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        return new Tool
        {
            Name = s.Name,
            Description = s.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }
}
