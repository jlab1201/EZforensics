using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Navigator groups: indicators by kind with evidence, data gaps, roles, sessions &amp; users; ghosts never counted.</summary>
public sealed class ProcessNavigatorGroupsTests
{
    private static readonly DateTime T0 = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ProcessNode N(int pid, string name, string path = "", int? session = 0, string user = "", int min = 0, ProcessMarkers m = ProcessMarkers.None) =>
        new() { Pid = pid, Ppid = 4, Name = name, Path = path, SessionId = session, User = user, CreateTime = T0.AddMinutes(min), Markers = m };

    private static ProcessGraph Annotated(params ProcessNode[] nodes)
    {
        var g = new ProcessGraph();
        g.AllNodes.AddRange(nodes);
        g.Roots.AddRange(nodes);
        var ghost = new ProcessNode { Pid = 9999, Name = "", IsGhost = true };
        ghost.Children.Add(nodes[0]);
        nodes[0].Parent = ghost;
        g.Ghosts.Add(ghost);
        g.Roots.Add(ghost);
        ProcessIndicators.Annotate(g);
        return g;
    }

    [Fact]
    public void Indicators_group_by_kind_with_evidence_and_roles_by_role_with_flagged_counts()
    {
        var g = Annotated(
            N(100, "svchost.exe", @"C:\Windows\System32\svchost.exe", 0, @"NT AUTHORITY\SYSTEM", 0),
            N(200, "svchost.exe", @"C:\Users\x\svchost.exe", 1, "alice", 1, ProcessMarkers.Injected),
            N(300, "foo.exe", "", 1, "alice", 2, ProcessMarkers.Orphan));
        var r = ProcessNavigatorGroups.Build(g);

        r.Indicators.Select(x => x.Key).Should().Contain(new[] { "Injected", "UnexpectedLocation" });
        var inj = r.Indicators.Single(x => x.Key == "Injected");
        inj.Count.Should().Be(1);
        inj.Members[0].Node.Pid.Should().Be(200);
        inj.Members[0].Evidence.Should().Contain("malfind");
        r.DataGaps.Single().Key.Should().Be("Orphan");
        r.DataGaps[0].Members[0].Node.Pid.Should().Be(300);

        var services = r.Roles.Single(x => x.Title == "Services");
        services.Members.Select(m => m.Node.Pid).Should().Equal(100);
        var other = r.Roles.Single(x => x.Title == ProcessRoles.OtherRole);
        other.Count.Should().Be(2);
        other.FlaggedCount.Should().Be(2);
        r.Roles.Last().Title.Should().Be(ProcessRoles.OtherRole);
    }

    [Fact]
    public void Sessions_group_by_id_and_user_with_unknowns_labelled_and_ghosts_never_members()
    {
        var g = Annotated(N(1, "a.exe", "", 0, "SYSTEM"), N(2, "b.exe", "", 1, "alice", 1), N(3, "c.exe", "", 1, "alice", 2), N(4, "d.exe", "", null, "", 3));
        var r = ProcessNavigatorGroups.Build(g);

        r.Sessions.Select(x => x.Title).Should().Equal("Session 0 · SYSTEM", "Session 1 · alice", "Session ? · (unknown user)");
        r.Sessions[1].Count.Should().Be(2);
        r.Sessions.Should().OnlyContain(x => x.Key.All(char.IsLetterOrDigit) || x.Key.Contains('_'));
        var all = r.Indicators.Concat(r.DataGaps).Concat(r.Roles).Concat(r.Sessions).SelectMany(x => x.Members);
        all.Should().NotContain(m => m.Node.IsGhost);
        r.Roles.Sum(x => x.Count).Should().Be(4);
        r.Sessions.Sum(x => x.Count).Should().Be(4);
    }

    [Fact]
    public void Members_are_in_creation_order_and_each_process_is_listed_once()
    {
        var g = Annotated(N(5, "x.exe", "", 1, "u", 5), N(6, "y.exe", "", 1, "u", 1));
        var grp = ProcessNavigatorGroups.Build(g).Sessions.Single();
        grp.Members.Select(m => m.Node.Pid).Should().Equal(6, 5);
    }
}
