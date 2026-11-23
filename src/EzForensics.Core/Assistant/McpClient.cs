using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

public sealed record McpToolInfo(string Name, string Description, JsonObject InputSchema, bool ReadOnly);
public sealed record McpToolResult(bool IsError, string Text);

/// <summary>Minimal MCP (JSON-RPC 2.0 over stdio lines) client: one reader task, pending calls matched by id.</summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly StdioProcessHost _host;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly object _gate = new();
    private long _nextId;
    private bool _closed;

    private McpClient(StdioProcessHost host, Action<string>? log)
    {
        _host = host; _log = log;
        _reader = Task.Run(ReadLoopAsync);
    }

    public static async Task<McpClient> ConnectAsync(StdioProcessHost host, TimeSpan timeout, CancellationToken ct, Action<string>? log = null)
    {
        var c = new McpClient(host, log);
        try
        {
            await c.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "EZ Forensics", ["version"] = typeof(McpClient).Assembly.GetName().Version?.ToString(3) ?? "0" },
            }, timeout, ct).ConfigureAwait(false);
            try { await host.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(), ct).ConfigureAwait(false); }
            catch (IOException) { /* server already gone; the next call reports it */ }
            return c;
        }
        catch { await c.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>The server process ended (or the client was disposed); a new connection is needed.</summary>
    public bool IsClosed { get { lock (_gate) return _closed; } }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        do
        {
            var p = new JsonObject();
            if (cursor != null) p["cursor"] = cursor;
            var r = await RequestAsync("tools/list", p, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (r["error"] is JsonNode err) throw new IOException("tools/list failed: " + (string?)err["message"]);
            var res = r["result"] as JsonObject;
            foreach (var t in res?["tools"] as JsonArray ?? new JsonArray())
            {
                if (t is not JsonObject o || (string?)o["name"] is not { } name) continue;
                var schema = o["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object" };
                var ro = o["annotations"]?["readOnlyHint"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                tools.Add(new McpToolInfo(name, (string?)o["description"] ?? "", schema, ro));
            }
            cursor = (string?)res?["nextCursor"];
        } while (!string.IsNullOrEmpty(cursor));
        return tools;
    }

    /// <summary>Full reply text; callers that feed a model apply <see cref="AssistantCaseTools.CapText"/> themselves.</summary>
    public async Task<McpToolResult> CallToolAsync(string name, JsonObject args, TimeSpan timeout, CancellationToken ct)
    {
        var r = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, timeout, ct).ConfigureAwait(false);
        if (r["error"] is JsonNode err) return new McpToolResult(true, (string?)err["message"] ?? "MCP error");
        var res = r["result"] as JsonObject;
        var items = (res?["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var text = string.Join("\n", items.Where(o => (string?)o["type"] == "text").Select(o => (string?)o["text"] ?? ""));
        var dropped = items.Count(o => (string?)o["type"] != "text");
        if (dropped > 0) text += $"\n({dropped} non-text content item(s) omitted)";
        text = CapReply(text);
        var isErr = res?["isError"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return new McpToolResult(isErr, text);
    }

    /// <summary>Hard ceiling on one engine reply, in characters; callers still apply their own, smaller model cap.</summary>
    public const int MaxReplyChars = 1_000_000;

    public static string CapReply(string text) =>
        text.Length <= MaxReplyChars ? text : text[..MaxReplyChars] + $"\n(engine output truncated: {text.Length - MaxReplyChars:N0} more character(s) not shown)";

    private async Task<JsonObject> RequestAsync(string method, JsonObject p, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed) throw new IOException("Spectre stopped");
            _pending[id] = tcs;
        }
        try
        {
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await _host.WriteLineAsync(msg.ToJsonString(), cts.Token).ConfigureAwait(false);
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"MCP {method} timed out after {timeout.TotalSeconds:0} s");
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var line in _host.ReadLinesAsync(_stop.Token).ConfigureAwait(false))
            {
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { _log?.Invoke("stdout: " + line); continue; }
                if (msg == null) { _log?.Invoke("stdout: " + line); continue; }
                if (msg["id"] is JsonValue idv && msg["method"] == null && idv.TryGetValue<long>(out var id)
                    && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(msg);
                // server requests / notifications are ignored
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_gate) _closed = true;
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var t)) t.TrySetException(new IOException("Spectre stopped"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false); // closes stdin, ends the process, which ends the reader
        _stop.Cancel();
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
    }
}
