using EzForensics.Core.Common;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class PluralTests
{
    [Theory]
    [InlineData(0, "0 documents")]
    [InlineData(1, "1 document")]
    [InlineData(2, "2 documents")]
    public void Of_uses_singular_only_for_one(long n, string expected) =>
        Plural.Of(n, "document").Should().Be(expected);

    [Fact]
    public void Of_accepts_irregular_plural()
    {
        Plural.Of(1, "entry", "entries").Should().Be("1 entry");
        Plural.Of(3, "entry", "entries").Should().Be("3 entries");
    }
}
