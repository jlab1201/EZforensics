using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class ConceptExtractorTests
{
    [Fact]
    public void Extract_FindsTechniqueArtifactEventId()
    {
        var hits = ConceptExtractor.Extract(new[] { "Mimikatz (T1003.001) dumps LSASS. Check Prefetch for it. Event ID 4624 shows the logon." });

        hits.Should().Contain(h => h.Kind == "technique" && h.Key == "T1003.001");
        hits.Should().Contain(h => h.Kind == "tactic" && h.Label == "Credential Access");
        hits.Should().Contain(h => h.Kind == "artifact" && h.Key == "prefetch");
        hits.Should().Contain(h => h.Kind == "eventid" && h.Key == "4624");
    }

    [Fact]
    public void Extract_PlainNumberList_NoEventId()
    {
        ConceptExtractor.Extract(new[] { "4624 4625 4688" }).Should().NotContain(h => h.Kind == "eventid");
    }

    [Fact]
    public void Extract_CountsOncePerChunk()
    {
        var hits = ConceptExtractor.Extract(new[]
        {
            "Prefetch Prefetch prefetch. Event ID 4624, EID 4624.",
            "Nothing here.",
            "Prefetch again",
        });
        hits.Single(h => h.Key == "prefetch").Count.Should().Be(2);
        hits.Single(h => h.Kind == "eventid" && h.Key == "4624").Count.Should().Be(1);
    }

    [Fact]
    public void Extract_MatchesToolsWholeWordOnly()
    {
        var hits = ConceptExtractor.Extract(new[] { "Run EvtxECmd on the logs. NotEvtxECmdish text." });
        hits.Should().Contain(h => h.Kind == "tool" && h.Key == "evtxecmd");
        ConceptExtractor.Extract(new[] { "xEvtxECmdx" }).Should().NotContain(h => h.Kind == "tool");
    }
}
