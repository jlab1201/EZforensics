using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class OpenAiCompatibleModelClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Body)> Seen = new();
        public Queue<Func<HttpResponseMessage>> Responses = new();
        public Exception? Throw;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (Throw != null) throw Throw;
            return Responses.Dequeue()();
        }
        public void Enqueue(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            Responses.Enqueue(() => new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static readonly Uri Base = new(OllamaDefaults.BaseUrl);
    private static readonly ToolSpec Spec = new("count_rows", "Counts rows", JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""")!.AsObject(), ToolKind.Read);

    private static (OpenAiCompatibleModelClient c, FakeHandler h) Make(string? key = null)
    {
        var h = new FakeHandler();
        return (new OpenAiCompatibleModelClient(new HttpClient(h), Base, "llama3", key, "SYS"), h);
    }

    private static string Reply(string message, string finish) =>
        "{\"choices\":[{\"message\":" + message + ",\"finish_reason\":\"" + finish + "\"}]}";

    [Fact]
    public async Task Request_shape_and_no_auth_without_key()
    {
        var (c, h) = Make();
        h.Enqueue(Reply("""{"role":"assistant","content":"hi"}""", "stop"));
        var step = await c.SendUserAsync("hello", new[] { Spec }, default);
        var (req, body) = h.Seen[0];
        req.RequestUri!.ToString().Should().Be("http://localhost:11434/v1/chat/completions");
        req.Headers.Authorization.Should().BeNull();
        var j = JsonNode.Parse(body)!;
        j["model"]!.GetValue<string>().Should().Be("llama3");
        j["stream"]!.GetValue<bool>().Should().BeFalse();
        j["max_tokens"]!.GetValue<int>().Should().Be(4096);
        j["messages"]![0]!["role"]!.GetValue<string>().Should().Be("system");
        j["messages"]![1]!["content"]!.GetValue<string>().Should().Be("hello");
        j["tools"]![0]!["type"]!.GetValue<string>().Should().Be("function");
        j["tools"]![0]!["function"]!["name"]!.GetValue<string>().Should().Be("count_rows");
        j["tools"]![0]!["function"]!["parameters"]!["type"]!.GetValue<string>().Should().Be("object");
        step.Texts.Should().Equal("hi");
        step.StopReason.Should().Be(ModelStopReason.EndTurn);
    }

    [Fact]
    public async Task Bearer_header_when_key_set()
    {
        var (c, h) = Make("sk-SECRET-123");
        h.Enqueue(Reply("""{"role":"assistant","content":"x"}""", "stop"));
        await c.SendUserAsync("q", Array.Empty<ToolSpec>(), default);
        h.Seen[0].Req.Headers.Authorization!.ToString().Should().Be("Bearer sk-SECRET-123");
        JsonNode.Parse(h.Seen[0].Body)!["tools"].Should().BeNull();
    }

    [Fact]
    public async Task Tool_calls_map_and_results_are_separate_tool_messages_in_one_request()
    {
        var (c, h) = Make();
        h.Enqueue(Reply("""{"role":"assistant","content":null,"tool_calls":[{"id":"a1","type":"function","function":{"name":"count_rows","arguments":"{\"x\":\"1\"}"}},{"id":"a2","type":"function","function":{"name":"count_rows","arguments":"{}"}}]}""", "tool_calls"));
        h.Enqueue(Reply("""{"role":"assistant","content":"done"}""", "stop"));
        var s1 = await c.SendUserAsync("go", new[] { Spec }, default);
        s1.StopReason.Should().Be(ModelStopReason.ToolUse);
        s1.ToolCalls.Select(t => t.Id).Should().Equal("a1", "a2");
        s1.ToolCalls[0].Input["x"]!.GetValue<string>().Should().Be("1");

        var s2 = await c.SendToolResultsAsync(new[] { (s1.ToolCalls[0], new ToolOutcome("5", false)), (s1.ToolCalls[1], new ToolOutcome("bad", true)) }, new[] { Spec }, default);
        s2.Texts.Should().Equal("done");
        h.Seen.Should().HaveCount(2);
        var msgs = JsonNode.Parse(h.Seen[1].Body)!["messages"]!.AsArray();
        msgs.Select(m => m!["role"]!.GetValue<string>()).Should().Equal("system", "user", "assistant", "tool", "tool");
        msgs[2]!["tool_calls"]!.AsArray().Should().HaveCount(2);
        msgs[3]!["tool_call_id"]!.GetValue<string>().Should().Be("a1");
        msgs[3]!["content"]!.GetValue<string>().Should().Be("5");
        msgs[4]!["tool_call_id"]!.GetValue<string>().Should().Be("a2");
        msgs[4]!["content"]!.GetValue<string>().Should().StartWith("Error:");
    }

    [Fact]
    public async Task Ollama_quirks_tool_calls_with_stop_missing_ids_bad_arguments()
    {
        var (c, h) = Make();
        h.Enqueue(Reply("""{"role":"assistant","content":"","tool_calls":[{"function":{"name":"count_rows","arguments":"not json"}},{"function":{"name":"count_rows","arguments":{"x":"y"}}}]}""", "stop"));
        var s = await c.SendUserAsync("go", new[] { Spec }, default);
        s.StopReason.Should().Be(ModelStopReason.ToolUse);
        s.ToolCalls.Select(t => t.Id).Should().Equal("call_1", "call_2");
        s.ToolCalls[0].Input.Should().BeEmpty();
        s.ToolCalls[0].ArgumentsInvalid.Should().BeTrue();
        s.ToolCalls[1].Input["x"]!.GetValue<string>().Should().Be("y");
        s.ToolCalls[1].ArgumentsInvalid.Should().BeFalse();
        s.Texts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("length", ModelStopReason.MaxTokens)]
    [InlineData("content_filter", ModelStopReason.Refusal)]
    [InlineData("weird", ModelStopReason.Other)]
    public async Task Finish_reasons(string finish, ModelStopReason expected)
    {
        var (c, h) = Make();
        h.Enqueue(Reply("""{"role":"assistant","content":"t"}""", finish));
        (await c.SendUserAsync("q", Array.Empty<ToolSpec>(), default)).StopReason.Should().Be(expected);
    }

    [Fact]
    public async Task Http_error_has_status_but_never_the_key()
    {
        var (c, h) = Make("sk-SECRET-123");
        h.Enqueue("""{"error":{"type":"invalid_request_error","message":"model not found"}}""", HttpStatusCode.NotFound);
        var act = () => c.SendUserAsync("q", Array.Empty<ToolSpec>(), default);
        var ex = (await act.Should().ThrowAsync<AssistantModelException>()).Which;
        ex.Message.Should().Contain("404").And.Contain("localhost:11434").And.Contain("model not found");
        ex.Message.Should().NotContain("sk-SECRET-123");
    }

    [Fact]
    public async Task Unreachable_host_and_timeout_messages()
    {
        var (c, h) = Make("sk-SECRET-123");
        h.Throw = new HttpRequestException("connection refused sk-SECRET-123");
        var act = () => c.SendUserAsync("q", Array.Empty<ToolSpec>(), default);
        var ex = (await act.Should().ThrowAsync<AssistantModelException>()).Which;
        ex.Message.Should().Contain("could not reach localhost:11434").And.Contain("Ollama");
        ex.Message.Should().NotContain("sk-SECRET");

        var (c2, h2) = Make();
        h2.Throw = new TaskCanceledException();
        var act2 = () => c2.SendUserAsync("q", Array.Empty<ToolSpec>(), default);
        var ex2 = (await act2.Should().ThrowAsync<AssistantModelException>()).Which;
        ex2.Message.Should().Contain("timed out");
    }

    [Fact]
    public async Task ListModels_parses_data_ids()
    {
        var h = new FakeHandler();
        h.Enqueue("""{"object":"list","data":[{"id":"llama3"},{"id":"qwen2.5"}]}""");
        var ids = await OpenAiCompatibleModelClient.ListModelsAsync(new HttpClient(h), new Uri("http://localhost:11434/v1"), "k", default);
        ids.Should().Equal("llama3", "qwen2.5");
        h.Seen[0].Req.Method.Should().Be(HttpMethod.Get);
        h.Seen[0].Req.RequestUri!.ToString().Should().Be("http://localhost:11434/v1/models");
    }

    private sealed class CountTool : IAssistantTool
    {
        public ToolSpec Spec => OpenAiCompatibleModelClientTests.Spec;
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => Task.FromResult(new ToolOutcome("42 rows", false));
    }

    private sealed class NullHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal proposal) { }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(true);
        public void Status(string text) { }
    }

    [Fact]
    public async Task Session_end_to_end_two_round_tool_loop()
    {
        var (c, h) = Make();
        h.Enqueue(Reply("""{"role":"assistant","content":null,"tool_calls":[{"id":"t1","type":"function","function":{"name":"count_rows","arguments":"{}"}}]}""", "tool_calls"));
        h.Enqueue(Reply("""{"role":"assistant","content":"There are 42 rows."}""", "stop"));
        var session = new AssistantSession(c, new IAssistantTool[] { new CountTool() }, new NullHost());
        var said = await session.AskAsync("how many rows?", default);
        said.Should().Equal("There are 42 rows.");
        h.Seen.Should().HaveCount(2);
        var last = JsonNode.Parse(h.Seen[1].Body)!["messages"]!.AsArray();
        last[^1]!["role"]!.GetValue<string>().Should().Be("tool");
        last[^1]!["content"]!.GetValue<string>().Should().Be("42 rows");
    }

    // ---- Ollama native API (a real context size; /v1 cannot set num_ctx and its default cut Spectre's rules off)

    private static (OpenAiCompatibleModelClient c, FakeHandler h) MakeNative(int ctx = 16384)
    {
        var h = new FakeHandler();
        return (new OpenAiCompatibleModelClient(new HttpClient(h), Base, "qwen3:8b", null, "SYS", 4096, ctx), h);
    }

    private static string Native(string message, int promptTokens, string done = "stop") =>
        "{\"message\":" + message + ",\"done\":true,\"done_reason\":\"" + done + "\",\"prompt_eval_count\":" + promptTokens +
        ",\"load_duration\":0,\"prompt_eval_duration\":3400000000,\"eval_count\":17,\"eval_duration\":1700000000}";

    private static string Generated(string response, string done = "stop") =>
        "{\"response\":" + System.Text.Json.JsonSerializer.Serialize(response) + ",\"done\":true,\"done_reason\":\"" + done + "\",\"prompt_eval_count\":900}";

    [Fact]
    public async Task A_model_without_tool_support_gets_the_tools_as_text_and_its_calls_are_read_back()
    {
        var (c, h) = MakeNative();
        h.Enqueue("""{"error":"registry.ollama.ai/FenkoHQ/Foundation-Sec-8B:latest does not support tools"}""", HttpStatusCode.BadRequest);
        h.Enqueue(Generated(" <tool_call>{\"name\": \"count_rows\", \"arguments\": {\"x\": \"a\"}}"));
        var step = await c.SendUserAsync("hello", new[] { Spec }, default);
        JsonNode.Parse(h.Seen[0].Body)!["tools"].Should().NotBeNull();
        h.Seen[1].Req.RequestUri!.ToString().Should().Be("http://localhost:11434/api/generate");
        var gen = JsonNode.Parse(h.Seen[1].Body)!;
        gen["raw"]!.GetValue<bool>().Should().BeTrue();
        gen["prompt"]!.GetValue<string>().Should().StartWith("SYS").And.Contain("- count_rows(x?: string)").And.EndWith("Examiner: hello\n\nSpectre:");
        gen["options"]!["stop"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain(TextToolProtocol.Close);
        gen["options"]!["num_predict"]!.GetValue<int>().Should().Be(TextToolProtocol.MaxReplyTokens);   // not the 4096 chat limit
        step.Texts.Should().Equal(OpenAiCompatibleModelClient.NoToolsNotice);
        step.ToolCalls.Single().Name.Should().Be("count_rows");
        step.ToolCalls.Single().Input["x"]!.GetValue<string>().Should().Be("a");
        step.StopReason.Should().Be(ModelStopReason.ToolUse);
        c.ToolsUnsupported.Should().BeTrue();

        h.Enqueue(Generated(" 3 rows."));
        var next = await c.SendToolResultsAsync(new[] { (step.ToolCalls[0], new ToolOutcome("3 rows", false)) }, new[] { Spec }, default);
        next.Texts.Should().Equal("3 rows.");
        next.StopReason.Should().Be(ModelStopReason.EndTurn);
        h.Seen.Should().HaveCount(3);
        JsonNode.Parse(h.Seen[2].Body)!["prompt"]!.GetValue<string>().Should()
            .Contain("Spectre: <tool_call>{\"name\":\"count_rows\",\"arguments\":{\"x\":\"a\"}}</tool_call>")
            .And.Contain("Tool result (count_rows): 3 rows").And.EndWith("Spectre:");
    }

    [Fact]
    public async Task A_text_reply_that_repeats_an_earlier_answer_is_sampled_once_more()
    {
        var (c, h) = MakeNative();
        h.Enqueue("""{"error":"x does not support tools"}""", HttpStatusCode.BadRequest);
        h.Enqueue(Generated(" The view is Processes.csv."));
        await c.SendUserAsync("hello", new[] { Spec }, default);
        h.Enqueue(Generated(" The view is Processes.csv."));
        h.Enqueue(Generated(" Two odd rows: 12 and 40."));
        var step = await c.SendUserAsync("anything odd?", new[] { Spec }, default);
        step.Texts.Should().Equal("Two odd rows: 12 and 40.");
        h.Seen.Should().HaveCount(4);
        JsonNode.Parse(h.Seen[3].Body)!["options"]!["temperature"]!.GetValue<double>().Should().BeGreaterThan(0.5);
    }

    [Fact]
    public async Task Native_mode_posts_to_api_chat_with_num_ctx_and_reads_tool_calls_and_usage()
    {
        var (c, h) = MakeNative();
        h.Enqueue(Native("""{"role":"assistant","content":"","tool_calls":[{"function":{"name":"count_rows","arguments":{"x":"a"}}}]}""", 5582));
        var step = await c.SendUserAsync("hello", new[] { Spec }, default);
        var (req, body) = h.Seen[0];
        req.RequestUri!.ToString().Should().Be("http://localhost:11434/api/chat");
        var j = JsonNode.Parse(body)!;
        j["options"]!["num_ctx"]!.GetValue<int>().Should().Be(16384);
        j["options"]!["num_predict"]!.GetValue<int>().Should().Be(4096);
        j["max_tokens"].Should().BeNull();
        j["think"].Should().BeNull();
        step.ToolCalls.Single().Name.Should().Be("count_rows");
        step.ToolCalls.Single().Input["x"]!.GetValue<string>().Should().Be("a");
        step.StopReason.Should().Be(ModelStopReason.ToolUse);
        c.LastPromptTokens.Should().Be(5582);
        c.ContextTokens.Should().Be(16384);
        c.LastTimings.Should().Be("load 0.0s, prompt 3.4s, 17 tokens in 1.7s");

        h.Enqueue(Native("""{"role":"assistant","content":"Done."}""", 5700));
        var next = await c.SendToolResultsAsync(new[] { (step.ToolCalls[0], new ToolOutcome("3 rows", false)) }, new[] { Spec }, default);
        var tool = JsonNode.Parse(h.Seen[1].Body)!["messages"]!.AsArray().Last()!;
        tool["role"]!.GetValue<string>().Should().Be("tool");
        tool["tool_name"]!.GetValue<string>().Should().Be("count_rows");
        next.Texts.Should().Equal("Done.");
        next.StopReason.Should().Be(ModelStopReason.EndTurn);
    }

    [Fact]
    public async Task Native_mode_drops_the_oldest_turns_when_the_window_is_nearly_full_but_keeps_the_system_prompt()
    {
        var (c, h) = MakeNative(1000);
        for (int i = 1; i <= 4; i++)
        {
            h.Enqueue(Native("{\"role\":\"assistant\",\"content\":\"a" + i + "\"}", i < 4 ? 900 : 100));
            await c.SendUserAsync("q" + i, Array.Empty<ToolSpec>(), default);
        }
        // every request but the last used 90%: q3 is sent without q1, q4 without q1 and q2
        var msgs = JsonNode.Parse(h.Seen[3].Body)!["messages"]!.AsArray();
        msgs[0]!["content"]!.GetValue<string>().Should().Be("SYS");
        msgs.Select(m => m!["content"]!.GetValue<string>()).Should().NotContain(new[] { "q1", "q2" }).And.Contain(new[] { "q3", "q4" });
    }

    [Fact]
    public void Native_root_strips_the_v1_suffix()
    {
        OpenAiCompatibleModelClient.NativeRoot(new Uri("http://localhost:11434/v1/")).ToString().Should().Be("http://localhost:11434/");
        OpenAiCompatibleModelClient.NativeRoot(new Uri("http://gpu-box:11434/v1")).ToString().Should().Be("http://gpu-box:11434/");
        OpenAiCompatibleModelClient.NativeRoot(new Uri("http://gpu-box:8080/")).ToString().Should().Be("http://gpu-box:8080/");
    }
}
