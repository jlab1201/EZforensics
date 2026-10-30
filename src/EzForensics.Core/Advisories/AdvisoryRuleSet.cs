using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Advisories;

/// <summary>When an advisory rule fires.</summary>
public enum AdvisoryTrigger
{
    Always,
    /// <summary>Copy suspected by the heuristics or acquisition declared as a copied collection.</summary>
    CopyDetected,
    /// <summary>Complement of <see cref="CopyDetected"/> (used for the softer "only valid on the original volume" notes).</summary>
    CopyNotDetected,
    LiveAcquisition,
    HiveDirtyReplayed,
    EseDatabaseDirty,
    /// <summary>Evidence was extracted from an archive (<see cref="EvidenceAcquisition.ExtractedArchive"/>); narrow with <see cref="AdvisoryRule.ArchiveCoverage"/>.</summary>
    ArchiveExtracted,
    /// <summary>Memory image that may be internally inconsistent (anything but a VMware snapshot or hibernation file; unknown counts).</summary>
    MemorySmear,
    /// <summary>Kernel-only / active memory dump: user-mode pages are not in the image.</summary>
    MemoryKernelDump,
    /// <summary>RITA data spans less than 24 hours (<see cref="AdvisoryContext.RitaDataSpanHours"/>); beacon detection needs repeated connections over time.</summary>
    RitaShortCapture,
}

/// <summary>How to recognise an artifact's CSV.</summary>
public sealed class AdvisoryArtifact
{
    public string Id { get; set; } = "";
    /// <summary>Other artifact ids (e.g. manifest ids like memory-pslist) that resolve to this artifact; matched before header signatures.</summary>
    public List<string> Aliases { get; set; } = new();
    public string Label { get; set; } = "";
    /// <summary>Plural noun for the parsed inputs ("prefetch files"), used in the copy evidence statement.</summary>
    public string InputNoun { get; set; } = "input files";
    /// <summary>Case-insensitive regexes over the CSV file name (legacy tool names and artifact-style names).</summary>
    public List<string> FileNamePatterns { get; set; } = new();
    /// <summary>Alternative header sets; the artifact matches when every header of any one set is present (case-insensitive).</summary>
    public List<List<string>> HeaderSignatures { get; set; } = new();
}

/// <summary>One data-driven advisory.</summary>
public sealed class AdvisoryRule
{
    public string Id { get; set; } = "";
    /// <summary>Artifact ids the rule applies to; empty or "*" = any CSV (see <see cref="KnownArtifactOnly"/>).</summary>
    public List<string> Artifacts { get; set; } = new();
    public List<string> ExceptArtifacts { get; set; } = new();
    /// <summary>For archiveExtracted rules: TimestampCoverage names the rule applies to (empty = any; unknown coverage counts as ModifiedOnly).</summary>
    public List<string> ArchiveCoverage { get; set; } = new();
    /// <summary>With "*": only fire when the CSV was identified as a known artifact.</summary>
    public bool KnownArtifactOnly { get; set; }
    public AdvisoryTrigger When { get; set; } = AdvisoryTrigger.Always;
    public AdvisorySeverity Severity { get; set; } = AdvisorySeverity.Info;
    public string Title { get; set; } = "";
    /// <summary>Placeholders: {artifact}, {inputNoun}, {copyEvidence} (appended automatically for CopyDetected rules when absent).</summary>
    public string Message { get; set; } = "";
    /// <summary>Column names; a trailing * is a prefix wildcard. Resolved against the CSV headers.</summary>
    public List<string> AffectedColumns { get; set; } = new();
    public List<string> ReliableColumns { get; set; } = new();
    /// <summary>When true (default) and <see cref="AffectedColumns"/> is non-empty, the rule is skipped if none of them is in the CSV.</summary>
    public bool RequireColumns { get; set; } = true;
    public string? ReferenceUrl { get; set; }
}

/// <summary>Strongly typed view of Resources/advisories/rules.json.</summary>
public sealed class AdvisoryRuleSet
{
    public int Version { get; set; } = 1;
    public string? Notes { get; set; }
    /// <summary>Order matters: the first matching artifact wins, so list the more specific signatures first.</summary>
    public List<AdvisoryArtifact> Artifacts { get; set; } = new();
    public List<AdvisoryRule> Rules { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<AdvisoryRuleSet> Embedded = new(LoadEmbeddedCore);

    /// <summary>The rules shipped inside EzForensics.Core (cached).</summary>
    public static AdvisoryRuleSet LoadEmbedded() => Embedded.Value;

    public static AdvisoryRuleSet Parse(string json) =>
        JsonSerializer.Deserialize<AdvisoryRuleSet>(json, JsonOptions) ?? throw new InvalidDataException("Advisory rules JSON is empty.");

    public static AdvisoryRuleSet LoadFromFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>An override JSON on disk when it exists and parses; otherwise the embedded rules.</summary>
    public static AdvisoryRuleSet LoadWithOverride(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            try { return LoadFromFile(overridePath); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { /* fall back */ }
        }
        return LoadEmbedded();
    }

    private static AdvisoryRuleSet LoadEmbeddedCore()
    {
        var asm = typeof(AdvisoryRuleSet).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("advisories.rules.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource advisories/rules.json not found.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public AdvisoryArtifact? FindArtifact(string? id) =>
        id is null ? null : Artifacts.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Artifacts.FirstOrDefault(a => a.Aliases.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Problems in the rule set (duplicate ids, unknown artifact references, bad regexes, missing text).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Artifacts)
        {
            if (string.IsNullOrWhiteSpace(a.Id)) errors.Add("Artifact with empty id.");
            else if (!ids.Add(a.Id)) errors.Add($"Duplicate artifact id '{a.Id}'.");
            if (a.FileNamePatterns.Count == 0 && a.HeaderSignatures.Count == 0 && a.Aliases.Count == 0) errors.Add($"Artifact '{a.Id}' has no way to be detected.");
            foreach (var p in a.FileNamePatterns)
            {
                try { _ = new Regex(p); }
                catch (ArgumentException) { errors.Add($"Artifact '{a.Id}' has an invalid regex '{p}'."); }
            }
        }
        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) errors.Add("Rule with empty id.");
            else if (!ruleIds.Add(r.Id)) errors.Add($"Duplicate rule id '{r.Id}'.");
            if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Message)) errors.Add($"Rule '{r.Id}' needs a title and a message.");
            foreach (var a in r.Artifacts.Concat(r.ExceptArtifacts))
                if (a != "*" && !ids.Contains(a)) errors.Add($"Rule '{r.Id}' references unknown artifact '{a}'.");
        }
        return errors;
    }
}
