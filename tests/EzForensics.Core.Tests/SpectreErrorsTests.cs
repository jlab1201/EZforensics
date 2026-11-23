using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreErrorsTests
{
    [Fact]
    public void Unknown_tool_asks_for_an_update()
    {
        SpectreErrors.Friendly(new SpectreToolException("spectre_timeline", "Unknown tool: spectre_timeline"))
            .Should().Be("This Spectre version can't do this yet. Update Spectre on the Tools page. Details are in app.log.");
    }

    [Fact]
    public void Timeout_unreadable_and_unavailable_are_one_short_sentence()
    {
        SpectreErrors.Friendly(new TimeoutException("MCP tools/call timed out after 300 s")).Should().Be("Spectre took too long to answer. Details are in app.log.");
        SpectreErrors.Friendly(new SpectreToolException("t", "Unreadable reply.", "{\"a\":1}")).Should().Be("Spectre sent a reply this app could not read. Details are in app.log.");
        SpectreErrors.Friendly(new IOException("Spectre is not available.")).Should().Be("Spectre is not available right now. Details are in app.log.");
        SpectreErrors.Friendly(new IOException("Spectre stopped")).Should().Contain("not available");
    }

    [Fact]
    public void Other_errors_keep_the_first_line_only_capped_and_never_json()
    {
        var s = SpectreErrors.Friendly(new SpectreToolException("t", "Case is locked\nTraceback blah blah"));
        s.Should().Be("Case is locked. Details are in app.log.");
        var long160 = SpectreErrors.Friendly(new InvalidOperationException(new string('a', 500)));
        long160.Length.Should().BeLessThan(160 + SpectreErrors.Suffix.Length + 2);
        SpectreErrors.Friendly(new SpectreToolException("t", "{\"error\":\"x\"}")).Should().NotContain("{");
    }
}
