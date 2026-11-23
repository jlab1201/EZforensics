using EzForensics.Core.Assistant;
using Xunit;

namespace EzForensics.Core.Tests;

public class McpReplyCapTests
{
    [Fact]
    public void Short_reply_is_unchanged() => Assert.Equal("abc", McpClient.CapReply("abc"));

    [Fact]
    public void Huge_reply_is_capped_with_a_marker()
    {
        var s = McpClient.CapReply(new string('x', McpClient.MaxReplyChars + 500));
        Assert.True(s.Length < McpClient.MaxReplyChars + 200);
        Assert.Contains("truncated", s);
        Assert.Contains("500", s);
    }
}
