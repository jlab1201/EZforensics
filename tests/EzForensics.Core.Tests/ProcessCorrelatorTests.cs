using System.Text;
using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.Processes;
using EzForensics.Core.Results;
using EzForensics.Core.Triage;
using FluentAssertions;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Tests;

/// <summary>Built-in process correlator: Processes.csv round trip, Exact / PathTime activity rows, the inputs fingerprint, Ingest planning and file names.</summary>
public sealed class ProcessCorrelatorTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezforensics_correlator_" + Guid.NewGuid().ToString("N"));
    private string Host => Path.Combine(_case, "PC1");

    public ProcessCorrelatorTests() => Directory.CreateDirectory(Path.Combine(Host, "Memory"));

    public void Dispose() { try { Directory.Delete(_case, true); } catch { } }

    private const string PsHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";
    private static string Ps(int pid, int ppid, string name, string create, string exit = "") =>
        $"0,{pid},{ppid},{name},0xffff{pid:x},5,100,1,False,2024-05-01 {create}.1234567,{(exit.Length > 0 ? "2024-05-01 " + exit + ".0000000" : "")},Disabled\r\n";
    private const string NetHeader = "TreeDepth,Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created\r\n";

    private void Memory(string name, string content) => File.WriteAllText(Path.Combine(Host, "Memory", name), content, new UTF8Encoding(false));

    private void WriteHost()
    {
        Memory("Memory_PsList.csv", PsHeader + Ps(600, 4, "services.exe", "09:00:00") + Ps(3000, 600, "chrome.exe", "10:00:00")
            + Ps(3100, 3000, "chrome.exe", "10:00:01", "10:30:00") + Ps(4000, 9999, "orphan.exe", "11:00:00"));
        Memory("Memory_NetScan.csv", NetHeader + "0,0xe01,TCPv4,10.0.0.5,50111,142.250.1.1,443,ESTABLISHED,3000,chrome.exe,2024-05-01 10:05:00.0000000\r\n");
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        using var tr = new StreamReader(path);
        using var r = CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true });
        var rows = new List<Dictionary<string, string>>();
        while (r.Read())
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.GetString(i);
            rows.Add(d);
        }
        return rows;
    }

    [Fact]
    public async Task Processes_csv_loads_back_to_the_same_graph_shape()
    {
        WriteHost();
        var o = await ProcessCorrelator.RunAsync(Host);
        o.State.Should().Be(ProcessCorrelationState.Built);
        o.Nodes.Should().Be(4);

        var live = ProcessGraphBuilder.Build(Host);
        var loaded = ProcessCorrelator.LoadGraph(Host);
        loaded.Should().NotBeNull("the output is fresh");
        static string Shape(ProcessGraph g) => string.Join(";", ProcessCorrelator.Ordered(g).Select(n => $"{n.Key}<{n.Parent?.Key}|{n.Name}|{n.Markers}|{n.Sources}|{n.ExitTime:O}"));
        Shape(loaded!).Should().Be(Shape(live));
        loaded!.Roots.Select(n => n.Key).Should().Equal(live.Roots.Select(n => n.Key));
        loaded.SourceCounts.Should().BeEquivalentTo(live.SourceCounts);
        loaded.AllNodes.Single(n => n.Pid == 3000).CreateTime.Should().Be(live.AllNodes.Single(n => n.Pid == 3000).CreateTime, "times keep their ticks (the key)");

        var rows = ReadCsv(ProcessCorrelator.NodesPath(Host));
        rows.Single(r => r["PID"] == "3000")["NetworkCount"].Should().Be("1");
        rows.Single(r => r["PID"] == "3000")["FirstSeen"].Should().StartWith("2024-05-01 10:05:00");
    }

    [Fact]
    public async Task Activity_rows_are_only_exact_or_path_time_and_reopen_their_row()
    {
        WriteHost();
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        var rows = ReadCsv(ProcessCorrelator.ActivityPath(Host));
        rows.Should().NotBeEmpty();
        rows.Select(r => r["Tier"]).Should().OnlyContain(t => t == nameof(LinkTier.Exact) || t == nameof(LinkTier.PathTime));
        var sock = rows.Single(r => r["Source"] == "windows.netscan");
        sock["PID"].Should().Be("3000");
        sock["Tab"].Should().Be("Network");
        var origin = ProcessCorrelator.OriginOf(Host, sock["SourceFile"], sock["SourceRow"]);
        origin.Should().NotBeNull();
        origin!.CsvPath.Should().Be(Path.Combine(Host, "Memory", "Memory_NetScan.csv"));
        origin.Filter.ToDisplayString().Should().Be("[PID] = '3000'");
        ProcessRowLocator.GridTarget(new[] { new ProcessActivityRow(null, LinkTier.Exact, "x", Array.Empty<string>(), origin) })!.CsvPath.Should().Be(origin.CsvPath);
    }

    [Fact]
    public void Filter_encoding_round_trips_conditions_and_and_groups_only()
    {
        var and = FilterGroup.And(new FilterCondition("EventId", ColumnFilterMode.Equals, "1"), new FilterCondition("TimeCreated", ColumnFilterMode.BeginsWith, "2024-05-01 10:00:00"));
        ProcessCorrelator.DecodeFilter(ProcessCorrelator.EncodeFilter(and))!.ToDisplayString().Should().Be(and.ToDisplayString());
        ProcessCorrelator.EncodeFilter(FilterGroup.Or(new FilterCondition("a", ColumnFilterMode.Equals, "1"))).Should().BeEmpty();
        ProcessCorrelator.DecodeFilter("").Should().BeNull();
        ProcessCorrelator.DecodeFilter("not json").Should().BeNull();
    }

    [Fact]
    public async Task Fingerprint_is_fresh_until_an_input_changes_or_appears()
    {
        WriteHost();
        ProcessCorrelator.IsUpToDate(Host).Should().BeFalse("nothing was built yet");
        await ProcessCorrelator.RunAsync(Host);
        ProcessCorrelator.IsUpToDate(Host).Should().BeTrue();
        ProcessCorrelator.InputFiles(Host).Should().NotContain(f => f.Contains(Path.DirectorySeparatorChar + ".ezprocesses" + Path.DirectorySeparatorChar));
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.UpToDate);

        File.AppendAllText(Path.Combine(Host, "Memory", "Memory_PsList.csv"), Ps(5000, 600, "cmd.exe", "12:00:00"));
        ProcessCorrelator.IsUpToDate(Host).Should().BeFalse("an input grew");
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        ProcessCorrelator.IsUpToDate(Host).Should().BeTrue();

        Memory("Memory_Handles.csv", "PID,Process,Offset,HandleValue,Type,GrantedAccess,Name\r\n");
        ProcessCorrelator.IsUpToDate(Host).Should().BeFalse("a new input appeared");
        ProcessCorrelator.LoadGraph(Host).Should().BeNull("a stale output is not loaded");
        (await ProcessCorrelator.RunAsync(Host, force: false)).State.Should().Be(ProcessCorrelationState.Built);
    }

    [Fact]
    public async Task No_process_source_is_not_built()
    {
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.NoSource);
        File.Exists(ProcessCorrelator.NodesPath(Host)).Should().BeFalse();
    }

    // ------------------------------------------------------------------ Ingest planning

    private static PlannedJob Tool(string rule, string host) =>
        new(new JobSpec("T", "T.exe", Array.Empty<string>(), @"C:\case\" + host + @"\X", rule, "X") { Provenance = new JobProvenance { RuleId = rule, Host = host } }, true,
            new DetectedArtifact(rule, rule, "T", "X", @"E:\" + host, false), false);

    [Fact]
    public void Compose_has_no_process_row_and_ProcessHosts_lists_the_hosts_with_a_process_source()
    {
        var plan = IngestSteps.Compose(new[]
        {
            Tool("evtx", "WS01"), Tool(MemoryTriage.RuleId, "WS01"), Tool(MemoryTriage.RuleId, "MEM01"), Tool("prefetch", "DC01"), Tool("mft", "WS01"),
        }, _case, new[] { @"E:\" }, plasoAvailable: true);

        plan.Should().NotContain(p => p.Artifact.RuleId == "ingest-process-correlation" || p.Spec.ToolId == ProcessCorrelator.ToolId);
        IngestSelections.AllRules().Select(r => r.Id).Should().NotContain("ingest-process-correlation");
        IngestDefaults.FastForensicsRuleIds.Should().NotContain("ingest-process-correlation");
        IngestSteps.ProcessHosts(_case, plan).Should().BeEquivalentTo(new[] { Path.Combine(_case, "WS01"), Path.Combine(_case, "MEM01") });
    }

    [Fact]
    public void ProcessHosts_includes_a_host_whose_folder_already_has_process_lists()
    {
        IngestSteps.ProcessHosts(_case, new[] { Tool("prefetch", "PC1") }).Should().BeEmpty();
        WriteHost();
        IngestSteps.ProcessHosts(_case, new[] { Tool("prefetch", "PC1") }).Should().ContainSingle().Which.Should().Be(Host);
    }

    // ------------------------------------------------------------------ hidden folder / legacy cleanup / category

    [Fact]
    public async Task Backend_files_are_hidden_under_ezprocesses_and_only_the_list_is_visible()
    {
        WriteHost();
        ProcessCorrelator.FolderFor(Host).Should().Be(Path.Combine(Host, ".ezprocesses"));
        ProcessCorrelator.FormatVersion.Should().Be(7);
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        Directory.GetFiles(Path.Combine(Host, "Processes")).Select(Path.GetFileName).Should().Equal("Processes.csv");
        File.Exists(Path.Combine(Host, ".ezprocesses", "Processes.csv")).Should().BeTrue();
        File.Exists(Path.Combine(Host, ".ezprocesses", "Processes_Sets.csv")).Should().BeTrue();
        ProcessCorrelator.Spec("x.exe", Host).Label.Should().Be("Process tree data: PC1");
        ArtifactNaming.IdentifyNewName("Processes_Activity.csv").Should().BeNull("the hidden backend files are not artifacts");
    }

    [Fact]
    public async Task Merged_list_has_readable_headers_one_row_per_real_process_ordered_by_create_time()
    {
        WriteHost();
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        var path = ProcessCorrelator.ListPath(Host);
        path.Should().Be(Path.Combine(Host, "Processes", "Processes.csv"));
        File.ReadLines(path).First().Should().Be("PID,PPID,Name,Path,CommandLine,User,Integrity,SessionId,Wow64,CreateTime,ExitTime,ParentName,Role,Indicators,Sources,Hash,Signature,Company,Threads,Handles,Offset");
        var rows = ReadCsv(path);
        rows.Should().HaveCount(4, "the orphan's missing parent 9999 is a ghost and has no row");
        rows.Select(r => r["PID"]).Should().Equal("600", "3000", "3100", "4000");
        rows.Select(r => r["CreateTime"]).Should().BeInAscendingOrder();
        rows[0]["Sources"].Should().Be("pslist");
        rows[0]["CreateTime"].Should().Be("2024-05-01 09:00:00.1234567");
        rows[2]["ParentName"].Should().Be("chrome.exe");
        rows[2]["ExitTime"].Should().Be("2024-05-01 10:30:00.0000000");
        Directory.GetFiles(Path.Combine(Host, "Processes"), "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Case_tree_lists_the_list_as_a_Processes_category_and_the_hidden_folder_stays_hidden()
    {
        WriteHost();
        await ProcessCorrelator.RunAsync(Host);
        CaseIndex.IsKnownCategory("Processes").Should().BeTrue();
        EvidenceKinds.ForFolder("Processes").Should().Be(EvidenceKind.Processes);
        CaseIndex.FromPath(@"PC1\Processes\Processes.csv").Category.Should().Be("Processes");
        ArtifactNaming.IdentifyNewName("Processes.csv").Should().NotBeNull();
        CaseTreeOrder.IsHiddenFolder(".ezprocesses").Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_correlator_files_are_deleted_after_a_build_and_the_new_list_is_kept_and_logged()
    {
        WriteHost();
        var legacy = Path.Combine(Host, "Processes");
        Directory.CreateDirectory(legacy);
        foreach (var n in new[] { "Processes.csv", "Processes_Activity.csv", "Processes_Sets.csv", "Processes_2.csv", ".inputs.json", "Processes.csv.ezstats.json", "Processes.ezflags.json" })
            File.WriteAllText(Path.Combine(legacy, n), "x");
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        Directory.Exists(legacy).Should().BeTrue("the new list lives there");
        Directory.GetFiles(legacy).Select(Path.GetFileName).Should().Equal("Processes.csv");
        File.ReadAllText(Path.Combine(legacy, "Processes.csv")).Should().StartWith("PID,PPID,Name");
        File.ReadAllText(Path.Combine(_case, "logs", "file_operations.log")).Should().Contain("DELETE").And.Contain("Processes_Activity.csv");
    }

    [Fact]
    public async Task Legacy_Processes_folder_with_a_foreign_file_is_kept_and_warned_about()
    {
        WriteHost();
        var legacy = Path.Combine(Host, "Processes");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Processes.csv"), "x");
        File.WriteAllText(Path.Combine(legacy, "notes.txt"), "mine");
        var log = new List<string>();
        (await ProcessCorrelator.RunAsync(Host, log: log.Add)).State.Should().Be(ProcessCorrelationState.Built);
        File.Exists(Path.Combine(legacy, "notes.txt")).Should().BeTrue();
        File.Exists(Path.Combine(legacy, "Processes.csv")).Should().BeTrue();
        log.Should().Contain(l => l.StartsWith("Warning:") && l.Contains("notes.txt"));
    }

    [Fact]
    public async Task User_source_and_integrity_survive_the_round_trip()
    {
        WriteHost();
        Memory("Memory_GetSids.csv", "TreeDepth,PID,Process,SID,Name\r\n0,3000,chrome.exe,S-1-5-21-1-2-3-1001,bob\r\n0,3000,chrome.exe,S-1-16-8192,Medium Mandatory Level\r\n");
        ProcessCorrelator.FormatVersion.Should().Be(7);
        ProcessCorrelator.InputFiles(Host).Should().Contain(p => p.EndsWith("Memory_GetSids.csv"));
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        var n = ProcessCorrelator.LoadGraph(Host)!.AllNodes.Single(x => x.Pid == 3000);
        (n.User, n.UserSource, n.Integrity).Should().Be(("bob", "getsids", "Medium"));
    }
}
