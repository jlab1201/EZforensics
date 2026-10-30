using EzForensics.Core.Execution;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

internal static class TriageTestData
{
    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                var p = Path.Combine(d.FullName, "tests", "TestData");
                if (Directory.Exists(p)) return p;
                d = d.Parent;
            }
            throw new DirectoryNotFoundException("tests/TestData not found");
        }
    }
    public static string Tout => Path.Combine(Root, "tout");
    public static string C => Path.Combine(Root, "tout", "C");
}

public class TriageLayoutTests
{
    [Fact]
    public void Tout_layout_finds_C()
    {
        var l = SourceLayoutDetector.Detect(TriageTestData.Tout);
        l.SystemRoots.Should().ContainSingle().Which.Should().EndWith("C");
        l.IsLiveVolume.Should().BeFalse();
    }

    [Fact]
    public void Direct_root_is_itself()
    {
        var l = SourceLayoutDetector.Detect(TriageTestData.C + "\\");
        l.SystemRoots.Should().ContainSingle().Which.Should().Be(TriageTestData.C);
    }

    [Fact]
    public void Unknown_folder_returns_source()
    {
        var src = Path.Combine(TriageTestData.Root, "unknown");
        var l = SourceLayoutDetector.Detect(src);
        l.SystemRoots.Should().Equal(src);
        l.Notes.Should().NotBeEmpty();
    }
}

public class TriageScannerTests
{
    private static List<DetectedArtifact> Scan()
        => ArtifactScanner.Scan(SourceLayoutDetector.Detect(TriageTestData.Tout), TriageRules.LoadEmbedded());

    [Fact]
    public void Rules_load_from_embedded_resource()
    {
        var rules = TriageRules.LoadEmbedded();
        rules.Should().HaveCountGreaterThan(15);
        rules.Single(r => r.Id == "jumplists").Recurse.Should().BeTrue();
        rules.Single(r => r.Id == "registry").Paths.Should().HaveCount(6);
    }

    [Theory]
    [InlineData("prefetch", 1)]
    [InlineData("evtx", 1)]
    [InlineData("mft", 1)]
    [InlineData("usnjrnl", 1)]
    [InlineData("amcache", 1)]
    [InlineData("appcompat", 1)]
    [InlineData("registry", 4)]      // SYSTEM, SOFTWARE, bob NTUSER, alice NTUSER, bob UsrClass -> 5 below
    [InlineData("shellbags", 2)]
    [InlineData("lnk", 1)]
    [InlineData("jumplists", 1)]
    [InlineData("recyclebin", 1)]
    [InlineData("srum", 1)]
    public void Detects_rule(string id, int expectedMin)
    {
        Scan().Count(a => a.RuleId == id).Should().BeGreaterThanOrEqualTo(expectedMin);
    }

    [Fact]
    public void Registry_is_per_match_with_five_hives()
    {
        var reg = Scan().Where(a => a.RuleId == "registry").ToList();
        reg.Should().HaveCount(5).And.OnlyContain(a => a.PerMatch);
    }

    [Fact]
    public void Undetected_rules_are_absent()
    {
        var ids = Scan().Select(a => a.RuleId).ToHashSet();
        ids.Should().NotContain(new[] { "sum", "activitiescache", "recentfilecache", "logfile" });
    }

    [Fact]
    public void Non_per_match_reports_count()
    {
        var lnk = Scan().Single(a => a.RuleId == "prefetch");
        lnk.MatchCount.Should().Be(1);
        lnk.MatchedPath.Should().EndWith("Prefetch");
    }

    [Fact]
    public void Missing_source_does_not_throw()
    {
        var l = new SourceLayout(new[] { Path.Combine(Path.GetTempPath(), "ezforensics_nope_" + Guid.NewGuid().ToString("N")) }, false, Array.Empty<string>());
        ArtifactScanner.Scan(l, TriageRules.LoadEmbedded()).Should().BeEmpty();
    }

    [Fact]
    public void Live_volume_synthesizes_mft()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ezforensics_live_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var l = new SourceLayout(new[] { tmp }, true, Array.Empty<string>());
            var mft = ArtifactScanner.Scan(l, TriageRules.LoadEmbedded()).Single(a => a.RuleId == "mft");
            mft.RequiresAdmin.Should().BeTrue();
        }
        finally { Directory.Delete(tmp, true); }
    }
}

public class TriagePlannerTests
{
    private static (IReadOnlyList<DetectedArtifact> found, string root) Found()
    {
        var l = SourceLayoutDetector.Detect(TriageTestData.Tout);
        return (ArtifactScanner.Scan(l, TriageRules.LoadEmbedded()), l.SystemRoots[0]);
    }

    [Fact]
    public void Output_dirs_and_args_are_expanded()
    {
        var (found, root) = Found();
        var specs = TriagePlanner.Plan(found, @"D:\case", root);

        var host = TriagePlanner.HostOf(found[0], root);
        var pf = specs.Single(s => s.ToolId == "PECmd");
        pf.OutputDir.Should().Be(Path.Combine(@"D:\case", host, "ProgramExecution"), "case layout v2: <case>\\<Host>\\<Category>, no tool folder");
        pf.Output!.StagingDir.Should().StartWith(@"D:\case\.ezstaging\");
        pf.Output.ArtifactName.Should().Be("Prefetch");
        pf.Output.FixedFileName.Should().Be("Prefetch.csv");
        pf.Args.Should().Equal("-d", Path.Combine(root, "Windows", "Prefetch"), "--csv", pf.Output.StagingDir, "-q", "--csvf", "Prefetch.csv");
        pf.ExeName.Should().Be("PECmd.exe");
        pf.Provenance!.Inputs.Should().ContainSingle().Which.Should().Be(new InputSelector(Path.Combine(root, "Windows", "Prefetch"), "*.pf", false));

        var srum = specs.Single(s => s.ToolId == "SrumECmd");
        srum.Args.Should().Contain(Path.Combine(root, "Windows", "System32", "config", "SOFTWARE"));

        specs.Single(s => s.Label == "$MFT").Exclusive.Should().BeTrue();
        specs.SelectMany(s => s.Args).Should().NotContain(a => a.Contains('{') && !a.Contains("toolDir") && a != "{eventIds}" && a != "{batchFile}");
    }

    [Fact]
    public void PerMatch_creates_one_job_each_with_sanitized_subfolder()
    {
        var (found, root) = Found();
        var specs = TriagePlanner.Plan(found, @"D:\case", root).Where(s => s.ToolId == "RECmd").ToList();
        specs.Should().HaveCount(5);
        // One shared category folder; per-hive variants are qualifiers in the file name, each job has its own staging folder.
        specs.Select(s => s.OutputDir).Distinct().Should().ContainSingle().Which.Should().EndWith(@"\Registry");
        specs.Select(s => s.Output!.StagingDir).Should().OnlyHaveUniqueItems();
        specs.Select(s => s.Output!.Qualifier).Should().OnlyHaveUniqueItems().And.Contain(new[] { "SYSTEM", "SOFTWARE", "NTUSER_bob" });
        var bob = specs.Single(s => s.Output!.Qualifier == "NTUSER_bob");
        bob.Args.Should().Equal("-f", Path.Combine(root, "Users", "bob", "NTUSER.DAT"), "--bn", "{batchFile}", "--csv", bob.Output!.StagingDir);
    }

    [Fact]
    public void Checklist_has_default_selection()
    {
        var (found, root) = Found();
        var jobs = TriagePlanner.PlanJobs(found, TriageRules.LoadEmbedded(), @"D:\case", root);
        jobs.Should().NotBeEmpty().And.Contain(j => j.SelectedByDefault);
        jobs.Single(j => j.Spec.ToolId == "SQLECmd").SelectedByDefault.Should().BeFalse();
    }

    [Fact]
    public void Mkape_import_converts_tokens()
    {
        var yaml = "Description: PECmd\nCategory: ProgramExecution\nProcessors:\n  -\n    Executable: PECmd.exe\n    CommandLine: -d %sourceDirectory% --csv \"%destinationDirectory%\" -q\n    ExportFormat: csv\n";
        var r = MkapeImporter.Parse(yaml).Single();
        r.ToolId.Should().Be("PECmd");
        r.Args.Should().Equal("-d", "{dir}", "--csv", "{out}", "-q");
        r.Mode.Should().Be(RuleMode.Dir);
    }
}
