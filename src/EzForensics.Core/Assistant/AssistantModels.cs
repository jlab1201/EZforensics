using System.Text.Json.Nodes;
using EzForensics.Core.Results;

namespace EzForensics.Core.Assistant;

/// <summary>What a tool may do. <c>Read</c> runs freely; <c>Proposal</c> only shows a proposal to the examiner; <c>Write</c> needs examiner confirmation first.</summary>
public enum ToolKind { Read, Proposal, Write }

/// <summary>Why the model stopped, independent of the provider.</summary>
public enum ModelStopReason { EndTurn, ToolUse, Refusal, MaxTokens, Other }

/// <summary>A tool offered to the model (JSON-schema input).</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema, ToolKind Kind);

/// <summary>A tool call requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, JsonObject Input)
{
    /// <summary>True when the model sent arguments that were not a JSON object (<see cref="Input"/> is then empty).</summary>
    public bool ArgumentsInvalid { get; init; }
}

/// <summary>Result of running a tool, sent back to the model.</summary>
public sealed record ToolOutcome(string Text, bool IsError);

/// <summary>One model response: text blocks, requested tool calls and the stop reason.</summary>
public sealed record ModelStep(IReadOnlyList<string> Texts, IReadOnlyList<ToolCall> ToolCalls, ModelStopReason StopReason, string? RefusalCategory = null);

/// <summary>Something the assistant suggests; the examiner applies it (or not).</summary>
public abstract record AssistantProposal(string ToolCallId);

/// <summary>A filter for the grid. <paramref name="CsvPath"/> is the CSV that was open when it was proposed.</summary>
public sealed record FilterProposal(string ToolCallId, FilterGroup Filter, string Display, string? CsvPath) : AssistantProposal(ToolCallId);

/// <summary>Rows to flag. <paramref name="Rows"/> are source row ids of <paramref name="CsvPath"/> (the CSV open when proposed).</summary>
public sealed record FlagProposal(string ToolCallId, string? CsvPath, IReadOnlyList<long> Rows, string FlagId, string Reason) : AssistantProposal(ToolCallId);

/// <summary>Open a case CSV in Analyze, optionally filtered. <paramref name="File"/> is the full path; <paramref name="Filter"/> was validated against that file's headers.</summary>
public sealed record OpenFileProposal(string ToolCallId, string File, FilterGroup? Filter, string Display, string Reason) : AssistantProposal(ToolCallId);

/// <summary>Clear every filter of the open CSV (<paramref name="CsvPath"/> is the CSV open when proposed).</summary>
public sealed record ClearFilterProposal(string ToolCallId, string? CsvPath) : AssistantProposal(ToolCallId);

/// <summary>Remove the flag from rows (source row ids of <paramref name="CsvPath"/>).</summary>
public sealed record UnflagProposal(string ToolCallId, string? CsvPath, IReadOnlyList<long> Rows, string Reason) : AssistantProposal(ToolCallId);

/// <summary>Sort the open CSV by <paramref name="Column"/> (an exact header of <paramref name="CsvPath"/>).</summary>
public sealed record SortProposal(string ToolCallId, string? CsvPath, string Column, bool Descending) : AssistantProposal(ToolCallId);

/// <summary>Select a source row of the open CSV and scroll it into view.</summary>
public sealed record GoToRowProposal(string ToolCallId, string? CsvPath, long Row) : AssistantProposal(ToolCallId);

/// <summary>Owns the provider-specific, append-only conversation history.</summary>
public interface IModelClient
{
    Task<ModelStep> SendUserAsync(string text, IReadOnlyList<ToolSpec> tools, CancellationToken ct);
    /// <summary>Sends the results of every tool call of the previous step in one message.</summary>
    Task<ModelStep> SendToolResultsAsync(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results, IReadOnlyList<ToolSpec> tools, CancellationToken ct);
}

/// <summary>A model client that knows how much of its context window the last request used (diagnostics only).</summary>
public interface IModelUsage
{
    /// <summary>Prompt tokens of the last request (null = not reported).</summary>
    int? LastPromptTokens { get; }
    /// <summary>The context window the client asks for (null = server default, unknown).</summary>
    int? ContextTokens { get; }
    /// <summary>Where the last request's time went ("load 0.0s, prompt 3.4s, 17 tokens in 1.7s"); null = not reported.</summary>
    string? LastTimings { get; }
}

/// <summary>A tool the assistant can call.</summary>
public interface IAssistantTool
{
    ToolSpec Spec { get; }
    Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct);
}

/// <summary>The app side of a session: shows proposals, confirms writes, reports status.</summary>
public interface IAssistantHost
{
    void ShowProposal(AssistantProposal proposal);
    /// <summary>
    /// What proposal tools call. Shows the proposal (and, in auto-mode, applies it) and returns the text the model gets back, so it
    /// knows what really happened. The default just shows it: <see cref="AssistantGridTools.ProposedText"/>.
    /// </summary>
    Task<string> HandleProposalAsync(AssistantProposal proposal)
    {
        ShowProposal(proposal);
        return Task.FromResult(AssistantGridTools.ProposedText);
    }
    Task<bool> ConfirmWriteAsync(ToolCall call);
    void Status(string text);

    /// <summary>The examiner's own words for the request being answered (without the context the app adds); null when unknown.</summary>
    string? Question => null;

    /// <summary>A tool call finished: the app shows it as a step line under the reply so the examiner sees what Spectre did.</summary>
    void ToolStep(ToolCall call, ToolOutcome outcome) { }
}

/// <summary>A model/provider failure. The message is safe to show: it never holds request data, headers or keys.</summary>
public sealed class AssistantModelException : Exception
{
    public AssistantModelException(string message) : base(message) { }
    public AssistantModelException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    /// <summary>HTTP status when the failure was an HTTP error response; null otherwise.</summary>
    public int? StatusCode { get; }
    public AssistantModelException(string message, Exception inner) : base(message, inner) { }
}
