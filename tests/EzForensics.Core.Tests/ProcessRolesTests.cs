using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Roles (roles.json), indicators with evidence, ghost parents and the Processes.csv round trip of them. Facts only.</summary>
public sealed class ProcessRolesTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezforensics_roles_" + Guid.NewGuid().ToString("N"));
    private string Host => Path.Combine(_case, "PC1");
    public ProcessRolesTests() => Directory.CreateDirectory(Path.Combine(Host, "Memory"));
    public void Dispose() { try { Directory.Delete(_case, true); } catch { } }

    private static readonly DateTime T0 = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ProcessNode Node(int pid, string name, string path = "", int ppid = 4, DateTime? create = null, ProcessSources src = ProcessSources.None) =>
        new() { Pid = pid, Ppid = ppid, Name = name, Path = path, CreateTime = create, Sources = src };

    private static ProcessGraph Graph(params ProcessNode[] nodes)
    {
        var g = new ProcessGraph();
        g.AllNodes.AddRange(nodes);
        g.Roots.AddRange(nodes);
        return g;
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe", "Services", RoleBasis.NamePath)]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\SVCHOST.EXE", "Services", RoleBasis.NamePath)]
    [InlineData(@"\??\C:\Windows\system32\svchost.exe", "Services", RoleBasis.NamePath)]
    [InlineData("", "Services", RoleBasis.NameOnly)]
    [InlineData(@"C:\Users\x\svchost.exe", "Other", RoleBasis.None)]
    public void Classify_uses_name_and_stated_path(string path, string role, RoleBasis basis)
    {
        ProcessRoles.Classify(Node(800, "svchost.exe", path)).Should().Be((role, basis));
    }

    [Fact]
    public void Classify_unknown_name_has_no_role_and_explorer_lives_in_windows()
    {
        ProcessRoles.Classify(Node(1, "notepad.exe", @"C:\Windows\notepad.exe")).Should().Be(("Other", RoleBasis.None));
        ProcessRoles.Classify(Node(2, "explorer.exe", @"C:\Windows\explorer.exe")).Should().Be(("Desktop", RoleBasis.NamePath));
        ProcessRoles.Classify(Node(3, "System")).Should().Be(("System foundation", RoleBasis.NameOnly));
    }

    [Theory]
    [InlineData("rundll32.exe", @"C:\Windows\System32\rundll32.exe", "Admin tools & LOLBins", RoleBasis.NamePath)]
    [InlineData("REGSVR32.EXE", @"C:\Windows\SysWOW64\regsvr32.exe", "Admin tools & LOLBins", RoleBasis.NamePath)]
    [InlineData("certutil.exe", "", "Admin tools & LOLBins", RoleBasis.NameOnly)]
    [InlineData("wmic.exe", @"C:\Windows\System32\wbem\WMIC.exe", "Admin tools & LOLBins", RoleBasis.NamePath)]
    [InlineData("net1.exe", @"C:\Windows\System32\net1.exe", "Admin tools & LOLBins", RoleBasis.NamePath)]
    [InlineData("msbuild.exe", @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe", "Admin tools & LOLBins", RoleBasis.NameOnly)]
    [InlineData("installutil.exe", @"C:\Windows\Microsoft.NET\Framework\v2.0.50727\InstallUtil.exe", "Admin tools & LOLBins", RoleBasis.NameOnly)]
    [InlineData("schtasks.exe", @"C:\Users\x\schtasks.exe", "Other", RoleBasis.None)]
    [InlineData("psexesvc.exe", @"C:\Windows\PSEXESVC.exe", "Remote access", RoleBasis.NameOnly)]
    [InlineData("mstsc.exe", @"C:\Windows\System32\mstsc.exe", "Remote access", RoleBasis.NamePath)]
    [InlineData("wsmprovhost.exe", @"C:\Windows\System32\wsmprovhost.exe", "Remote access", RoleBasis.NamePath)]
    [InlineData("anydesk.exe", @"C:\Program Files (x86)\AnyDesk\AnyDesk.exe", "Remote access", RoleBasis.NameOnly)]
    [InlineData("ScreenConnect.ClientService.exe", @"C:\Program Files (x86)\ScreenConnect Client (abc)\ScreenConnect.ClientService.exe", "Remote access", RoleBasis.NameOnly)]
    [InlineData("cmd.exe", @"C:\Windows\System32\cmd.exe", "Shells & scripts", RoleBasis.NamePath)]
    public void Classify_admin_tools_and_remote_access(string name, string path, string role, RoleBasis basis)
    {
        ProcessRoles.Classify(Node(900, name, path)).Should().Be((role, basis));
    }

    [Fact]
    public void Admin_and_remote_tools_raise_no_parent_singleton_or_session_indicator()
    {
        var images = ProcessRoles.Images.Values.Where(i => i.Role is "Admin tools & LOLBins" or "Remote access").ToList();
        images.Select(i => i.Name).Should().Contain(new[] { "rundll32.exe", "regsvr32.exe", "certutil.exe", "forfiles.exe", "psexesvc.exe", "teamviewer.exe", "screenconnect.clientservice.exe" });
        images.Should().OnlyContain(i => i.Parents.Count == 0 && !i.Singleton && !i.Session0);
        // two co-existing copies in a user session, in their normal folders: no indicator at all
        var a = Node(901, "net.exe", @"C:\Windows\System32\net.exe", 600, T0, ProcessSources.PsList);
        var b = Node(902, "net.exe", @"C:\Windows\System32\net.exe", 600, T0, ProcessSources.PsList);
        var r = Node(903, "rdpclip.exe", @"C:\Windows\System32\rdpclip.exe", 600, T0, ProcessSources.PsList);
        a.SessionId = b.SessionId = r.SessionId = 2;
        var g = Graph(a, b, r);
        ProcessIndicators.Annotate(g);
        g.AllNodes.Should().OnlyContain(n => n.Indicators.Count == 0);
        a.Role.Should().Be("Admin tools & LOLBins");
        r.Role.Should().Be("Remote access");
    }

    [Fact]
    public void UnexpectedLocation_quotes_actual_and_expected_and_role_is_withheld()
    {
        var g = Graph(Node(800, "svchost.exe", @"C:\Users\x\svchost.exe", 600));
        ProcessIndicators.Annotate(g);
        var n = g.AllNodes[0];
        n.Role.Should().Be("Other");
        n.RoleBasis.Should().Be(RoleBasis.None);
        var i = n.Indicators.Single(x => x.Kind == IndicatorKind.UnexpectedLocation);
        i.Evidence.Should().Contain(@"C:\Users\x\svchost.exe").And.Contain(@"\windows\system32\");
    }

    [Fact]
    public void Correct_location_has_no_indicator()
    {
        var g = Graph(Node(800, "svchost.exe", @"C:\Windows\System32\svchost.exe", 600));
        ProcessIndicators.Evaluate(g).Should().BeEmpty();
    }

    [Fact]
    public void Two_evtx_only_lsass_that_never_overlap_are_not_duplicates()
    {
        var a = Node(700, "lsass.exe", create: T0, src: ProcessSources.Evtx4688);
        var b = Node(710, "lsass.exe", create: T0.AddDays(3), src: ProcessSources.Evtx4688);
        ProcessIndicators.Evaluate(Graph(a, b)).Should().NotContain(i => i.Kind == IndicatorKind.DuplicateSingleton);
    }

    [Fact]
    public void Two_active_pslist_lsass_are_duplicates_and_each_names_the_other()
    {
        var a = Node(700, "lsass.exe", create: T0, src: ProcessSources.PsList);
        var b = Node(710, "lsass.exe", create: T0.AddMinutes(5), src: ProcessSources.PsList);
        var dups = ProcessIndicators.Evaluate(Graph(a, b)).Where(i => i.Kind == IndicatorKind.DuplicateSingleton).ToList();
        dups.Should().HaveCount(2);
        dups.Single(d => d.Node == a).Evidence.Should().Contain("PID 710");
        dups.Single(d => d.Node == b).Evidence.Should().Contain("PID 700");
    }

    [Fact]
    public void Known_lifetimes_that_overlap_are_duplicates_but_a_pslist_instance_after_an_exited_one_is_not()
    {
        var a = Node(700, "wininit.exe", create: T0, src: ProcessSources.Evtx4688); a.ExitTime = T0.AddHours(2);
        var b = Node(710, "wininit.exe", create: T0.AddHours(1), src: ProcessSources.Evtx4688); b.ExitTime = T0.AddHours(3);
        ProcessIndicators.Evaluate(Graph(a, b)).Should().Contain(i => i.Kind == IndicatorKind.DuplicateSingleton);

        var c = Node(720, "wininit.exe", create: T0, src: ProcessSources.Evtx4688); c.ExitTime = T0.AddHours(1);
        var d = Node(730, "wininit.exe", create: T0.AddHours(2), src: ProcessSources.PsList);
        ProcessIndicators.Evaluate(Graph(c, d)).Should().NotContain(i => i.Kind == IndicatorKind.DuplicateSingleton);
    }

    [Fact]
    public void UnexpectedSession_for_session0_images_with_a_known_other_session()
    {
        var bad = Node(700, "lsass.exe"); bad.SessionId = 1;
        var ok = Node(710, "services.exe"); ok.SessionId = 0;
        var unknown = Node(720, "wininit.exe");
        var svc = Node(730, "svchost.exe"); svc.SessionId = 2;   // svchost is not session-0 only
        var ind = ProcessIndicators.Evaluate(Graph(bad, ok, unknown, svc)).Where(i => i.Kind == IndicatorKind.UnexpectedSession).ToList();
        ind.Should().ContainSingle().Which.Node.Should().BeSameAs(bad);
        ind[0].Evidence.Should().Contain("SessionId 1");
    }

    [Fact]
    public void EncodedCommand_decodes_utf16_base64_and_flags_undecodable()
    {
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("Write-Host hi"));
        var a = Node(1, "powershell.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        a.CmdLine = $"powershell.exe -NoP -w hidden -EncodedCommand {b64}";
        var b = Node(2, "pwsh.exe"); b.CmdLine = "pwsh -ec !!notbase64";
        var c = Node(3, "powershell.exe"); c.CmdLine = "powershell.exe -ExecutionPolicy Bypass -File x.ps1";
        var ind = ProcessIndicators.Evaluate(Graph(a, b, c)).Where(i => i.Kind == IndicatorKind.EncodedCommand).ToList();
        ind.Should().HaveCount(2);
        ind.Single(i => i.Node == a).Evidence.Should().Contain("decoded: Write-Host hi");
        ind.Single(i => i.Node == b).Evidence.Should().Contain("not valid Base64");
    }

    [Fact]
    public void Markers_become_indicators_and_orphans_are_data_gaps_only()
    {
        var n = Node(5, "x.exe", ppid: 77);
        n.Markers = ProcessMarkers.Injected | ProcessMarkers.Orphan | ProcessMarkers.FromLogsOnly;
        var g = Graph(n);
        ProcessIndicators.Evaluate(g).Select(i => i.Kind).Should().Equal(IndicatorKind.Injected);
        ProcessIndicators.DataGaps(g).Select(x => x.Kind).Should().BeEquivalentTo(new[] { DataGapKind.Orphan, DataGapKind.FromLogsOnly });
    }

    [Fact]
    public void SuspiciousParent_rules_still_work_from_roles_json()
    {
        SuspiciousParentRules.Find("explorer.exe", "svchost.exe")!.Why.Should().Be("svchost.exe should be started by services.exe");
        SuspiciousParentRules.Find("services.exe", "svchost.exe").Should().BeNull();
        SuspiciousParentRules.Find("winword.exe", "cmd.exe").Should().NotBeNull();
        SuspiciousParentRules.Find("lsass.exe", "anything.exe").Should().NotBeNull();
    }

    // ---- ghost parents ----

    private const string ListHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";
    private void Memory(string name, string content) => File.WriteAllText(Path.Combine(Host, "Memory", name), content, new UTF8Encoding(false));

    private void WriteOrphans() => Memory("Memory_PsList.csv", ListHeader +
        "0,4,0,System,0x1,1,0,,False,2024-05-01T00:00:00+00:00,,Disabled\r\n" +
        "0,800,9999,a.exe,0x2,1,0,1,False,2024-05-01T01:00:00+00:00,,Disabled\r\n" +
        "0,810,9999,b.exe,0x3,1,0,1,False,2024-05-01T02:00:00+00:00,,Disabled\r\n" +
        "0,820,8888,c.exe,0x4,1,0,1,False,2024-05-01T03:00:00+00:00,,Disabled\r\n");

    [Fact]
    public void Orphans_sharing_a_ppid_share_one_ghost_that_is_a_root_outside_AllNodes()
    {
        WriteOrphans();
        var g = ProcessGraphBuilder.Build(Host);
        g.Ghosts.Select(x => x.Pid).Should().BeEquivalentTo(new[] { 9999, 8888 });
        var ghost = g.Ghosts.Single(x => x.Pid == 9999);
        ghost.IsGhost.Should().BeTrue();
        ghost.Key.Should().Be("ghost:9999");
        ghost.Children.Select(c => c.Pid).Should().Equal(800, 810);
        ghost.Name.Should().BeEmpty("no stated ParentImage");
        ghost.CreateTime.Should().BeNull();
        g.Roots.Should().Contain(ghost).And.NotContain(g.AllNodes.Where(n => n.Pid is 800 or 810 or 820));
        g.AllNodes.Should().HaveCount(4).And.NotContain(x => x.IsGhost);
        ProcessIndicators.Evaluate(g).Should().NotContain(i => i.Node.IsGhost);
        g.AllNodes.Single(n => n.Pid == 800).Markers.Should().HaveFlag(ProcessMarkers.Orphan);
    }

    [Fact]
    public void Ghost_name_comes_only_from_a_stated_parent_image_and_is_empty_when_children_disagree()
    {
        var g = new ProcessGraph();
        var a = Node(1, "a.exe", ppid: 50); a.ParentImage = @"C:\Windows\System32\cmd.exe"; a.Markers = ProcessMarkers.Orphan;
        var b = Node(2, "b.exe", ppid: 50); b.ParentImage = @"c:\windows\system32\CMD.EXE"; b.Markers = ProcessMarkers.Orphan;
        var c = Node(3, "c.exe", ppid: 60); c.ParentImage = @"C:\x\one.exe"; c.Markers = ProcessMarkers.Orphan;
        var d = Node(4, "d.exe", ppid: 60); d.ParentImage = @"C:\y\two.exe"; d.Markers = ProcessMarkers.Orphan;
        g.AllNodes.AddRange(new[] { a, b, c, d });
        g.Roots.AddRange(g.AllNodes);
        typeof(ProcessGraphBuilder).GetMethod("AttachGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { g });
        var g50 = g.Ghosts.Single(x => x.Pid == 50);
        g50.Name.Should().Be("cmd.exe");
        g50.Path.Should().Be(@"C:\Windows\System32\cmd.exe");
        var g60 = g.Ghosts.Single(x => x.Pid == 60);
        g60.Name.Should().BeEmpty();
        g60.Path.Should().BeEmpty();
    }

    [Fact]
    public async Task Ghosts_are_excluded_from_counts_and_activity_and_round_trip_with_roles_and_indicators()
    {
        Memory("Memory_PsList.csv", ListHeader +
            "0,4,0,System,0x0,1,0,,False,2024-05-01T00:00:00+00:00,,Disabled\r\n" +
            "0,600,4,services.exe,0x1,1,0,1,False,2024-05-01T01:00:00+00:00,,Disabled\r\n" +
            "0,700,600,svchost.exe,0x2,1,0,0,False,2024-05-01T02:00:00+00:00,,Disabled\r\n" +
            "0,800,9999,a.exe,0x3,1,0,1,False,2024-05-01T03:00:00+00:00,,Disabled\r\n");
        var o = await ProcessCorrelator.RunAsync(Host);
        o.State.Should().Be(ProcessCorrelationState.Built);
        o.Nodes.Should().Be(4, "ghosts are not processes");

        var live = ProcessGraphBuilder.Build(Host);
        live.AllNodes.Should().HaveCount(4);
        live.AllNodes.Single(n => n.Pid == 600).Indicators.Select(i => i.Kind).Should().Contain(IndicatorKind.SuspiciousParent).And.Contain(IndicatorKind.UnexpectedSession);

        var act = new ProcessActivity(live);
        act.Counts(live.Ghosts.Single()).Values.Should().OnlyContain(v => v == 0);
        ProcessActivity.DataTabs.SelectMany(t => act.SetsFor(live.Ghosts.Single(), t)).Should().BeEmpty();

        var csv = File.ReadAllText(ProcessCorrelator.NodesPath(Host));
        csv.Should().Contain("Role,RoleBasis,Indicators,IsGhost");
        var loaded = ProcessCorrelator.LoadGraph(Host)!;
        loaded.AllNodes.Should().HaveCount(4);
        loaded.Ghosts.Should().ContainSingle().Which.Key.Should().Be("ghost:9999");
        loaded.Ghosts[0].Children.Should().ContainSingle().Which.Pid.Should().Be(800);
        loaded.Roots.Should().Contain(loaded.Ghosts[0]);
        var svc = loaded.AllNodes.Single(n => n.Pid == 600);
        svc.Role.Should().Be("Services");
        svc.RoleBasis.Should().Be(RoleBasis.NameOnly);
        string Kinds(ProcessGraph g) => string.Join("|", g.AllNodes.OrderBy(n => n.Pid).Select(n => n.Pid + ":" + ProcessIndicators.KindsText(n)));
        Kinds(loaded).Should().Be(Kinds(live));
        ProcessCorrelator.ReadManifest(Host)!.Version.Should().Be(ProcessCorrelator.FormatVersion);
    }
}
