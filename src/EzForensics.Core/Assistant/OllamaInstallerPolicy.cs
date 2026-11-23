using EzForensics.Core.Setup;

namespace EzForensics.Core.Assistant;

/// <summary>Pure checks applied to the downloaded Ollama installer before it is run.</summary>
public static class OllamaInstallerPolicy
{
    private static readonly string[] Hosts =
    {
        "ollama.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>True for an https URL on ollama.com (or a subdomain) or one of the GitHub release hosts.</summary>
    public static bool IsAllowedDownloadHost(Uri? finalUri)
    {
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps) return false;
        var host = finalUri.Host.ToLowerInvariant();
        return host.EndsWith(".ollama.com", StringComparison.Ordinal) || Array.IndexOf(Hosts, host) >= 0;
    }

    /// <summary>Null when the signature is valid and the signer is Ollama; else the reason the installer must not run.</summary>
    public static string? SignatureProblem(ScriptSignature sig)
    {
        if (!string.Equals(sig.Status, "Valid", StringComparison.Ordinal))
            return $"its Authenticode signature is not valid (status: {sig.Status})";
        if (sig.SignerSubject is null || !SignerIsOllama(sig.SignerSubject))
            return $"it is not signed by Ollama (signer: {sig.SignerSubject ?? "none"})";
        return null;
    }

    /// <summary>The subject's CN or O contains "Ollama".</summary>
    public static bool SignerIsOllama(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var kv = part.Trim();
            if ((kv.StartsWith("CN=", StringComparison.Ordinal) || kv.StartsWith("O=", StringComparison.Ordinal))
                && kv.Contains("Ollama", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
