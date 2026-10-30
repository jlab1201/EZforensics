using EzForensics.App.Services;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The self-update feed: https URLs and local folders only (a plain http feed could hand out any package).</summary>
public sealed class AppUpdaterFeedTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://updates.example.org/ezforensics/")]
    [InlineData(@"C:\Releases\EzForensics")]
    [InlineData(@"\\server\share\releases")]
    public void Accepts_https_and_local_folders(string feed) => AppUpdater.FeedProblem(feed).Should().BeNull();

    [Theory]
    [InlineData("http://updates.example.org/ezforensics/")]
    [InlineData("HTTP://github.com/owner/repo")]
    [InlineData("ftp://updates.example.org/")]
    [InlineData("releases")]
    [InlineData("")]
    public void Refuses_plain_http_other_schemes_and_relative_paths(string feed) => AppUpdater.FeedProblem(feed).Should().NotBeNull();

    [Fact]
    public async Task CheckAsync_refuses_http_feed_before_contacting_it()
    {
        var text = await AppUpdater.CheckAsync("http://updates.example.org/", apply: false);
        text.Should().StartWith("Updates are off").And.Contain("https");
    }
}
