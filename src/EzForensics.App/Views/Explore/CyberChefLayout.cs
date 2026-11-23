using System.Text.Json;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views.Explore;

/// <summary>Persisted CyberChef window bounds (<c>cyberchef-layout.json</c> in the data root; AppSettings lives in Core).</summary>
public sealed class CyberChefLayout
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "cyberchef-layout.json");

    public static CyberChefLayout Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (File.Exists(path)) return JsonSerializer.Deserialize<CyberChefLayout>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
