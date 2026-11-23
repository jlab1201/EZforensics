using System.Security.Cryptography;
using EzForensics.Core.Explore;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ExploreExportTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Export_copies_with_relative_paths_and_manifest_hashes()
    {
        var caseDir = Path.Combine(_tmp.Path, "Case");
        var ev = Path.Combine(_tmp.Path, "Evidence");
        Directory.CreateDirectory(Path.Combine(ev, "Users", "bob"));
        var a = Path.Combine(ev, "Users", "bob", "a.bin");
        var b = Path.Combine(ev, "top.txt");
        File.WriteAllBytes(a, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        File.WriteAllText(b, "hello");
        var mtime = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, mtime);
        var sources = ExploreSources.Load(caseDir);
        sources.Add(ev);

        var res = ExploreExport.Export(sources, new[] { a, b, Path.Combine(ev, "missing.bin") });

        res.Folder.Should().StartWith(Path.Combine(caseDir, "_explore", "exports"));
        Path.GetFileName(res.Folder).Should().MatchRegex(@"^\d{8}-\d{6}");
        res.Copied.Should().Be(2);
        res.Failed.Should().Be(1);
        var copyA = Path.Combine(res.Folder, "Evidence", "Users", "bob", "a.bin");
        File.ReadAllBytes(copyA).Should().Equal(File.ReadAllBytes(a));
        File.GetLastWriteTimeUtc(copyA).Should().Be(mtime);
        File.Exists(Path.Combine(res.Folder, "Evidence", "top.txt")).Should().BeTrue();

        var m = ExploreExport.ReadManifest(res.Folder)!;
        m.Kind.Should().Be(ExportManifest.ManifestKind);
        var ra = m.Files.Single(f => f.Source == a);
        ra.RelativePath.Should().Be(Path.Combine("Evidence", "Users", "bob", "a.bin"));
        ra.Size.Should().Be(5000);
        ra.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a))));
        ra.Sha1.Should().Be(Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(a))));
        ra.Md5.Should().Be(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(a))));
        ra.Verified.Should().BeTrue();
        m.Files.Single(f => f.Source.EndsWith("missing.bin")).Error.Should().NotBeNull();
        Directory.EnumerateFiles(res.Folder, "*.part", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Same_second_exports_get_separate_folders_and_name_clashes_are_numbered()
    {
        var caseDir = Path.Combine(_tmp.Path, "Case");
        var f = Path.Combine(_tmp.Path, "x.txt");
        File.WriteAllText(f, "x");
        var now = new DateTime(2026, 10, 30, 12, 0, 0);
        var r1 = ExploreExport.Export(caseDir, new[] { new ExportItem(f, "x.txt"), new ExportItem(f, "x.txt") }, now: now);
        var r2 = ExploreExport.Export(caseDir, new[] { new ExportItem(f, @"..\..\escape.txt") }, now: now);
        Path.GetFileName(r1.Folder).Should().Be("20261030-120000");
        Path.GetFileName(r2.Folder).Should().Be("20261030-120000-2");
        r1.Manifest.Files.Select(x => x.RelativePath).Should().Equal("x.txt", "x (2).txt");
        r2.Manifest.Files.Single().RelativePath.Should().Be("escape.txt");
        File.Exists(Path.Combine(r2.Folder, "escape.txt")).Should().BeTrue();
    }

    [Fact]
    public void Relative_path_without_a_source_keeps_the_drive()
    {
        ExploreExport.RelativeFor(@"C:\Users\bob\a.exe").Should().Be(@"C\Users\bob\a.exe");
    }
}
