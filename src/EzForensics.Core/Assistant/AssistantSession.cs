using System.Text.RegularExpressions;

namespace EzForensics.Core.Assistant;

/// <summary>One conversation. Facts come from tools; proposals are never applied here.</summary>
public sealed class AssistantSession
{
    private static readonly Regex KeyLike = new(@"sk-[A-Za-z0-9_\-]{4,}", RegexOptions.Compiled);

    private readonly IModelClient _model;
    private Dictionary<string, IAssistantTool> _tools;
    private IReadOnlyList<ToolSpec> _specs;
    private readonly IAssistantHost _host;
    private readonly int _maxRounds;
    private readonly Action<string>? _log;

    /// <param name="log">Diagnostics: tool names, whether their arguments parsed and ok/error per call. Never argument values or results.</param>
    public AssistantSession(IModelClient model, IReadOnlyList<IAssistantTool> tools, IAssistantHost host, int maxToolRounds = 25, Action<string>? log = null)
    {
        _model = model; _host = host; _maxRounds = maxToolRounds; _log = log;
        _tools = null!; _specs = null!;
        SetTools(tools);
    }

    /// <summary>Replaces the offered tools (the examiner moved to another page). Takes effect with the next <see cref="AskAsync"/>.</summary>
    public void SetTools(IReadOnlyList<IAssistantTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Spec.Name, StringComparer.Ordinal);
        _specs = tools.Select(t => t.Spec).ToList();
    }

    /// <summary>Names of the tools offered now.</summary>
    public IReadOnlyList<string> ToolNames => _specs.Select(s => s.Name).ToList();

    /// <summary>Sends the user's text and runs tool rounds until the model answers. Returns what the model said (plus notices).</summary>
    /// <param name="question">The examiner's own words, without the context blocks the app adds to <paramref name="userText"/>;
    /// used to tell whether they asked for an action. Null = <paramref name="userText"/>.</param>
    /// <exception cref="AssistantModelException">The model call failed; the message is sanitized.</exception>
    public async Task<IReadOnlyList<string>> AskAsync(string userText, CancellationToken ct, string? question = null)
    {
        var said = new List<string>();
        bool ranTools = false, nudged = false, proposed = false, actionNudged = false;
        var actionNames = _specs.Where(s => s.Kind == ToolKind.Proposal || SpectrePageTools.CrossPage.Contains(s.Name)).Select(s => s.Name).ToList();
        var wantsAction = _specs.Any(s => s.Kind == ToolKind.Proposal) && SpectreSkills.IsActionRequest(question ?? userText);
        var actionTool = SpectreSkills.ActionToolFor(question ?? userText, actionNames);
        var keepFrom = 0;
        var step = await CallModelAsync(() => _model.SendUserAsync(userText, _specs, ct)).ConfigureAwait(false);
        for (int round = 0; ; round++)
        {
            foreach (var t in step.Texts.Where(t => !string.IsNullOrWhiteSpace(t)))
                if (!said.Contains(t.Trim())) said.Add(t.Trim());   // a reminded model often repeats its earlier answer word for word
            if (_model is IModelUsage { LastPromptTokens: { } used } u)
                _log?.Invoke($"Spectre round {round}: prompt {used} tokens" + (u.ContextTokens is { } ctx ? $" of {ctx}" : "") + (u.LastTimings is { } t ? $" ({t})" : "") + ".");
            _log?.Invoke(step.ToolCalls.Count == 0
                ? $"Spectre round {round}: model answered without a tool call (stop: {step.StopReason})."
                : $"Spectre round {round}: model asked for " + string.Join(", ", step.ToolCalls.Select(c => LogName(c.Name) + (c.ArgumentsInvalid ? " (arguments not JSON)" : ""))));
            if (step.StopReason == ModelStopReason.Refusal)
            {
                said.Add(N("refusal", ("category", step.RefusalCategory ?? "unspecified")));
                return said;
            }
            if (step.ToolCalls.Count == 0 && said.Count == 0 && !nudged && step.StopReason != ModelStopReason.MaxTokens)
            {
                // Small thinking models (qwen3) sometimes end a turn with nothing but hidden thinking: ask once more instead of showing nothing.
                nudged = true;
                _log?.Invoke("Spectre: empty reply, asking the model once more.");
                var nudge = ranTools ? EmptyAfterToolsNudge : EmptyReplyNudge;
                step = await CallModelAsync(() => _model.SendUserAsync(nudge, _specs, ct)).ConfigureAwait(false);
                continue;
            }
            // a reply that says it did something ("I applied grid_apply_filter ...") without a tool call changed nothing either
            var claimed = step.ToolCalls.Count == 0 && !proposed && actionNames.Count > 0 && step.Texts.Any(SpectreSkills.ClaimsAction);
            if (step.ToolCalls.Count == 0 && (wantsAction || claimed) && !proposed && !actionNudged)
            {
                // Small models answer "flag them" with a list, or say "Filtered on Name" without calling the tool: remind them once.
                // If they then act, the earlier text (the list or the false claim) is dropped so the examiner sees the one-line reply.
                actionNudged = true;
                keepFrom = said.Count;
                var tool = actionTool ?? step.Texts.Select(t => SpectreSkills.ToolNamedIn(t, actionNames)).FirstOrDefault(n => n != null);
                _log?.Invoke(wantsAction ? "Spectre: action asked but no action tool called, reminding the model once."
                    : "Spectre: the reply claims an action but no tool was called, reminding the model once.");
                step = await CallModelAsync(() => _model.SendUserAsync(ActionNudgeFor(tool, actionNames), _specs, ct)).ConfigureAwait(false);
                continue;
            }
            if (step.ToolCalls.Count == 0)
            {
                if (actionNudged && proposed && keepFrom > 0) said.RemoveRange(0, keepFrom);
                // still no action after the reminder: whatever it says ("I applied a filter"), the app did not change, so a claim
                // that it did is dropped rather than shown as if it were true
                if (actionNudged && !proposed && !(said.LastOrDefault()?.EndsWith('?') ?? false))
                {
                    said.RemoveAll(SpectreSkills.ClaimsAction);
                    said.Add(NothingApplied);
                }
                if (step.StopReason == ModelStopReason.MaxTokens) said.Add(N("cut_off"));
                if (said.Count == 0)
                {
                    _log?.Invoke("Spectre: the reply stayed empty after asking again.");
                    said.Add(proposed ? EmptyAfterAction : ranTools ? EmptyAfterRead : EmptyNoTool);
                }
                return said;
            }
            if (round >= _maxRounds) { said.Add(N("max_rounds", ("max", _maxRounds.ToString()))); return said; }

            ranTools = true;
            var results = new List<(ToolCall Call, ToolOutcome Outcome)>();
            foreach (var call in step.ToolCalls)   // results of ALL calls go back in ONE message
            {
                var outcome = await RunOneAsync(call, ct).ConfigureAwait(false);
                if (!outcome.IsError && _tools.TryGetValue(call.Name, out var ran) && (ran.Spec.Kind == ToolKind.Proposal || SpectrePageTools.CrossPage.Contains(call.Name))) proposed = true;   // explore_search acts without a card
                _log?.Invoke($"Spectre tool {LogName(call.Name)}: {(outcome.IsError ? "error" : "ok")}");
                try { _host.ToolStep(call, outcome); } catch (Exception ex) { _log?.Invoke($"Spectre step line failed ({ex.GetType().Name})."); }
                results.Add((call, outcome));
            }
            step = await CallModelAsync(() => _model.SendToolResultsAsync(results, _specs, ct)).ConfigureAwait(false);
        }
    }

    private static string N(string key, params (string, string)[] v) => PromptStore.Section("nudges", key, v);

    /// <summary>Sent once when the examiner asked for an action and the model answered without calling an action tool.</summary>
    public static string ActionNudge => N("action_nudge");

    /// <summary>
    /// The reminder, naming the one tool when the examiner's verb points to it ("filter ..." = grid_apply_filter); otherwise listing
    /// the action tools offered on this page (<paramref name="offered"/>; null = all of them).
    /// </summary>
    public static string ActionNudgeFor(string? tool, IReadOnlyCollection<string>? offered = null) => tool != null
        ? N("action_nudge_tool", ("tool", tool))
        : offered is not { Count: > 0 } ? ActionNudge
        : N("action_nudge_offered", ("tools", string.Join(", ", offered)));

    /// <summary>Added when the examiner asked for an action and the model still called no action tool after the reminder.</summary>
    public static string NothingApplied => N("nothing_applied");

    /// <summary>Sent once when the model's whole reply was empty.</summary>
    public static string EmptyReplyNudge => N("empty_reply");

    /// <summary>Sent once when the model ran tools and then said nothing.</summary>
    public static string EmptyAfterToolsNudge => N("empty_after_tools");

    /// <summary>Shown instead of an empty reply (the model said nothing even when asked again).</summary>
    public static string EmptyAfterAction => N("empty_after_action");
    public static string EmptyAfterRead => N("empty_after_read");
    public static string EmptyNoTool => N("empty_no_tool");

    private static async Task<ModelStep> CallModelAsync(Func<Task<ModelStep>> send)
    {
        try { return await send().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (AssistantModelException ex) { throw new AssistantModelException(Redact(ex.Message)); }
        catch (Exception ex) { throw new AssistantModelException($"The model request failed ({ex.GetType().Name})."); }
    }

    private async Task<ToolOutcome> RunOneAsync(ToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool)) return new($"Unknown tool '{call.Name}'.", true);
        if (call.ArgumentsInvalid) return new($"The arguments of {call.Name} were not a JSON object. Call it again with JSON arguments.", true);
        _host.Status($"Running {call.Name}...");
        if (tool.Spec.Kind == ToolKind.Write && !await _host.ConfirmWriteAsync(call).ConfigureAwait(false))
            return new("The examiner declined this write. Do not retry it unless asked.", true);
        try { return await tool.RunAsync(call, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new($"{call.Name} failed: {Redact(ex.Message)}", true);   // message only: never request data
        }
    }

    /// <summary>A tool name as the model sent it, safe for the log (it is model output): name characters only, at most 64.</summary>
    private static string LogName(string name)
    {
        var n = new string(name.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.').Take(64).ToArray());
        return n.Length == 0 ? "(unnamed)" : n;
    }

    private static string Redact(string s) => KeyLike.Replace(s, "[redacted]");
}
