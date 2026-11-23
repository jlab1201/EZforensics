using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests.AccuracyReview;

public sealed class SpectreCaseToolsAccuracyTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezacc-" + Guid.NewGuid().ToString("N"));

    private sealed class Host : IAssistantHost
    {
        public void ShowProposal(AssistantProposal p) { }
        public Task<bool> ConfirmWriteAsync(ToolCall c) => Task.FromResult(false);
        public void Status(string t) { }
    }

    public SpectreCaseToolsAccuracyTests() => Directory.CreateDirectory(Path.Combine(_case, "PC1"));
    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_case, "PC1", name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return Path.Combine("PC1", name);
    }

    private static async Task<ToolOutcome> Run(AssistantCaseTools t, string name, JsonObject input) =>
        await t.Tools.Single(x => x.Spec.Name == name).RunAsync(new ToolCall("c1", name, input), default);

    private AssistantCaseTools Tools() => new(() => _case, new Host(), false);

    // 9a: csv_query footer survives the 8000-char cap on wide rows
    [Fact]
    public async Task Item9a_csv_query_keeps_its_Showing_footer_on_wide_rows()
    {
        var sb = new StringBuilder();
        var cols = Enumerable.Range(0, 40).Select(i => "C" + i).ToArray();
        sb.AppendLine(string.Join(",", cols));
        for (int r = 0; r < 30; r++) sb.AppendLine(string.Join(",", cols.Select(_ => new string('x', 150))));
        var f = Write("wide.csv", sb.ToString());
        using var t = Tools();
        var o = await Run(t, "csv_query", new JsonObject { ["file"] = f, ["limit"] = 20 });
        o.Text.Should().Contain("more (use offset)", "the footer saying how many rows were not shown must not be cut off");
    }

    [Fact]
    public async Task Item9a_csv_group_by_keeps_its_smaller_groups_footer_on_wide_rows()
    {
        var sb = new StringBuilder("K,A,B,C,D\n");
        for (int r = 0; r < 60; r++) sb.Append("k").Append(r).Append(',').Append(string.Join(",", Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 190)))).Append('\n');
        var f = Write("g.csv", sb.ToString());
        using var t = Tools();
        var o = await Run(t, "csv_group_by", new JsonObject { ["file"] = f, ["column"] = "K", ["show_columns"] = new JsonArray("A", "B", "C", "D"), ["top"] = 50 });
        o.Text.Should().Contain("smaller groups", "60 groups, top 50: the +10 footer must survive the cap");
    }

    // 9b: more than MaxDistinct distinct values: silently dropped
    [Fact]
    public async Task Item9b_column_values_discloses_when_distinct_values_were_dropped()
    {
        var sb = new StringBuilder("V\n");
        for (int r = 0; r < 50_050; r++) sb.Append("v").Append(r).Append('\n');
        var f = Write("many.csv", sb.ToString());
        using var t = Tools();
        var o = await Run(t, "csv_column_values", new JsonObject { ["file"] = f, ["column"] = "V" });
        var head = o.Text.Split('\n')[0];
        System.Text.RegularExpressions.Regex.IsMatch(head, "50,?050 distinct|(?i)(cap|limit|more than|at least|approx|not counted|dropped)")
            .Should().BeTrue("50,050 distinct values exist; header says: " + head);
    }

    [Fact]
    public async Task Item9b_group_by_discloses_when_groups_were_dropped()
    {
        var sb = new StringBuilder("V\n");
        for (int r = 0; r < 50_050; r++) sb.Append("v").Append(r).Append('\n');
        var f = Write("many2.csv", sb.ToString());
        using var t = Tools();
        var o = await Run(t, "csv_group_by", new JsonObject { ["file"] = f, ["column"] = "V" });
        var head = o.Text.Split('\n')[0];
        System.Text.RegularExpressions.Regex.IsMatch(head, "50,?050 group|(?i)(cap|limit|more than|at least|approx|not counted|dropped)")
            .Should().BeTrue("50,050 groups exist; header says: " + head);
    }

    // 9c: case_search "first line" must be usable as a csv_get_rows row number and header must not count as a hit
    [Fact]
    public async Task Item9c_case_search_first_line_is_a_csv_get_rows_row_number()
    {
        var f = Write("s.csv", "Name,Note\nfoo,x\nbar,needle here\nbaz,y\n");
        using var t = Tools();
        var o = await Run(t, "case_search", new JsonObject { ["query"] = "needle" });
        var line = o.Text.Split('\n').First(l => l.Contains("s.csv"));
        var first = long.Parse(line.Split('|').Last().Trim());
        var rows = await Run(t, "csv_get_rows", new JsonObject { ["file"] = f, ["rows"] = new JsonArray(first) });
        rows.Text.Should().Contain("needle", "the reported first line " + first + " is a physical line (header = 1), but csv_get_rows row 1 = first data row");
    }

    [Fact]
    public async Task Item9c_case_search_does_not_count_the_header_row_as_a_match()
    {
        Write("h.csv", "Name,Payload\nfoo,x\nbar,y\n");
        using var t = Tools();
        var o = await Run(t, "case_search", new JsonObject { ["query"] = "payload" });
        o.Text.Should().Contain("appears in 0 of", "'Payload' occurs only in the header, not in any data row");
    }

    // 9d: row estimate with quoted newlines, no approximation sign
    [Fact]
    public void Item9d_inventory_row_count_ignores_newlines_inside_quoted_cells()
    {
        // 2 data rows, each with a 3-line quoted cell
        Write("q.csv", "A,B\n1,\"l1\nl2\nl3\"\n2,\"l1\nl2\nl3\"\n");
        var text = CaseInventory.Build(_case);
        var line = text.Split('\n').First(l => l.Contains("q.csv"));
        System.Text.RegularExpressions.Regex.IsMatch(line, @"rows(≈| )2\b").Should().BeTrue(line);
    }

    // 9e: cache keyed on length only
    [Fact]
    public async Task Item9e_same_size_rewrite_is_not_served_from_the_stale_cache()
    {
        var f = Write("c.csv", "K\nAAAA\nBBBB\n");
        using var t = Tools();
        var o1 = await Run(t, "csv_query", new JsonObject { ["file"] = f });
        o1.Text.Should().Contain("AAAA");
        Write("c.csv", "K\nZZZZ\nYYYY\n"); // same length
        var o2 = await Run(t, "csv_query", new JsonObject { ["file"] = f });
        o2.Text.Should().Contain("ZZZZ").And.NotContain("AAAA");
    }

}
