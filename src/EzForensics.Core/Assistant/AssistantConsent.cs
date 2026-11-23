using System.Text.Json;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Assistant;

/// <summary>
/// Consent to send case content to a cloud model provider. Not needed for local providers.
/// Stored in the user's data root (<c>assistant-consent.json</c>), never in the case folder: a case received from someone else
/// must not be able to pre-approve sending its data anywhere. Keyed by the full case path, the provider and (for a custom
/// provider) the base URL, so a changed address asks again. A null/empty case path is the "no case open" scope.
/// </summary>
public static class AssistantConsent
{
    /// <summary>The case-level hidden folder (Spectre's case link lives here). It no longer holds consent.</summary>
    public const string FolderName = ".ezassistant";
    public const string StoreFileName = "assistant-consent.json";

    private sealed record Entry(string Case, string Provider, string BaseUrl, DateTime GrantedUtc, string User, string Model);

    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly object Gate = new();

    private static string StorePath(string? storeRoot) => Path.Combine(storeRoot ?? AppPaths.DataRoot, StoreFileName);

    internal static string NormalizeCase(string? caseDir)
    {
        if (string.IsNullOrWhiteSpace(caseDir)) return "";
        try { return Path.GetFullPath(caseDir).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch (Exception) { return caseDir.Trim().ToLowerInvariant(); }
    }

    private static string NormalizeUrl(string provider, string? baseUrl) =>
        string.Equals(provider, AiProviders.Custom, StringComparison.OrdinalIgnoreCase) ? (baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant() : "";

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Opts) ?? new List<Entry>();
        }
        catch (Exception) { return new List<Entry>(); }
    }

    /// <summary>True when consent for <paramref name="provider"/> (and, for a custom provider, <paramref name="baseUrl"/>) was granted for this case.</summary>
    public static bool Has(string? caseDir, string provider, string? baseUrl = null, string? storeRoot = null)
    {
        var key = NormalizeCase(caseDir);
        var url = NormalizeUrl(provider, baseUrl);
        lock (Gate)
            return Load(StorePath(storeRoot)).Any(e => e.Case == key && e.BaseUrl == url && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Records the examiner's consent in the user's data root.</summary>
    public static void Grant(string? caseDir, string provider, string model, string? baseUrl = null, string? storeRoot = null)
    {
        var key = NormalizeCase(caseDir);
        var url = NormalizeUrl(provider, baseUrl);
        var path = StorePath(storeRoot);
        lock (Gate)
        {
            var all = Load(path);
            all.RemoveAll(e => e.Case == key && e.BaseUrl == url && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase));
            all.Add(new Entry(key, provider, url, DateTime.UtcNow, Environment.UserName, model));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Opts));
        }
    }
}
