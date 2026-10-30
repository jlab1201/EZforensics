using EzForensics.Core.Runtimes;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Scripted probe: files that exist, PATH entries and canned process answers keyed on "exe arg1 arg2...".</summary>
internal sealed class FakeRuntimeProbe : IRuntimeProbe
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OnPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Answer for a command whose display string contains the key (first match wins).</summary>
    public List<(string Contains, string? Output)> Answers { get; } = new();
    public List<string> Calls { get; } = new();

    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => false;
    public string? FindOnPath(string fileName) => OnPath.TryGetValue(fileName, out var p) ? p : null;

    public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var line = exe + " " + string.Join(" ", args);
        Calls.Add(line);
        foreach (var (c, o) in Answers) if (line.Contains(c, StringComparison.Ordinal)) return o;
        return null;
    }
}

public class ZeekEngineTests
{
    private static readonly string Docker = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
    private static readonly string Wsl = @"C:\Windows\system32\wsl.exe";

    private static ZeekInstall NativeInstall(bool local = true) =>
        new(ZeekEngine.Native, ZeekOrigin.Configured, @"C:\Zeek\bin\zeek.exe", @"C:\Zeek\bin\zeek.exe", "9.0.0", @"C:\Zeek\bin", local);

    private static ZeekInstall DockerInstall() =>
        new(ZeekEngine.Docker, ZeekOrigin.Docker, Docker, "zeek", "9.0.0", ZeekLocator.DockerImage, true, ZeekLocator.DockerImage,
            new DockerImageInfo("sha256:aaa", new[] { "zeek/zeek@sha256:70733f4e" }, 1));

    private static ZeekInstall WslInstall() =>
        new(ZeekEngine.Wsl, ZeekOrigin.Wsl, Wsl, "/opt/zeek/bin/zeek", "9.0.0", "WSL Ubuntu", true, WslDistro: "Ubuntu-24.04");

    // ------------------------------------------------------------------ command builder

    [Fact]
    public void Native_command_runs_in_the_output_folder_with_local_and_tsv()
    {
        var inv = ZeekCommandBuilder.Build(NativeInstall(), new ZeekRunOptions { Pcap = @"D:\ev\cap one.pcap", OutputDir = @"C:\case\Network\Zeek\cap one.pcap" });
        inv.Exe.Should().Be(@"C:\Zeek\bin\zeek.exe");
        inv.Args.Should().Equal("-C", "-r", @"D:\ev\cap one.pcap", "local", "LogAscii::use_json=F");
        inv.WorkingDir.Should().Be(@"C:\case\Network\Zeek\cap one.pcap");
        inv.CancelCommand.Should().BeNull();
    }

    [Fact]
    public void Native_command_omits_local_when_the_install_has_no_site_policy()
    {
        var inv = ZeekCommandBuilder.Build(NativeInstall(local: false), new ZeekRunOptions { Pcap = @"D:\ev\a.pcap", OutputDir = @"C:\o" });
        inv.Args.Should().Equal("-C", "-r", @"D:\ev\a.pcap", "LogAscii::use_json=F");
    }

    [Fact]
    public void Docker_command_mounts_input_read_only_and_output_as_workdir()
    {
        var inv = ZeekCommandBuilder.Build(DockerInstall(), new ZeekRunOptions { Pcap = @"D:\ev dir\x.pcapng", OutputDir = @"C:\case\Network\Zeek\x.pcapng" }, "ezforensics-zeek-test");
        inv.Exe.Should().Be(Docker);
        inv.Args.Should().Equal("run", "--rm", "--name", "ezforensics-zeek-test", "--network", "none",
            "--mount", @"type=bind,source=D:\ev dir,target=/data/in,readonly",
            "--mount", @"type=bind,source=C:\case\Network\Zeek\x.pcapng,target=/data/out",
            "-w", "/data/out", "zeek/zeek:lts", "zeek", "-C", "-r", "/data/in/x.pcapng", "local", "LogAscii::use_json=F");
        inv.ContainerName.Should().Be("ezforensics-zeek-test");
        inv.CancelCommand.Should().Equal(Docker, "stop", "--time", "5", "ezforensics-zeek-test");
    }

    [Fact]
    public void Docker_mount_with_a_comma_is_csv_quoted()
    {
        DockerRuntime.MountSpec(@"D:\a,b", "/data/in", true).Should().Be("type=bind,\"source=D:\\a,b\",target=/data/in,readonly");
    }

    [Fact]
    public void Wsl_command_translates_paths_and_uses_exec()
    {
        var inv = ZeekCommandBuilder.Build(WslInstall(), new ZeekRunOptions { Pcap = @"D:\Ev Dir\x.pcap", OutputDir = @"C:\Cases\c1\Network\Zeek\x.pcap" });
        inv.Exe.Should().Be(Wsl);
        inv.Args.Should().Equal("-d", "Ubuntu-24.04", "--cd", "/mnt/c/Cases/c1/Network/Zeek/x.pcap", "-e",
            "/opt/zeek/bin/zeek", "-C", "-r", "/mnt/d/Ev Dir/x.pcap", "local", "LogAscii::use_json=F");
        inv.Environment.Should().ContainKey("WSL_UTF8");
        inv.CancelCommand.Should().Equal(Wsl, "-d", "Ubuntu-24.04", "-u", "root", "-e", "pkill", "-f", "--", @"zeek -C -r /mnt/d/Ev Dir/x\.pcap");
    }

    [Fact]
    public void Wsl_command_refuses_unc_paths()
    {
        var act = () => ZeekCommandBuilder.Build(WslInstall(), new ZeekRunOptions { Pcap = @"\\server\share\x.pcap", OutputDir = @"C:\o" });
        act.Should().Throw<ArgumentException>().WithMessage("*network shares*");
    }

    [Theory]
    [InlineData(@"C:\", "/mnt/c/")]
    [InlineData(@"C:\Users\a b\x.pcap", "/mnt/c/Users/a b/x.pcap")]
    [InlineData(@"d:\X\", "/mnt/d/X")]
    [InlineData(@"\\srv\share\x", null)]
    public void ToWslPath(string win, string? expected) => WslRuntime.ToWslPath(win).Should().Be(expected);

    [Fact]
    public void ZeekArgs_puts_extra_scripts_before_the_json_override()
    {
        ZeekCommandBuilder.ZeekArgs("/in/a.pcap", true, new[] { "policy/protocols/conn/mac-logging", " " })
            .Should().Equal("-C", "-r", "/in/a.pcap", "local", "policy/protocols/conn/mac-logging", "LogAscii::use_json=F");
    }

    // ------------------------------------------------------------------ WSL parsing

    [Fact]
    public void Wsl_list_parsing_skips_header_and_docker_internal_distros()
    {
        var text = "  NAME              STATE           VERSION\n* docker-desktop    Stopped         2\n  Ubuntu-24.04      Running         2\n  Debian            Stopped         1\n";
        var all = WslRuntime.ParseList(text);
        all.Select(d => d.Name).Should().Equal("docker-desktop", "Ubuntu-24.04", "Debian");
        all[0].IsDefault.Should().BeTrue();
        all[0].IsDockerInternal.Should().BeTrue();
        all[1].IsRunning.Should().BeTrue();
        all[2].Version.Should().Be(1);
        // UTF-16 read as UTF-8 (old wsl.exe without WSL_UTF8): NULs are stripped.
        WslRuntime.ParseList(string.Join("\0", "  Ubuntu    Running    2".ToCharArray())).Single().Name.Should().Be("Ubuntu");
    }

    [Fact]
    public void Os_release_parsing()
    {
        var r = WslRuntime.ParseOsRelease("PRETTY_NAME=\"Ubuntu 24.04.4 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nID=ubuntu\n");
        r!.IsUbuntu.Should().BeTrue();
        r.VersionId.Should().Be("24.04");
        WslRuntime.ParseOsRelease("garbage").Should().BeNull();
    }

    // ------------------------------------------------------------------ discovery

    [Fact]
    public void Configured_native_zeek_wins()
    {
        var p = new FakeRuntimeProbe();
        p.Files.Add(@"C:\Zeek\bin\zeek.exe");
        p.Files.Add(@"C:\Zeek\share\zeek\site\local.zeek");
        p.Answers.Add((@"zeek.exe --version", "zeek version 8.0.1"));
        var d = new ZeekLocator(p).Discover(@"C:\Zeek\bin\zeek.exe");
        d.Selected!.Engine.Should().Be(ZeekEngine.Native);
        d.Selected.Origin.Should().Be(ZeekOrigin.Configured);
        d.Selected.Version.Should().Be("8.0.1");
        d.Selected.HasLocalScript.Should().BeTrue();
        d.Setup.Should().Be(ZeekSetupOption.None);
        p.Calls.Should().NotContain(c => c.Contains("docker"), "Docker is not probed when a native Zeek answers");
    }

    [Fact]
    public void Docker_image_is_used_when_the_engine_runs_and_the_image_is_pulled()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["docker.exe"] = Docker;
        p.Answers.Add(("version --format", "28.4.0\n"));
        p.Answers.Add(("image inspect", "sha256:abc|zeek/zeek@sha256:70733f4e|178044378\n"));
        p.Answers.Add(("zeek/zeek:lts zeek --version", "zeek version 9.0.0\n"));
        var d = new ZeekLocator(p).Discover(null, includeWsl: false);
        d.Selected!.Engine.Should().Be(ZeekEngine.Docker);
        d.Selected.Version.Should().Be("9.0.0");
        d.Selected.Image!.Digest.Should().Be("sha256:70733f4e");
        var details = d.Selected.ManifestDetails();
        details["docker.repoDigest"].Should().Be("zeek/zeek@sha256:70733f4e");
        details["docker.imageId"].Should().Be("sha256:abc");
        details["zeek.engine"].Should().Be("docker");
    }

    [Fact]
    public void Docker_running_without_image_means_one_click_pull()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["docker.exe"] = Docker;
        p.Answers.Add(("version --format", "28.4.0"));
        var d = new ZeekLocator(p).Discover(null, includeWsl: false);
        d.Selected.Should().BeNull();
        d.Setup.Should().Be(ZeekSetupOption.DockerPull);
        d.Docker.EngineRunning.Should().BeTrue();
    }

    [Fact]
    public void Docker_desktop_installed_but_stopped_means_start_then_pull()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["docker.exe"] = Docker;
        p.Files.Add(Path.Combine(DockerRuntime.DefaultDesktopDir, "Docker Desktop.exe"));
        var d = new ZeekLocator(p).Discover(null, includeWsl: false);
        d.Setup.Should().Be(ZeekSetupOption.StartDockerThenPull);
        d.Notes.Should().Contain(n => n.Contains("not running"));
    }

    [Fact]
    public void Wsl_distro_with_zeek_is_found()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["wsl.exe"] = Wsl;
        p.Answers.Add(("-l -v", "  NAME   STATE   VERSION\n* Ubuntu-24.04   Running   2\n"));
        p.Answers.Add(("sh -c", "ZEEK=/opt/zeek/bin/zeek\nzeek version 9.0.0\nLOCAL=1\n"));
        var d = new ZeekLocator(p).Discover(null, includeDocker: false);
        d.Selected!.Engine.Should().Be(ZeekEngine.Wsl);
        d.Selected.WslDistro.Should().Be("Ubuntu-24.04");
        d.Selected.ZeekPath.Should().Be("/opt/zeek/bin/zeek");
        d.Selected.HasLocalScript.Should().BeTrue();
        d.Selected.Version.Should().Be("9.0.0");
    }

    [Fact]
    public void Wsl_without_zeek_means_install_in_the_private_distro()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["wsl.exe"] = Wsl;
        p.Answers.Add(("-l -v", "  NAME   STATE   VERSION\n* docker-desktop   Stopped   2\n  Ubuntu   Stopped   2\n"));
        p.Answers.Add(("cat /etc/os-release", "ID=ubuntu\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"\n"));
        var d = new ZeekLocator(p).Discover(null);
        d.Selected.Should().BeNull();
        d.Setup.Should().Be(ZeekSetupOption.EzDistroApt);
        d.WslCandidate.Should().Be(EzDistro.Name);
        p.Calls.Should().NotContain(c => c.Contains("cat /etc/os-release"), "the user's distros are no longer install targets");
        p.Calls.Should().NotContain(c => c.Contains("-d docker-desktop"), "Docker Desktop's internal distro is never used");
    }

    [Fact]
    public void Nothing_available_means_guidance()
    {
        var d = new ZeekLocator(new FakeRuntimeProbe()).Discover(null);
        d.Selected.Should().BeNull();
        d.Setup.Should().Be(ZeekSetupOption.Guidance);
        ZeekToolState.Evaluate(false, null, null, d.Setup).Should().Be(ZeekToolStatus.NeedsRuntime);
    }

    [Theory]
    [InlineData("zeek version 9.0.0", "9.0.0")]
    [InlineData("zeek version 8.1.0-dev.123", "8.1.0-dev.123")]
    [InlineData("nothing", null)]
    public void ParseVersion(string text, string? expected) => ZeekLocator.ParseVersion(text).Should().Be(expected);

    // ------------------------------------------------------------------ setup steps

    [Fact]
    public void Setup_steps_are_argv_lists()
    {
        ZeekSetup.DockerPullStep(Docker).Args.Should().Equal("pull", "zeek/zeek:lts");
        ZeekSetup.DockerRemoveStep(Docker).Args.Should().Equal("image", "rm", "zeek/zeek:lts");
        var w = ZeekSetup.WslInstallStep(Wsl, "Ubuntu");
        w.Args.Take(6).Should().Equal("-d", "Ubuntu", "-u", "root", "-e", "sh");
        w.Args[6].Should().Be("-c");
        w.Args[7].Should().Contain("download.opensuse.org/repositories/security:/zeek/").And.Contain("apt-get install -y --no-install-recommends zeek")
            .And.Contain("signed-by=").And.NotContain("\r");
        ZeekSetup.WslRemoveScript.Should().Contain("ezhub-security-zeek.list");
    }

    [Fact]
    public void Setup_state_round_trips()
    {
        using var t = new TempDir();
        var s = new ZeekSetupState { DockerImagePulled = "zeek/zeek:lts" };
        s.WslDistros.Add("Ubuntu");
        s.Save(t.Path);
        var back = ZeekSetupState.Load(t.Path);
        back.DockerImagePulled.Should().Be("zeek/zeek:lts");
        back.WslDistros.Should().Equal("Ubuntu");
        ZeekSetupState.Load(Path.Combine(t.Path, "missing")).IsEmpty.Should().BeTrue();
    }

    // ------------------------------------------------------------------ latest version / status

    [Fact]
    public void GitHub_latest_release_parsing()
    {
        var (v, pub) = ZeekReleaseCheck.ParseGitHubLatest("{\"tag_name\":\"v9.0.0\",\"published_at\":\"2026-08-21T20:24:47Z\",\"draft\":false}");
        v.Should().Be("9.0.0");
        pub.Should().Be(new DateTime(2026, 8, 21, 20, 24, 47, DateTimeKind.Utc));
        ZeekReleaseCheck.ParseGitHubLatest("not json").Version.Should().BeNull();
    }

    [Fact]
    public void Docker_hub_moving_tag_resolves_to_its_release()
    {
        var json = "{\"results\":[{\"name\":\"latest\",\"digest\":\"sha256:70\",\"last_updated\":\"2026-09-14T18:11:00Z\"}," +
                   "{\"name\":\"lts\",\"digest\":\"sha256:70\"},{\"name\":\"9.0\",\"digest\":\"sha256:70\"},{\"name\":\"9.0.0\",\"digest\":\"sha256:70\"}," +
                   "{\"name\":\"9.0.0-amd64\",\"digest\":\"sha256:3f\"},{\"name\":\"8.0.10\",\"digest\":\"sha256:73\"}]}";
        var tags = DockerRuntime.ParseHubTags(json);
        tags.Should().HaveCount(6);
        DockerRuntime.ResolveMovingTag(tags, "lts").Should().Be(("9.0.0", "sha256:70"));
        DockerRuntime.ResolveMovingTag(tags, "missing").Should().Be(((string?)null, (string?)null));
    }

    [Theory]
    [InlineData("9.0.1", "9.0.0", true)]
    [InlineData("9.0.10", "9.0.9", true)]
    [InlineData("v9.0.0", "9.0.0", false)]
    [InlineData("8.2.2", "9.0.0", false)]
    [InlineData("9.0.0", "unknown", false)]
    public void IsNewer(string latest, string installed, bool expected) => ZeekReleaseCheck.IsNewer(latest, installed).Should().Be(expected);

    [Fact]
    public void Status_uses_the_image_digest_for_docker_and_the_version_otherwise()
    {
        var latest = new ZeekRelease("9.0.1", null, "9.0.0", "sha256:70733f4e", null);
        ZeekToolState.Evaluate(false, DockerInstall(), latest, ZeekSetupOption.None).Should().Be(ZeekToolStatus.Installed);
        ZeekToolState.Evaluate(false, DockerInstall(), latest with { ImageDigest = "sha256:new" }, ZeekSetupOption.None).Should().Be(ZeekToolStatus.UpdateAvailable);
        ZeekToolState.Evaluate(false, WslInstall(), latest, ZeekSetupOption.None).Should().Be(ZeekToolStatus.UpdateAvailable);
        ZeekToolState.Evaluate(false, NativeInstall(), null, ZeekSetupOption.None).Should().Be(ZeekToolStatus.Installed);
        ZeekToolState.Evaluate(true, null, null, ZeekSetupOption.Guidance).Should().Be(ZeekToolStatus.Detecting);
        ZeekToolState.Evaluate(false, null, null, ZeekSetupOption.DockerPull).Should().Be(ZeekToolStatus.NotInstalled);
        ZeekToolState.Label(ZeekToolStatus.NeedsRuntime).Should().Be("Needs Docker or WSL");
    }
}
