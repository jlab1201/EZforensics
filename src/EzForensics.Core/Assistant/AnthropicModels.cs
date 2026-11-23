using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>Anthropic model listing (GET /v1/models) for the Options dropdown and the Test button.</summary>
public static class AnthropicModels
{
    public const string BaseUrl = "https://api.anthropic.com/v1/";

    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, string apiKey, CancellationToken ct)
        => await ListAsync(http, new Uri(BaseUrl), apiKey, ct).ConfigureAwait(false);

    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, Uri baseUrl, string apiKey, CancellationToken ct)
    {
        var url = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        var host = url.Authority;
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url, "models?limit=1000"));
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        string text;
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new AssistantModelException($"{host}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (AssistantModelException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AssistantModelException($"{host}: the request timed out."); }
        catch (HttpRequestException) { throw new AssistantModelException($"could not reach {host}"); }

        var ids = new List<string>();
        try
        {
            if (JsonNode.Parse(text)?["data"] is JsonArray data)
                foreach (var m in data)
                    if (m?["id"]?.GetValue<string>() is { Length: > 0 } id) ids.Add(id);
        }
        catch (JsonException) { throw new AssistantModelException($"{host}: the response was not valid JSON."); }
        return ids;
    }
}
