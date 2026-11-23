using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreEngineSetupTests
{
    [Fact]
    public void Probe_script_has_one_line_per_tool_and_only_LF()
    {
        var s = SpectreEngineTools.ProbeScript();
        s.Should().NotContain("\r");
        foreach (var t in SpectreEngineTools.All) s.Should().Contain($"echo \"{t.Id}=${{v:-MISSING}}\"");
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(SpectreEngineTools.All.Count);
    }

    [Fact]
    public void Probe_output_becomes_statuses_and_ignores_noise()
    {
        var map = SpectreEngineTools.ParseProbe("wsl: some notice\r\nspectre=0.1.0\r\nsleuthkit=4.12.1-1\r\nregripper=MISSING\r\ntshark=installed\r\nbogus=1\r\n");
        map["spectre"].Should().Be(new SpectreToolStatus("spectre", true, "0.1.0"));
        map["sleuthkit"].Version.Should().Be("4.12.1-1");
        map["regripper"].Installed.Should().BeFalse();
        map["tshark"].Should().Be(new SpectreToolStatus("tshark", true, null));
        map.Should().NotContainKey("bogus");
        map["zeek"].Installed.Should().BeFalse("tools missing from the output count as not installed");
        map["spectre"].Text.Should().Be("Installed, version 0.1.0");
    }

    [Fact]
    public void Manifest_ids_are_unique_and_every_apt_tool_names_a_package()
    {
        SpectreEngineTools.All.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        SpectreEngineTools.All.Where(t => t.Kind == SpectreInstallKind.Apt).Should().OnlyContain(t => t.Packages.Count > 0);
    }

    [Fact]
    public void Apt_script_is_one_update_and_one_install_for_all_packages()
    {
        var s = SpectreEngineSetup.AptScript(SpectreEngineTools.All.Where(t => t.Kind == SpectreInstallKind.Apt));
        s.Should().StartWith("set -e\nexport DEBIAN_FRONTEND=noninteractive\n").And.NotContain("\r");
        Occurrences(s, "apt-get update").Should().Be(1);
        Occurrences(s, "apt-get install").Should().Be(1);
        s.Should().Contain("sleuthkit").And.Contain("libevtx-utils").And.Contain("regripper").And.Contain("tcpdump").And.Contain("tshark");
        s.Should().Contain("debconf-set-selections").And.Contain("ln -sf").And.Contain("/usr/local/bin/rip.pl");
    }

    [Fact]
    public void Venv_script_installs_from_the_source_and_quotes_it()
    {
        var s = SpectreEngineSetup.VenvScript("/mnt/d/My Work/Spectre", withPlaso: false);
        s.Should().Contain("python3 python3-venv libmagic1t64 libpango-1.0-0").And.Contain("python3 -m venv /opt/ezforensics/spectre/venv");
        s.Should().Contain("pip install --upgrade '/mnt/d/My Work/Spectre[report]'");
        SpectreEngineSetup.VenvScript("/mnt/d/S", true).Should().Contain("'/mnt/d/S[report,plaso]'");
    }

    [Fact]
    public void MemProcFs_script_uses_the_github_release_and_the_spectre_default_dir()
    {
        var s = SpectreEngineSetup.MemProcFsScript();
        s.Should().Contain("ufrisk/MemProcFS/releases/latest").And.Contain("expanded_assets").And.Contain("linux_x64").And.Contain("/opt/memprocfs").And.Contain("libfuse2t64");
        s.Should().NotContain("api.github.com");   // 403 once the unauthenticated rate limit is used up
    }

    [Fact]
    public void Plans_per_tool_and_for_all()
    {
        const string wsl = @"C:\Windows\System32\wsl.exe";
        SpectreEngineSetup.PlanFor(wsl, SpectreEngineTools.Find("tcpdump")!, null).Steps.Should().ContainSingle()
            .Which.ToolId.Should().Be(SpectreEngineSetup.ToolIdApt);
        var noSource = SpectreEngineSetup.PlanFor(wsl, SpectreEngineTools.Find("spectre")!, null);
        noSource.Ok.Should().BeFalse();
        noSource.Problem.Should().Contain("EZFORENSICS_SPECTRE_SRC");
        SpectreEngineSetup.PlanFor(wsl, SpectreEngineTools.Find("plaso")!, null, "/mnt/d/Spectre").Steps[0].ToolId.Should().Be(SpectreEngineSetup.ToolIdPlaso);

        // missing optional tools (tshark, Zeek, MemProcFS, plaso) are not part of "all"
        var allMissing = SpectreEngineTools.ParseProbe("");
        var plan = SpectreEngineSetup.PlanAll(wsl, allMissing, null, "/mnt/d/Spectre");
        plan.Steps.Select(s => s.ToolId).Should().Equal(SpectreEngineSetup.ToolIdApt, SpectreEngineSetup.ToolIdVenv);
        plan.Steps[0].Args.Last().Should().Contain("sleuthkit").And.NotContain("tshark");
        // an optional apt tool that is already installed gets updated
        var installed = SpectreEngineTools.ParseProbe(string.Join("\n", SpectreEngineTools.All.Select(t => t.Id + "=1.0")));
        plan = SpectreEngineSetup.PlanAll(wsl, installed, null, "/mnt/d/Spectre");
        plan.Steps.Select(s => s.ToolId).Should().Equal(SpectreEngineSetup.ToolIdApt, SpectreEngineSetup.ToolIdVenv);
        plan.Steps[0].Args.Last().Should().Contain("tshark");
    }

    [Fact]
    public void Source_comes_from_the_environment_or_a_sibling_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezf-spectre-" + Guid.NewGuid().ToString("N"));
        var spectre = Path.Combine(root, "Spectre");
        Directory.CreateDirectory(Path.Combine(spectre, "src", "spectre"));
        Directory.CreateDirectory(Path.Combine(root, "EzForensics", "bin"));
        try
        {
            SpectreEngineSetup.FindSource(Path.Combine(root, "EzForensics", "bin"), envValue: "").Should().BeNull("no pyproject.toml yet");
            File.WriteAllText(Path.Combine(spectre, "pyproject.toml"), "");
            SpectreEngineSetup.FindSource(Path.Combine(root, "EzForensics", "bin"), envValue: "").Should().Be(spectre);
            SpectreEngineSetup.FindSource(null, envValue: spectre).Should().Be(spectre);
            SpectreEngineSetup.FindSource(null, envValue: root).Should().BeNull();
        }
        finally { Directory.Delete(root, true); }
    }

    private static int Occurrences(string s, string part) => (s.Length - s.Replace(part, "").Length) / part.Length;
}
