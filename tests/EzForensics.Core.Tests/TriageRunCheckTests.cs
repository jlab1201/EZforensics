using EzForensics.Core.Execution;
using EzForensics.Core.Tools;
using EzForensics.Core.Triage;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>
/// Fresh-install regression ("Run selected does nothing"): every Run selected outcome must be explained. Simulates a brand-new VM:
/// an empty tools folder (nothing downloaded yet), a real scan + plan of the test evidence.
/// </summary>
public class TriageRunCheckTests
{
    private static IReadOnlyList<PlannedJob> PlanTestEvidence(string caseDir)
    {
        var layout = SourceLayoutDetector.Detect(TriageTestData.Tout);
        var found = ArtifactScanner.Scan(layout, TriageRules.LoadEmbedded());
        return TriagePlanner.PlanJobs(found, TriageRules.LoadEmbedded(), caseDir);
    }

    [Fact]
    public void Fresh_install_with_empty_tools_folder_reports_every_missing_EZ_tool()
    {
        using var tools = new TempDir();
        var locator = new ToolLocator();
        locator.Rescan(tools.Path);   // empty: nothing downloaded yet
        var plan = PlanTestEvidence(Path.Combine(tools.Path, "case"));
        plan.Should().NotBeEmpty("the test evidence has artifacts, so a plan exists even without tools");

        var missing = TriageRunCheck.MissingTools(plan.Select(p => p.Spec), locator);

        missing.Should().NotBeEmpty();
        missing.Should().OnlyHaveUniqueItems();
        missing.Should().BeEquivalentTo(plan.Select(p => p.Spec.ExeName).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Installed_tool_is_not_reported()
    {
        using var tools = new TempDir();
        var exe = Path.Combine(tools.Path, "MFTECmd.exe");
        File.WriteAllText(exe, "");
        var spec = new JobSpec("MFTECmd", "MFTECmd.exe", Array.Empty<string>(), tools.Path, "MFT", "FileSystem");

        TriageRunCheck.ToolMissing(spec, new FakeLocator(exe)).Should().BeNull();
        TriageRunCheck.ToolMissing(spec, new FakeLocator(null)).Should().Be("MFTECmd.exe");
        // resolved by the locator but deleted since the last rescan: the job would fail at start
        TriageRunCheck.ToolMissing(spec, new FakeLocator(Path.Combine(tools.Path, "gone.exe"))).Should().Be("MFTECmd.exe");
    }

    [Fact]
    public void Explicit_exe_path_is_checked_only_when_rooted()
    {
        using var tools = new TempDir();
        var gone = Path.Combine(tools.Path, "python.exe");
        var rooted = new JobSpec("vol", "vol", Array.Empty<string>(), tools.Path, "info", "Memory", ExePath: gone);
        var bare = rooted with { ExePath = "wsl.exe" };

        TriageRunCheck.ToolMissing(rooted, new FakeLocator(null)).Should().Be(gone);
        TriageRunCheck.ToolMissing(bare, new FakeLocator(null)).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0, null, false, false, false, "Scan")]
    [InlineData(5, 0, null, false, false, false, "No jobs are ticked")]
    [InlineData(5, 2, "HOST: duplicate", false, false, false, "HOST: duplicate")]
    [InlineData(5, 2, null, false, false, true, "case folder is empty")]
    [InlineData(5, 2, null, true, false, false, "still in progress")]
    [InlineData(5, 2, null, false, true, false, "memory image")]
    public void Disabled_run_button_always_has_a_reason(int planned, int selected, string? sysErr, bool active, bool memPlanning, bool caseEmpty, string expected)
    {
        TriageRunCheck.BlockedReason(new RunGate(planned, selected, sysErr, active, memPlanning, caseEmpty))
            .Should().NotBeNullOrWhiteSpace().And.Contain(expected);
    }

    [Fact]
    public void Runnable_gate_has_no_reason()
        => TriageRunCheck.BlockedReason(new RunGate(5, 2, null, false, false, false)).Should().BeNull();

    [Fact]
    public void Nothing_queued_message_is_never_empty()
    {
        // Previously: only RITA rows ticked (or every skip list empty) gave "Nothing was queued: " with no reason.
        TriageRunCheck.Describe(0, new RunSkips(), @"C:\case").Should().StartWith("Nothing was queued: ").And.Contain("Tick");
        var rita = new RunSkips();
        rita.RitaNotes.Add("RITA is not set up yet.");
        TriageRunCheck.Describe(0, rita, @"C:\case").Should().Contain("RITA is not set up yet.");
    }

    [Fact]
    public void Missing_tools_message_names_the_tools_and_offers_setup()
    {
        var skips = new RunSkips { MissingToolJobs = 3 };
        skips.MissingTools.AddRange(new[] { "MFTECmd.exe", "PECmd.exe" });

        skips.Any.Should().BeTrue();
        var none = TriageRunCheck.Describe(0, skips, @"C:\case");
        none.Should().StartWith("Nothing was queued").And.Contain("MFTECmd.exe").And.Contain("PECmd.exe").And.Contain("Open setup");
        TriageRunCheck.Describe(4, skips, @"C:\case").Should().StartWith("Queued 4 job(s)").And.Contain("3 job(s) skipped");
    }

    [Fact]
    public void Clean_run_message()
        => TriageRunCheck.Describe(2, new RunSkips(), @"C:\case").Should().Be(@"Queued 2 job(s). Output goes to C:\case.");
}
