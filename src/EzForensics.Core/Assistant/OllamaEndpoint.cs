using System.Net;
using System.Text.RegularExpressions;
using EzForensics.Core.Setup;

namespace EzForensics.Core.Assistant;

/// <summary>Which Ollama server "Use local AI" talks to: the one Options > AI points at, else OLLAMA_HOST, else the local default.</summary>
public static class OllamaEndpoint
{
    /// <summary>Server root without a trailing slash or "/v1" (for example <c>http://localhost:11434</c>).</summary>
    /// <param name="provider">The assistant's provider id; the base URL only counts for "ollama".</param>
    /// <param name="baseUrl">Options > AI base URL.</param>
    /// <param name="ollamaHostEnv">The OLLAMA_HOST variable ("host:port", "host", ":port" or a full URL).</param>
    public static string Resolve(string? provider, string? baseUrl, string? ollamaHostEnv)
    {
        if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(baseUrl))
        {
            var u = baseUrl.Trim().TrimEnd('/');
            if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u = u[..^3];
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return u.TrimEnd('/');
        }
        return FromHostVariable(ollamaHostEnv) ?? AiHardware.OllamaApiRoot;
    }

    /// <summary>OLLAMA_HOST as a URL, or null when it is empty or not usable. "0.0.0.0" and "::" (listen addresses) become localhost.</summary>
    public static string? FromHostVariable(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        var scheme = "http";
        var m = Regex.Match(v, "^(https?)://", RegexOptions.IgnoreCase);
        if (m.Success) { scheme = m.Groups[1].Value.ToLowerInvariant(); v = v[m.Length..]; }
        v = v.Split('/')[0];
        if (v.Length == 0) return null;
        var hasPort = Regex.IsMatch(v, @":\d+$");
        var host = hasPort ? v[..v.LastIndexOf(':')] : v;
        var port = hasPort ? v[(v.LastIndexOf(':') + 1)..] : "11434";
        if (host.Length == 0 || host == "0.0.0.0" || host == "::" || host == "[::]") host = "localhost";
        var root = $"{scheme}://{host}:{port}";
        return Uri.TryCreate(root, UriKind.Absolute, out _) ? root : null;
    }

    /// <summary>The address is this PC (localhost or a loopback IP): only then can the app start Ollama itself.</summary>
    public static bool IsLoopback(string root)
    {
        if (!Uri.TryCreate(root, UriKind.Absolute, out var u)) return false;
        if (string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}
