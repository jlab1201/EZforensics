using System.IO;
using System.Text;
using EzForensics.App.Services;
using Xunit;

namespace EzForensics.App.Tests;

public class ApiKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezkeys-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RoundTripNoPlaintextAndDelete()
    {
        var s = new ApiKeyStore(_dir);
        Assert.False(s.Has("anthropic"));
        Assert.Null(s.TryLoad("anthropic"));
        s.Save("anthropic", "sk-ant-SECRET-123");
        Assert.True(s.Has("anthropic"));
        Assert.False(s.Has("openai"));
        Assert.Equal("sk-ant-SECRET-123", s.TryLoad("anthropic"));
        foreach (var f in Directory.GetFiles(_dir))
            Assert.DoesNotContain("SECRET", Encoding.Latin1.GetString(File.ReadAllBytes(f)));
        Assert.DoesNotContain("SECRET", s.ToString());
        s.Delete("anthropic");
        Assert.False(s.Has("anthropic"));
        Assert.Null(s.TryLoad("anthropic"));
    }
}
