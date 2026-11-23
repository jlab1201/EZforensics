using System.Text.Json.Nodes;

/// <summary>
/// Fake MCP server over stdio lines: "--fake-mcp [--hang] [--crash-after-init]" (--hang: tools/call never answers;
/// --crash-after-init: exits right after answering initialize). "--fake-touch &lt;path&gt;" just creates the file (cancel-command tests).
/// </summary>
internal static class FakeMcpTool
{
    public static bool Handles(string[] args) => args.Length >= 1 && (args[0] == "--fake-mcp" || args[0] == "--fake-touch");

    public static int Run(string[] args)
    {
        if (args[0] == "--fake-touch")
        {
            File.WriteAllText(args[1], "x");
            return 0;
        }
        Console.Error.WriteLine("fake-mcp: started");
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            JsonObject msg;
            try { msg = JsonNode.Parse(line)!.AsObject(); } catch { continue; }
            var id = msg["id"]?.DeepClone();
            var method = (string?)msg["method"];
            if (id is null) continue; // notification
            if (args.Contains("--hang") && method == "tools/call") { Thread.Sleep(Timeout.Infinite); }
            if (method == "tools/call" && (string?)msg["params"]?["name"] == "fail")
            {
                Reply(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "bad params" } });
                continue;
            }
            JsonNode result = method switch
            {
                "initialize" => new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                                                 ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "0" } },
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(
                    new JsonObject {
                        ["name"] = "echo", ["description"] = "Echo.",
                        ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}},"required":["x"],"additionalProperties":false}"""),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = true } },
                    new JsonObject {
                        ["name"] = "mutate", ["description"] = "Writes.",
                        ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""") }) },
                "tools/call" => new JsonObject { ["isError"] = false, ["content"] = EchoContent(msg["params"]?["arguments"]?["x"]?.GetValue<string>() ?? "") },
                _ => new JsonObject(),
            };
            if (method == "tools/list") { Console.Out.WriteLine("this is not json"); } // noise the client must ignore
            Reply(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            if (args.Contains("--crash-after-init") && method == "initialize") return 3;
        }
        return 0;
    }

    /// <summary>The echoed text; "image:" prefixed input also adds a non-text item.</summary>
    private static JsonArray EchoContent(string x)
    {
        var items = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = x.StartsWith("image:") ? x[6..] : x });
        if (x.StartsWith("image:")) items.Add(new JsonObject { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" });
        return items;
    }

    private static void Reply(JsonObject o)
    {
        Console.Out.WriteLine(o.ToJsonString());
        Console.Out.Flush();
    }
}
