using System.Text;
using EzForensics.Core.Platforms;
using EzForensics.Core.Platforms.Linux;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Temp folder with helpers to lay out evidence fixtures ('/' separated relative paths).</summary>
internal sealed class EvidenceFixture : IDisposable
{
    public string Root { get; }

    public EvidenceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ezforensics-platforms-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Path_(string rel) => Path.Combine(new[] { Root }.Concat(rel.Split('/')).ToArray());

    public EvidenceFixture File(string rel, string content = "")
    {
        var p = Path_(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content, new UTF8Encoding(false));
        return this;
    }

    public EvidenceFixture Dir(string rel)
    {
        Directory.CreateDirectory(Path_(rel));
        return this;
    }

    /// <summary>Creates a file symlink; false when the OS refuses (Windows without Developer Mode / admin).</summary>
    public bool FileLink(string rel, string target)
    {
        var p = Path_(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        try { System.IO.File.CreateSymbolicLink(p, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class OsFamilyDetectorTests
{
    [Fact]
    public void Windows_root_is_detected_from_config_folder()
    {
        using var fx = new EvidenceFixture().File("Windows/System32/config/SYSTEM", "x").Dir("Users");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Windows);
        os.Distribution.Should().Be("windows");
        os.Evidence.Should().Contain("Windows/System32/config").And.Contain("Windows/System32/config/SYSTEM");
    }

    [Fact]
    public void Ubuntu_os_release_gives_id_name_and_version()
    {
        using var fx = new EvidenceFixture()
            .File("etc/os-release", "# comment\nNAME=\"Ubuntu\"\nVERSION_ID=\"22.04\"\nID=ubuntu\nID_LIKE=debian\nPRETTY_NAME=\"Ubuntu 22.04.4 LTS\"\n")
            .File("etc/hostname", "web01\n")
            .Dir("var/log");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().Be("ubuntu");
        os.Version.Should().Be("22.04");
        os.DisplayName.Should().Be("Ubuntu 22.04.4 LTS");
        os.HostName.Should().Be("web01");
        os.Evidence.Should().Contain(e => e.StartsWith("etc/os-release: ID=ubuntu NAME=Ubuntu VERSION_ID=22.04"));
        os.Evidence.Should().Contain("var/log");
    }

    [Fact]
    public void Usr_lib_os_release_is_used_when_etc_copy_is_missing()
    {
        using var fx = new EvidenceFixture().File("usr/lib/os-release", "ID='fedora'\nVERSION_ID=40\nNAME=Fedora Linux\n").Dir("etc");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().Be("fedora");
        os.Version.Should().Be("40");
        os.DisplayName.Should().Be("Fedora Linux");
    }

    [Fact]
    public void Lsb_release_is_the_first_fallback()
    {
        using var fx = new EvidenceFixture().File("etc/lsb-release", "DISTRIB_ID=Ubuntu\nDISTRIB_RELEASE=14.04\nDISTRIB_DESCRIPTION=\"Ubuntu 14.04.6 LTS\"\n");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().Be("ubuntu");
        os.Version.Should().Be("14.04");
        os.DisplayName.Should().Be("Ubuntu 14.04.6 LTS");
    }

    [Fact]
    public void Redhat_release_is_parsed()
    {
        using var fx = new EvidenceFixture().File("etc/redhat-release", "CentOS Linux release 7.9.2009 (Core)\n");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().Be("centos");
        os.Version.Should().Be("7.9.2009");
    }

    [Theory]
    [InlineData("Red Hat Enterprise Linux Server release 6.10 (Santiago)", "rhel", "6.10")]
    [InlineData("Rocky Linux release 9.3 (Blue Onyx)", "rocky", "9.3")]
    [InlineData("Fedora release 38 (Thirty Eight)", "fedora", "38")]
    [InlineData("Mystery OS", "mystery", null)]
    public void Redhat_release_lines(string line, string distro, string? version)
    {
        var (d, v) = OsFamilyDetector.ParseRedHatRelease(line);
        d.Should().Be(distro);
        v.Should().Be(version);
    }

    [Fact]
    public void Debian_version_is_the_last_fallback()
    {
        using var fx = new EvidenceFixture().File("etc/debian_version", "11.9\n");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().Be("debian");
        os.Version.Should().Be("11.9");
    }

    [Fact]
    public void Passwd_and_var_log_without_release_file_is_linux_without_distribution()
    {
        using var fx = new EvidenceFixture().File("etc/passwd", "root:x:0:0:root:/root:/bin/bash\n").Dir("var/log");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().BeNull();
        os.Evidence.Should().Contain("etc/passwd").And.Contain("var/log");
    }

    [Fact]
    public void Passwd_alone_is_not_enough()
    {
        using var fx = new EvidenceFixture().File("etc/passwd", "root:x:0:0:root:/root:/bin/bash\n");
        OsFamilyDetector.Detect(fx.Root).Family.Should().Be(OsFamily.Unknown);
    }

    [Fact]
    public void Upper_case_copy_is_found_case_insensitively()
    {
        using var fx = new EvidenceFixture().File("ETC/OS-RELEASE", "ID=debian\nVERSION_ID=\"12\"\n");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        os.Distribution.Should().Be("debian");
        os.Version.Should().Be("12");
    }

    [Fact]
    public void MacOS_plist_gives_product_version()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>ProductBuildVersion</key><string>23E224</string>
              <key>ProductName</key>
              <string>macOS</string>
              <key>ProductVersion</key>
              <string>14.4.1</string>
            </dict></plist>
            """;
        using var fx = new EvidenceFixture().File("System/Library/CoreServices/SystemVersion.plist", plist)
            .File("private/etc/passwd", "root:*:0:0:System Administrator:/var/root:/bin/sh\n").Dir("private/var/log");
        var os = OsFamilyDetector.Detect(fx.Root);
        os.Family.Should().Be(OsFamily.MacOS);
        os.Version.Should().Be("14.4.1");
        os.DisplayName.Should().Be("macOS");
    }

    [Fact]
    public void Empty_or_missing_folder_is_unknown()
    {
        using var fx = new EvidenceFixture().Dir("stuff");
        OsFamilyDetector.Detect(fx.Root).Family.Should().Be(OsFamily.Unknown);
        OsFamilyDetector.Detect(Path.Combine(fx.Root, "nope")).Family.Should().Be(OsFamily.Unknown);
    }

    [Fact]
    public void Os_release_parsing_handles_quotes_and_escapes()
    {
        var kv = OsFamilyDetector.ParseOsRelease("A=\"say \\\"hi\\\" \\$x\"\r\nB='single'\nexport C=plain\n=bad\nnoequals\n");
        kv["A"].Should().Be("say \"hi\" $x");
        kv["B"].Should().Be("single");
        kv["C"].Should().Be("plain");
        kv.Should().HaveCount(3);
    }

    [Fact]
    public void FindSystemRoots_finds_nested_collections()
    {
        using var fx = new EvidenceFixture()
            .File("uac-web01-linux/[root]/etc/os-release", "ID=ubuntu\n")
            .File("kape/C/Windows/System32/config/SOFTWARE", "x")
            .Dir("other/empty");
        var roots = OsFamilyDetector.FindSystemRoots(fx.Root);
        roots.Should().HaveCount(2);
        roots.Should().Contain(r => r.Root.EndsWith("[root]") && r.Os.Family == OsFamily.Linux);
        roots.Should().Contain(r => r.Root.EndsWith(Path.DirectorySeparatorChar + "C") && r.Os.Family == OsFamily.Windows);
    }
}

public class EvidencePathTests
{
    [Fact]
    public void Glob_matches_wildcards_and_double_star_case_insensitively()
    {
        using var fx = new EvidenceFixture()
            .File("home/alice/.bash_history", "ls")
            .File("home/bob/.BASH_HISTORY", "id")
            .File("home/bob/.zsh_history", "x")
            .File("root/.bash_history", "whoami")
            .File("var/spool/cron/crontabs/alice", "* * * * * x")
            .File("var/spool/cron/nested/deep/bob", "y");

        EvidencePath.Glob(fx.Root, "home/*/.bash_history").Select(f => f.RelativePath)
            .Should().Equal("home/alice/.bash_history", "home/bob/.BASH_HISTORY");
        EvidencePath.Glob(fx.Root, "var/spool/cron/**").Select(f => f.RelativePath)
            .Should().Equal("var/spool/cron/crontabs/alice", "var/spool/cron/nested/deep/bob");
        EvidencePath.Glob(fx.Root, "ROOT/.bash_history").Should().ContainSingle().Which.FullPath.Should().NotBeNull();
        EvidencePath.Glob(fx.Root, "nope/*").Should().BeEmpty();
    }

    [Fact]
    public void Resolve_never_climbs_above_the_root()
    {
        using var fx = new EvidenceFixture().File("etc/passwd", "x");
        EvidencePath.ResolveFile(fx.Root, "../../../etc/passwd").Should().Be(fx.Path_("etc/passwd"));
    }

    [Fact]
    public void Absolute_symlinks_are_rerooted_inside_the_evidence()
    {
        using var fx = new EvidenceFixture().File("usr/lib/os-release", "ID=arch\n");
        if (!fx.FileLink("etc/os-release", "/usr/lib/os-release")) return;   // no symlink privilege on this machine
        fx.FileLink("home/eve/.bash_history", "/dev/null");

        EvidencePath.ResolveFile(fx.Root, "etc/os-release").Should().Be(fx.Path_("usr/lib/os-release"));
        OsFamilyDetector.Detect(fx.Root).Distribution.Should().Be("arch");

        var hist = EvidencePath.Glob(fx.Root, "home/*/.bash_history").Should().ContainSingle().Subject;
        hist.IsLink.Should().BeTrue();
        hist.LinkTarget.Should().Be("/dev/null");
        hist.FullPath.Should().BeNull();
    }
}

public class ArtifactParserRegistryTests
{
    private sealed class FakeParser : IArtifactParser
    {
        public string Id { get; init; } = "linux-fake";
        public OsFamily Platform { get; init; } = OsFamily.Linux;
        public string Category { get; init; } = "Logs";
        public string OutputName { get; init; } = "Fake.csv";
        public IEnumerable<string> InputGlobs { get; init; } = new[] { "var/log/fake*" };
        public Task<ArtifactParseResult> ParseAsync(ArtifactParseContext ctx, CancellationToken ct) => Task.FromResult(new ArtifactParseResult());
    }

    [Fact]
    public void Default_registry_has_the_passwd_parser()
    {
        var r = ArtifactParserRegistry.CreateDefault();
        r.Find("linux-passwd").Should().BeOfType<LinuxPasswdParser>();
        r.ForPlatform(OsFamily.Linux).Should().ContainSingle();
        r.ForPlatform(OsFamily.Windows).Should().BeEmpty();
    }

    [Theory]
    [InlineData("users.csv")]
    [InlineData("EzForensics-Users.csv")]
    [InlineData("Users")]
    [InlineData("Logins_.csv")]
    public void Bad_output_names_are_rejected(string name)
    {
        var r = new ArtifactParserRegistry();
        r.Invoking(x => x.Register(new FakeParser { OutputName = name })).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Users.csv")]
    [InlineData("Logins_Failed.csv")]
    [InlineData("ShellHistory_Bash.csv")]
    public void Good_output_names_are_accepted(string name) => ArtifactOutputLayout.IsValidOutputName(name).Should().BeTrue();

    [Fact]
    public void Contract_violations_are_rejected()
    {
        var r = new ArtifactParserRegistry();
        r.Register(new FakeParser());
        r.Invoking(x => x.Register(new FakeParser { OutputName = "Other.csv" })).Should().Throw<InvalidOperationException>("duplicate id");
        r.Invoking(x => x.Register(new FakeParser { Id = "linux-fake2" })).Should().Throw<InvalidOperationException>("same output file");
        r.Invoking(x => x.Register(new FakeParser { Id = "x", Platform = OsFamily.Unknown })).Should().Throw<ArgumentException>();
        r.Invoking(x => x.Register(new FakeParser { Id = "Upper" })).Should().Throw<ArgumentException>();
        r.Invoking(x => x.Register(new FakeParser { Id = "y", InputGlobs = new[] { "/etc/passwd" } })).Should().Throw<ArgumentException>();
        r.Invoking(x => x.Register(new FakeParser { Id = "z", InputGlobs = new[] { "../etc/passwd" } })).Should().Throw<ArgumentException>();
        r.Invoking(x => x.Register(new FakeParser { Id = "w", Category = "logs" })).Should().Throw<ArgumentException>();
        r.Register(new FakeParser { Id = "windows-fake", Platform = OsFamily.Windows });   // same output on another platform is fine
    }

    [Fact]
    public void Discover_detects_platform_and_inputs()
    {
        using var fx = new EvidenceFixture().File("etc/os-release", "ID=ubuntu\n").File("etc/passwd", "root:x:0:0::/root:/bin/bash\n");
        var r = ArtifactParserRegistry.CreateDefault();
        r.Register(new FakeParser());
        var (os, matches) = r.Discover(fx.Root);
        os.Family.Should().Be(OsFamily.Linux);
        matches.Should().ContainSingle().Which.Parser.Id.Should().Be("linux-passwd");
        matches[0].Inputs.Should().ContainSingle().Which.RelativePath.Should().Be("etc/passwd");

        using var win = new EvidenceFixture().File("Windows/System32/config/SYSTEM", "x").File("etc/passwd", "root:x:0:0::/:/bin/sh\n");
        r.Discover(win.Root).Matches.Should().BeEmpty("Linux parsers never run on a Windows root");
    }

    [Theory]
    [InlineData("web01", "web01")]
    [InlineData("  ", "Unknown")]
    [InlineData(null, "Unknown")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("..", "Unknown")]
    [InlineData("CON", "_CON")]
    public void Host_folder_is_sanitised(string? host, string expected) => ArtifactOutputLayout.SafeHostFolder(host).Should().Be(expected);
}

public class ArtifactCsvWriterTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData(" padded", "\" padded\"")]
    public void Escape_follows_rfc4180(string? value, string expected) => ArtifactCsvWriter.Escape(value).Should().Be(expected);

    [Fact]
    public void Abandoned_writer_leaves_nothing_and_commit_never_overwrites()
    {
        using var fx = new EvidenceFixture();
        var target = Path.Combine(fx.Root, "Out.csv");
        using (var w = new ArtifactCsvWriter(target, new[] { "A" })) w.WriteRow("1");
        Directory.GetFiles(fx.Root).Should().BeEmpty();

        using (var w = new ArtifactCsvWriter(target, new[] { "A" })) { w.WriteRow("1"); w.Commit().Should().Be(target); }
        using (var w = new ArtifactCsvWriter(target, new[] { "A" })) { w.WriteRow("2"); w.Commit().Should().Be(Path.Combine(fx.Root, "Out (2).csv")); }
        File.ReadAllText(target).Should().Be("A\r\n1\r\n");
    }
}

public class LinuxPasswdParserTests
{
    private const string Passwd =
        "root:x:0:0:root:/root:/bin/bash\n" +
        "daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\n" +
        "# a comment\n" +
        "\n" +
        "sync:x:4:65534:sync:/bin:/bin/sync\n" +
        "nobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin\n" +
        "alice:x:1000:1000:Alice Smith,Room 1,,:/home/alice:/bin/zsh\r\n" +
        "svc:x:600:600:Service \"svc\":/srv:\n" +
        "toor:x:0:0::/root:/bin/sh\n" +
        "legacy:$1$abc$def:1001:1001::/home/legacy:/bin/false\n" +
        "broken:x:1002\n" +
        "+@netgroup::::::\n";

    [Fact]
    public void Parse_classifies_accounts_and_warns()
    {
        var warnings = new List<string>();
        var entries = LinuxPasswdParser.Parse(Passwd, "etc/passwd", 1000, warnings);
        entries.Select(e => e.UserName).Should().Equal("root", "daemon", "sync", "nobody", "alice", "svc", "toor", "legacy");

        var byName = entries.ToDictionary(e => e.UserName);
        byName["root"].IsSystemAccount.Should().BeTrue();
        byName["root"].HasLoginShell.Should().BeTrue();
        byName["daemon"].HasLoginShell.Should().BeFalse();
        byName["sync"].HasLoginShell.Should().BeFalse();
        byName["nobody"].IsSystemAccount.Should().BeTrue();
        byName["alice"].IsSystemAccount.Should().BeFalse();
        byName["alice"].Gecos.Should().Be("Alice Smith,Room 1,,");
        byName["alice"].Shell.Should().Be("/bin/zsh");
        byName["svc"].HasLoginShell.Should().BeTrue("an empty shell means /bin/sh");
        byName["legacy"].HasLoginShell.Should().BeFalse();

        warnings.Should().Contain(w => w.Contains("'toor' has UID 0"));
        warnings.Should().Contain(w => w.Contains("'toor' shares UID 0 with 'root'"));
        warnings.Should().Contain(w => w.Contains("'legacy' has a password hash in passwd"));
        warnings.Should().Contain(w => w.Contains("etc/passwd:11: expected 7 fields"));
        warnings.Should().Contain(w => w.Contains("NIS compat entry"));
        warnings.Should().NotContain(w => w.Contains("'root' has UID 0"));
    }

    [Fact]
    public void Login_defs_uid_min_is_honoured()
    {
        using var fx = new EvidenceFixture().File("etc/login.defs", "# defaults\nUID_MIN\t\t\t  500\nUID_MAX 60000\n");
        LinuxPasswdParser.ReadUidMin(fx.Root).Should().Be(500);
        using var none = new EvidenceFixture();
        LinuxPasswdParser.ReadUidMin(none.Root).Should().Be(LinuxPasswdParser.DefaultUidMin);
    }

    [Fact]
    public async Task End_to_end_writes_host_category_users_csv()
    {
        using var evidence = new EvidenceFixture()
            .File("etc/os-release", "ID=ubuntu\nVERSION_ID=\"22.04\"\n")
            .File("etc/hostname", "web01\n")
            .File("etc/login.defs", "UID_MIN 500\n")
            .File("etc/passwd", Passwd);
        using var caseDir = new EvidenceFixture();

        var registry = ArtifactParserRegistry.CreateDefault();
        var (os, matches) = registry.Discover(evidence.Root);
        var match = matches.Should().ContainSingle().Subject;
        var progress = new List<ArtifactParseProgress>();
        var result = await ArtifactParserRegistry.RunAsync(match, evidence.Root, caseDir.Root, os, progress: new SyncProgress(progress.Add));

        var expected = Path.Combine(caseDir.Root, "web01", "Accounts", "Users.csv");
        result.OutputFiles.Should().Equal(expected);
        result.Rows.Should().Be(8);
        result.Warnings.Should().NotBeEmpty();
        progress.Should().NotBeEmpty();

        var lines = File.ReadAllText(expected).Split("\r\n");
        lines[0].Should().Be("UserName,Uid,Gid,Gecos,Home,Shell,IsSystemAccount,HasLoginShell");
        lines.Should().Contain("alice,1000,1000,\"Alice Smith,Room 1,,\",/home/alice,/bin/zsh,No,Yes");
        lines.Should().Contain("svc,600,600,\"Service \"\"svc\"\"\",/srv,,No,Yes", "UID_MIN 500 from login.defs makes 600 a normal user");
        lines.Should().Contain("daemon,1,1,daemon,/usr/sbin,/usr/sbin/nologin,Yes,No");

        // A second run never overwrites the first.
        var again = await ArtifactParserRegistry.RunAsync(match, evidence.Root, caseDir.Root, os);
        again.OutputFiles.Should().Equal(Path.Combine(caseDir.Root, "web01", "Accounts", "Users (2).csv"));
    }

    private sealed class SyncProgress : IProgress<ArtifactParseProgress>
    {
        private readonly Action<ArtifactParseProgress> _a;
        public SyncProgress(Action<ArtifactParseProgress> a) => _a = a;
        public void Report(ArtifactParseProgress value) => _a(value);
    }
}
