using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.Platforms;

namespace EzForensics.Core.Triage;

/// <summary>A checklist entry for the triage UI.</summary>
public sealed record PlannedJob(JobSpec Spec, bool SelectedByDefault, DetectedArtifact Artifact, bool RequiresAdmin);

/// <summary>
/// Turns scan hits into jobs. Case layout v2: every job writes into a private staging folder (<c>&lt;case&gt;\.ezstaging\...</c>, its
/// <c>{out}</c>) and its results are published to <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;[_&lt;Variant&gt;][_&lt;Qualifier&gt;].csv</c>
/// (<see cref="OutputPublisher"/>). The host level is always present, also for a single system.
/// </summary>
public static class TriagePlanner
{
    /// <summary>Plan with the embedded rules.</summary>
    public static IReadOnlyList<JobSpec> Plan(IReadOnlyList<DetectedArtifact> found, string caseDir, string systemRoot)
        => Plan(found, TriageRules.LoadEmbedded(), caseDir, systemRoot);

    public static IReadOnlyList<JobSpec> Plan(IReadOnlyList<DetectedArtifact> found, IReadOnlyList<TriageRule> rules, string caseDir, string systemRoot)
        => PlanJobs(found, rules, caseDir, systemRoot).Select(p => p.Spec).ToList();

    /// <summary>Checklist form; every hit is planned against its own system root.</summary>
    public static IReadOnlyList<PlannedJob> PlanJobs(IReadOnlyList<DetectedArtifact> found, IReadOnlyList<TriageRule> rules, string caseDir)
        => PlanJobs(found, rules, caseDir, "");

    /// <summary>Checklist form: one PlannedJob per spec, with its default selection.</summary>
    /// <param name="systemRoot">Root for hits without <see cref="DetectedArtifact.SystemRoot"/> (legacy callers).</param>
    public static IReadOnlyList<PlannedJob> PlanJobs(IReadOnlyList<DetectedArtifact> found, IReadOnlyList<TriageRule> rules, string caseDir, string systemRoot)
    {
        var byId = rules.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = new List<PlannedJob>();
        var hosts = found.Select(a => HostOf(a, !string.IsNullOrEmpty(a.SystemRoot) ? a.SystemRoot : systemRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var usedQualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in found)
        {
            if (!byId.TryGetValue(a.RuleId, out var rule)) continue;
            var root = !string.IsNullOrEmpty(a.SystemRoot) ? a.SystemRoot : systemRoot;
            var host = HostOf(a, root);
            var output = rule.EffectiveOutput;
            var category = SafeCategory(rule.Category);
            var finalDir = ArtifactOutputLayout.DirectoryFor(caseDir, host, category);

            var qualifier = UniqueQualifier(usedQualifiers, host, rule.Id, QualifierFor(output.Qualifier, a.MatchedPath, root, rule.PerMatch));
            var staging = StagingDir(caseDir, host, rule.Id, qualifier, a.MatchedPath);
            var fixedName = output.Csvf ? Compose(output.Name, qualifier) + ".csv" : null;

            string label = rule.Label;
            if (rule.PerMatch) label = $"{rule.Label}: {RelativeTo(a.MatchedPath, root)}";
            if (hosts > 1) label = $"{host} · {label}";

            var args = rule.Args.Select(t => Expand(t, rule, a.MatchedPath, staging, root)).ToList();
            var userCsvf = args.FindIndex(x => x.Equals("--csvf", StringComparison.OrdinalIgnoreCase));
            if (userCsvf >= 0 && userCsvf + 1 < args.Count && !string.IsNullOrWhiteSpace(args[userCsvf + 1]))
                fixedName = Path.GetFileName(args[userCsvf + 1]);   // custom command names its own CSV
            else if (fixedName != null) args.AddRange(new[] { "--csvf", fixedName });
            var spec = new JobSpec(rule.ToolId, rule.ExeName, args, finalDir, label, category, rule.Exclusive,
                Output: new OutputPlan(staging, output.Name, output.ArtifactId, qualifier, fixedName),
                Provenance: new JobProvenance
                {
                    RuleId = rule.Id,
                    ArtifactId = output.ArtifactId,
                    Host = host,
                    HostNameSource = a.System?.HostNameSource,
                    SystemRoot = root,
                    AcquisitionKind = (a.Acquisition ?? (string.IsNullOrEmpty(root) ? null : HostDiscovery.Kind(root)))?.ToString(),
                    OsFamily = a.System?.OsFamily.ToString(),
                    Inputs = InputsFor(rule, a.MatchedPath, root),
                    Archive = a.System?.Archive,
                });
            list.Add(new PlannedJob(spec, rule.DefaultSelected && a.DefaultSelected, a, a.RequiresAdmin));
        }
        return list;
    }

    /// <summary>Expands tokens. {toolDir} is intentionally left for the job runner (the planner does not know the tool location).</summary>
    public static string Expand(string template, TriageRule rule, string matched, string outDir, string root)
    {
        var dir = rule.Mode == RuleMode.Dir ? matched : (Path.GetDirectoryName(matched) ?? matched);
        var softwareHive = Path.Combine(root, "Windows", "System32", "config", "SOFTWARE");
        return template
            .Replace("{file}", matched, StringComparison.Ordinal)
            .Replace("{dir}", dir, StringComparison.Ordinal)
            .Replace("{out}", outDir, StringComparison.Ordinal)
            .Replace("{root}", root, StringComparison.Ordinal)
            .Replace("{softwareHive}", softwareHive, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ layout v2 helpers (shared with Persistence / Network triage)

    /// <summary>
    /// Host folder for a system: <see cref="ArtifactOutputLayout.SafeHostFolder"/>; a host named like a case-level folder (Timeline, logs,
    /// SystemInfo) or a category gets a <c>Host_</c> prefix so the layout stays unambiguous.
    /// </summary>
    public static string HostFolder(string? host)
    {
        var h = ArtifactOutputLayout.SafeHostFolder(host);
        return CaseIndex.CaseLevelFolders.Contains(h) || CaseIndex.IsKnownCategory(h) || CaseIndex.IsCaseWideFolder(h) ? "Host_" + h : h;
    }

    /// <summary>Host folder of a hit: its discovered system's name, else the root's folder name (no hive read), else <c>HOST</c>.</summary>
    public static string HostOf(DetectedArtifact a, string root) =>
        HostFolder(!string.IsNullOrEmpty(a.Host) ? a.Host
            : string.IsNullOrEmpty(root) ? HostDiscovery.DefaultHostName
            : HostDiscovery.FolderHostName(root) is { } f ? HostDiscovery.Sanitize(f) : HostDiscovery.DefaultHostName);

    /// <summary><c>&lt;Name&gt;[_&lt;Qualifier&gt;]</c>.</summary>
    public static string Compose(string name, string? qualifier) => string.IsNullOrEmpty(qualifier) ? name : name + "_" + qualifier;

    /// <summary>Private staging folder of one job: <c>&lt;case&gt;\.ezstaging\&lt;host&gt;_&lt;rule&gt;[_&lt;qualifier&gt;]_&lt;hash&gt;</c> (stable per host, rule and match).</summary>
    public static string StagingDir(string caseDir, string host, string ruleId, string? qualifier, string matched)
    {
        var key = $"{host}|{ruleId}|{qualifier}|{matched}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..8];
        var name = string.Join("_", new[] { ArtifactNaming.SanitizeQualifier(host), ArtifactNaming.SanitizeQualifier(ruleId), qualifier, hash }
            .Where(s => !string.IsNullOrEmpty(s)));
        if (name.Length > 80) name = name[..71] + "_" + hash;
        return Path.Combine(caseDir, OutputPlan.StagingFolderName, name);
    }

    /// <summary>Qualifier of a matched path (sanitised to <c>[A-Za-z0-9]</c> parts joined by '_').</summary>
    public static string? QualifierFor(OutputQualifier kind, string matched, string root, bool perMatch)
    {
        var rel = RelativeTo(matched, root);
        var segs = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        string? user = segs.Length >= 2 && segs[0].Equals("Users", StringComparison.OrdinalIgnoreCase) ? segs[1] : null;
        string fileName = Path.GetFileName(matched.TrimEnd('\\', '/'));
        switch (kind)
        {
            case OutputQualifier.None:
                return null;
            case OutputQualifier.User:
                return ArtifactNaming.Qualifier(user ?? Path.GetFileNameWithoutExtension(fileName));
            case OutputQualifier.Hive:
                var stem = Path.GetFileNameWithoutExtension(fileName);
                if (stem.Equals("NTUSER", StringComparison.OrdinalIgnoreCase)) return ArtifactNaming.Qualifier("NTUSER", user);
                if (stem.Equals("UsrClass", StringComparison.OrdinalIgnoreCase)) return ArtifactNaming.Qualifier("UsrClass", user);
                var upper = stem.ToUpperInvariant();
                return ArtifactNaming.Qualifier(upper is "SYSTEM" or "SOFTWARE" or "SAM" or "SECURITY" or "DEFAULT" ? upper : stem);
            default:
                return perMatch || kind == OutputQualifier.File ? ArtifactNaming.Qualifier(Path.GetFileNameWithoutExtension(fileName)) : null;
        }
    }

    /// <summary>Second and later uses of one qualifier for the same host and rule get <c>_2</c>, <c>_3</c>...</summary>
    public static string? UniqueQualifier(HashSet<string> used, string host, string ruleId, string? qualifier)
    {
        var baseKey = $"{host}|{ruleId}|";
        if (used.Add(baseKey + qualifier)) return qualifier;
        for (int n = 2; ; n++)
        {
            var q = string.IsNullOrEmpty(qualifier) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{qualifier}_{n}";
            if (used.Add(baseKey + q)) return q;
        }
    }

    /// <summary>Category folder name (alphanumeric, starts upper-case; imported rules may carry spaces).</summary>
    public static string SafeCategory(string? category)
    {
        var c = ArtifactNaming.Pascal(category);
        if (c.Length == 0) return "Other";
        if (!char.IsLetter(c[0])) c = "C" + c;
        return char.ToUpperInvariant(c[0]) + c[1..];
    }

    /// <summary>Inputs whose facts are recorded: the matched file (plus the SOFTWARE hive for SRUM), or the folder's files matching the mask.</summary>
    public static IReadOnlyList<InputSelector> InputsFor(TriageRule rule, string matched, string root)
    {
        if (rule.Mode == RuleMode.File)
        {
            var list = new List<InputSelector> { new(matched) };
            if (rule.Args.Any(a => a.Contains("{softwareHive}", StringComparison.Ordinal)) && !string.IsNullOrEmpty(root))
                list.Add(new InputSelector(Path.Combine(root, "Windows", "System32", "config", "SOFTWARE")));
            return list;
        }
        return string.IsNullOrEmpty(rule.Mask) ? Array.Empty<InputSelector>() : new[] { new InputSelector(matched, rule.Mask, rule.Recurse) };
    }

    private static string RelativeTo(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        var r = root.TrimEnd('\\', '/');
        if (path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
            return path[(r.Length + 1)..];
        return path;
    }
}
