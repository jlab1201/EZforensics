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
        File.ReadAllLines(csv)[0].Should().Be("\"Name\",\"SHA1\",\"URL\",\"Size\",\"IsNet9\"");
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
}
