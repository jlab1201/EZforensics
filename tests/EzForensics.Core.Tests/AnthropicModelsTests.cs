using System.Net;
using EzForensics.Core.Assistant;
using Xunit;

namespace EzForensics.Core.Tests;

public class AnthropicModelsTests
{
    private sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body), ReasonPhrase = "x" });
        }
    }

    [Fact]
    public async Task ListsIdsWithHeaders()
    {
        var h = new Fake(HttpStatusCode.OK, "{\"data\":[{\"id\":\"claude-a\"},{\"id\":\"claude-b\"}]}");
        var ids = await AnthropicModels.ListAsync(new HttpClient(h), "k-123", default);
        Assert.Equal(new[] { "claude-a", "claude-b" }, ids);
        Assert.Equal("k-123", h.Seen!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", h.Seen.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("api.anthropic.com", h.Seen.RequestUri!.Host);
        Assert.StartsWith("/v1/models", h.Seen.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task UnauthorizedDoesNotLeakKey()
    {
        var h = new Fake(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key SECRETKEY\"}}");
        var ex = await Assert.ThrowsAsync<AssistantModelException>(() => AnthropicModels.ListAsync(new HttpClient(h), "SECRETKEY", default));
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("SECRETKEY", ex.Message);
    }
}
