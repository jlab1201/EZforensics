using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Settings;

namespace EzForensics.App.Services;

/// <summary>
/// Assistant API keys, one per provider, encrypted with DPAPI (current Windows user) in <c>ai-keys\&lt;provider&gt;.bin</c> under the data folder.
/// A key is never logged, put in an exception message or shown in the UI.
/// </summary>
public sealed class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EzForensics.AiKey.v1");
    private readonly string _dir;

    public ApiKeyStore() : this(Path.Combine(AppPaths.DataRoot, "ai-keys")) { }
    public ApiKeyStore(string directory) { _dir = directory; }

    public void Save(string provider, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { Delete(provider); return; }
        Directory.CreateDirectory(_dir);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FileFor(provider), cipher);
    }

    public string? TryLoad(string provider)
    {
        try
        {
            var path = FileFor(provider);
            if (!File.Exists(path)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Has(string provider) => File.Exists(FileFor(provider));

    public void Delete(string provider)
    {
        try { File.Delete(FileFor(provider)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public override string ToString() => nameof(ApiKeyStore);

    private string FileFor(string provider)
    {
        var name = new string((provider ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (name.Length == 0) throw new ArgumentException("Provider name is empty.");
        return Path.Combine(_dir, name + ".bin");
    }
}
