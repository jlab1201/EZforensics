using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// The Spectre engine's EZ Forensics skill (src/spectre/knowledge/ezforensics/SKILL.md in the Spectre repo, shipped in its package) is generated from the skills registry.
/// Set <c>EZFORENSICS_WRITE_SPECTRE_SKILL=1</c> to (re)write it (<c>EZFORENSICS_SPECTRE_REPO</c> picks another Spectre checkout or worktree); otherwise, when the Spectre repo sits next to this one, the test
/// fails if the file is out of date.
/// </summary>
public class SpectreEngineSkillTests
{
    [Fact]
    public void The_generated_skill_lists_pages_actions_and_limits()
    {
        var md = SpectreSkills.SkillMarkdown();
        md.Should().StartWith("---\nname: ezforensics\n");
        md.Should().Contain("## Jobs").And.Contain("Spectre tools: jobs_retry, jobs_cancel");
        md.Should().Contain("## Every page").And.Contain(SpectrePageTools.GoToPage);
        md.Should().Contain("never changes the AI provider or API keys");
        md.Should().Contain("- auto_mode: off");
        md.Should().NotContain("\r");
    }

    [Fact]
    public void The_Spectre_repo_copy_is_up_to_date()
    {
        var repo = FindSpectreRepo();
        if (repo == null) return;   // Spectre is not checked out next to EZ Forensics
        var path = Path.Combine(repo, "src", "spectre", "knowledge", "ezforensics", "SKILL.md");
        var md = SpectreSkills.SkillMarkdown();
        if (Environment.GetEnvironmentVariable("EZFORENSICS_WRITE_SPECTRE_SKILL") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, md);
        }
        File.Exists(path).Should().BeTrue("run the tests once with EZFORENSICS_WRITE_SPECTRE_SKILL=1 to create it");
        File.ReadAllText(path).Replace("\r\n", "\n").Should().Be(md, "the skills registry changed: rerun with EZFORENSICS_WRITE_SPECTRE_SKILL=1 and commit it in the Spectre repo");
    }

    /// <summary>The Spectre repo next to the EZ Forensics checkout (…/Projects/Spectre), found by walking up from the test folder.</summary>
    private static string? FindSpectreRepo()
    {
        // EZFORENSICS_SPECTRE_REPO points at another checkout or worktree of the Spectre repo
        if (Environment.GetEnvironmentVariable("EZFORENSICS_SPECTRE_REPO") is { Length: > 0 } over && File.Exists(Path.Combine(over, "pyproject.toml"))) return over;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Spectre");
            if (File.Exists(Path.Combine(candidate, "pyproject.toml")) && Directory.Exists(Path.Combine(candidate, "skills"))) return candidate;
        }
        return null;
    }
}
