using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AssistantGridTests
{
    private static readonly string[] H = { "TimeCreated", "EventId", "Computer", "Payload" };
    private static readonly string[] Flags = { "informational", "suspicious", "custom1" };

    private sealed class Host : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<bool> ConfirmWriteAsync(ToolCall c) => Task.FromResult(false);
        public void Status(string t) { }
    }

    private static GridContext Ctx(string? csv = @"C:\case\H\Logs\evtx.csv") => new(csv, H, 100, 10, "", new[]
    {
        new GridSampleRow(7, new[] { "2026-10-01", "4688", "PC1", "powershell -enc" }),
        new GridSampleRow(42, new[] { "2026-10-02", "4624", "PC1", "logon" }),
    });

    private static (IReadOnlyList<IAssistantTool> Tools, Host Host) Tools(Func<GridContext>? ctx = null)
    {
        var host = new Host();
        return (AssistantGridTools.Create(ctx ?? (() => Ctx()), () => Flags, host), host);
    }

    private static Task<ToolOutcome> Run(IReadOnlyList<IAssistantTool> tools, string name, JsonObject input) =>
        tools.Single(t => t.Spec.Name == name).RunAsync(new ToolCall("id1", name, input), default);

    [Fact]
    public void Valid_filter_parses_to_group()
    {
        var (g, err) = AssistantGrid.ParseFilter("[EventId] = '4688' And Contains([Payload], 'powershell')", H);
        err.Should().BeNull();
        g.Should().NotBeNull();
        g!.ToDisplayString().Should().Contain("EventId");
    }

    [Fact]
    public void Column_match_is_case_insensitive()
        => AssistantGrid.ParseFilter("[eventid] = '4688'", H).Error.Should().BeNull();

    [Fact]
    public void Unknown_column_is_rejected()
    {
        var (g, err) = AssistantGrid.ParseFilter("[CommandLine] = 'x'", H);
        g.Should().BeNull();
        err.Should().Contain("CommandLine");
    }

    [Fact]
    public void Syntax_error_is_reported()
        => AssistantGrid.ParseFilter("[EventId] = ", H).Error.Should().NotBeNullOrEmpty();

    [Fact]
    public void Empty_filter_is_rejected()
        => AssistantGrid.ParseFilter("  ", H).Error.Should().NotBeNullOrEmpty();

    [Fact]
    public void Flags_are_validated_against_the_supplied_list_not_the_defaults()
    {
        AssistantGrid.ValidateFlag("evil", Flags).Should().Contain("evil");
        AssistantGrid.ValidateFlag("custom1", Flags).Should().BeNull();
        AssistantGrid.ValidateFlag(FlagCatalog.Malicious, Flags).Should().NotBeNull();   // a default, but not configured here
    }

    [Fact]
    public void Row_range_is_validated()
    {
        AssistantGrid.ValidateRows(new long[] { 0, 99 }, 100).Should().BeNull();
        AssistantGrid.ValidateRows(new long[] { 100 }, 100).Should().Contain("100");
        AssistantGrid.ValidateRows(new long[] { -1 }, 100).Should().NotBeNull();
        AssistantGrid.ValidateRows(Array.Empty<long>(), 100).Should().NotBeNull();
    }

    [Fact]
    public async Task Get_context_returns_csv_columns_flags_and_row_ids()
    {
        var (tools, _) = Tools();
        var o = await Run(tools, "grid_get_context", new JsonObject());
        o.IsError.Should().BeFalse();
        var j = JsonNode.Parse(o.Text)!;
        j["csv"]!.GetValue<string>().Should().EndWith("evtx.csv");
        j["columns"]!.AsArray().Should().HaveCount(4);
        j["flagIds"]!.AsArray().Should().HaveCount(3);
        j["sampleRows"]![1]!["row"]!.GetValue<long>().Should().Be(43);   // as shown (1-based); source id is 42
    }

    [Fact]
    public async Task Get_context_without_csv_is_an_error()
    {
        var (tools, _) = Tools(() => Ctx(null));
        (await Run(tools, "grid_get_context", new JsonObject())).IsError.Should().BeTrue();
    }

    [Fact]
    public void Tool_kinds()
    {
        var (tools, _) = Tools();
        tools.Single(t => t.Spec.Name == "grid_get_context").Spec.Kind.Should().Be(ToolKind.Read);
        tools.Single(t => t.Spec.Name == "grid_apply_filter").Spec.Kind.Should().Be(ToolKind.Proposal);
        tools.Single(t => t.Spec.Name == "grid_flag_rows").Spec.Kind.Should().Be(ToolKind.Proposal);
    }

    [Fact]
    public async Task Filter_proposal_carries_csv_path_and_says_not_applied()
    {
        var (tools, host) = Tools();
        var o = await Run(tools, "grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" });
        o.IsError.Should().BeFalse();
        o.Text.Should().Contain("NOT applied");
        var p = host.Proposals.Should().ContainSingle().Which.Should().BeOfType<FilterProposal>().Subject;
        p.CsvPath.Should().EndWith("evtx.csv");
        p.ToolCallId.Should().Be("id1");
        p.Display.Should().Contain("EventId");
    }

    [Fact]
    public async Task Bad_filter_creates_no_proposal()
    {
        var (tools, host) = Tools();
        (await Run(tools, "grid_apply_filter", new JsonObject { ["filter"] = "[Nope] = 'x'" })).IsError.Should().BeTrue();
        (await Run(tools, "grid_apply_filter", new JsonObject())).IsError.Should().BeTrue();
        host.Proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task Flag_proposal_carries_csv_path_and_source_rows()
    {
        var (tools, host) = Tools();
        var o = await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(8, 43, 8), ["flag"] = "suspicious", ["reason"] = "encoded powershell" });
        o.IsError.Should().BeFalse();
        o.Text.Should().Contain("NOT applied");
        var p = host.Proposals.Should().ContainSingle().Which.Should().BeOfType<FlagProposal>().Subject;
        p.CsvPath.Should().EndWith("evtx.csv");
        p.Rows.Should().Equal(7, 42);
        p.FlagId.Should().Be("suspicious");
        p.Reason.Should().Be("encoded powershell");
    }

    [Theory]
    [InlineData("rows-out-of-range")]
    [InlineData("row-zero")]
    [InlineData("negative")]
    [InlineData("unknown-flag")]
    [InlineData("not-integers")]
    [InlineData("no-rows")]
    [InlineData("wrong-csv")]
    public async Task Invalid_flag_proposals_are_rejected_and_not_shown(string kind)
    {
        var (tools, host) = Tools();
        var input = kind switch
        {
            "rows-out-of-range" => new JsonObject { ["rows"] = new JsonArray(101), ["flag"] = "suspicious", ["reason"] = "r" },   // RowCount + 1
            "row-zero" => new JsonObject { ["rows"] = new JsonArray(0), ["flag"] = "suspicious", ["reason"] = "r" },
            "negative" => new JsonObject { ["rows"] = new JsonArray(-3), ["flag"] = "suspicious", ["reason"] = "r" },
            "unknown-flag" => new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "malicious", ["reason"] = "r" },
            "not-integers" => new JsonObject { ["rows"] = new JsonArray("a"), ["flag"] = "suspicious", ["reason"] = "r" },
            "no-rows" => new JsonObject { ["rows"] = new JsonArray(), ["flag"] = "suspicious", ["reason"] = "r" },
            _ => new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "suspicious", ["reason"] = "r", ["csv"] = @"C:\other\file.csv" },
        };
        (await Run(tools, "grid_flag_rows", input)).IsError.Should().BeTrue();
        host.Proposals.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"C:\case\H\Logs\evtx.csv")]
    [InlineData(@"H\Logs\evtx.csv")]
    [InlineData("evtx.csv")]
    [InlineData("H/Logs/EVTX.csv")]
    public async Task Flag_accepts_the_open_csv_by_full_relative_or_file_name(string csv)
    {
        // the view shows only the file name and app_state the case-relative path; models echo either one back
        var (tools, host) = Tools();
        var r = await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(7), ["flag"] = "suspicious", ["csv"] = csv });
        r.IsError.Should().BeFalse(r.Text);
        host.Proposals.Single().Should().BeOfType<FlagProposal>().Which.CsvPath.Should().Be(@"C:\case\H\Logs\evtx.csv");
    }

    [Theory]
    [InlineData("Suspicious", "suspicious")]
    [InlineData("SUSPICIOUS", "suspicious")]
    [InlineData("Informational", "informational")]
    [InlineData("Benign", "benign")]
    [InlineData("benign / ruled out", "benign")]
    [InlineData("Ruled", null)]
    [InlineData("evil", null)]
    public void Flag_names_resolve_by_id_or_label_ignoring_case(string name, string? id)
    {
        var flags = FlagCatalog.Defaults().Select(f => (f.Id, f.Label)).ToList();
        AssistantGrid.ResolveFlag(name, flags).Should().Be(id);
    }

    [Fact]
    public async Task Flag_by_label_proposes_the_flag_id_and_lists_valid_flags_on_error()
    {
        var host = new Host();
        var tools = AssistantGridTools.Create(() => Ctx(), () => Flags, host, () => new[] { ("suspicious", "Suspicious"), ("custom1", "Needs review") });
        (await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(7), ["flag"] = "Needs review" })).IsError.Should().BeFalse();
        host.Proposals.Single().Should().BeOfType<FlagProposal>().Which.FlagId.Should().Be("custom1");
        var bad = await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(7), ["flag"] = "evil" });
        bad.IsError.Should().BeTrue();
        bad.Text.Should().Contain("suspicious = Suspicious").And.Contain("custom1 = Needs review");
    }

    [Fact]
    public async Task Rows_may_be_a_single_number_or_numeric_strings()
    {
        var (tools, host) = Tools();
        (await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = 8, ["flag"] = "suspicious" })).IsError.Should().BeFalse();
        (await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray("43", 8), ["flag"] = "suspicious" })).IsError.Should().BeFalse();
        (await Run(tools, "grid_go_to_row", new JsonObject { ["row"] = "43" })).IsError.Should().BeFalse();
        host.Proposals.OfType<FlagProposal>().Select(p => p.Rows).Should().BeEquivalentTo(new[] { new long[] { 7 }, new long[] { 42, 7 } });
    }

    [Fact]
    public void Filter_errors_teach_the_syntax_and_the_columns()
    {
        AssistantGrid.ParseFilter("EventId = 4688", H).Error.Should().Contain("[EventId] = '4688'");
        AssistantGrid.ParseFilter("[Cmd] = 'x'", H).Error.Should().Contain("Columns: TimeCreated, EventId");
    }

    [Fact]
    public void Describe_lists_the_flags_when_given()
    {
        var text = Ctx().Describe(flags: new[] { ("suspicious", "Suspicious") });
        text.Should().Contain("Flags (id = label): suspicious = Suspicious");
        Ctx().Describe().Should().NotContain("Flags");
    }

    [Fact]
    public async Task Flag_proposal_without_open_csv_is_rejected()
    {
        var (tools, host) = Tools(() => Ctx(null));
        (await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "suspicious", ["reason"] = "r" })).IsError.Should().BeTrue();
        host.Proposals.Should().BeEmpty();
    }

    [Fact]
    public void Describe_shows_file_counts_filter_selected_and_top_rows_without_empty_columns()
    {
        var top = new[] { new GridSampleRow(1, new[] { "2026-10-01", "4688", "", "cmd.exe" }), new GridSampleRow(2, new[] { "2026-10-01", "4624", "", "logon" }) };
        var sel = new[] { new GridSampleRow(812, new[] { "2026-10-02", "4688", "", "powershell -enc AAA" }) };
        var text = new GridContext(@"C:\case\EvtxECmd_Output.csv", H, 5000, 120, "[EventId] = '4688'", top, sel).Describe();
        text.Should().Contain("File: EvtxECmd_Output.csv").And.Contain("120 shown of 5000").And.Contain("[EventId] = '4688'");
        text.Should().Contain("Selected rows").And.Contain("813 | 2026-10-02 | 4688 | powershell -enc AAA");
        text.Should().Contain("Top rows").And.Contain("2 | 2026-10-01 | 4688 | cmd.exe");
        text.Should().NotContain("| Computer |"); // empty in every listed row
    }

    [Fact]
    public async Task Row_numbers_are_the_ones_the_examiner_sees_row_3_is_source_row_2()
    {
        var ctx = new GridContext(@"C:\case\H\Logs\evtx.csv", H, 100, 10, "", new[] { new GridSampleRow(2, new[] { "2026-10-01", "4688", "PC1", "x" }) });
        ctx.Describe().Should().Contain("3 | 2026-10-01");   // the view line for source 2 starts with "3 | "
        var host = new Host();
        var tools = AssistantGridTools.Create(() => ctx, () => Flags, host);
        (await Run(tools, "grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(3), ["flag"] = "suspicious", ["reason"] = "r" })).IsError.Should().BeFalse();
        host.Proposals.Single().Should().BeOfType<FlagProposal>().Which.Rows.Should().Equal(2);
        var shown = JsonNode.Parse((await Run(tools, "grid_get_context", new JsonObject())).Text)!;
        shown["sampleRows"]![0]!["row"]!.GetValue<long>().Should().Be(3);
    }

    [Fact]
    public void Describe_says_when_no_csv_is_open() =>
        new GridContext(null, Array.Empty<string>(), 0, 0, "", Array.Empty<GridSampleRow>()).Describe().Should().Be("No CSV is open in the results table.");

    [Theory]
    [InlineData("<think>long reasoning</think>\n\nRow 812 looks odd.", "Row 812 looks odd.")]
    [InlineData("Plain answer.", "Plain answer.")]
    [InlineData("<think>cut off mid", "")]
    [InlineData("[after grid_sort with \"group by PID\" and csv_group_by] Grouped by PID.", "Grouped by PID.")]
    [InlineData("[after grid_apply_filter] Applied filter.", "Applied filter.")]
    [InlineData("[EventId] = '4688' matches 4 rows.", "[EventId] = '4688' matches 4 rows.")]
    public void Thinking_is_removed_from_local_model_replies(string raw, string expected) =>
        OpenAiCompatibleModelClient.StripThinking(raw).Should().Be(expected);

    // ------------------------------------------------------------ clear filter / unflag / sort / go to row

    [Fact]
    public async Task Clear_filter_makes_a_proposal_for_the_open_csv()
    {
        var (tools, host) = Tools();
        var r = await Run(tools, "grid_clear_filter", new JsonObject());
        r.IsError.Should().BeFalse();
        r.Text.Should().Be(AssistantGridTools.ProposedText);
        host.Proposals.Single().Should().BeOfType<ClearFilterProposal>().Which.CsvPath.Should().Be(@"C:\case\H\Logs\evtx.csv");
    }

    [Fact]
    public async Task Tools_say_no_csv_is_open()
    {
        var (tools, host) = Tools(() => Ctx(null));
        foreach (var (name, input) in new (string, JsonObject)[]
        {
            ("grid_clear_filter", new JsonObject()), ("grid_unflag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["reason"] = "x" }),
            ("grid_sort", new JsonObject { ["column"] = "EventId" }), ("grid_go_to_row", new JsonObject { ["row"] = 1 }),
        })
            (await Run(tools, name, input)).IsError.Should().BeTrue(name);
        host.Proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task Unflag_validates_rows_like_flag()
    {
        var (tools, host) = Tools();
        (await Run(tools, "grid_unflag_rows", new JsonObject { ["rows"] = new JsonArray(6, 101), ["reason"] = "x" })).IsError.Should().BeTrue(); // 101 = RowCount + 1
        (await Run(tools, "grid_unflag_rows", new JsonObject { ["rows"] = new JsonArray(0), ["reason"] = "x" })).IsError.Should().BeTrue();   // rows start at 1
        (await Run(tools, "grid_unflag_rows", new JsonObject { ["rows"] = new JsonArray(), ["reason"] = "x" })).IsError.Should().BeTrue();
        (await Run(tools, "grid_unflag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["reason"] = "x", ["csv"] = @"C:\other.csv" })).Text.Should().Contain("evtx.csv is open");
        host.Proposals.Should().BeEmpty();
        var ok = await Run(tools, "grid_unflag_rows", new JsonObject { ["rows"] = new JsonArray(6, 6, 8), ["reason"] = "false positive" });
        ok.IsError.Should().BeFalse();
        host.Proposals.Single().Should().BeOfType<UnflagProposal>().Which.Rows.Should().Equal(5, 7);
    }

    [Fact]
    public async Task Sort_needs_a_real_column_and_uses_its_exact_name()
    {
        var (tools, host) = Tools();
        (await Run(tools, "grid_sort", new JsonObject { ["column"] = "Nope" })).Text.Should().Contain("Unknown column").And.Contain("EventId");
        host.Proposals.Should().BeEmpty();
        (await Run(tools, "grid_sort", new JsonObject { ["column"] = "eventid", ["descending"] = true })).IsError.Should().BeFalse();
        host.Proposals.Single().Should().BeOfType<SortProposal>().Which.Should().Match<SortProposal>(s => s.Column == "EventId" && s.Descending);
    }

    [Fact]
    public async Task Go_to_row_validates_the_row_id()
    {
        var (tools, host) = Tools();
        (await Run(tools, "grid_go_to_row", new JsonObject { ["row"] = -1 })).IsError.Should().BeTrue();
        (await Run(tools, "grid_go_to_row", new JsonObject { ["row"] = 101 })).IsError.Should().BeTrue();
        (await Run(tools, "grid_go_to_row", new JsonObject { ["row"] = 0 })).IsError.Should().BeTrue();
        (await Run(tools, "grid_go_to_row", new JsonObject { ["row"] = "seven" })).IsError.Should().BeTrue();
        host.Proposals.Should().BeEmpty();
        (await Run(tools, "grid_go_to_row", new JsonObject { ["row"] = 43 })).IsError.Should().BeFalse();
        host.Proposals.Single().Should().BeOfType<GoToRowProposal>().Which.Row.Should().Be(42);   // source id
    }

    [Fact]
    public async Task The_tool_returns_what_the_host_says_happened()
    {
        var host = new AutoHost();
        var tools = AssistantGridTools.Create(() => Ctx(), () => Flags, host);
        (await Run(tools, "grid_clear_filter", new JsonObject())).Text.Should().Be(AssistantGridTools.AppliedText);
    }

    private sealed class AutoHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal p) { }
        public Task<string> HandleProposalAsync(AssistantProposal p) => Task.FromResult(AssistantGridTools.AppliedText);
        public Task<bool> ConfirmWriteAsync(ToolCall c) => Task.FromResult(false);
        public void Status(string t) { }
    }
}
