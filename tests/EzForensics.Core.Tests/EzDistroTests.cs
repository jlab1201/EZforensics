using System.Net;
using System.Security.Cryptography;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Setup;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class EzDistroTests
{
    private const string Wsl = @"C:\Windows\system32\wsl.exe";

    // ------------------------------------------------------------------ command lines and scripts

    [Fact]
    public void Import_command_line_is_per_user_wsl2_into_the_data_folder()
    {
        EzDistro.ImportArgs(@"C:\d\wsl\EzForensics", @"C:\d\wsl\downloads\rootfs.tar.gz")
            .Should().Equal("--import", "EzForensics", @"C:\d\wsl\EzForensics", @"C:\d\wsl\downloads\rootfs.tar.gz", "--version", "2");
        EzDistro.UnregisterArgs().Should().Equal("--unregister", "EzForensics");
        EzDistro.InstallDir.Should().EndWith(Path.Combine("wsl", "EzForensics"));
    }

    [Fact]
    public void Scripts_run_as_root_in_the_private_distro_with_lf_only()
    {
        var args = EzDistro.ScriptArgs("echo a\r\necho b\r");
        args.Take(6).Should().Equal("-d", "EzForensics", "-u", "root", "-e", "sh");
        args[6].Should().Be("-c");
        args[7].Should().Be("echo a\necho b\n");
        EzDistro.ProvisionScript.Should().NotContain("\r");
        EzDistro.WslConf.Should().NotContain("\r");
        EzDistro.ProvisionStep(Wsl).Args.Should().NotContain(a => a.Contains('\r'));
        ZeekSetup.WslInstallStep(Wsl, EzDistro.Name).Args.Should().ContainInOrder("-d", "EzForensics", "-u", "root", "-e", "sh", "-c");
    }

    [Fact]
    public void Provision_script_writes_wsl_conf_installs_base_packages_and_the_marker()
    {
        var s = EzDistro.ProvisionScript;
        s.Should().StartWith("set -e\n");
        s.Should().Contain("cat > /etc/wsl.conf <<'EZWSLCONF'\n# Written by EZ Forensics");
        s.Should().Contain("\nEZWSLCONF\n", "the heredoc terminator must be alone at column 0");
        s.Should().Contain("apt-get install -y --no-install-recommends ca-certificates curl gnupg procps");
        s.Should().Contain($"> {EzDistro.MarkerPath}");
        s.Should().Contain("schema " + EzDistro.SchemaVersion);
        EzDistro.WslConf.Should().Contain("systemd=false").And.Contain("default=root").And.Contain("root=/mnt/")
            .And.Contain("[interop]\nenabled=false").And.Contain("appendWindowsPath=false").And.Contain("generateResolvConf=true");
    }

    [Theory]
    [InlineData("EZDISTRO=ubuntu-base 24.04.5 / schema 1\n", "ubuntu-base 24.04.5 / schema 1")]
    [InlineData("\r\nEZDISTRO=x\r\n", "x")]
    [InlineData("", null)]
    [InlineData("EZDISTRO=\n", null)]
    public void ParseProbe(string output, string? expected) => EzDistro.ParseProbe(output).Should().Be(expected);

    // ------------------------------------------------------------------ pin

    [Fact]
    public void Rootfs_is_pinned_to_an_official_https_url_and_sha256()
    {
        foreach (var pin in new[] { EzDistro.UbuntuBaseAmd64, EzDistro.UbuntuBaseArm64 })
        {
            EzDistro.IsOfficialUrl(pin.Url).Should().BeTrue();
            pin.Url.Should().Contain("/" + pin.Version + "/").And.EndWith($"-{pin.Architecture}.tar.gz");
            pin.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        }
        EzDistro.UbuntuBaseAmd64.Bytes.Should().Be(30_028_293);
        EzDistro.IsOfficialUrl("http://cdimage.ubuntu.com/x.tar.gz").Should().BeFalse();
        EzDistro.IsOfficialUrl("https://example.com/x.tar.gz").Should().BeFalse();
    }

    [Fact]
    public void Verify_checks_size_and_sha256()
    {
        var dir = Directory.CreateTempSubdirectory("ezdistro-").FullName;
        try
        {
            var f = Path.Combine(dir, "r.tar.gz");
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(f, bytes);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var pin = new EzRootfsPin("T", "1", "amd64", "https://cdimage.ubuntu.com/r.tar.gz", sha, 5);
            EzDistro.Verify(f, pin).Should().BeTrue();
            EzDistro.Verify(f, pin with { Sha256 = sha.ToUpperInvariant() }).Should().BeTrue();
            EzDistro.Verify(f, pin with { Bytes = 6 }).Should().BeFalse();
            EzDistro.Verify(f, pin with { Bytes = 0 }).Should().BeTrue("size 0 = not pinned");
            EzDistro.Verify(f, pin with { Sha256 = new string('0', 64) }).Should().BeFalse();
            EzDistro.Verify(Path.Combine(dir, "missing"), pin).Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FixedHandler(byte[] body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    [Fact]
    public async Task Download_keeps_a_verified_file_and_rejects_a_mismatch()
    {
        var dir = Directory.CreateTempSubdirectory("ezdistro-").FullName;
        try
        {
            var body = new byte[] { 9, 8, 7 };
            var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var pin = new EzRootfsPin("T", "1", "amd64", "https://cdimage.ubuntu.com/ubuntu-base/r.tar.gz", sha, 3);
            var handler = new FixedHandler(body);
            using var http = new HttpClient(handler);
            var path = await EzDistro.DownloadRootfsAsync(http, pin, dir, null, null, default);
            path.Should().Be(Path.Combine(dir, "r.tar.gz"));
            File.ReadAllBytes(path).Should().Equal(body);
            await EzDistro.DownloadRootfsAsync(http, pin, dir, null, null, default);
            handler.Calls.Should().Be(1, "a verified copy is re-used");

            var bad = pin with { Url = "https://cdimage.ubuntu.com/ubuntu-base/bad.tar.gz", Sha256 = new string('a', 64), Bytes = 0 };
            var act = () => EzDistro.DownloadRootfsAsync(http, bad, dir, null, null, default);
            await act.Should().ThrowAsync<InvalidDataException>();
            Directory.GetFiles(dir).Should().Equal(new[] { path }, "the mismatching download is deleted");

            var foreign = pin with { Url = "https://example.com/r.tar.gz" };
            await ((Func<Task>)(() => EzDistro.DownloadRootfsAsync(http, foreign, dir, null, null, default))).Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ state

    [Fact]
    public void Inspect_reports_wsl_disabled_missing_and_ready()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["wsl.exe"] = Wsl;
        EzDistro.Inspect(p).WslAvailable.Should().BeFalse("wsl --status failed");

        p.Answers.Add(("--status", "Default Version: 2"));
        p.Answers.Add(("-l -v", "  NAME   STATE   VERSION\n* Ubuntu   Running   2\n"));
        var missing = EzDistro.Inspect(p);
        missing.WslAvailable.Should().BeTrue();
        missing.Exists.Should().BeFalse();
        missing.Summary.Should().Be("Not installed");

        p.Answers.Insert(0, ("-l -v", "  NAME   STATE   VERSION\n* Ubuntu   Running   2\n  EzForensics   Stopped   2\n"));
        p.Answers.Insert(0, ("-d EzForensics -u root -e sh -c", "EZDISTRO=ubuntu-base 24.04.5 / schema 1\n"));
        var ready = EzDistro.Inspect(p);
        ready.Ready.Should().BeTrue();
        ready.BaseVersion.Should().Be("ubuntu-base 24.04.5 / schema 1");
        p.Calls.Should().NotContain(c => c.Contains("-d Ubuntu"), "the user's distros are never started");
    }

    [Theory]
    [InlineData("ubuntu-base 24.04.5 / schema 1", 1)]
    [InlineData("ubuntu-base 24.04.5 / schema 12", 12)]
    [InlineData("ubuntu-base 24.04.5", 0)]
    [InlineData("x", 0)]
    [InlineData(null, 0)]
    public void Marker_schema(string? marker, int expected) => EzDistro.MarkerSchema(marker).Should().Be(expected);

    [Fact]
    public void A_marker_from_an_older_schema_is_not_ready()
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["wsl.exe"] = Wsl;
        p.Answers.Add(("--status", "Default Version: 2"));
        p.Answers.Add(("-l -v", "  NAME   STATE   VERSION\n  EzForensics   Stopped   2\n"));
        p.Answers.Add(("-d EzForensics -u root -e sh -c", "EZDISTRO=ubuntu-base 24.04.5 / schema 0\n"));
        var info = EzDistro.Inspect(p);
        info.Exists.Should().BeTrue();
        info.Provisioned.Should().BeFalse("the first-boot script must run again");
        info.Ready.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://cdimage.ubuntu.com/x.tar.gz", true)]
    [InlineData("https://ubuntu.com/x", true)]
    [InlineData("http://cdimage.ubuntu.com/x.tar.gz", false)]
    [InlineData("https://evilubuntu.com/x.tar.gz", false)]
    [InlineData("https://cdimage.ubuntu.com.evil.net/x", false)]
    public void Redirect_target_must_be_https_on_ubuntu_com(string url, bool ok) => EzDistro.IsUbuntuHttps(new Uri(url)).Should().Be(ok);

    [Fact]
    public async Task A_registered_distro_that_was_not_listed_keeps_its_disk()
    {
        var root = Directory.CreateTempSubdirectory("ezdistro-").FullName;
        var before = Environment.GetEnvironmentVariable("EZFORENSICS_WSL_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("EZFORENSICS_WSL_ROOT", Path.Combine(root, "wsl"));
            Directory.CreateDirectory(EzDistro.InstallDir);
            File.WriteAllBytes(EzDistro.VhdxPath(), new byte[] { 1 });
            var p = new FakeRuntimeProbe();
            p.OnPath["wsl.exe"] = Wsl;
            p.Answers.Add(("--status", "Default Version: 2"));
            p.Answers.Add(("-l -v", null));                                  // listing failed / timed out
            p.Answers.Add(("-d EzForensics -u root -e true", ""));         // but the distro answers
            var steps = new List<RuntimeStep>();
            using var http = new HttpClient(new FixedHandler(Array.Empty<byte>()));
            var (ok, message) = await EzDistro.EnsureAsync(http, (s, _) => { steps.Add(s); return Task.FromResult<EzForensics.Core.Execution.Job>(null!); }, _ => { }, probe: p);
            ok.Should().BeFalse();
            message.Should().Contain("registered");
            File.Exists(EzDistro.VhdxPath()).Should().BeTrue("a disk that may belong to a registered distro is never deleted");
            steps.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EZFORENSICS_WSL_ROOT", before);
            Directory.Delete(root, true);
        }
    }

    // ------------------------------------------------------------------ Zeek locator preference

    private static FakeRuntimeProbe WslProbe(string list)
    {
        var p = new FakeRuntimeProbe();
        p.OnPath["wsl.exe"] = Wsl;
        p.Answers.Add(("-l -v", list));
        return p;
    }

    private const string ZeekProbe = "ZEEK=/opt/zeek/bin/zeek\nzeek version 9.0.0\nLOCAL=1\n";

    [Fact]
    public void Locator_prefers_the_private_distro_over_the_users_running_default_distro()
    {
        var p = WslProbe("  NAME   STATE   VERSION\n* Ubuntu   Running   2\n  EzForensics   Stopped   2\n");
        p.Answers.Add(("sh -c", ZeekProbe));
        var d = new ZeekLocator(p).Discover(null, includeDocker: false);
        d.Selected!.WslDistro.Should().Be(EzDistro.Name);
        d.Selected.IsEzDistro.Should().BeTrue();
        d.Selected.OriginLabel.Should().Be("EZ Forensics distro");
        p.Calls.First(c => c.Contains("sh -c")).Should().Contain("-d EzForensics");
    }

    [Fact]
    public void Locator_falls_back_to_the_users_distro_then_docker()
    {
        var p = WslProbe("  NAME   STATE   VERSION\n* Ubuntu   Running   2\n  EzForensics   Stopped   2\n");
        p.Answers.Add(("-d EzForensics", null));
        p.Answers.Add(("-d Ubuntu", ZeekProbe));
        var d = new ZeekLocator(p).Discover(null, includeDocker: false);
        d.Selected!.WslDistro.Should().Be("Ubuntu");
        d.Selected.IsEzDistro.Should().BeFalse();
    }

    [Fact]
    public void Locator_prefers_wsl_over_a_pulled_docker_image()
    {
        var p = WslProbe("  NAME   STATE   VERSION\n  EzForensics   Stopped   2\n");
        p.OnPath["docker.exe"] = @"C:\docker.exe";
        p.Answers.Add(("version --format", "28.4.0\n"));
        p.Answers.Add(("image inspect", "sha256:abc|zeek/zeek@sha256:70733f4e|178044378\n"));
        p.Answers.Add(("zeek/zeek:lts zeek --version", "zeek version 9.0.0\n"));
        p.Answers.Add(("sh -c", ZeekProbe));
        var d = new ZeekLocator(p).Discover(null);
        d.Selected!.Engine.Should().Be(ZeekEngine.Wsl);
        d.Selected.WslDistro.Should().Be(EzDistro.Name);
        d.Docker.EngineRunning.Should().BeTrue("the Docker state is still reported");

        var noWslZeek = WslProbe("  NAME   STATE   VERSION\n  EzForensics   Stopped   2\n");
        noWslZeek.OnPath["docker.exe"] = @"C:\docker.exe";
        noWslZeek.Answers.Add(("version --format", "28.4.0\n"));
        noWslZeek.Answers.Add(("image inspect", "sha256:abc|zeek/zeek@sha256:70733f4e|178044378\n"));
        noWslZeek.Answers.Add(("zeek/zeek:lts zeek --version", "zeek version 9.0.0\n"));
        new ZeekLocator(noWslZeek).Discover(null).Selected!.Engine.Should().Be(ZeekEngine.Docker);
    }

    [Fact]
    public void Wsl_probe_order_is_private_distro_first_and_never_docker_internal()
    {
        var list = WslRuntime.ParseList("  NAME   STATE   VERSION\n* docker-desktop   Running   2\n  Ubuntu   Running   2\n  EzForensics   Stopped   2\n  Debian   Stopped   2\n")
            .Where(d => !d.IsDockerInternal).ToList();
        ZeekLocator.WslProbeOrder(list).Select(d => d.Name).Should().Equal("EzForensics", "Ubuntu");
    }

    // ------------------------------------------------------------------ setup catalog

    [Fact]
    public void Catalog_has_the_linux_environment_item_required_by_zeek()
    {
        var cat = SetupCatalog.Default;
        var item = cat.Find(SetupIds.EzDistro)!;
        item.Title.Should().Be("EZ Forensics Linux environment (WSL)");
        item.DownloadMB.Should().Be(EzDistro.DownloadMB);
        cat.BundleOf(SetupIds.EzDistro)!.Id.Should().Be(SetupIds.BundleNetwork);
        cat.Find(SetupIds.Zeek)!.Requires.Should().Contain(SetupIds.EzDistro);
    }
}
