using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using EzForensics.Core.Tools;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ToolsUpdaterTests
{
    private const string Html = """
        <html><body>
        <a href="https://f001.backblazeb2.com/file/EricZimmermanTools/PECmd.zip">PECmd</a>
        <a href="https://download.ericzimmermanstools.com/PECmd.zip">PECmd dup</a>
        <a href="https://download.ericzimmermanstools.com/net9/PECmd.zip">PECmd 9</a>
        <a href="https://download.ericzimmermanstools.com/net9/MFTECmd.zip">MFTECmd 9</a>
        <a href="https://download.ericzimmermanstools.com/kape.zip">kape</a>
        <a href="https://download.ericzimmermanstools.com/All.zip">all</a>
        <a href="https://download.ericzimmermanstools.com/net9/All_9.zip">all9</a>
        <a href="https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip">script</a>
        <a href="https://example.com/readme.html">no</a>
        </body></html>
        """;

    [Fact]
    public void Connection_reset_is_transient_and_explained_as_network_not_file_lock()
    {
        var reset = new IOException("Unable to read data from the transport connection.", new System.Net.Sockets.SocketException(10054));
        ToolUpdater.IsTransient(reset).Should().BeTrue();
        ToolUpdater.IsTransient(new IOException("There is not enough space on the disk.")).Should().BeFalse();
        ToolUpdater.IsTransient(new HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound)).Should().BeFalse();
        ToolUpdater.IsTransient(new HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway)).Should().BeTrue();
        EzForensics.Core.Setup.SetupRunner.Explain(reset).Should().Contain("connection").And.NotContain("Close programs");
    }

    [Fact]
    public void Parses_rewrites_dedupes_and_filters()
    {
        var both = ToolUpdater.ParseLinks(Html, 0);
        both.Should().BeEquivalentTo(new[]
        {
            "https://download.ericzimmermanstools.com/PECmd.zip",
            "https://download.ericzimmermanstools.com/net9/PECmd.zip",
            "https://download.ericzimmermanstools.com/net9/MFTECmd.zip",
            "https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip",
        });
        ToolUpdater.ParseLinks(Html, 9).Should().BeEquivalentTo(new[]
        {
            "https://download.ericzimmermanstools.com/net9/PECmd.zip",
            "https://download.ericzimmermanstools.com/net9/MFTECmd.zip",
            "https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip",
        });
        ToolUpdater.ParseLinks(Html, 4).Should().BeEquivalentTo(new[]
        {
            "https://download.ericzimmermanstools.com/PECmd.zip",
            "https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip",
        });
    }

    [Fact]
    public void Details_csv_round_trips_and_diff_detects_changes()
    {
        using var t = new TempDir();
        var csv = Path.Combine(t.Path, ToolUpdater.DetailsFileName);
        var local = new[]
        {
            new RemoteDetail("PECmd.zip", "aaa", "https://download.ericzimmermanstools.com/PECmd.zip", 100, false),
            new RemoteDetail("MFTECmd.zip", "old", "https://download.ericzimmermanstools.com/net9/MFTECmd.zip", 200, true),
        };
        ToolUpdater.WriteDetails(csv, local);
        File.ReadAllLines(csv)[0].Should().Be("\"Name\",\"SHA1\",\"URL\",\"Size\",\"IsNet9\",\"SHA256\"");
        var read = ToolUpdater.ReadDetails(csv);
        read.Should().BeEquivalentTo(local);

        var remote = new[]
        {
            new RemoteToolFile("PECmd.zip", local[0].Url, "aaa", 100, false, RemoteFileStatus.New),
            new RemoteToolFile("MFTECmd.zip", local[1].Url, "new", 200, true, RemoteFileStatus.New),
            new RemoteToolFile("PECmd.zip", "https://download.ericzimmermanstools.com/net9/PECmd.zip", "x", 5, true, RemoteFileStatus.New),
        };
        var diff = ToolUpdater.Diff(remote, read);
        diff.Select(d => d.Status).Should().Equal(RemoteFileStatus.UpToDate, RemoteFileStatus.Changed, RemoteFileStatus.New);
    }

    private sealed class ZipHandler(byte[] body, string? redirectTo = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (redirectTo != null) request.RequestUri = new Uri(redirectTo); // what HttpClient does after following a redirect
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    private static byte[] ZipWith(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("http://download.ericzimmermanstools.com/PECmd.zip")]
    [InlineData("https://evil.example.com/PECmd.zip")]
    [InlineData("https://download.ericzimmermanstools.com.evil.com/PECmd.zip")]
    public async Task Download_rejects_http_and_hosts_outside_the_allowlist(string url)
    {
        using var t = new TempDir();
        var handler = new ZipHandler(ZipWith(("PECmd.exe", "x")));
        var updater = new ToolUpdater(new HttpClient(handler));
        var f = new RemoteToolFile("PECmd.zip", url, "e", 0, false, RemoteFileStatus.New);
        var act = () => updater.DownloadAsync(new[] { f }, t.Path, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Refusing*");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Download_rejects_a_redirect_off_the_allowlist()
    {
        using var t = new TempDir();
        var updater = new ToolUpdater(new HttpClient(new ZipHandler(ZipWith(("PECmd.exe", "x")), "https://evil.example.com/PECmd.zip")));
        var f = new RemoteToolFile("PECmd.zip", "https://download.ericzimmermanstools.com/PECmd.zip", "e", 0, false, RemoteFileStatus.New);
        var act = () => updater.DownloadAsync(new[] { f }, t.Path, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(t.Path, "PECmd.exe")).Should().BeFalse();
        Directory.GetFiles(t.Path, "*.part").Should().BeEmpty();
    }

    [Fact]
    public async Task Download_logs_and_records_the_sha256_of_the_zip()
    {
        using var t = new TempDir();
        var body = ZipWith(("PECmd.exe", "hello"));
        var expected = Convert.ToHexString(SHA256.HashData(body));
        var updater = new ToolUpdater(new HttpClient(new ZipHandler(body))) { SignatureVerifier = new FakeVerifier(new(true, "CN=ZimTech LLC, O=ZimTech LLC", null)) };
        var f = new RemoteToolFile("PECmd.zip", "https://download.ericzimmermanstools.com/net9/PECmd.zip", "etag1", body.Length, true, RemoteFileStatus.New);
        var lines = new List<string>();
        await updater.DownloadAsync(new[] { f }, t.Path, null, CancellationToken.None, lines.Add);

        lines.Should().Contain(l => l.Contains(expected) && l.Contains("PECmd.zip"));
        lines.Should().Contain(l => l.Contains("signer CN=ZimTech LLC"));
        File.ReadAllText(Path.Combine(t.Path, "net", "PECmd.exe")).Should().Be("hello");
        var row = ToolUpdater.ReadDetails(Path.Combine(t.Path, ToolUpdater.DetailsFileName)).Should().ContainSingle().Subject;
        row.Sha256.Should().Be(expected);
        row.Sha1.Should().Be("etag1");
    }

    [Fact]
    public void ExtractZip_throws_on_a_zip_slip_entry()
    {
        using var t = new TempDir();
        var zip = Path.Combine(t.Path, "evil.zip");
        File.WriteAllBytes(zip, ZipWith(("ok.txt", "ok"), ("../evil.txt", "bad")));
        var target = Path.Combine(t.Path, "out");
        Directory.CreateDirectory(target);

        var act = () => ToolUpdater.ExtractZip(zip, target);
        act.Should().Throw<InvalidDataException>().WithMessage("*../evil.txt*");
        File.Exists(Path.Combine(t.Path, "evil.txt")).Should().BeFalse();
    }

    [Fact]
    public void Details_without_a_sha256_column_still_read()
    {
        using var t = new TempDir();
        var csv = Path.Combine(t.Path, ToolUpdater.DetailsFileName);
        File.WriteAllText(csv, "\"Name\",\"SHA1\",\"URL\",\"Size\",\"IsNet9\"\r\n\"PECmd.zip\",\"aaa\",\"https://download.ericzimmermanstools.com/PECmd.zip\",\"100\",\"False\"\r\n");
        ToolUpdater.ReadDetails(csv).Should().ContainSingle().Which.Sha256.Should().BeEmpty();
    }

    private sealed class FakeVerifier(PeSignature sig) : IPeSignatureVerifier
    {
        public PeSignature Verify(string path) => sig;
    }

    [Fact]
    public async Task Download_keeps_previous_version_when_signature_is_bad()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "PECmd.exe"), "old");
        var updater = new ToolUpdater(new HttpClient(new ZipHandler(ZipWith(("PECmd.exe", "new"), ("PECmd.dll", "d")))))
            { SignatureVerifier = new FakeVerifier(new(false, null, "it is not signed")) };
        var f = new RemoteToolFile("PECmd.zip", "https://download.ericzimmermanstools.com/PECmd.zip", "e", 0, false, RemoteFileStatus.New);
        var failed = await updater.DownloadAsync(new[] { f }, t.Path, null, CancellationToken.None);

        failed.Should().ContainSingle().Which.Should().Contain("PECmd.exe").And.Contain("previous version");
        File.ReadAllText(Path.Combine(t.Path, "PECmd.exe")).Should().Be("old");
        File.Exists(Path.Combine(t.Path, "PECmd.dll")).Should().BeFalse();
        Directory.GetDirectories(t.Path).Should().BeEmpty();
        File.Exists(Path.Combine(t.Path, "PECmd.zip")).Should().BeFalse();
        File.Exists(Path.Combine(t.Path, ToolUpdater.DetailsFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task Download_rejects_a_valid_signature_from_the_wrong_signer()
    {
        using var t = new TempDir();
        var updater = new ToolUpdater(new HttpClient(new ZipHandler(ZipWith(("PECmd.exe", "x")))))
            { SignatureVerifier = new FakeVerifier(new(true, "CN=Someone Else", null)) };
        var f = new RemoteToolFile("PECmd.zip", "https://download.ericzimmermanstools.com/PECmd.zip", "e", 0, false, RemoteFileStatus.New);
        (await updater.DownloadAsync(new[] { f }, t.Path, null, CancellationToken.None)).Should().ContainSingle().Which.Should().Contain("unexpected signer");
        File.Exists(Path.Combine(t.Path, "PECmd.exe")).Should().BeFalse();
    }

    [Theory]
    [InlineData("PECmd.zip", true)]
    [InlineData("Get-ZimmermanTools.zip", true)]
    [InlineData("C:evil.zip", false)]
    [InlineData("evil.zip:stream", false)]
    [InlineData("..\\evil.zip", false)]
    [InlineData("a/b.zip", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Safe_file_name_check(string name, bool ok) => ToolUpdater.IsSafeFileName(name).Should().Be(ok);

    [Fact]
    public void ParseLinks_skips_names_with_a_colon()
    {
        var html = "<a href=\"https://download.ericzimmermanstools.com/C:evil.zip\">x</a><a href=\"https://download.ericzimmermanstools.com/PECmd.zip\">y</a>";
        ToolUpdater.ParseLinks(html, 0).Should().ContainSingle().Which.Should().EndWith("/PECmd.zip");
    }

    [Fact]
    public async Task Download_refuses_an_unsafe_name_before_any_request()
    {
        using var t = new TempDir();
        var handler = new ZipHandler(ZipWith(("x.txt", "x")));
        var updater = new ToolUpdater(new HttpClient(handler));
        var f = new RemoteToolFile("C:evil.zip", "https://download.ericzimmermanstools.com/C:evil.zip", "e", 0, false, RemoteFileStatus.New);
        await FluentActions.Awaiting(() => updater.DownloadAsync(new[] { f }, t.Path, null, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(0);
    }

    private const string ModernHtml = """
        <html><body>
        <a href="https://download.ericzimmermanstools.com/EvtxECmd.zip">EvtxECmd net4</a>
        <a href="https://download.ericzimmermanstools.com/net/EvtxECmd.zip">EvtxECmd</a>
        <a href="https://download.ericzimmermanstools.com/RECmd.zip">RECmd net4</a>
        <a href="https://download.ericzimmermanstools.com/net/RECmd.zip">RECmd</a>
        <a href="https://download.ericzimmermanstools.com/net/SQLECmd.zip">SQLECmd</a>
        <a href="https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip">script</a>
        </body></html>
        """;

    [Fact]
    public void Modern_index_links_under_net_are_modern_builds()
    {
        ToolUpdater.IsModernUrl("https://download.ericzimmermanstools.com/net/RECmd.zip").Should().BeTrue();
        ToolUpdater.IsModernUrl("https://download.ericzimmermanstools.com/net9/RECmd.zip").Should().BeTrue();
        ToolUpdater.IsModernUrl("https://download.ericzimmermanstools.com/net10/RECmd.zip").Should().BeTrue();
        ToolUpdater.IsModernUrl("https://download.ericzimmermanstools.com/RECmd.zip").Should().BeFalse();
        ToolUpdater.ParseLinks(ModernHtml, 9).Should().BeEquivalentTo(new[]
        {
            "https://download.ericzimmermanstools.com/net/EvtxECmd.zip",
            "https://download.ericzimmermanstools.com/net/RECmd.zip",
            "https://download.ericzimmermanstools.com/net/SQLECmd.zip",
            "https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip",
        });
    }

    [Fact]
    public void PickBuild_prefers_modern_and_flags_a_net4_fallback()
    {
        var all = ToolUpdater.ParseLinks(ModernHtml, 0)
            .Select(u => new RemoteToolFile(ToolUpdater.FileNameOf(u), u, "e", 1, ToolUpdater.IsModernUrl(u), RemoteFileStatus.New)).ToList();
        var recmd = ToolUpdater.PickBuild(all, "RECmd.zip", true, out var fb);
        recmd!.Url.Should().EndWith("/net/RECmd.zip"); fb.Should().BeFalse();
        ToolUpdater.PickBuild(all, "SQLECmd.zip", true, out fb)!.Url.Should().EndWith("/net/SQLECmd.zip");
        ToolUpdater.PickBuild(all, "RECmd.zip", false, out _)!.IsNet9.Should().BeFalse();
        ToolUpdater.PickBuild(all, "SQLECmd.zip", false, out _).Should().BeNull();

        var onlyNet4 = new[] { new RemoteToolFile("Old.zip", "https://download.ericzimmermanstools.com/Old.zip", "e", 1, false, RemoteFileStatus.New) };
        ToolUpdater.PickBuild(onlyNet4, "Old.zip", true, out fb)!.IsNet9.Should().BeFalse();
        fb.Should().BeTrue();
    }

    [Fact]
    public void Stale_net4_exe_is_removed_only_for_managed_exes_in_the_root()
    {
        using var t = new TempDir();
        var staging = Path.Combine(t.Path, "stage"); Directory.CreateDirectory(staging);
        File.Copy(FakeToolPaths.Exe, Path.Combine(staging, "RECmd.exe"));
        File.Copy(FakeToolPaths.Exe, Path.Combine(staging, "Other.exe"));
        File.Copy(FakeToolPaths.Dll, Path.Combine(t.Path, "RECmd.exe"));     // managed net4 copy: removed
        File.WriteAllText(Path.Combine(t.Path, "RECmd.exe.config"), "<configuration/>");
        File.Copy(FakeToolPaths.Exe, Path.Combine(t.Path, "Other.exe"));     // native: kept
        File.WriteAllText(Path.Combine(t.Path, "Shared.dll"), "x");
        var lines = new List<string>();
        ToolUpdater.RemoveStaleNet4Copies(staging, t.Path, "RECmd.zip", lines.Add);
        File.Exists(Path.Combine(t.Path, "RECmd.exe")).Should().BeFalse();
        File.Exists(Path.Combine(t.Path, "RECmd.exe.config")).Should().BeFalse();
        File.Exists(Path.Combine(t.Path, "Other.exe")).Should().BeTrue();
        File.Exists(Path.Combine(t.Path, "Shared.dll")).Should().BeTrue();
        lines.Should().ContainSingle().Which.Should().Contain("RECmd.exe");
    }
}
