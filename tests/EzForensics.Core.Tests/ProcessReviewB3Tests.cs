using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Pre-release review (B3) regressions: Navigator key uniqueness, tolerant roles.json loading.</summary>
public sealed class ProcessReviewB3Tests
{
    [Fact]
    public void Session_group_keys_stay_unique_when_slugs_collide()
    {
        var g = new ProcessGraph();
        g.AllNodes.Add(new ProcessNode { Pid = 1, Name = "a.exe", SessionId = 1, User = @"DOM\bob" });
        g.AllNodes.Add(new ProcessNode { Pid = 2, Name = "b.exe", SessionId = 1, User = "DOM_bob" });
        g.Roots.AddRange(g.AllNodes);
        var r = ProcessNavigatorGroups.Build(g);
        r.Sessions.Should().HaveCount(2);
        r.Sessions.Select(s => s.Key).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Roles_file_with_null_entry_or_missing_role_loads()
    {
        var json = "{\"images\":{\"a.exe\":null,\"b.exe\":{\"dirs\":[\"\\\\windows\\\\\"]}}}";
        var roles = ProcessRoles.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        roles.ContainsKey("a.exe").Should().BeFalse();
        roles["b.exe"].Role.Should().Be(ProcessRoles.OtherRole);
    }
}
