using EzForensics.Core.Analysis;
using EzForensics.Core.Case;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The synthetic demo case (samples\demo-case, copied next to the test assembly): it loads into a process tree with indicators and a Review page with findings.</summary>
public sealed class DemoCaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ezf-demo-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly string Source = Path.Combine(AppContext.BaseDirectory, DemoCase.FolderName);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private async Task<string> Create()
    {
        DemoCase.IsAvailable(Source).Should().BeTrue("the test project copies samples\\demo-case next to the assembly");
        var r = await DemoCase.CreateAsync(_root, Source, new DateTime(2026, 3, 14, 12, 0, 0));
        r.Warnings.Should().BeEmpty();
        return r.CaseDir;
    }

    [Fact]
    public async Task Copy_makes_a_new_case_folder_with_the_marker_and_the_process_tree_data()
    {
        var dir = await Create();
        Path.GetFileName(dir).Should().Be("Demo-20260314_120000");
        CaseLayout.ReadMarker(dir)!.LayoutVersion.Should().Be(CaseLayout.CurrentVersion);
        var host = Path.Combine(dir, "DEMO-WS01");
        ProcessCorrelator.IsUpToDate(host).Should().BeTrue();

        var second = await DemoCase.CreateAsync(_root, Source, new DateTime(2026, 3, 14, 12, 0, 0));
        Path.GetFileName(second.CaseDir).Should().Be("Demo-20260314_120000-2");   // never reuses a folder
    }

    [Fact]
    public async Task Process_graph_flags_hollowing_the_encoded_command_and_the_odd_parents()
    {
        var host = Path.Combine(await Create(), "DEMO-WS01");
        foreach (var g in new[] { ProcessGraphBuilder.Build(host), ProcessCorrelator.LoadGraph(host)! })
        {
            ProcessNode Node(int pid) => g.AllNodes.Single(n => n.Pid == pid && !n.IsGhost);
            bool Has(int pid, IndicatorKind k) => Node(pid).Indicators.Any(i => i.Kind == k);

            Has(6104, IndicatorKind.Hollowed).Should().BeTrue();              // svchost: hollowprocesses
            Has(6104, IndicatorKind.Injected).Should().BeTrue();              // svchost: malfind
            Has(6104, IndicatorKind.SuspiciousParent).Should().BeTrue();      // svchost started by rundll32
            Node(6104).Parent!.Name.Should().Be("rundll32.exe");
            Has(5232, IndicatorKind.EncodedCommand).Should().BeTrue();        // powershell -enc
            Node(5232).Indicators.Single(i => i.Kind == IndicatorKind.EncodedCommand).Evidence.Should().Contain("Write-Output demo");
            Has(5232, IndicatorKind.SuspiciousParent).Should().BeTrue();      // Word started PowerShell
            Node(5232).Parent!.Name.Should().BeEquivalentTo("winword.exe");
            Node(5480).CmdLine.Should().Contain(@"AppData\Local\Temp\msupd.dll");
            // the normal svchost instances stay clean
            Node(1012).Indicators.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Findings_and_key_events_load_from_the_case_analysis()
    {
        var dir = await Create();
        var store = CaseAnalysisStore.Open(dir);
        store.LoadWarning.Should().BeNull();
        var a = store.Data;
        a.Items.Should().HaveCountGreaterThan(0).And.OnlyContain(i => i.Origin == ItemOrigin.Analyst);
        AnalysisView.ActiveFindings(a).Should().HaveCount(a.Items.Count);
        AnalysisView.ActiveKeyEvents(a).Should().HaveCountGreaterThan(0);
        a.Items.SelectMany(i => i.Mitre).Should().Contain(new[] { "T1055.012", "T1059.001", "T1218.011", "T1543.003", "T1078" });

        // every evidence row points at an existing CSV row
        foreach (var r in a.Items.SelectMany(i => i.SourceRows))
        {
            var csv = Path.Combine(dir, r.CsvRelPath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(csv).Should().BeTrue(r.CsvRelPath);
            (File.ReadAllLines(csv).Length - 1).Should().BeGreaterThan((int)r.Row, r.Key.ToString());
        }
        // key events are in time order and each holds findings
        var times = AnalysisView.ActiveKeyEvents(a).Select(k => KeyEventOps.Effective(a, k).Time).ToList();
        times.Should().OnlyContain(t => t != null).And.BeInAscendingOrder();
        // "Add my analysis" has nothing to change: the items match the Analyze workspace
        AnalysisSync.Preview(a).AnyChange.Should().BeFalse();
    }
}
