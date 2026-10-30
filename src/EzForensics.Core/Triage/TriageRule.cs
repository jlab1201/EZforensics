using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Triage;

public enum RuleMode { File, Dir }

/// <summary>One detection + command rule. Tokens in Args: {file} {dir} {out} {root} {softwareHive} {toolDir}.</summary>
public sealed class TriageRule
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>Globs relative to the system root ('*' and '?' per path segment, case-insensitive).</summary>
    public List<string> Paths { get; set; } = new();
    public RuleMode Mode { get; set; } = RuleMode.File;
    /// <summary>Dir mode: the directory matches only if it contains a file matching this mask.</summary>
    public string? Mask { get; set; }
    public bool Recurse { get; set; }
    public bool PerMatch { get; set; }
    public List<string> Args { get; set; } = new();
    public bool DefaultSelected { get; set; } = true;
    public bool Exclusive { get; set; }
    public bool RequiresAdmin { get; set; }
    /// <summary>When true and the source is a live NTFS volume, a missing match is synthesized (e.g. $MFT).</summary>
    public bool LiveVolumeMatch { get; set; }
    /// <summary>Output naming (case layout v2). Null (e.g. imported .mkape rules) = <see cref="RuleOutput.Default"/>.</summary>
    public RuleOutput? Output { get; set; }

    /// <summary><see cref="Output"/>, or a default built from the tool id / label.</summary>
    public RuleOutput EffectiveOutput => Output ?? RuleOutput.Default(this);
}

/// <summary>How a matched path becomes the qualifier of the output name.</summary>
public enum OutputQualifier
{
    /// <summary>No qualifier (one job per system).</summary>
    None,
    /// <summary>The profile name of <c>Users\&lt;name&gt;\...</c> (<c>LNK_jdoe.csv</c>).</summary>
    User,
    /// <summary>A registry hive: <c>SYSTEM</c>, <c>SOFTWARE</c>, ..., <c>NTUSER_&lt;user&gt;</c>, <c>UsrClass_&lt;user&gt;</c>.</summary>
    Hive,
    /// <summary>The matched file / folder name without extension.</summary>
    File,
}

/// <summary>
/// Output naming spec of a triage rule: files land in <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;\</c> as
/// <c>&lt;Name&gt;[_&lt;Variant&gt;][_&lt;Qualifier&gt;].csv</c> (variants come from <c>ArtifactNaming.RenameTable</c>).
/// </summary>
public sealed class RuleOutput
{
    /// <summary>Artifact base name, PascalCase, no tool prefix (<c>Prefetch</c>, <c>EventLogs</c>, <c>Registry</c>).</summary>
    public string Name { get; set; } = "";
    /// <summary>Default artifact id recorded in the manifest (<c>prefetch</c>, <c>eventlog</c>...).</summary>
    public string ArtifactId { get; set; } = "";
    /// <summary>The tool writes a single CSV: pass <c>--csvf &lt;Name&gt;[_&lt;Qualifier&gt;].csv</c>.</summary>
    public bool Csvf { get; set; }
    public OutputQualifier Qualifier { get; set; } = OutputQualifier.None;

    /// <summary>Default for rules without an <c>output</c> block: name from the tool id (alphanumerics), artifact id from the rule id.</summary>
    public static RuleOutput Default(TriageRule rule)
    {
        var name = Execution.ArtifactNaming.Pascal(string.IsNullOrWhiteSpace(rule.ToolId) ? rule.Label : rule.ToolId);
        if (name.Length == 0 || !char.IsLetter(name[0])) name = "Output" + name;
        return new RuleOutput
        {
            Name = char.ToUpperInvariant(name[0]) + name[1..],
            ArtifactId = Execution.ArtifactNaming.KebabId(rule.Id),
            Qualifier = rule.PerMatch ? OutputQualifier.File : OutputQualifier.None,
        };
    }
}

public static class TriageRules
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class RuleFile { public List<TriageRule> Rules { get; set; } = new(); }

    private static IReadOnlyList<TriageRule>? _embedded;

    /// <summary>Embedded Resources/triage/rules.json (cached).</summary>
    public static IReadOnlyList<TriageRule> LoadEmbedded()
    {
        if (_embedded != null) return _embedded;
        var asm = typeof(TriageRules).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("triage.rules.json", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return _embedded = Parse(r.ReadToEnd());
    }

    public static IReadOnlyList<TriageRule> Parse(string json)
        => JsonSerializer.Deserialize<RuleFile>(json, JsonOptions)?.Rules ?? new List<TriageRule>();

    public static IReadOnlyList<TriageRule> LoadFile(string path) => Parse(File.ReadAllText(path));
}
