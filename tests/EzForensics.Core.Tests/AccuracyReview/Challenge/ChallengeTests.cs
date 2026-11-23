using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests.AccuracyReview.Challenge;

/// <summary>
/// Second-review counter-tests. "Passes on main" tests show a first-review claim is wrong; "fails on main" tests are new repros.
/// </summary>
public sealed class ChallengeTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezchal-" + Guid.NewGuid().ToString("N"));

    private sealed class Host : IAssistantHost
    {
        public void ShowProposal(AssistantProposal p) { }
        public Task<bool> ConfirmWriteAsync(ToolCall c) => Task.FromResult(false);
        public void Status(string t) { }
    }

    public ChallengeTests() => Directory.CreateDirectory(Path.Combine(_case, "PC1"));
    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_case, "PC1", name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return Path.Combine("PC1", name);
    }

    private static async Task<ToolOutcome> Run(AssistantCaseTools t, string name, JsonObject input) =>
        await t.Tools.Single(x => x.Spec.Name == name).RunAsync(new ToolCall("c1", name, input), default);

    // Item 2 claims "the same engine serves Spectre's csv_query" so an unknown column is silently dropped there too.
    // Passes on main: csv_query rejects it (AssistantCaseTools.MatchAsync -> AssistantGrid.ParseFilter -> MissingColumns).
    [Fact]
    public async Task Item2_csv_query_rejects_a_filter_on_an_unknown_column()
    {
        var f = Write("a.csv", "A,B\nx,1\ny,2\n");
        using var t = new AssistantCaseTools(() => _case, new Host());
        var o = await Run(t, "csv_query", new JsonObject { ["file"] = f, ["filter"] = "Not ([Missing] = 'q')" });
        o.IsError.Should().BeTrue();
        o.Text.Should().Contain("Unknown column");
    }

    // Item 22 is understated: past MaxDistinct, values first seen later are never counted, so the TOP list can miss the most
    // frequent value, not only the distinct count. Fails on main.
    [Fact]
    public async Task Item22_most_frequent_value_seen_after_the_distinct_cap_is_still_listed()
    {
        var sb = new StringBuilder("V\n");
        for (int r = 0; r < 50_000; r++) sb.Append("u").Append(r).Append('\n');
        for (int r = 0; r < 500; r++) sb.Append("late.exe\n");
        var f = Write("cap.csv", sb.ToString());
        using var t = new AssistantCaseTools(() => _case, new Host());
        var o = await Run(t, "csv_column_values", new JsonObject { ["file"] = f, ["column"] = "V", ["top"] = 5 });
        o.Text.Should().Contain("500 x late.exe", "late.exe is by far the most frequent value; output was:\n" + o.Text);
    }

    // New: merging / the combined view renames an input column called SourceFile (EvtxECmd has one: the .evtx path) to
    // "SourceFile (2)" and puts the CSV file name under "SourceFile". Saved filters on [SourceFile] then match the wrong values. Fails on main.
    [Fact]
    public async Task New_merge_keeps_EvtxECmd_SourceFile_values_under_SourceFile()
    {
        var a = Path.Combine(_case, "PC1", "EvtxECmd_Output.csv");
        var b = Path.Combine(_case, "PC1", "EvtxECmd_Output2.csv");
        File.WriteAllText(a, "EventId,SourceFile\n4688,C:\\Windows\\System32\\winevt\\Logs\\Security.evtx\n");
        File.WriteAllText(b, "EventId,SourceFile\n4624,C:\\Windows\\System32\\winevt\\Logs\\Security.evtx\n");
        var outp = Path.Combine(_case, "merged.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a, "PC1"), new CsvMergeInput(b, "PC1") }, outp);
        var lines = File.ReadAllLines(outp);
        var headers = lines[0].Split(',');
        var col = Array.IndexOf(headers, "SourceFile");
        lines[1].Split(',')[col].Should().EndWith("Security.evtx", "header: " + lines[0] + " row: " + lines[1]);
    }
}
