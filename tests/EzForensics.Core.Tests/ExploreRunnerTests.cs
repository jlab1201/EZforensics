using EzForensics.Core.Explore;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ExploreRunnerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static string Sample(string name) => File.ReadAllText(Path.Combine(TriageTestData.Root, "explore", name));

    // ---- DIE (samples captured from diec 3.21: notepad.exe, a zip, a signed .NET Framework dll)

    [Fact]
    public void Die_parses_native_pe_sample()
    {
        var r = DieRunner.Parse(Sample("die-notepad.json"))!;
        r.FileTypes.Should().Equal("PE64");
        r.Detections.Should().Contain(d => d.Type == "linker" && d.Name == "Microsoft Linker" && d.Version == "14.38.33145");
        r.Detections.Should().Contain(d => d.Type == "compiler" && d.Info == "LTCG/C");
        r.IsPacked.Should().BeFalse();
        r.Verdict.Should().BeNull();
    }

    [Fact]
    public void Die_parses_dotnet_and_heuristic_entries()
    {
        var r = DieRunner.Parse(Sample("die-dotnet.json"))!;
        r.Detections.Should().Contain(d => d.Name == ".NET Framework" && d.Version == "CLR 4.0.30319");
        r.Detections.Should().Contain(d => d.Type == "sign tool" && d.Name == "Windows Authenticode");
        var heur = r.Detections.Single(d => d.Heuristic);
        heur.Type.Should().Be("debug data");
        heur.Text.Should().StartWith("(Heur)");
    }

    [Fact]
    public void Die_unknown_zip_has_no_known_detections()
    {
        var r = DieRunner.Parse(Sample("die-zip.json"))!;
        r.FileTypes.Should().Equal("ZIP");
        r.Known.Should().BeEmpty();
        r.Detections.Single().IsUnknown.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"x\":1}")]
    public void Die_rejects_non_die_json(string json) => DieRunner.Parse(json).Should().BeNull();

    [Fact]
    public void Die_packer_verdict()
    {
        var r = DieRunner.Parse("""{"detects":[{"filetype":"PE32","values":[{"info":"NRV,brute","name":"UPX","string":"Packer: UPX(3.96)[NRV,brute]","type":"packer","version":"3.96"}]}]}""")!;
        r.IsPacked.Should().BeTrue();
        r.Verdict.Should().Be("Packer: UPX");
    }

    // ---- ExifTool (samples captured from exiftool 13.60 -j -G1 -a)

    [Fact]
    public void Exif_parses_groups_in_order()
    {
        var r = ExifRunner.Parse(Sample("exif-notepad.json"))!;
        r.SourceFile.Should().EndWith("notepad.exe");
        r.Groups.Select(g => g.Name).Should().StartWith(new[] { "ExifTool", "System", "File", "EXE" });
        r.FileType.Should().Be("Win64 EXE");
        r.Get("EXE:CompanyName").Should().Be("Microsoft Corporation");
        r.Get("OriginalFileName").Should().Be("NOTEPAD.EXE");
        r.Get("EXE:LinkerVersion").Should().Be("14.38");
        r.Get("Nope:Missing").Should().BeNull();
    }

    [Fact]
    public void Exif_parses_image_sample_numbers_and_strings()
    {
        var r = ExifRunner.Parse(Sample("exif-jpg.json"))!;
        r.MimeType.Should().Be("image/jpeg");
        r.Get("File:ImageWidth").Should().Be("3840");
        r.Get("Composite:ImageSize").Should().Be("3840x2400");
        r.Groups.Should().Contain(g => g.Name == "Adobe");
    }

    [Fact]
    public void Exif_handles_arrays_errors_and_garbage()
    {
        var r = ExifRunner.Parse("""[{"SourceFile":"x","ExifTool:Error":"File not found","XMP-dc:Subject":["a","b"],"Plain":1}]""")!;
        r.Error.Should().Be("File not found");
        r.Get("XMP-dc:Subject").Should().Be("a, b");
        r.Groups.Should().Contain(g => g.Name == "");
        ExifRunner.Parse("[]").Should().BeNull();
        ExifRunner.Parse("nope").Should().BeNull();
    }

    [Fact]
    public void Exif_argfile_reads_utf8_paths_without_running_them()
    {
        var a = ExifRunner.BuildArgFile(@"C:\évidence\файл.jpg");
        a.Should().Contain("-j").And.Contain("-G1").And.Contain("-a").And.Contain("filename=utf8");
        a.Should().NotContain("-n").And.NotContain("-overwrite_original");
        a[^1].Should().Be(@"C:\évidence\файл.jpg");
    }

    // ---- Runners against the fake tool (process plumbing, raw capture, timeout, cache)

    [Fact]
    public async Task Die_runner_captures_raw_json_and_parses_it()
    {
        var f = Path.Combine(_tmp.Path, "sample.bin");
        File.WriteAllBytes(f, new byte[42]);
        var o = await new DieRunner(FakeToolPaths.Exe, "3.21").RunAsync(f);
        o.Ok.Should().BeTrue(o.Error);
        o.Tool.Should().Be(DieRunner.ToolKey);
        o.ToolVersion.Should().Be("3.21");
        var r = DieRunner.Parse(o.Raw)!;
        r.Verdict.Should().Be("Packer: UPX");
        r.Detections.Single().FileType.Should().Be("Binary");
    }

    [Fact]
    public async Task Exif_runner_passes_unicode_path_through_argfile()
    {
        var f = Path.Combine(_tmp.Path, "fotó-файл.jpg");
        File.WriteAllText(f, "hello");
        var o = await new ExifRunner(FakeToolPaths.Exe).RunAsync(f);
        o.Ok.Should().BeTrue(o.Error);
        var r = ExifRunner.Parse(o.Raw)!;
        r.Get("System:FileName").Should().Be("fotó-файл.jpg");
        r.Get("System:FileSize").Should().Be("5 bytes");
    }

    [Fact]
    public async Task Runner_times_out_and_reports_it()
    {
        var f = Path.Combine(_tmp.Path, "hang.bin");
        File.WriteAllText(f, "x");
        var o = await new DieRunner(FakeToolPaths.Exe).RunAsync(f, TimeSpan.FromSeconds(1));
        o.TimedOut.Should().BeTrue();
        o.Ok.Should().BeFalse();
        o.Error.Should().Contain("Timed out");
    }

    [Fact]
    public async Task Runner_cancellation_is_reported()
    {
        var f = Path.Combine(_tmp.Path, "hang2.bin");
        File.WriteAllText(f, "x");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var o = await new DieRunner(FakeToolPaths.Exe).RunAsync(f, TimeSpan.FromMinutes(1), cts.Token);
        o.Cancelled.Should().BeTrue();
        o.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_tool_is_an_error_not_an_exception()
    {
        var f = Path.Combine(_tmp.Path, "a.bin");
        File.WriteAllText(f, "x");
        var o = await new DieRunner(Path.Combine(_tmp.Path, "nope", "diec.exe")).RunAsync(f);
        o.Ok.Should().BeFalse();
        o.Error.Should().Contain("not installed");
    }

    [Fact]
    public async Task Cache_runs_once_then_serves_from_disk_and_skips_failures()
    {
        var caseDir = Path.Combine(_tmp.Path, "case");
        var f = Path.Combine(_tmp.Path, "c.bin");
        File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
        var sha = FileFactsReader.Hash(f).Sha256;
        var cache = new ResultCache(caseDir);
        var die = new DieRunner(FakeToolPaths.Exe, "3.21");

        var (o1, r1) = await die.GetAsync(cache, f, sha);
        o1.FromCache.Should().BeFalse();
        r1!.Verdict.Should().Be("Packer: UPX");
        File.Exists(Path.Combine(caseDir, "_explore", "results", sha.ToLowerInvariant(), "die.json")).Should().BeTrue();

        var (o2, r2) = await die.GetAsync(cache, f, sha);
        o2.FromCache.Should().BeTrue();
        o2.Raw.Should().Be(o1.Raw);
        r2!.Detections.Should().HaveCount(1);

        int runs = 0;
        var fail = await cache.GetOrRunAsync(sha, "exif", _ => { runs++; return Task.FromResult(new ExploreToolOutput("exif", null, "", 1, false, false, "boom", null, "")); });
        fail.Ok.Should().BeFalse();
        cache.TryGet(sha, "exif").Should().BeNull();
        var ok = await cache.GetOrRunAsync(sha, "exif", _ => { runs++; return Task.FromResult(new ExploreToolOutput("exif", "13.60", "[{}]", 0, false, false, null, null, "t")); });
        (await cache.GetOrRunAsync(sha, "exif", _ => { runs++; return Task.FromResult(ok); })).FromCache.Should().BeTrue();
        runs.Should().Be(2);
        (await cache.GetOrRunAsync(sha, "exif", _ => { runs++; return Task.FromResult(ok); }, refresh: true)).FromCache.Should().BeFalse();
        runs.Should().Be(3);

        cache.Invalidate(sha);
        cache.TryGet(sha, "die").Should().BeNull();
    }

    [Theory]
    [InlineData("../../etc", "die")]
    [InlineData("abc", "die")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "..\\x")]
    public void Cache_rejects_unsafe_keys(string sha, string tool) =>
        ((Action)(() => new ResultCache(_tmp.Path).PathFor(sha, tool))).Should().Throw<ArgumentException>();
}
