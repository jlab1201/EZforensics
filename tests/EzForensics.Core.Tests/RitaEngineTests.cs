using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EzForensics.Core.Execution;
using EzForensics.Core.Rita;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class RitaEngineTests
{
    // ------------------------------------------------------------------ pins

    [Fact]
    public void Pins_are_well_formed()
    {
        OciBlobFetcher.IsSha256Digest(RitaRelease.IndexDigest).Should().BeTrue();
        foreach (var p in new[] { RitaRelease.Amd64, RitaRelease.Arm64 })
        {
            OciBlobFetcher.IsSha256Digest(p.ManifestDigest).Should().BeTrue();
            OciBlobFetcher.IsSha256Digest(p.LayerDigest).Should().BeTrue();
            p.BinarySha256.Should().MatchRegex("^[0-9a-f]{64}$");
            p.LayerBytes.Should().BeGreaterThan(1_000_000);
            p.BinaryBytes.Should().BeGreaterThan(p.LayerBytes);
        }
        foreach (var c in new[] { RitaRelease.ClickHouseAmd64, RitaRelease.ClickHouseArm64 })
        {
            c.Url.Should().StartWith("https://github.com/ClickHouse/ClickHouse/releases/download/v24.1.6.52-stable/");
            c.Sha512.Should().MatchRegex("^[0-9a-f]{128}$");
            c.FileName.Should().Contain(c.Architecture);
        }
        RitaRelease.Version.Should().Be("5.1.2");
        RitaRelease.ImageReference.Should().Be("ghcr.io/activecm/rita@" + RitaRelease.IndexDigest);
        RitaRelease.HttpExtensionsSha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ------------------------------------------------------------------ database names, log names, staging

    [Theory]
    [InlineData(@"C:\Cases\My-Case 2026!", "WS-01")]
    [InlineData(@"C:\Cases\x", null)]
    [InlineData(@"C:\Cases\123", "Unknown")]
    [InlineData(@"C:\Cases\averyveryveryverylongcasenamethatgoesonandon_and_on_forever", "averyveryveryverylonghostnamethatgoesonandon")]
    [InlineData(@"C:\Cases\Ünïcødé", "_Case")]
    public void Database_names_satisfy_rita(string caseDir, string? host)
    {
        var db = RitaCommandBuilder.DatabaseName(caseDir, host);
        RitaCommandBuilder.IsValidDatabaseName(db).Should().BeTrue(db);
        db.Length.Should().BeLessOrEqualTo(63);
        db.Should().StartWith("ez_").And.NotContain("-");
        RitaCommandBuilder.DatabaseName(caseDir, host).Should().Be(db, "deterministic");
    }

    [Fact]
    public void Database_names_differ_per_host_and_case()
    {
        var a = RitaCommandBuilder.DatabaseName(@"C:\Cases\A", "WS1");
        a.Should().MatchRegex("^ez_a_ws1_[0-9a-f]{6}$");
        RitaCommandBuilder.DatabaseName(@"C:\Cases\A", null).Should().MatchRegex("^ez_a_case_[0-9a-f]{6}$");
        RitaCommandBuilder.DatabaseName(@"C:\Cases\A", "WS2").Should().NotBe(a);
        RitaCommandBuilder.DatabaseName(@"D:\Other\A", "WS1").Should().NotBe(a);
        RitaCommandBuilder.IsValidDatabaseName("ez-bad").Should().BeFalse();
        RitaCommandBuilder.IsValidDatabaseName("metadatabase").Should().BeFalse();
        RitaCommandBuilder.IsValidDatabaseName("Ez_x").Should().BeFalse();
    }

    [Theory]
    [InlineData("conn.log", "conn.log")]
    [InlineData(@"D:\c\Unknown\Network\Zeek_Conn_cap.log", "conn.log")]
    [InlineData("Zeek_OpenConn_cap_2.log", "open_conn.log")]
    [InlineData("Zeek_Dns_PCAP1.log", "dns.log")]
    [InlineData("Zeek_Http_x.log", "http.log")]
    [InlineData("Zeek_Ssl_x.log", "ssl.log")]
    [InlineData("open_ssl.log", "open_ssl.log")]
    [InlineData("conn.10:00:00-11:00:00.log", "conn.log")]
    [InlineData("Zeek_Conn_cap.csv", null)]
    [InlineData("Zeek_ConnSummary_cap.log", null)]
    [InlineData("files.log", null)]
    [InlineData("Zeek_X509_cap.log", null)]
    [InlineData("conn_summary.log", null)]
    public void Original_log_names(string file, string? expected) => RitaCommandBuilder.OriginalLogName(file).Should().Be(expected);

    [Fact]
    public void Staging_gives_one_plain_folder_per_capture()
    {
        var staged = RitaCommandBuilder.Stage(new[]
        {
            new RitaCaptureLogs("a.pcap", new[] { @"D:\c\Zeek_Conn_a.log", @"D:\c\Zeek_Dns_a.log", @"D:\c\Zeek_Files_a.log", @"D:\c\Zeek_Conn_a_2.log" }),
            new RitaCaptureLogs("b 2025.10:00.pcap", new[] { @"D:\c\Zeek_X509_b.log" }),   // nothing RITA reads: skipped, no folder used
            new RitaCaptureLogs("c.pcap", new[] { @"D:\c\b\conn.log" }),
        });
        staged.Select(s => (s.Folder, s.Name, s.Capture)).Should().Equal(
            ("c01", "conn.log", "a.pcap"), ("c01", "dns.log", "a.pcap"), ("c02", "conn.log", "c.pcap"));
        staged[0].SourcePath.Should().Be(@"D:\c\Zeek_Conn_a.log");
    }

    [Fact]
    public void Inputs_text_round_trips_with_lf_only()
    {
        var staged = RitaCommandBuilder.Stage(new[] { new RitaCaptureLogs("cap\tx.pcap", new[] { @"D:\Cases\A B\Zeek_Conn_cap.log" }) });
        var text = RitaCommandBuilder.InputsText(staged, p => WslRuntime.ToWslPath(p), TimeSpan.FromMinutes(25));
        text.Should().NotContain("\r");
        text.Should().StartWith("#timeout 1500\n");
        text.Should().Contain("c01\tconn.log\tcap_x.pcap\t/mnt/d/Cases/A B/Zeek_Conn_cap.log\n");
        var (timeout, logs) = RitaCommandBuilder.ParseInputs(text);
        timeout.Should().Be(TimeSpan.FromMinutes(25));
        logs.Should().ContainSingle().Which.SourcePath.Should().Be("/mnt/d/Cases/A B/Zeek_Conn_cap.log");
        FluentActions.Invoking(() => RitaCommandBuilder.InputsText(staged.Select(s => s with { SourcePath = @"\\server\share\conn.log" }), p => WslRuntime.ToWslPath(p), TimeSpan.Zero))
            .Should().Throw<ArgumentException>().WithMessage("*not visible inside WSL*");
    }

    // ------------------------------------------------------------------ pipeline script / invocation

    [Fact]
    public void Pipeline_script_runs_the_planned_steps()
    {
        var s = RitaCommandBuilder.PipelineScript("/mnt/d/case/.ezstaging/Rita_x", "ez_case_ws1_abcdef");
        s.Should().NotContain("\r");
        s.Should().StartWith("# " + RitaCommandBuilder.ScriptMarker + " ez_case_ws1_abcdef\n");
        s.Should().Contain("exec 9>" + RitaSetup.LockFile).And.Contain("flock 9");
        s.Should().Contain("trap cleanup EXIT");
        s.Should().Contain("CHCONF=" + RitaSetup.ClickHouseConfig + "\n").And.Contain("server --config-file=\"$CHCONF\"");
        s.Should().Contain("--port 19000 --query 'SELECT 1'");
        s.Should().Contain("import --config \"$RUN/config.hjson\" --database \"$DB\" --logs \"$RUN/logs\" --rebuild");
        s.Should().NotContain("--rolling");
        s.Should().Contain("view --config \"$RUN/config.hjson\" --stdout --limit 1000000 \"$DB\" > \"$OUT/rita_view.csv.tmp\"");
        s.Should().Contain("FORMAT CSVWithNames").And.Contain("FROM threat_mixtape").And.Contain("MD5(uid)");
        s.Should().Contain("\"update_check_enabled\": false");
        // every heredoc terminator stands alone on its line
        var lines = s.Split('\n');
        lines.Count(l => l.Contains("<<'EZFORENSICS_EOF_7f3c'")).Should().Be(lines.Count(l => l == "EZFORENSICS_EOF_7f3c"));
        FluentActions.Invoking(() => RitaCommandBuilder.PipelineScript("/x", "bad-name")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Pipeline_switches_clickhouse_to_blob_storage_on_wsl1()
    {
        var s = RitaCommandBuilder.PipelineScript("/mnt/d/x", "ez_case_ws1_abcdef");
        var lines = s.Split('\n').ToList();
        int detect = lines.FindIndex(l => l == "if grep -q Microsoft /proc/sys/kernel/osrelease 2>/dev/null; then");
        int server = lines.FindIndex(l => l.Contains("clickhouse\" server --config-file=\"$CHCONF\"", StringComparison.Ordinal));
        detect.Should().BeGreaterThan(0, "WSL1 kernels report 4.4.0-xxxxx-Microsoft (WSL2: ...-microsoft-standard-WSL2, lower case)");
        server.Should().BeGreaterThan(detect);
        s.Should().Contain("CHCONF=\"$RUN/clickhouse-wsl1.xml\"").And.Contain("mkdir -p " + RitaSetup.DataDirWsl1);
        s.Should().Contain("<type>local_blob_storage</type>").And.Contain("<path>" + RitaSetup.DataDirWsl1 + "/</path>");
    }

    [Fact]
    public void Wsl1_clickhouse_config_keeps_its_data_apart_on_a_blob_disk()
    {
        var x = RitaSetup.ClickHouseConfigXmlFor(wsl1: true);
        x.Should().Contain("<path>/var/lib/ezhub/clickhouse-wsl1/</path>").And.Contain("<tmp_path>/var/lib/ezhub/clickhouse-wsl1/tmp/</tmp_path>");
        x.Should().NotContain(RitaSetup.DataDir + "/");
        x.Should().Contain("<ezblob>\n").And.Contain("<path>/var/lib/ezhub/clickhouse-wsl1/blob/</path>").And.Contain("<main><disk>ezblob</disk></main>");
        x.Should().Contain("<tcp_port>19000</tcp_port>").And.NotContain("\r");
        var doc = System.Xml.Linq.XDocument.Parse(x);
        doc.Root!.Element("storage_configuration")!.Element("policies")!.Element("default").Should().NotBeNull();
        System.Xml.Linq.XDocument.Parse(RitaSetup.ClickHouseConfigXml).Root!.Element("storage_configuration").Should().BeNull("WSL2 keeps the default disk");
        RitaSetup.UninstallScript.Should().Contain(RitaSetup.DataDirWsl1);
        RitaSetup.DeleteDatabasesScript.Should().Contain("rm -rf " + RitaSetup.DataDir + " " + RitaSetup.DataDirWsl1);
    }

    [Fact]
    public void Import_failure_puts_ritas_own_error_on_stderr_and_in_the_last_line()
    {
        var s = RitaCommandBuilder.PipelineScript("/mnt/d/x", "ez_case_ws1_abcdef");
        s.Should().Contain("if [ \"$rc\" = 0 ]; then implog | tail -n 200; else implog | tail -n 40 >&2; fi");
        s.Should().Contain("grep -E ' (FTL|ERR|PNC) |^(Error|error|panic)'");
        s.Should().Contain("echo \"EZFORENSICS: rita import failed (exit $rc)${ERR:+: $ERR}\" >&2");
        var lines = s.Split('\n').ToList();
        lines.FindIndex(l => l.Contains("rita import failed", StringComparison.Ordinal)).Should()
            .BeGreaterThan(lines.FindIndex(l => l.Contains("ERR=$(implog", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cancel_stops_rita_and_clickhouse_only_for_the_lock_holder()
    {
        var s = RitaCommandBuilder.PipelineScript("/mnt/d/x", "ez_case_ws1_abcdef");
        var lines = s.Split('\n').ToList();
        int flock = lines.FindIndex(l => l.Contains("flock 9"));
        int owner = lines.FindIndex(l => l == "echo \"$DB\" > " + RitaSetup.OwnerFile);
        owner.Should().BeGreaterThan(flock, "the owner is written once the lock is held");
        s.Should().Contain("rm -f " + RitaSetup.OwnerFile);

        var cancel = RitaCommandBuilder.WslCancelArgs("ez_case_ws1_abcdef")[^1];
        var guard = cancel.IndexOf($"if [ \"$(cat {RitaSetup.OwnerFile} 2>/dev/null)\" = 'ez_case_ws1_abcdef' ]; then", StringComparison.Ordinal);
        guard.Should().BeGreaterThan(cancel.IndexOf("[E]ZFORENSICS-RITA-PIPELINE", StringComparison.Ordinal));
        cancel.IndexOf("[/]opt/ezhub/rita/bin/rita", StringComparison.Ordinal).Should().BeGreaterThan(guard);
        cancel.IndexOf("[/]opt/ezhub/rita/bin/clickhouse server", StringComparison.Ordinal).Should().BeGreaterThan(guard);
        FluentActions.Invoking(() => RitaCommandBuilder.WslCancelArgs("x'; rm -rf /")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Inputs_fields_are_never_empty()
    {
        var staged = RitaCommandBuilder.Stage(new[] { new RitaCaptureLogs("", new[] { @"D:\c\conn.log" }) });
        RitaCommandBuilder.InputsText(staged, p => WslRuntime.ToWslPath(p), TimeSpan.FromMinutes(1))
            .Should().Contain("c01\tconn.log\t_\t/mnt/d/c/conn.log\n", "an empty field would collapse under IFS=tab and shift the path");
    }

    [Fact]
    public void Install_downloads_are_https_only()
    {
        var s = RitaSetup.InstallScript("/mnt/c/x/rita");
        s.Split('\n').Where(l => l.TrimStart().StartsWith("curl ", StringComparison.Ordinal)).Should().HaveCount(2).And.OnlyContain(l => l.Contains("--proto '=https' --proto-redir '=https'"));
    }

    [Fact]
    public void Wsl_invocation_runs_as_root_in_the_private_distro_and_cancels_with_pkill()
    {
        var install = new RitaInstall(RitaEngine.Wsl, @"C:\Windows\System32\wsl.exe", "5.1.2", "24.1.6.52", EzDistro.Name);
        var inv = RitaCommandBuilder.Build(install, @"D:\case\.ezstaging\Rita_x", "ez_case_ws1_abcdef");
        inv.Exe.Should().Be(install.Exe);
        inv.Args.Take(6).Should().Equal("-d", "EzForensics", "-u", "root", "-e", "sh");
        inv.Args[^1].Should().Contain("OUT='/mnt/d/case/.ezstaging/Rita_x'");
        inv.CancelCommand.Should().NotBeNull();
        inv.CancelCommand![0].Should().Be(install.Exe);
        inv.CancelCommand[^1].Should().Contain("pkill -f '[E]ZFORENSICS-RITA-PIPELINE ez_case_ws1_abcdef'").And.Contain("[/]opt/ezhub/rita/bin/clickhouse server");
        inv.Environment!["WSL_UTF8"].Should().Be("1");
        FluentActions.Invoking(() => RitaCommandBuilder.Build(install, @"\\srv\share\case", "ez_a_b_abcdef")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RitaCommandBuilder.Build(install with { Engine = RitaEngine.Docker }, @"D:\x", "ez_a_b_abcdef"))
            .Should().Throw<NotSupportedException>();
        var direct = RitaCommandBuilder.Build(install with { Engine = RitaEngine.Direct, Exe = @"C:\fake.exe" }, @"D:\x", "ez_a_b_abcdef",
            new RitaOptions { InternalSubnets = new[] { "10.1.0.0/16" } });
        direct.Args.Should().Equal("--fake-rita", "--out", @"D:\x", "--db", "ez_a_b_abcdef", "--subnets", "10.1.0.0/16");
    }

    // ------------------------------------------------------------------ setup files

    [Fact]
    public void Clickhouse_config_is_local_small_and_quiet()
    {
        var x = RitaSetup.ClickHouseConfigXml;
        x.Should().Contain("<listen_host>127.0.0.1</listen_host>").And.Contain("<tcp_port>19000</tcp_port>").And.Contain("<http_port>18123</http_port>");
        x.Should().Contain("<max_server_memory_usage_to_ram_ratio>0.5</max_server_memory_usage_to_ram_ratio>");
        x.Should().Contain("<path>/var/lib/ezhub/clickhouse/</path>");
        x.Should().NotContain("query_log").And.NotContain("part_log").And.NotContain("\r");
        RitaSetup.ClickHouseUsersXml.Should().Contain("<ip>127.0.0.1</ip>").And.Contain("<log_queries>0</log_queries>");
        RitaSetup.RitaEnv.Should().Contain("DB_ADDRESS=127.0.0.1:19000").And.Contain("LOGGING_ENABLED=false").And.Contain("CONFIG_DIR=/opt/ezhub/rita/etc");
    }

    [Fact]
    public void Rita_config_takes_subnets_and_keeps_feeds_offline_by_default()
    {
        var off = RitaSetup.RitaConfigHjson(new RitaOptions { InternalSubnets = new[] { "10.10.0.0/16", "bogus", "192.168.1.5" } });
        off.Should().Contain("\"internal_subnets\": [\"10.10.0.0/16\", \"192.168.1.5/32\"]");
        off.Should().Contain("\"online_feeds\": []").And.Contain("\"update_check_enabled\": false").And.NotContain("\r");
        RitaSetup.RitaConfigHjson(new RitaOptions { OnlineThreatIntel = true }).Should().Contain("feodotracker.abuse.ch");
        RitaSetup.RitaConfigHjson(new RitaOptions { InternalSubnets = new[] { "nonsense" } }).Should().Contain("\"10.0.0.0/8\"");
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData(" 192.168.1.7 ", "192.168.1.7/32")]
    [InlineData("fd00::/8", "fd00::/8")]
    [InlineData("10.0.0.0/33", null)]
    [InlineData("10.0.0/8", null)]
    [InlineData("", null)]
    public void Cidrs_are_normalised(string input, string? expected) => RitaOptions.NormalizeCidr(input).Should().Be(expected);

    [Fact]
    public void Clickhouse_password_is_generated_on_the_distro_and_never_in_the_script_or_command_lines()
    {
        RitaSetup.ClickHouseUsersXml.Should().Contain("<password_sha256_hex>" + RitaSetup.PasswordShaPlaceholder + "</password_sha256_hex>").And.NotContain("<password></password>");
        RitaSetup.RitaEnv.Should().Contain("CLICKHOUSE_PASSWORD=" + RitaSetup.PasswordPlaceholder);
        var s = RitaSetup.InstallScript("/mnt/c/r/rita");
        s.Should().Contain("[ ! -s " + RitaSetup.PasswordFile + " ]").And.Contain("/dev/urandom").And.Contain("chmod 0600 " + RitaSetup.PasswordFile)
            .And.Contain("chmod 0600 " + RitaSetup.EtcDir + "/.env").And.Contain("chmod 0600 " + RitaSetup.ClickHouseUsers)
            .And.Contain("chmod 0600 " + RitaSetup.ClientConfig).And.Contain("sha256sum | cut");
        s.Should().NotContain("set -x").And.NotContain("echo \"$CHPW").And.NotContain("echo $CHPW").And.NotContain("--password");
        var install = RitaSetup.InstallStep(@"C:\w\wsl.exe", "/mnt/c/r");
        string.Join(" ", install.Args).Should().NotContain("--password");
        var run = RitaCommandBuilder.PipelineScript("/mnt/c/out", "ez_case_host_abc123");
        run.Should().Contain("clickhouse\" client --config-file " + RitaSetup.ClientConfig).And.NotContain("--password").And.NotContain("CLICKHOUSE_PASSWORD=");
        run.Should().NotContain("$(cat " + RitaSetup.PasswordFile);
    }

    [Fact]
    public void Uninstall_takes_the_analysis_lock_and_fails_clearly_when_one_runs()
    {
        var u = RitaSetup.UninstallScript;
        u.Should().Contain("exec 9>" + RitaSetup.LockFile).And.Contain("flock -w 30 9").And.Contain("a RITA analysis is running").And.Contain("exit 75");
        u.IndexOf("flock", StringComparison.Ordinal).Should().BeLessThan(u.IndexOf("rm -rf", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_script_verifies_every_download()
    {
        var s = RitaSetup.InstallScript("/mnt/c/Users/x/AppData/Local/EzForensicsData/cache/rita/5.1.2/rita");
        s.Should().NotContain("\r");
        s.Should().Contain(RitaRelease.ClickHouse.Sha512 + "  $T\" | sha512sum -c -");
        s.Should().Contain(RitaRelease.Pin.BinarySha256);
        s.Should().Contain(RitaRelease.HttpExtensionsSha256);
        s.Should().Contain("'*/usr/bin/clickhouse'");
        s.Should().Contain("cat > " + RitaSetup.ClickHouseConfig);
        s.Should().Contain("echo \"" + RitaSetup.MarkerText + "\" > " + RitaSetup.MarkerPath);
        RitaSetup.UninstallScript.Should().Contain("rm -rf /opt/ezhub/rita /var/lib/ezhub/clickhouse");
        RitaSetup.DeleteDatabasesScript.Should().Contain("flock -w 30 9").And.Contain("rm -rf /var/lib/ezhub/clickhouse");
        RitaSetup.InstallStep(@"C:\w\wsl.exe", "/mnt/c/r").Args.Take(4).Should().Equal("-d", "EzForensics", "-u", "root");
    }

    // ------------------------------------------------------------------ locator

    private static readonly EzDistroInfo ReadyDistro = new(true, true, 2, "Stopped", true, "ubuntu-base 24.04.5 / schema 1", @"C:\x", 1, @"C:\Windows\System32\wsl.exe");

    [Fact]
    public void Locator_reads_the_probe()
    {
        RitaLocator.FromProbe(ReadyDistro, "DATABYTES=4096\n").Setup.Should().Be(RitaSetupOption.Install);
        var d = RitaLocator.FromProbe(ReadyDistro, $"EZRITA={RitaSetup.MarkerText}\nRITAVER=RITA version v5.1.2\nCHVER=ClickHouse local version 24.1.6.52 (official build).\nDATABYTES=123456\n");
        d.Installed.Should().BeTrue();
        d.Setup.Should().Be(RitaSetupOption.None);
        d.RitaVersion.Should().Be("5.1.2");
        d.ClickHouseVersion.Should().Be("24.1.6.52");
        d.DataBytes.Should().Be(123456);
        d.Selected!.Engine.Should().Be(RitaEngine.Wsl);
        d.Selected.Distro.Should().Be(EzDistro.Name);
        d.Summary.Should().Contain("RITA 5.1.2");
        var old = RitaLocator.FromProbe(ReadyDistro, "EZRITA=rita 5.1.1 / clickhouse 24.1.6.52 / schema 1\n");
        old.Setup.Should().Be(RitaSetupOption.Update);
        old.RitaVersion.Should().Be("5.1.1");
        RitaLocator.MarkerPart("rita 5.1.2 / clickhouse 24.1.6.52 / schema 1", "clickhouse").Should().Be("24.1.6.52");
    }

    [Fact]
    public void Locator_without_wsl_needs_wsl()
    {
        var probe = new FakeRuntimeProbe();
        RitaLocator.Discover(probe).Setup.Should().Be(RitaSetupOption.NeedsWsl);
    }

    // ------------------------------------------------------------------ OCI

    [Fact]
    public void Oci_layer_extraction_takes_only_the_wanted_file()
    {
        using var t = new TempDir();
        var tgz = Path.Combine(t.Path, "layer.tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            void Add(string name, string content)
            {
                var e = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
                tar.WriteEntry(e);
            }
            Add("etc/passwd", "root:x");
            Add("./rita", "ELF-binary");
        }
        var dest = Path.Combine(t.Path, "out", "rita");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        OciBlobFetcher.ExtractEntry(tgz, "rita", dest).Should().BeTrue();
        File.ReadAllText(dest).Should().Be("ELF-binary");
        Directory.GetFiles(Path.GetDirectoryName(dest)!).Should().ContainSingle();
        OciBlobFetcher.ExtractEntry(tgz, "missing", dest + "2").Should().BeFalse();
        OciBlobFetcher.VerifyBinary(dest, RitaRelease.Amd64).Should().BeFalse();
        OciBlobFetcher.DigestOf(Encoding.UTF8.GetBytes("abc")).Should().Be("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        OciBlobFetcher.ParseLayers("{\"layers\":[{\"digest\":\"sha256:aa\",\"size\":5}]}").Should().Equal(("sha256:aa", 5L));
    }

    // ------------------------------------------------------------------ result conversion

    private const string View = "Viewing database: ez_x\n"
        + "Severity,Source IP,Destination IP,FQDN,Beacon Score,Strobe,Total Duration,Long Connection Score,Subdomains,C2 Over DNS Score,Threat Intel,Prevalence,First Seen,Missing Host Header,Connection Count,Total Bytes,Port:Proto:Service,Modifiers\n"
        + "High,10.0.0.5,203.0.113.9,,0.93,false,12.5,0,0,0,false,0.1,2 hours ago,false,800,123456,\"443:tcp:ssl\",\"rare_signature:x,y\"\n"
        + "Low,10.0.0.5,::,evil.example.com,0.6,false,3.2,0,0,0,false,1,5 minutes ago,false,23,1895,\"443:tcp:ssl\",\"\"\n"
        + "Medium,10.0.0.6,198.51.100.1,,0,false,50000,0.8,0,0,false,1,1 hour ago,false,1,99,\"22:tcp:ssh\",\"\"\n"
        + "Critical,::,::,tunnel.example.org,0,false,0,0,900,0.9,false,0,1 day ago,false,0,0,\"\",\"\"\n"
        + "None,10.0.0.7,192.0.2.1,,0,true,0,0,0,0,false,1,1 hour ago,false,4000,1,\"80:tcp:http\",\"\"\n";

    private static void WriteWork(string dir, bool spans = true)
    {
        File.WriteAllText(Path.Combine(dir, RitaCommandBuilder.ViewFile), View);
        File.WriteAllText(Path.Combine(dir, RitaCommandBuilder.InfoFile), "rita.version=RITA version v5.1.2\nclickhouse.version=ClickHouse local version 24.1.6.52 (official build).\nrita.db=ez_x\nnodata=0\n");
        if (!spans) return;
        File.WriteAllText(Path.Combine(dir, RitaCommandBuilder.PairsFile), "\"src\",\"dst\",\"first_ts\",\"last_ts\",\"captures\"\n\"10.0.0.5\",\"203.0.113.9\",1700000000,1700003600,\"a.pcap|b.pcap\"\n\"10.0.0.6\",\"198.51.100.1\",1700000100,1700050100,\"a.pcap\"\n");
        File.WriteAllText(Path.Combine(dir, RitaCommandBuilder.SniFile), "\"src\",\"fqdn\",\"first_ts\",\"last_ts\",\"captures\"\n\"10.0.0.5\",\"evil.example.com\",1700000200,1700000500,\"b.pcap\"\n");
        File.WriteAllText(Path.Combine(dir, RitaCommandBuilder.DnsFile), "\"fqdn\",\"first_ts\",\"last_ts\",\"captures\"\n\"tunnel.example.org\",1700001000,1700002000,\"a.pcap\"\n");
        File.WriteAllText(Path.Combine(dir, RitaCommandBuilder.SpanFile), "\"capture\",\"first_ts\",\"last_ts\"\n\"a.pcap\",1700000000,1700036000\n\"b.pcap\",1700018000,1700054000\n\"\",1600000000,1700054000\n");
    }

    private static List<string[]> Csv(string path) => RitaResultConverter.ReadRecords(File.ReadAllText(path)).Where(r => r.Length > 1).ToList();

    [Fact]
    public void Converter_writes_four_files_with_first_last_seen_and_captures()
    {
        using var t = new TempDir();
        WriteWork(t.Path);
        var r = RitaResultConverter.Convert(t.Path, captures: new[] { "a.pcap", "b.pcap" });
        r.Files.Select(Path.GetFileName).Should().Equal("Rita_Threats.csv", "Rita_Beacons.csv", "Rita_LongConnections.csv", "Rita_DnsTunneling.csv");
        (r.Threats, r.Beacons, r.LongConnections, r.DnsTunneling).Should().Be((5, 3, 1, 1));
        r.MissingSpans.Should().Be(1);
        r.RitaVersion.Should().Be("5.1.2");
        r.ClickHouseVersion.Should().Be("24.1.6.52");
        r.Database.Should().Be("ez_x");
        r.DataSpanHours.Should().Be(15, "a 10 h + b 10 h overlapping by 5 h; the unlinked row is ignored");
        RitaResultConverter.FormatHours(12.5).Should().Be("12.5");

        var threats = Csv(Path.Combine(t.Path, "Rita_Threats.csv"));
        var h = threats[0].ToList();
        h.Take(7).Should().Equal("DateTimeUtc", "FirstSeenUtc", "LastSeenUtc", "Severity", "Src", "Dst", "Fqdn");
        h.Should().Contain(new[] { "Beacon Score", "Total Duration", "C2 Over DNS Score", "First Seen (relative)", "Modifiers" });
        h.Should().NotContain(new[] { "Source IP", "Destination IP", "FQDN", "First Seen" });
        h[^1].Should().Be("Captures");
        string Cell(string[] row, string col) => row[h.IndexOf(col)];
        var ip = threats[1];
        Cell(ip, "DateTimeUtc").Should().Be("2023-11-14 22:13:20.0000000");
        Cell(ip, "FirstSeenUtc").Should().Be("2023-11-14 22:13:20.0000000");
        Cell(ip, "LastSeenUtc").Should().Be("2023-11-14 23:13:20.0000000");
        (Cell(ip, "Severity"), Cell(ip, "Src"), Cell(ip, "Dst"), Cell(ip, "Fqdn")).Should().Be(("High", "10.0.0.5", "203.0.113.9", ""));
        Cell(ip, "Modifiers").Should().Be("rare_signature:x,y");
        Cell(ip, "Captures").Should().Be("a.pcap; b.pcap");
        var sni = threats[2];
        (Cell(sni, "Dst"), Cell(sni, "Fqdn"), Cell(sni, "Captures")).Should().Be(("", "evil.example.com", "b.pcap"));
        Cell(sni, "FirstSeenUtc").Should().Be("2023-11-14 22:16:40.0000000");
        var dns = threats[4];
        (Cell(dns, "Src"), Cell(dns, "Fqdn"), Cell(dns, "Captures")).Should().Be(("", "tunnel.example.org", "a.pcap"));
        Cell(threats[5], "FirstSeenUtc").Should().BeEmpty("no span for this pair: left empty");

        Csv(Path.Combine(t.Path, "Rita_Beacons.csv")).Skip(1).Select(x => x[4]).Should().Equal("10.0.0.5", "10.0.0.5", "10.0.0.7");
        Csv(Path.Combine(t.Path, "Rita_LongConnections.csv")).Skip(1).Single()[4].Should().Be("10.0.0.6");
        Csv(Path.Combine(t.Path, "Rita_DnsTunneling.csv")).Skip(1).Single()[6].Should().Be("tunnel.example.org");
    }

    [Fact]
    public void Converter_tolerates_missing_enrichment_and_empty_results()
    {
        using var t = new TempDir();
        WriteWork(t.Path, spans: false);
        var r = RitaResultConverter.Convert(t.Path, captures: new[] { "only.pcap" });
        r.MissingSpans.Should().Be(5);
        r.DataSpanHours.Should().BeNull();
        var rows = Csv(Path.Combine(t.Path, "Rita_Threats.csv"));
        rows.Skip(1).Should().OnlyContain(x => x[1] == "" && x[x.Length - 1] == "only.pcap", "a single capture fills Captures");

        using var e = new TempDir();
        File.WriteAllText(Path.Combine(e.Path, RitaCommandBuilder.ViewFile), "");
        File.WriteAllText(Path.Combine(e.Path, RitaCommandBuilder.InfoFile), "nodata=1\n");
        var empty = RitaResultConverter.Convert(e.Path);
        empty.NoData.Should().BeTrue();
        empty.Threats.Should().Be(0);
        foreach (var f in RitaResultConverter.OutputFiles)
        {
            var lines = File.ReadAllLines(Path.Combine(e.Path, f));
            lines.Should().ContainSingle("header only");
            lines[0].Should().StartWith("DateTimeUtc,FirstSeenUtc,LastSeenUtc,Severity,Src,Dst,Fqdn,").And.EndWith(",Captures");
        }
    }

    [Fact]
    public void Covered_hours_merge_overlaps()
    {
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RitaResultConverter.CoveredHours(new[] { (t0, t0.AddHours(2)), (t0.AddHours(1), t0.AddHours(3)), (t0.AddDays(365), t0.AddDays(365).AddHours(1)) })
            .Should().BeApproximately(4, 1e-9);
        RitaResultConverter.CoveredHours(Array.Empty<(DateTime, DateTime)>()).Should().Be(0);
        RitaResultConverter.Ip("::ffff:10.1.2.3").Should().Be("10.1.2.3");
        RitaResultConverter.Ip("::").Should().BeEmpty();
        RitaResultConverter.Ip("2001:db8::1").Should().Be("2001:db8::1");
    }

    // ------------------------------------------------------------------ output naming, settings

    [Theory]
    [InlineData("Rita_Threats.csv", "Rita_Threats.csv", "rita-threats")]
    [InlineData("Rita_Beacons.csv", "Rita_Beacons.csv", "rita-beacons")]
    [InlineData("Rita_LongConnections.csv", "Rita_LongConnections.csv", "rita-long-connections")]
    [InlineData("Rita_DnsTunneling.csv", "Rita_DnsTunneling.csv", "rita-dns-tunneling")]
    public void Rita_outputs_keep_their_names_and_get_artifact_ids(string written, string final, string artifactId)
    {
        var plan = new OutputPlan(@"C:\case\.ezstaging\x", RitaJobs.OutputName, RitaJobs.DefaultArtifactId);
        ArtifactNaming.FinalFileName(RitaJobs.ToolId, plan, written).Should().Be(final);
        ArtifactNaming.Map(RitaJobs.ToolId, plan, written).ArtifactId.Should().Be(artifactId);
        ArtifactNaming.IdentifyNewName(final).Should().Be((RitaJobs.ToolId, artifactId));
        ArtifactNaming.IdentifyNewName(Path.GetFileNameWithoutExtension(final) + "_PARTIAL.csv").Should().Be((RitaJobs.ToolId, artifactId));
    }

    [Fact]
    public void Settings_default_to_rfc1918_offline_and_round_trip()
    {
        var s = new AppSettings();
        s.RitaEnabled.Should().BeTrue();
        s.RitaOnlineThreatIntel.Should().BeFalse();
        s.RitaInternalSubnets.Should().Equal("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fd00::/8");
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "settings.json"));
        s.RitaInternalSubnets = new List<string> { "10.9.0.0/16" };
        s.RitaOnlineThreatIntel = true;
        store.Save(s);
        var back = store.Load();
        back.RitaInternalSubnets.Should().Equal("10.9.0.0/16");
        back.RitaOnlineThreatIntel.Should().BeTrue();
        RitaOptions.FromSettings(back).InternalSubnets.Should().Equal("10.9.0.0/16");
        RitaOptions.FromSettings(new AppSettings { RitaInternalSubnets = new List<string>() }).InternalSubnets.Should().Equal(RitaOptions.DefaultInternalSubnets);
        JsonSerializer.Serialize(new AppSettings()).Should().Contain("RitaInternalSubnets");
    }
}
