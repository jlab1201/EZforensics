using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class AssistantCaseToolsTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezcase-" + Guid.NewGuid().ToString("N"));

    private sealed class Host : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public List<string> Statuses { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<bool> ConfirmWriteAsync(ToolCall c) => Task.FromResult(false);
        public void Status(string t) => Statuses.Add(t);
    }

    public AssistantCaseToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_case, "PC1", "Logs"));
        Directory.CreateDirectory(Path.Combine(_case, "PC2"));
        File.WriteAllText(Path.Combine(_case, "PC1", "Logs", "evtx.csv"),
            "TimeCreated,EventId,Payload\n2026-10-01,4688,powershell -enc AAA\n2026-10-02,4624,logon\n2026-10-03,4688,cmd /c whoami\n2026-10-04,4688,\"multi\nline\"\n");
        File.WriteAllText(Path.Combine(_case, "PC2", "run.csv"), "Name,Path\nfoo,C:\\a\nbar,C:\\powershell.exe\n");
        File.WriteAllText(Path.Combine(_case, "PC2", "notes.txt"), "not a csv");
    }

    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private (AssistantCaseTools Tools, Host Host) Make(bool open = true, bool noCase = false)
    {
        var host = new Host();
        return (new AssistantCaseTools(() => noCase ? null : _case, host, open), host);
    }

    private static async Task<ToolOutcome> Run(AssistantCaseTools t, string name, JsonObject input) =>
        await t.Tools.Single(x => x.Spec.Name == name).RunAsync(new ToolCall("c1", name, input), default);

    private static readonly string Evtx = Path.Combine("PC1", "Logs", "evtx.csv");
    private static readonly string Run1 = Path.Combine("PC2", "run.csv");

    [Fact]
    public void All_case_tools_are_read_and_open_is_a_proposal()
    {
        var (t, _) = Make();
        t.Tools.Where(x => x.Spec.Name != "analyze_open_file").Should().OnlyContain(x => x.Spec.Kind == ToolKind.Read);
        t.Tools.Single(x => x.Spec.Name == "analyze_open_file").Spec.Kind.Should().Be(ToolKind.Proposal);
        Make(open: false).Tools.Tools.Should().NotContain(x => x.Spec.Name == "analyze_open_file");
    }

    [Fact]
    public async Task Without_a_file_the_csv_tools_read_the_file_open_on_screen()
    {
        var open = Path.Combine(_case, Run1);
        using var t = new AssistantCaseTools(() => _case, new Host(), openFile: () => open);
        var q = await Run(t, "csv_query", new JsonObject { ["filter"] = "Contains([Path], 'powershell')" });
        q.IsError.Should().BeFalse(q.Text);
        q.Text.Should().Contain("run.csv").And.Contain("1 of 2 rows");
        (await Run(t, "csv_group_by", new JsonObject { ["column"] = "Name" })).Text.Should().Contain("foo");
        t.LastQueriedFile.Should().Be(open);
        (await Run(t, "csv_query", new JsonObject { ["file"] = Evtx })).Text.Should().Contain("evtx.csv");
        t.Tools.Single(x => x.Spec.Name == "csv_query").Spec.InputSchema["required"]?.AsArray().Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task List_files_shows_only_csvs_with_size_and_filters()
    {
        var (t, _) = Make();
        var all = await Run(t, "case_list_files", new JsonObject());
        all.IsError.Should().BeFalse();
        all.Text.Should().Contain("2 CSV file(s)").And.Contain("evtx.csv").And.Contain("run.csv").And.NotContain("notes.txt");
        var some = await Run(t, "case_list_files", new JsonObject { ["contains"] = "run" });
        some.Text.Should().Contain("1 CSV file(s)").And.NotContain("evtx.csv");
        AssistantCaseTools.CountCsvFiles(_case).Should().Be(2);
    }

    [Theory]
    [InlineData("..\\x.csv")]
    [InlineData("PC1/../../x.csv")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("PC2\\notes.txt")]
    [InlineData("PC2\\missing.csv")]
    public async Task Bad_paths_are_rejected(string file)
    {
        var (t, _) = Make();
        foreach (var tool in new[] { "csv_query", "csv_get_rows" })
            (await Run(t, tool, new JsonObject { ["file"] = file, ["rows"] = new JsonArray(0) })).IsError.Should().BeTrue();
        (await Run(t, "case_search", new JsonObject { ["query"] = "x", ["files"] = new JsonArray(file) })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task No_case_is_an_error()
    {
        var (t, _) = Make(noCase: true);
        (await Run(t, "case_list_files", new JsonObject())).IsError.Should().BeTrue();
        (await Run(t, "csv_query", new JsonObject { ["file"] = Evtx })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Search_skips_the_header_and_reports_a_csv_get_rows_row_number()
    {
        var (t, _) = Make();
        // "Payload" is only a header in evtx.csv; "whoami" is data row 3 (row 4 holds a quoted two-line cell)
        (await Run(t, "case_search", new JsonObject { ["query"] = "payload" })).Text.Should().Contain("appears in 0 of");
        var r = await Run(t, "case_search", new JsonObject { ["query"] = "whoami", ["files"] = new JsonArray(Evtx) });
        r.Text.Should().Contain("| 3");
        (await Run(t, "csv_get_rows", new JsonObject { ["file"] = Evtx, ["rows"] = new JsonArray(3) })).Text.Should().Contain("whoami");
    }

    [Fact]
    public async Task Query_and_group_by_footers_survive_the_cap_on_wide_rows()
    {
        var cols = Enumerable.Range(0, 40).Select(i => "C" + i).ToArray();
        var wide = new System.Text.StringBuilder(string.Join(",", cols) + "\n");
        for (int r = 0; r < 30; r++) wide.Append(string.Join(",", cols.Select(_ => new string('x', 150)))).Append("\n");
        File.WriteAllText(Path.Combine(_case, "PC2", "wide.csv"), wide.ToString());
        var g = new System.Text.StringBuilder("K,A,B,C,D\n");
        for (int r = 0; r < 60; r++) g.Append('k').Append(r).Append(',').Append(string.Join(",", Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 190)))).Append("\n");
        File.WriteAllText(Path.Combine(_case, "PC2", "grp.csv"), g.ToString());
        var (t, _) = Make();
        var q = await Run(t, "csv_query", new JsonObject { ["file"] = Path.Combine("PC2", "wide.csv"), ["limit"] = 20 });
        q.Text.Should().Contain("more (use offset)").And.NotContain("output cut");
        var shown = int.Parse(System.Text.RegularExpressions.Regex.Match(q.Text, @"Showing (\d+) from").Groups[1].Value);
        shown.Should().BeLessThan(20).And.Be(q.Text.Split('\n').Count(l => l.StartsWith("1 | ") || System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+ \| x")));
        var gr = await Run(t, "csv_group_by", new JsonObject { ["file"] = Path.Combine("PC2", "grp.csv"), ["column"] = "K", ["show_columns"] = new JsonArray("A", "B", "C", "D"), ["top"] = 50 });
        gr.Text.Should().Contain("smaller groups").And.NotContain("output cut");
    }

    [Fact]
    public async Task A_same_size_rewrite_is_not_served_from_the_cache()
    {
        var path = Path.Combine(_case, "PC2", "c.csv");
        File.WriteAllText(path, "K\nAAAA\nBBBB\n");
        var (t, _) = Make();
        (await Run(t, "csv_query", new JsonObject { ["file"] = Path.Combine("PC2", "c.csv") })).Text.Should().Contain("AAAA");
        File.WriteAllText(path, "K\nZZZZ\nYYYY\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var o = await Run(t, "csv_query", new JsonObject { ["file"] = Path.Combine("PC2", "c.csv") });
        o.Text.Should().Contain("ZZZZ").And.NotContain("AAAA");
    }

    [Fact]
    public async Task Search_counts_hits_per_file()
    {
        var (t, _) = Make();
        var r = await Run(t, "case_search", new JsonObject { ["query"] = "powershell" });
        r.Text.Should().Contain("2 of 2 file(s)").And.Contain("evtx.csv").And.Contain("run.csv");
        (await Run(t, "case_search", new JsonObject { ["query"] = "powershell", ["files"] = new JsonArray(Run1) })).Text.Should().Contain("1 of 1");
    }

    [Fact]
    public async Task Query_filters_limits_and_reports_total()
    {
        var (t, host) = Make();
        var r = await Run(t, "csv_query", new JsonObject { ["file"] = Evtx, ["filter"] = "[EventId] = '4688'", ["limit"] = 2 });
        r.IsError.Should().BeFalse(r.Text);
        r.Text.Should().Contain("3 of 4 rows match").And.Contain("1 | 2026-10-01 | 4688 | powershell -enc AAA").And.Contain("3 | 2026-10-03")
            .And.NotContain("4624").And.Contain("1 more");
        host.Statuses.Should().Contain(s => s.StartsWith("Indexing evtx.csv"));
        var page2 = await Run(t, "csv_query", new JsonObject { ["file"] = Evtx, ["filter"] = "[EventId] = '4688'", ["offset"] = 2, ["columns"] = new JsonArray("Payload") });
        page2.Text.Should().Contain("4 | multi line").And.NotContain("powershell");
    }

    [Fact]
    public async Task Query_row_ids_match_the_csv_index_source_rows()
    {
        var (t, _) = Make();
        var r = await Run(t, "csv_query", new JsonObject { ["file"] = Evtx, ["filter"] = "Contains([Payload], 'whoami')" });
        r.Text.Should().Contain("\n3 | 2026-10-03 | 4688 | cmd /c whoami");
        using var idx = await CsvIndex.OpenAsync(Path.Combine(_case, Evtx), null, default);
        await idx.IndexingCompleted;
        var hits = await FilterEngine.FilterAsync(idx, new FilterSpec { Expression = AssistantGrid.ParseFilter("Contains([Payload], 'whoami')", idx.Headers).Group }, null, default);
        hits.Should().Equal(2);
        var rows = await Run(t, "csv_get_rows", new JsonObject { ["file"] = Evtx, ["rows"] = new JsonArray(3, 1) });
        rows.Text.Should().Contain("3 | 2026-10-03").And.Contain("1 | 2026-10-01");
    }

    [Fact]
    public async Task Bad_filter_or_column_names_the_valid_columns()
    {
        var (t, _) = Make();
        var f = await Run(t, "csv_query", new JsonObject { ["file"] = Evtx, ["filter"] = "[Nope] = 'x'" });
        f.IsError.Should().BeTrue(); f.Text.Should().Contain("Nope").And.Contain("TimeCreated, EventId, Payload");
        var c = await Run(t, "csv_query", new JsonObject { ["file"] = Evtx, ["columns"] = new JsonArray("Nope") });
        c.IsError.Should().BeTrue(); c.Text.Should().Contain("EventId");
        (await Run(t, "csv_get_rows", new JsonObject { ["file"] = Evtx, ["rows"] = new JsonArray(99) })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Column_values_count_distinct_values()
    {
        var (t, _) = Make();
        var r = await Run(t, "csv_column_values", new JsonObject { ["file"] = Evtx, ["column"] = "EventId" });
        r.Text.Should().Contain("2 distinct").And.Contain("3 x 4688").And.Contain("1 x 4624");
        var f = await Run(t, "csv_column_values", new JsonObject { ["file"] = Evtx, ["column"] = "EventId", ["filter"] = "Contains([Payload], 'logon')" });
        f.Text.Should().Contain("1 distinct").And.Contain("1 x 4624");
        (await Run(t, "csv_column_values", new JsonObject { ["file"] = Evtx, ["column"] = "Nope" })).IsError.Should().BeTrue();
    }

    // 120k unique values with a frequent value that first appears after the 50k cap, 600 times
    private string WriteManyDistinct(string name)
    {
        var path = Path.Combine(_case, name);
        using (var w = new StreamWriter(path))
        {
            w.WriteLine("V");
            for (int r = 0; r < 120_000; r++)
            {
                w.WriteLine("u" + r);
                if (r >= 60_000 && r % 100 == 0) w.WriteLine("late.exe");
            }
        }
        return name;
    }

    [Fact]
    public async Task Column_values_past_the_distinct_cap_keep_late_frequent_values_and_say_approximate()
    {
        var (t, _) = Make();
        var r = await Run(t, "csv_column_values", new JsonObject { ["file"] = WriteManyDistinct("many.csv"), ["column"] = "V" });
        r.IsError.Should().BeFalse(r.Text);
        var lines = r.Text.Split('\n');
        lines[0].Should().Contain("more than 50,000 distinct values").And.Contain("approximate");
        var top = lines[1].TrimEnd();
        top.Should().EndWith(" x late.exe");
        int n = int.Parse(top.TrimStart('≈').Split(' ')[0]);
        n.Should().BeInRange(550, 600);
        top.Should().StartWith("≈");
    }

    [Fact]
    public async Task Group_by_past_the_cap_keeps_late_frequent_values_and_says_approximate()
    {
        var (t, _) = Make();
        var r = await Run(t, "csv_group_by", new JsonObject { ["file"] = WriteManyDistinct("many2.csv"), ["column"] = "V" });
        r.IsError.Should().BeFalse(r.Text);
        var lines = r.Text.Split('\n');
        lines[0].Should().Contain("more than 50,000 groups").And.Contain("approximate");
        lines[2].Should().StartWith("late.exe | ≈");
    }

    [Fact]
    public async Task Column_values_below_the_cap_stay_exact_without_approximation_marks()
    {
        var (t, _) = Make();
        var r = await Run(t, "csv_column_values", new JsonObject { ["file"] = Evtx, ["column"] = "EventId" });
        r.Text.Should().NotContain("≈").And.NotContain("approximate");
    }

    [Fact]
    public async Task Group_by_counts_rows_per_value_with_other_columns()
    {
        var (t, _) = Make();
        var r = await Run(t, "csv_group_by", new JsonObject { ["file"] = Evtx, ["column"] = "EventId", ["show_columns"] = new JsonArray("TimeCreated") });
        r.IsError.Should().BeFalse();
        r.Text.Should().Contain("2 group(s) over 4 row(s)").And.Contain("EventId | rows | first row | TimeCreated")
            .And.Contain("4688 | 3 | 1 | 2026-10-01, 2026-10-03, 2026-10-04").And.Contain("4624 | 1 | 2 | 2026-10-02");
        r.Text.IndexOf("4688 |", StringComparison.Ordinal).Should().BeLessThan(r.Text.IndexOf("4624 |", StringComparison.Ordinal));
        var f = await Run(t, "csv_group_by", new JsonObject { ["file"] = Evtx, ["column"] = "EventId", ["filter"] = "Contains([Payload], 'logon')" });
        f.Text.Should().Contain("1 group(s)").And.Contain("4624 | 1 | 2");
        (await Run(t, "csv_group_by", new JsonObject { ["file"] = Evtx, ["column"] = "EventId", ["show_columns"] = new JsonArray("Nope") })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Output_is_capped_for_wide_cells()
    {
        File.WriteAllText(Path.Combine(_case, "wide.csv"), "A\n" + string.Join("\n", Enumerable.Range(0, 100).Select(_ => new string('x', 5000))) + "\n");
        var (t, _) = Make();
        var r = await Run(t, "csv_query", new JsonObject { ["file"] = "wide.csv", ["limit"] = 100 });
        r.Text.Length.Should().BeLessThan(AssistantCaseTools.MaxOutputChars + 200);
        r.Text.Should().NotContain(new string('x', AssistantCaseTools.CellChars + 5));
    }

    [Fact]
    public async Task Open_file_proposal_validates_filter_against_that_files_headers()
    {
        var (t, host) = Make();
        var bad = await Run(t, "analyze_open_file", new JsonObject { ["file"] = Run1, ["filter"] = "[EventId] = '1'", ["reason"] = "r" });
        bad.IsError.Should().BeTrue(); bad.Text.Should().Contain("Name, Path");
        host.Proposals.Should().BeEmpty();

        var ok = await Run(t, "analyze_open_file", new JsonObject { ["file"] = Run1, ["filter"] = "Contains([Path], 'powershell')", ["reason"] = "look" });
        ok.IsError.Should().BeFalse(ok.Text);
        var p = host.Proposals.Should().ContainSingle().Subject.Should().BeOfType<OpenFileProposal>().Subject;
        p.File.Should().Be(Path.Combine(_case, "PC2", "run.csv"));
        p.Filter.Should().NotBeNull();
        p.Reason.Should().Be("look");

        (await Run(t, "analyze_open_file", new JsonObject { ["file"] = "..\\evil.csv", ["reason"] = "r" })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Cache_stays_small_and_dispose_closes_it()
    {
        for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(_case, $"f{i}.csv"), "A\n1\n2\n");
        var (t, _) = Make();
        for (int i = 0; i < 5; i++) (await Run(t, "csv_query", new JsonObject { ["file"] = $"f{i}.csv" })).IsError.Should().BeFalse();
        t.Dispose();
        (await Run(t, "csv_query", new JsonObject { ["file"] = "f0.csv" })).IsError.Should().BeTrue();
    }

    // ------------------------------------------------------------------ flagging rows of another case file

    private (IReadOnlyList<IAssistantTool> Grid, Host Host, AssistantCaseTools Case) GridWithCase(string? openRel = null)
    {
        var (ct, host) = Make();
        var open = Path.Combine(_case, openRel ?? Run1);
        var ctx = new GridContext(open, new[] { "Name", "Path" }, 2, 2, "", Array.Empty<GridSampleRow>());
        var grid = AssistantGridTools.Create(() => ctx, () => new[] { "suspicious" }, host, () => new[] { ("suspicious", "Suspicious") }, ct);
        return (grid, host, ct);
    }

    private static Task<ToolOutcome> RunGrid(IReadOnlyList<IAssistantTool> g, string name, JsonObject input) =>
        g.Single(x => x.Spec.Name == name).RunAsync(new ToolCall("g1", name, input), default);

    [Theory]
    [InlineData("evtx.csv")]
    [InlineData("PC1/Logs/evtx.csv")]
    public async Task Rows_of_another_case_file_are_flagged_in_that_file(string csv)
    {
        var (g, host, ct) = GridWithCase();
        using var _ = ct;
        var r = await RunGrid(g, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1, 3), ["flag"] = "Suspicious", ["csv"] = csv });
        r.IsError.Should().BeFalse(r.Text);
        var p = host.Proposals.Single().Should().BeOfType<FlagProposal>().Subject;
        p.CsvPath.Should().Be(Path.Combine(_case, Evtx));
        p.Rows.Should().Equal(0, 2);   // row numbers 1 and 3 as csv_query shows them
    }

    [Fact]
    public async Task Rows_beyond_the_other_files_length_are_refused()
    {
        var (g, _, ct) = GridWithCase();
        using var _1 = ct;
        (await RunGrid(g, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(9), ["flag"] = "suspicious", ["csv"] = "evtx.csv" })).Text.Should().Contain("valid 1..4");
        (await RunGrid(g, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "suspicious", ["csv"] = "nope.csv" })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task A_filter_flags_every_matching_row()
    {
        var (g, host, ct) = GridWithCase();
        using var _ = ct;
        var r = await RunGrid(g, "grid_flag_rows", new JsonObject { ["filter"] = "[EventId] = '4688'", ["flag"] = "suspicious", ["csv"] = "evtx.csv" });
        r.IsError.Should().BeFalse(r.Text);
        host.Proposals.Single().Should().BeOfType<FlagProposal>().Which.Rows.Should().Equal(0, 2, 3);
        (await RunGrid(g, "grid_flag_rows", new JsonObject { ["filter"] = "[EventId] = '1'", ["flag"] = "suspicious", ["csv"] = "evtx.csv" })).Text.Should().Contain("no rows match");
    }

    [Fact]
    public async Task A_filter_on_the_open_file_works_without_csv()
    {
        var (g, host, ct) = GridWithCase();
        using var _ = ct;
        var r = await RunGrid(g, "grid_unflag_rows", new JsonObject { ["filter"] = "Contains([Path], 'powershell')" });
        r.IsError.Should().BeFalse(r.Text);
        var p = host.Proposals.Single().Should().BeOfType<UnflagProposal>().Subject;
        p.CsvPath.Should().Be(Path.Combine(_case, Run1));
        p.Rows.Should().Equal(1);
    }

    [Fact]
    public async Task A_file_name_alone_resolves_when_it_is_unique()
    {
        var (ct, _) = Make();
        using var _1 = ct;
        (await Run(ct, "csv_query", new JsonObject { ["file"] = "evtx.csv", ["limit"] = 1 })).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Flag_them_without_csv_after_reading_another_file_asks_which_file()
    {
        var (g, host, ct) = GridWithCase();
        using var _ = ct;
        await Run(ct, "csv_query", new JsonObject { ["file"] = "evtx.csv", ["limit"] = 2 });
        var r = await RunGrid(g, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "suspicious" });
        r.IsError.Should().BeTrue();
        r.Text.Should().Contain("evtx.csv").And.Contain("run.csv");
        (await RunGrid(g, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "suspicious", ["csv"] = "evtx.csv" })).IsError.Should().BeFalse();
        // once the file was named, the open file works without csv again
        (await RunGrid(g, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "suspicious" })).IsError.Should().BeFalse();
        host.Proposals.Should().HaveCount(2);
    }
}
