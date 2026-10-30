using EzForensics.Core.Results;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class EvidenceKindTests
{
    [Theory]
    [InlineData("EventLogs", EvidenceKind.EventLogs)]
    [InlineData("programexecution", EvidenceKind.ProgramExecution)]
    [InlineData("SRUMDatabase", EvidenceKind.Database)]
    [InlineData("SQLDatabases", EvidenceKind.Database)]
    [InlineData("Network", EvidenceKind.Network)]
    [InlineData("Persistence", EvidenceKind.Persistence)]
    [InlineData("Timeline", EvidenceKind.Timeline)]
    [InlineData("WORKSTATION01", EvidenceKind.Other)]
    [InlineData("EvtxECmd", EvidenceKind.Other)]
    public void ForFolder_maps_category_folders(string name, EvidenceKind expected) =>
        EvidenceKinds.ForFolder(name).Should().Be(expected);
}
