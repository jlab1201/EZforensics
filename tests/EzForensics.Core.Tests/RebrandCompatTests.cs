using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.Persistence;
using EzForensics.Core.SystemInfo;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>Cases, logs and manifests written before the EZ Hub → EZ Forensics rename keep working next to new ones.</summary>
public class RebrandCompatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_rebrand_" + Guid.NewGuid().ToString("N")[..8]);

    public RebrandCompatTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Old_and_new_manifests_are_both_listed_in_run_order()
    {
        foreach (var n in new[] { "EzHub_manifest_20261001T100000Z.json", "EzForensics_manifest_20261002T100000Z.json",
                                  "EzHub_manifest_20261003T100000Z.json", "Other_manifest_20261004T100000Z.json", "EzHub_manifest_x.txt" })
            File.WriteAllText(Path.Combine(_dir, n), "{}");

        CaseIndex.ManifestFiles(_dir).Select(Path.GetFileName).Should().Equal(
            "EzHub_manifest_20261001T100000Z.json", "EzForensics_manifest_20261002T100000Z.json", "EzHub_manifest_20261003T100000Z.json");
        CaseIndex.ManifestFiles(Path.Combine(_dir, "missing")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("EzForensics_manifest_20261002T100000Z.json", true)]
    [InlineData("EzHub_manifest_20261001T100000Z.json", true)]
    [InlineData("ezforensics_MANIFEST_20261001T100000Z.JSON", true)]
    [InlineData("ezhub_MANIFEST_20261001T100000Z.JSON", true)]
    [InlineData("EzForensics_manifest_20261002T100000Z.csv", false)]
    [InlineData("Other_manifest_20261002T100000Z.json", false)]
    public void Both_manifest_names_are_recognised_and_protected(string name, bool expected)
    {
        CaseIndex.IsManifestFileName(name).Should().Be(expected);
        CaseFileOperations.IsProtectedFileName(name).Should().Be(expected);
    }

    [Fact]
    public void A_new_run_writes_the_new_manifest_name()
    {
        var w = new ManifestWriter(_dir);
        Path.GetFileName(w.Path).Should().MatchRegex(@"^EzForensics_manifest_\d{8}T\d{6}Z\.json$");
    }

    [Fact]
    public void Legacy_built_in_tool_ids_in_old_manifests_load_as_the_current_ids()
    {
        var p = Path.Combine(_dir, "EzHub_manifest_20261001T100000Z.json");
        File.WriteAllText(p, """
            {"jobs":[
              {"toolId":"EzHub.Persistence","outputFiles":[{"path":"C:\\c\\Persistence.csv","toolId":"EzHub.Persistence"}]},
              {"toolId":"ezhub-computer-details","outputFiles":[]},
              {"toolId":"PECmd","outputFiles":[{"path":"C:\\c\\Prefetch.csv","toolId":"PECmd"}]}
            ]}
            """);
        var m = RunManifest.Load(p);
        m.Jobs.Select(j => j.ToolId).Should().Equal(PersistenceCollector.ToolId, ComputerDetailsStore.ToolId, "PECmd");
        m.Jobs[0].OutputFiles[0].ToolId.Should().Be(PersistenceCollector.ToolId);
        m.Jobs[2].OutputFiles[0].ToolId.Should().Be("PECmd");
    }

    [Fact]
    public void Layout_v1_persistence_folders_old_and_new_map_to_the_collector()
    {
        CaseIndex.LegacyToolId("Persistence", "EzHub").Should().Be(PersistenceCollector.ToolId);
        CaseIndex.LegacyToolId("Persistence", "EzForensics").Should().Be(PersistenceCollector.ToolId);
        CaseIndex.LegacyToolId("Execution", "PECmd").Should().Be("PECmd");
    }

    [Fact]
    public void Job_log_headers_old_and_new_are_header_lines()
    {
        JobLogFormat.IsHeaderLine(JobLogFormat.JobPrefix + Guid.NewGuid()).Should().BeTrue();
        JobLogFormat.IsHeaderLine("# EzHub job " + Guid.NewGuid()).Should().BeTrue();
        JobLogFormat.IsHeaderLine("EzHub job output line").Should().BeFalse();
    }

    [Fact]
    public void The_collector_writes_the_new_file_names()
    {
        PersistenceCollector.ToolId.Should().Be("EzForensics.Persistence");
        new[] { PersistenceCollector.MainCsv, PersistenceCollector.TasksCsv, PersistenceCollector.WmiCsv }
            .Should().OnlyContain(n => n.StartsWith("EzForensics_", StringComparison.Ordinal));
    }
}
