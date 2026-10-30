using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>PID link detection (columns, tokens in text cells, time column) and PID to process instance resolution (facts only).</summary>
public sealed class PidLinksTests
{
    [Theory]
    [InlineData("PID")]
    [InlineData("ppid")]
    [InlineData("ProcessId")]
    [InlineData("ProcessID")]
    [InlineData("ParentProcessId")]
    [InlineData("NewProcessId")]
    [InlineData("TargetPID")]
    [InlineData("ParentPID")]
    [InlineData("Process ID")]
    [InlineData("OwningProcess")]
    [InlineData("PID (PID)")]
    [InlineData(" ProcessId ")]
    public void Pid_columns_are_recognised(string header) => PidLinks.IsPidColumn(header).Should().BeTrue();

    [Fact]
    public void HasPidSource_is_true_for_a_PID_column_or_an_EvtxECmd_token_column()
    {
        PidLinks.HasPidSource(new[] { "PID", "PPID", "ImageFileName", "CreateTime" }).Should().BeTrue();   // a pslist header set
        PidLinks.HasPidSource(new[] { "TimeCreated", "ProcessId", "Message" }).Should().BeTrue();
        PidLinks.HasPidSource(new[] { "TimeCreated", "EventId", "MapDescription", "UserName", "PayloadData1", "PayloadData4", "ExecutableInfo" }).Should().BeTrue();   // EvtxECmd
        PidLinks.HasPidSource(new[] { "payloaddata6" }).Should().BeTrue();
    }

    [Fact]
    public void HasPidSource_is_false_for_files_without_a_PID_column_or_token_column()
    {
        PidLinks.HasPidSource(new[] { "HivePath", "KeyPath", "ValueName", "ValueData", "LastWriteTimestamp" }).Should().BeFalse();   // registry
        PidLinks.HasPidSource(new[] { "SourceFilename", "ExecutableName", "RunCount", "LastRun" }).Should().BeFalse();   // prefetch
        PidLinks.HasPidSource(new[] { "TimeCreated", "Message" }).Should().BeFalse();
        PidLinks.IsPidTokenColumn("PayloadData7").Should().BeFalse();
        PidLinks.IsPidTokenColumn(null).Should().BeFalse();
    }

    [Fact]
    public void HasPidSource_is_false_without_usable_headers()
    {
        PidLinks.HasPidSource(Array.Empty<string>()).Should().BeFalse();
        PidLinks.HasPidSource(new[] { "", "  " }).Should().BeFalse();
    }

    [Theory]
    [InlineData("ThreadId")]
    [InlineData("TID")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Name")]
    public void Other_columns_are_not_pid_columns(string? header) => PidLinks.IsPidColumn(header).Should().BeFalse();

    [Theory]
    [InlineData("412", 412)]
    [InlineData(" 4 ", 4)]
    [InlineData("0x1a2c", 0x1a2c)]
    [InlineData("0X10", 16)]
    public void Pid_cells_parse(string cell, int pid)
    {
        PidLinks.TryParsePid(cell, out var p).Should().BeTrue();
        p.Should().Be(pid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("12 ")]  // trimmed -> valid, covered below
    public void Non_pids_are_not_links(string? cell)
    {
        if (cell == "12 ") { PidLinks.TryParsePid(cell, out _).Should().BeTrue(); return; }
        PidLinks.TryParsePid(cell, out _).Should().BeFalse();
    }

    [Fact]
    public void Tokens_are_found_in_text_with_hex_and_decimal_values()
    {
        const string text = "SubjectUserSid: S-1-5-18 | NewProcessId: 0x1a2c | ParentProcessId=412 | ThreadId: 5 | PID: 0";
        var tokens = PidLinks.FindTokens(text);
        tokens.Select(t => t.Pid).Should().Equal(0x1a2c, 412);
        tokens.Select(t => text.Substring(t.Start, t.Length)).Should().Equal("0x1a2c", "412");
    }

    [Fact]
    public void Tokens_ignore_thread_ids_and_text_without_id()
    {
        PidLinks.FindTokens("ThreadId: 5").Should().BeEmpty();
        PidLinks.FindTokens("nothing to see here").Should().BeEmpty();
        PidLinks.FindTokens("").Should().BeEmpty();
        PidLinks.FindTokens(null).Should().BeEmpty();
        PidLinks.FindTokens("pid=4").Should().ContainSingle().Which.Pid.Should().Be(4);
    }

    [Fact]
    public void Time_column_follows_the_priority_order()
    {
        PidLinks.TimeColumnIndex(new[] { "Id", "Timestamp", "TimeCreated" }).Should().Be(2);
        PidLinks.TimeColumnIndex(new[] { "Id", "SourceModified", "datetime" }).Should().Be(2);
        PidLinks.TimeColumnIndex(new[] { "a", "b" }).Should().Be(-1);
        PidLinks.TimeColumnIndex(new[] { "CREATETIME" }).Should().Be(0);
    }

    // ------------------------------------------------------------------ resolver

    private static readonly DateTime T0 = new(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ProcessNode N(int pid, DateTime? create, DateTime? exit = null, ProcessSources src = ProcessSources.PsList, bool ghost = false) =>
        new() { Pid = pid, Name = "p" + pid, CreateTime = create, ExitTime = exit, Sources = src, IsGhost = ghost };

    [Fact]
    public void Memory_row_selects_the_memory_instance_and_create_time_breaks_a_tie()
    {
        var mem = N(100, T0, null, ProcessSources.PsList);
        var logOnly = N(100, T0.AddHours(5), null, ProcessSources.Evtx4688);
        var r = ProcessPidResolver.Resolve(new[] { mem, logOnly }, 100, null, @"C:\case\PC1\Memory\Memory_NetScan.csv");
        r.Kind.Should().Be(PidResolutionKind.Found);
        r.Nodes.Should().ContainSingle().Which.Should().BeSameAs(mem);

        var second = N(100, T0.AddHours(2), null, ProcessSources.PsScan);
        var tie = ProcessPidResolver.Resolve(new[] { mem, second }, 100, null, @"C:\case\PC1\Memory\Memory_PsList.csv", T0.AddHours(2).AddMilliseconds(400));
        tie.Kind.Should().Be(PidResolutionKind.Found);
        tie.Nodes.Single().Should().BeSameAs(second);

        ProcessPidResolver.Resolve(new[] { mem, second }, 100, null, @"C:\case\PC1\Memory\Memory_PsList.csv").Kind.Should().Be(PidResolutionKind.Ambiguous);
    }

    [Fact]
    public void Evtx_row_inside_a_lifetime_selects_that_instance()
    {
        var a = N(100, T0, T0.AddHours(1), ProcessSources.Evtx4688);
        var b = N(100, T0.AddHours(3), null, ProcessSources.Evtx4688);
        var r = ProcessPidResolver.Resolve(new[] { a, b }, 100, T0.AddHours(4), @"C:\case\PC1\EventLogs\x.csv");
        r.Kind.Should().Be(PidResolutionKind.Found);
        r.Nodes.Single().Should().BeSameAs(b);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, T0.AddMinutes(30), null).Nodes.Single().Should().BeSameAs(a);
    }

    [Fact]
    public void Two_instances_and_a_time_outside_both_is_ambiguous()
    {
        var a = N(100, T0, T0.AddHours(1), ProcessSources.Evtx4688);
        var b = N(100, T0.AddHours(3), T0.AddHours(4), ProcessSources.Evtx4688);
        var r = ProcessPidResolver.Resolve(new[] { a, b }, 100, T0.AddHours(2), @"C:\case\PC1\EventLogs\x.csv");
        r.Kind.Should().Be(PidResolutionKind.Ambiguous);
        r.Nodes.Should().HaveCount(2);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, null, null).Kind.Should().Be(PidResolutionKind.Ambiguous);
    }

    [Fact]
    public void A_node_without_create_time_never_contains_the_row_time()
    {
        var a = N(100, null, null, ProcessSources.Evtx4688);
        var b = N(100, T0.AddHours(9), null, ProcessSources.Evtx4688);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, T0, null).Kind.Should().Be(PidResolutionKind.Ambiguous);
    }

    [Fact]
    public void A_single_instance_is_chosen_and_unknown_or_ghost_pids_are_not_found()
    {
        var a = N(100, T0);
        ProcessPidResolver.Resolve(new[] { a }, 100, T0.AddYears(1), null).Nodes.Single().Should().BeSameAs(a);
        ProcessPidResolver.Resolve(new[] { a }, 999, null, null).Kind.Should().Be(PidResolutionKind.NotFound);
        ProcessPidResolver.Resolve(new[] { N(7, null, null, ProcessSources.None, ghost: true) }, 7, null, null).Kind.Should().Be(PidResolutionKind.NotFound);
    }

    [Fact]
    public void Row_time_is_read_from_the_time_column_like_the_grid_parses_dates()
    {
        var headers = new[] { "RecordNumber", "TimeCreated", "PayloadData1" };
        PidLinks.RowTime(headers, new[] { "1", "2024-03-01 10:20:30.1234567", "x" })
            .Should().Be(new DateTime(2024, 3, 1, 10, 20, 30, DateTimeKind.Utc).AddTicks(1234567));
        PidLinks.RowTime(headers, new[] { "1", "2024-03-01T10:20:30Z", "x" }).Should().Be(new DateTime(2024, 3, 1, 10, 20, 30, DateTimeKind.Utc));
        PidLinks.RowTime(headers, new[] { "1", "", "x" }).Should().BeNull();
        PidLinks.RowTime(headers, new[] { "1", "not a date", "x" }).Should().BeNull();
        PidLinks.RowTime(headers, new[] { "1" }).Should().BeNull();
        PidLinks.RowTime(new[] { "Name" }, new[] { "a" }).Should().BeNull();
        PidLinks.RowTime(headers, new[] { "1", "2024-03-01 10:20:30", "x" })!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Row_create_time_comes_only_from_a_CreateTime_column()
    {
        var mem = new[] { "PID", "PPID", "ImageFileName", "CreateTime", "ExitTime" };
        PidLinks.RowCreateTime(mem, new[] { "4", "0", "System", "2024-03-01 08:00:00.000000", "" }).Should().Be(new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc));
        PidLinks.RowCreateTime(new[] { "TimeCreated" }, new[] { "2024-03-01 08:00:00" }).Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\case\PC1\EventLogs\EventLogs.csv", @"C:\case\PC1")]
    [InlineData(@"C:\case\PC1\Memory\Memory_windows.pslist.csv", @"C:\case\PC1")]
    [InlineData(@"C:\case\PC1\a\b\deep.csv", @"C:\case\PC1")]
    [InlineData(@"C:\case\SuperTimeline_20240301.csv", null)]
    [InlineData(@"C:\case\_Case\EventLogs\x.csv", null)]
    [InlineData(@"C:\case\.ezcache\x.csv", null)]
    [InlineData(@"C:\other\PC2\Memory\Memory_windows.pslist.csv", @"C:\other\PC2")]
    [InlineData(@"C:\other\PC2\x.csv", @"C:\other\PC2")]
    public void Host_folder_of_a_csv(string csv, string? host) => PidLinks.HostFolderOfCsv(@"C:\case\", csv).Should().Be(host);

    [Fact]
    public void Process_list_csv_resolves_by_pid_and_create_time_only()
    {
        const string list = @"C:\case\PC1\Processes\Processes.csv";
        ProcessPidResolver.IsProcessListCsv(list).Should().BeTrue();
        ProcessPidResolver.IsProcessListCsv(@"C:\case\PC1\Memory\Processes.csv").Should().BeFalse();
        var a = N(100, T0, null, ProcessSources.Evtx4688);
        var b = N(100, T0.AddHours(2), null, ProcessSources.Evtx4688);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, T0.AddHours(2), list, T0.AddHours(2)).Nodes.Should().ContainSingle().Which.Should().BeSameAs(b);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, null, list, T0).Nodes.Should().ContainSingle().Which.Should().BeSameAs(a);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, T0, list, T0.AddHours(1)).Kind.Should().Be(PidResolutionKind.Ambiguous);
        ProcessPidResolver.Resolve(new[] { a, b }, 100, null, list).Kind.Should().Be(PidResolutionKind.Ambiguous);
        ProcessPidResolver.Resolve(new[] { a }, 100, null, list, T0.AddHours(1)).Kind.Should().Be(PidResolutionKind.Ambiguous, "no guess from a single PID with another CreateTime");
    }

    [Fact]
    public void Host_folder_without_a_case_root_is_the_csv_folder()
    {
        PidLinks.HostFolderOfCsv(null, @"C:\x\PC1\Memory\Memory_windows.pslist.csv").Should().Be(@"C:\x\PC1");
        PidLinks.HostFolderOfCsv(null, @"C:\x\PC1\.ezprocesses\Processes.csv").Should().Be(@"C:\x\PC1");
        PidLinks.HostFolderOfCsv(null, @"C:\x\PC1\Processes\Processes.csv").Should().Be(@"C:\x\PC1");
        PidLinks.HostFolderOfCsv("", "").Should().BeNull();
    }
}
