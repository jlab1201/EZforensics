using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AnthropicModelClientTests
{
    private const string Key = "sk-ant-SECRETKEY-123456";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Body)> Seen = new();
        public readonly Queue<(string Json, HttpStatusCode Code)> Responses = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var (json, code) = Responses.Dequeue();
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        public void Enqueue(string json, HttpStatusCode code = HttpStatusCode.OK) => Responses.Enqueue((json, code));
    }

    private static readonly ToolSpec Spec = new("count_rows", "Counts rows",
        JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}},"required":["x"]}""")!.AsObject(), ToolKind.Read);

    private static (AnthropicModelClient c, FakeHandler h) Make(string? model = null)
    {
        var h = new FakeHandler();
        return (new AnthropicModelClient(Key, model, "SYS", http: new HttpClient(h), baseUrl: "http://localhost:9"), h);
    }

    private static string Msg(string content, string stop, string extra = "") =>
        "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":" + content +
        ",\"stop_reason\":\"" + stop + "\"" + extra + ",\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    [Fact]
    public async Task Text_turn_request_shape_and_key_only_in_header()
    {
        var (c, h) = Make();
        h.Enqueue(Msg("""[{"type":"text","text":"hi"}]""", "end_turn"));
        var step = await c.SendUserAsync("hello", new[] { Spec }, default);
        step.Texts.Should().Equal("hi");
        step.StopReason.Should().Be(ModelStopReason.EndTurn);

        var (req, body) = h.Seen[0];
        req.RequestUri!.ToString().Should().NotContain("SECRETKEY");
        req.RequestUri.AbsolutePath.Should().EndWith("/v1/messages");
        var j = JsonNode.Parse(body)!;
        j["model"]!.GetValue<string>().Should().Be(AnthropicDefaults.Model);
        j["max_tokens"]!.GetValue<int>().Should().Be(16000);
        j["thinking"]!["type"]!.GetValue<string>().Should().Be("adaptive");
        j["output_config"]!["effort"]!.GetValue<string>().Should().Be("medium");
        foreach (var banned in new[] { "temperature", "top_p", "tool_choice" }) j[banned].Should().BeNull();
        j["thinking"]!["budget_tokens"].Should().BeNull();
        j["tools"]![0]!["name"]!.GetValue<string>().Should().Be("count_rows");
        j["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>().Should().Be("object");
        j["tools"]![0]!["input_schema"]!["required"]![0]!.GetValue<string>().Should().Be("x");
        body.Should().NotContain("SECRETKEY");
    }

    [Fact]
    public async Task Tool_use_round_trip_echoes_thinking_and_sends_one_user_message_of_results()
    {
        var (c, h) = Make("claude-custom");
        h.Enqueue(Msg("""
            [{"type":"thinking","thinking":"hmm","signature":"SIG123"},
             {"type":"text","text":"checking"},
             {"type":"tool_use","id":"tu_1","name":"count_rows","input":{"x":"1"}},
             {"type":"tool_use","id":"tu_2","name":"count_rows","input":{"x":"2"}}]
            """, "tool_use"));
        h.Enqueue(Msg("""[{"type":"text","text":"done"}]""", "end_turn"));

        var s1 = await c.SendUserAsync("go", new[] { Spec }, default);
        s1.StopReason.Should().Be(ModelStopReason.ToolUse);
        s1.Texts.Should().Equal("checking");
        s1.ToolCalls.Select(t => t.Id).Should().Equal("tu_1", "tu_2");
        s1.ToolCalls[0].Input["x"]!.GetValue<string>().Should().Be("1");

        var s2 = await c.SendToolResultsAsync(new[] { (s1.ToolCalls[0], new ToolOutcome("5", false)), (s1.ToolCalls[1], new ToolOutcome("bad", true)) }, new[] { Spec }, default);
        s2.Texts.Should().Equal("done");

        var j = JsonNode.Parse(h.Seen[1].Body)!;
        j["model"]!.GetValue<string>().Should().Be("claude-custom");
        var msgs = j["messages"]!.AsArray();
        msgs.Select(m => m!["role"]!.GetValue<string>()).Should().Equal("user", "assistant", "user");
        var asst = msgs[1]!["content"]!.AsArray();
        asst.Select(b => b!["type"]!.GetValue<string>()).Should().Equal("thinking", "text", "tool_use", "tool_use");
        asst[0]!["signature"]!.GetValue<string>().Should().Be("SIG123");
        asst[2]!["id"]!.GetValue<string>().Should().Be("tu_1");
        asst[2]!["input"]!["x"]!.GetValue<string>().Should().Be("1");
        var results = msgs[2]!["content"]!.AsArray();
        results.Should().HaveCount(2);
        results[0]!["type"]!.GetValue<string>().Should().Be("tool_result");
        results[0]!["tool_use_id"]!.GetValue<string>().Should().Be("tu_1");
        results[0]!["content"]!.GetValue<string>().Should().Be("5");
        results[1]!["tool_use_id"]!.GetValue<string>().Should().Be("tu_2");
        results[1]!["is_error"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Refusal_maps_with_category()
    {
        var (c, h) = Make();
        h.Enqueue(Msg("[]", "refusal", ",\"stop_details\":{\"type\":\"refusal\",\"category\":\"cyber\",\"explanation\":null}"));
        var step = await c.SendUserAsync("q", Array.Empty<ToolSpec>(), default);
        step.StopReason.Should().Be(ModelStopReason.Refusal);
        step.RefusalCategory.Should().Be("cyber");
    }

    [Fact]
    public async Task Max_tokens_maps()
    {
        var (c, h) = Make();
        h.Enqueue(Msg("""[{"type":"text","text":"cut"}]""", "max_tokens"));
        (await c.SendUserAsync("q", Array.Empty<ToolSpec>(), default)).StopReason.Should().Be(ModelStopReason.MaxTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limited")]
    [InlineData(HttpStatusCode.InternalServerError, "service error")]
    public async Task Http_errors_become_friendly_exceptions_without_the_key(HttpStatusCode code, string expected)
    {
        var (c, h) = Make();
        for (var i = 0; i < 5; i++)   // the SDK retries 429/5xx
            h.Enqueue($"{{\"type\":\"error\",\"error\":{{\"type\":\"x\",\"message\":\"bad key {Key}\"}}}}", code);
        var act = () => c.SendUserAsync("q", Array.Empty<ToolSpec>(), default);
        var ex = (await act.Should().ThrowAsync<AssistantModelException>()).Which;
        ex.Message.Should().Contain(expected).And.NotContain("SECRETKEY");
        ex.ToString().Should().NotContain("SECRETKEY");
        h.Seen.All(s => !s.Req.RequestUri!.ToString().Contains("SECRETKEY")).Should().BeTrue();
    }
}
