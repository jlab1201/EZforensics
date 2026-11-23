using EzForensics.Core.Settings;
using Xunit;

namespace EzForensics.Core.Tests;

public class AiSettingsTests
{
    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.Equal("ollama", s.AiProvider);
        Assert.Equal("", s.AiModel);
        Assert.Equal("", s.AiBaseUrl);
    }

    [Fact]
    public void RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezai-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Save(new AppSettings { AiProvider = "anthropic", AiModel = "claude-x", AiBaseUrl = "http://h/v1" });
            var back = store.Load();
            Assert.Equal("anthropic", back.AiProvider);
            Assert.Equal("claude-x", back.AiModel);
            Assert.Equal("http://h/v1", back.AiBaseUrl);
        }
        finally { File.Delete(path); }
    }
}
