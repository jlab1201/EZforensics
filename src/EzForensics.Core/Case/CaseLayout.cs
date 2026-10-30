using System.Reflection;
using System.Text;
using System.Text.Json;

namespace EzForensics.Core.Case;

/// <summary>How the outputs of a case folder are laid out.</summary>
public enum CaseLayoutVersion
{
    /// <summary>Nothing to go by (empty or foreign folder).</summary>
    Unknown = 0,
    /// <summary>v1: <c>&lt;case&gt;\&lt;Category&gt;\&lt;ToolId&gt;[\&lt;per-match folder&gt;]\&lt;tool's file name&gt;.csv</c>.</summary>
    Legacy = 1,
    /// <summary>v2: <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;[_&lt;Variant&gt;][_&lt;Qualifier&gt;].csv</c> (no tool folders).</summary>
    HostArtifact = 2,
}

/// <summary>Contents of <c>&lt;case&gt;\case.json</c>.</summary>
public sealed class CaseMarker
{
    public string Kind { get; set; } = CaseLayout.MarkerKind;
    public int LayoutVersion { get; set; }
    public string? CreatedUtc { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>The case layout marker: <c>&lt;case&gt;\case.json</c> = <c>{ "kind": "EzForensics.Case", "layoutVersion": 2, ... }</c>.</summary>
public static class CaseLayout
{
    public const string MarkerFileName = "case.json";
    public const string MarkerKind = "EzForensics.Case";
    /// <summary>Layout written by new triage runs.</summary>
    public const int CurrentVersion = (int)CaseLayoutVersion.HostArtifact;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string MarkerPath(string caseDir) => Path.Combine(caseDir, MarkerFileName);

    /// <summary>The marker, or null when absent / unreadable.</summary>
    public static CaseMarker? ReadMarker(string caseDir)
    {
        try
        {
            var p = MarkerPath(caseDir);
            if (!File.Exists(p)) return null;
            var m = JsonSerializer.Deserialize<CaseMarker>(File.ReadAllText(p), Json);
            return m != null && string.Equals(m.Kind, MarkerKind, StringComparison.OrdinalIgnoreCase) ? m : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Writes the marker when the case has none (an existing marker is never rewritten). Atomic.</summary>
    public static void EnsureMarker(string caseDir, int version = CurrentVersion)
    {
        var p = MarkerPath(caseDir);
        if (File.Exists(p)) return;
        Directory.CreateDirectory(caseDir);
        var m = new CaseMarker
        {
            LayoutVersion = version,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
        };
        var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m, Json), new UTF8Encoding(false));
        try { File.Move(tmp, p, overwrite: false); }
        catch (IOException) { File.Delete(tmp); }   // another job wrote it first
    }
}
