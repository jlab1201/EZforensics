using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class DocumentCleanerTests
{
    [Fact]
    public void Clean_RemovesRepeatedHeaderAndPageNumbers()
    {
        var bodies = new[] { "Prefetch files record execution.", "Event logs show logons.", "The MFT tracks every file.", "Registry hives hold persistence." };
        var pages = Enumerable.Range(1, 4)
            .Select(n => $"SANS FOR508 | Page {n}\n{bodies[n - 1]}\n{n}")
            .ToList();
        var text = string.Join("\n", pages);

        var r = DocumentCleaner.Clean(text, "fallback", pages);

        r.Text.Should().NotContain("SANS FOR508");
        r.Text.Should().NotContain("Page 1");
        r.Text.Split('\n').Should().NotContain("2");
        r.Text.Should().Be(string.Join("\n", bodies));
        r.CleanRatio.Should().BeLessThan(1.0).And.BeGreaterThan(0.0);
    }

    [Fact]
    public void Clean_KeepsBodyLinesDifferingOnlyByNumber()
    {
        var pages = Enumerable.Range(1, 4).Select(n => $"Intro {n}x\nEvent ID {4620 + n}\nPage {n}").ToList();
        var r = DocumentCleaner.Clean(string.Join("\n", pages), "t", pages);
        for (var n = 1; n <= 4; n++) r.Text.Should().Contain($"Event ID {4620 + n}");
    }

    [Fact]
    public void Clean_RemovesPageXofY()
    {
        var r = DocumentCleaner.Clean("Hello\nPage 3 of 10\nWorld", "t");
        r.Text.Should().Be("Hello\nWorld");
    }

    [Fact]
    public void Clean_JoinsHyphenation()
    {
        DocumentCleaner.Clean("digital forens-\nics rocks", "t").Text.Should().Be("digital forensics rocks");
    }

    [Fact]
    public void Clean_CollapsesBlankLines_AndTakesHeadingTitle()
    {
        var r = DocumentCleaner.Clean("intro\n\n\n\n\n# My Title\n\nbody", "fallback");
        r.Text.Should().Be("intro\n\n# My Title\n\nbody");
        r.Title.Should().Be("My Title");
        DocumentCleaner.Clean("no heading", "fallback").Title.Should().Be("fallback");
    }

    [Fact]
    public void Clean_ScannedPdfNoText_ReturnsEmpty_RatioZero()
    {
        var r = DocumentCleaner.Clean("", "scan", new[] { "", "", "" });
        r.Text.Should().BeEmpty();
        r.CleanRatio.Should().Be(0);
        r.Title.Should().Be("scan");
    }
}
