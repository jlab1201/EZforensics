using System.Diagnostics;
using EzForensics.Core.Persistence;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class PersistenceHeuristicsTimeoutTests
{
    [Fact]
    public void Hostile_command_line_does_not_hang_apply()
    {
        var sw = Stopwatch.StartNew();
        var e = new PersistenceEntry { Command = string.Concat(Enumerable.Repeat("powershell.exe ", 60_000)) };
        PersistenceHeuristics.Apply(e, null);
        PersistenceHeuristics.ImageFromCommand(new string('a', 300_000));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Encoded_command_is_still_flagged()
    {
        var e = new PersistenceEntry { Command = "powershell.exe -nop -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA" };
        PersistenceHeuristics.Apply(e, null);
        e.Flags.Should().Contain(PersistenceHeuristics.EncodedPowerShell);
    }
}
