using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Explore;
using FluentAssertions;

namespace EzForensics.Core.Tests;

[Collection("EnvironmentVariables")]
public sealed class ExploreToolPathTests
{
    [Fact]
    public void Die_exiftool_and_cyberchef_install_under_data_root_tools_explore()
    {
        var saved = (Environment.GetEnvironmentVariable("EZFORENSICS_EXPLORE_TOOLS"), Environment.GetEnvironmentVariable("EZHUB_EXPLORE_TOOLS"));
        try
        {
            Environment.SetEnvironmentVariable("EZFORENSICS_EXPLORE_TOOLS", null);
            Environment.SetEnvironmentVariable("EZHUB_EXPLORE_TOOLS", null);
            var root = Path.Combine(EzForensics.Core.Settings.AppPaths.DataRoot, "Tools", "Explore");
            ExploreToolLocator.ManagedRoot.Should().Be(root);
            var loc = new ExploreToolLocator(); // what the setup step uses
            foreach (var tool in new[] { ExploreTool.Die, ExploreTool.ExifTool, ExploreTool.CyberChef })
            {
                var dir = loc.ToolDir(tool);
                Path.GetDirectoryName(dir).Should().Be(root, tool.ToString());
                loc.EntryPath(tool).Should().StartWith(dir + Path.DirectorySeparatorChar);
            }
            new[] { ExploreTool.Die, ExploreTool.ExifTool, ExploreTool.CyberChef }.Select(loc.ToolDir).Should().OnlyHaveUniqueItems();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EZFORENSICS_EXPLORE_TOOLS", saved.Item1);
            Environment.SetEnvironmentVariable("EZHUB_EXPLORE_TOOLS", saved.Item2);
        }
    }
}

public sealed class ExploreToolSetupTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Pinned_specs_are_official_https_with_hash_and_size()
    {
        ExploreToolReleases.All.Should().HaveCount(3);
        foreach (var s in ExploreToolReleases.All)
        {
            ExploreToolReleases.IsOfficialUrl(s.Url).Should().BeTrue(s.Url);
            s.Sha256.Should().MatchRegex("^[0-9A-F]{64}$");
            s.Size.Should().BeGreaterThan(1_000_000);
            ExploreToolReleases.For(s.Tool).Should().BeSameAs(s);
        }
        ExploreToolReleases.ExifTool.Renames!["exiftool(-k).exe"].Should().Be("exiftool.exe");
    }

    [Theory]
    [InlineData("https://github.com/horsicq/DIE-engine/releases/download/3.21/x.zip", true)]
    [InlineData("https://sourceforge.net/projects/exiftool/files/x.zip/download", true)]
    [InlineData("http://github.com/x.zip", false)]
    [InlineData("https://github.com.evil.example/x.zip", false)]
    [InlineData("https://example.com/x.zip", false)]
    [InlineData("not a url", false)]
    public void Only_official_hosts_are_allowed(string url, bool ok) => ExploreToolReleases.IsOfficialUrl(url).Should().Be(ok);

    [Fact]
    public void Extract_strips_prefix_renames_and_rejects_zip_slip()
    {
        var spec = ExploreToolReleases.ExifTool;
        var zip = MakeZip(("exiftool-13.60_64/exiftool(-k).exe", "exe"), ("exiftool-13.60_64/exiftool_files/perl.exe", "p"), ("other/skip.txt", "x"));
        var dir = Path.Combine(_tmp.Path, "ok");
        ExploreToolSetup.Extract(zip, spec, dir).Should().Be(2);
        File.Exists(Path.Combine(dir, "exiftool.exe")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "exiftool_files", "perl.exe")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "skip.txt")).Should().BeFalse();

        var evil = MakeZip(("exiftool-13.60_64/exiftool(-k).exe", "exe"), ("exiftool-13.60_64/../../evil.txt", "x"));
        ((Action)(() => ExploreToolSetup.Extract(evil, spec, Path.Combine(_tmp.Path, "bad")))).Should().Throw<InvalidDataException>();
        File.Exists(Path.Combine(_tmp.Path, "evil.txt")).Should().BeFalse();

        var noEntry = MakeZip(("exiftool-13.60_64/readme.txt", "x"));
        ((Action)(() => ExploreToolSetup.Extract(noEntry, spec, Path.Combine(_tmp.Path, "none")))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Install_verifies_extracts_writes_marker_and_locator_sees_it()
    {
        var bytes = File.ReadAllBytes(MakeZip(("die/diec.exe", "exe"), ("die/db/PE/x.sg", "sig")));
        var spec = SpecFor(ExploreToolReleases.Die, bytes);
        var loc = new ExploreToolLocator(Path.Combine(_tmp.Path, "tools"));
        loc.IsInstalled(ExploreTool.Die).Should().BeFalse();
        using var http = new HttpClient(new StubHandler(bytes));
        var entry = await ExploreToolSetup.InstallAsync(http, spec, loc);
        entry.Should().Be(loc.DiecExe);
        var g = loc.Get(ExploreTool.Die);
        g.IsInstalled.Should().BeTrue();
        g.Version.Should().Be(spec.Version);
        g.IsCurrent.Should().BeTrue();
        Directory.Exists(loc.ToolDir(ExploreTool.Die) + ".download").Should().BeFalse();
        Directory.Exists(loc.ToolDir(ExploreTool.Die) + ".tmp").Should().BeFalse();
        ExploreToolSetup.Uninstall(loc, ExploreTool.Die).Should().BeTrue();
        loc.IsInstalled(ExploreTool.Die).Should().BeFalse();
    }

    [Fact]
    public async Task Tampered_download_is_rejected_and_the_old_install_kept()
    {
        var good = File.ReadAllBytes(MakeZip(("die/diec.exe", "exe")));
        var spec = SpecFor(ExploreToolReleases.Die, good);
        var tampered = (byte[])good.Clone();
        tampered[^10] ^= 0xFF;
        var target = Path.Combine(_tmp.Path, "die");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "old");
        using var http = new HttpClient(new StubHandler(tampered));
        var act = () => ExploreToolSetup.InstallAsync(http, spec, target);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*SHA-256*");
        File.Exists(Path.Combine(target, "keep.txt")).Should().BeTrue();
        Directory.Exists(target + ".download").Should().BeFalse();
        Directory.Exists(target + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Size_mismatch_and_unofficial_url_are_rejected()
    {
        var bytes = File.ReadAllBytes(MakeZip(("die/diec.exe", "exe")));
        var spec = SpecFor(ExploreToolReleases.Die, bytes);
        using var http = new HttpClient(new StubHandler(bytes.Concat(new byte[] { 1 }).ToArray()));
        await ((Func<Task>)(() => ExploreToolSetup.InstallAsync(http, spec, Path.Combine(_tmp.Path, "a"))))
            .Should().ThrowAsync<InvalidDataException>();
        var evil = spec with { Url = "https://example.com/die.zip" };
        await ((Func<Task>)(() => ExploreToolSetup.InstallAsync(http, evil, Path.Combine(_tmp.Path, "b"))))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private static ExploreToolSpec SpecFor(ExploreToolSpec baseSpec, byte[] bytes) =>
        baseSpec with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Size = bytes.Length };

    private string MakeZip(params (string Name, string Text)[] entries)
    {
        var p = Path.Combine(_tmp.Path, Guid.NewGuid().ToString("N") + ".zip");
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
            foreach (var (n, t) in entries)
            {
                using var s = z.CreateEntry(n).Open();
                s.Write(Encoding.UTF8.GetBytes(t));
            }
        return p;
    }
}
