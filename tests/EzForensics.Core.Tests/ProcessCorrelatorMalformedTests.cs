using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ProcessCorrelatorMalformedTests
{
    [Fact]
    public void ReadNodes_ignores_parent_links_that_would_form_a_cycle()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezf-nodes-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, "Key,PID,PPID,ParentKey,Name\n10:0,10,20,20:0,a.exe\n20:0,20,10,10:0,b.exe\n30:0,30,30,30:0,c.exe\n");
        try
        {
            var g = ProcessCorrelator.ReadNodes(path);
            g.AllNodes.Should().HaveCount(3);
            g.Roots.Should().NotBeEmpty();
            g.Roots.Should().Contain(n => n.Pid == 30);
            ProcessCorrelator.Ordered(g).Should().HaveCount(3);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[[null,\"Equals\",\"x\"]]")]
    [InlineData("[[\"A\",null,null]]")]
    [InlineData("{}")]
    public void DecodeFilter_returns_null_for_malformed_json_shapes(string json) =>
        ProcessCorrelator.DecodeFilter(json).Should().BeNull();
}
