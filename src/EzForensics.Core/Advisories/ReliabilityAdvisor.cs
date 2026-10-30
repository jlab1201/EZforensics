using System.Text.RegularExpressions;

namespace EzForensics.Core.Advisories;

/// <summary>
/// Produces forensic timestamp-reliability advisories for one EZ tool CSV (pure; no I/O beyond the rule set).
/// Rules come from Resources/advisories/rules.json (or an override); copy detection lives in <see cref="CopyHeuristics"/>.
/// </summary>
public sealed class ReliabilityAdvisor
{
    private readonly AdvisoryRuleSet _rules;
    private readonly CopyHeuristicOptions _heuristics;
    private readonly Dictionary<string, Regex[]> _nameRegex = new(StringComparer.OrdinalIgnoreCase);

    public ReliabilityAdvisor(AdvisoryRuleSet? rules = null, CopyHeuristicOptions? heuristics = null)
    {
        _rules = rules ?? AdvisoryRuleSet.LoadEmbedded();
        _heuristics = heuristics ?? new CopyHeuristicOptions();
        foreach (var a in _rules.Artifacts)
            _nameRegex[a.Id] = a.FileNamePatterns
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToArray();
    }

    public AdvisoryRuleSet Rules => _rules;

    /// <summary>Advisories only (sorted by severity, highest first).</summary>
    public IReadOnlyList<Advisory> GetAdvisories(AdvisoryContext context) => Evaluate(context).Advisories;

    public AdvisoryReport Evaluate(AdvisoryContext context)
    {
        var artifact = _rules.FindArtifact(context.ArtifactId) ?? Identify(context.Headers, context.CsvFileName);
        var copy = CopyHeuristics.Assess(context.InputTimestampFacts, context.Acquisition,
            artifact?.InputNoun ?? "input files", _heuristics);
        var headers = context.Headers ?? Array.Empty<string>();

        var results = new List<(Advisory Advisory, int Order)>();
        for (var i = 0; i < _rules.Rules.Count; i++)
        {
            var rule = _rules.Rules[i];
            if (!AppliesTo(rule, artifact) || !Triggered(rule.When, context, copy) || !CoverageMatches(rule, context)) continue;

            var affected = Resolve(rule.AffectedColumns, headers);
            if (rule.RequireColumns && rule.AffectedColumns.Count > 0 && affected.Count == 0) continue;

            results.Add((new Advisory
            {
                Id = rule.Id,
                Severity = rule.Severity,
                Title = rule.Title,
                Message = Render(rule, artifact, copy),
                AffectedColumns = affected,
                ReliableColumns = Resolve(rule.ReliableColumns, headers),
                ReferenceUrl = string.IsNullOrWhiteSpace(rule.ReferenceUrl) ? null : rule.ReferenceUrl,
            }, i));
        }

        return new AdvisoryReport
        {
            ArtifactId = artifact?.Id,
            ArtifactLabel = artifact?.Label,
            Copy = copy,
            Advisories = results
                .OrderByDescending(r => r.Advisory.Severity)
                .ThenBy(r => r.Order)
                .Select(r => r.Advisory)
                .ToList(),
        };
    }

    /// <summary>Identify the artifact from the header signature first (most reliable), then from the file name.</summary>
    public AdvisoryArtifact? Identify(IReadOnlyList<string>? headers, string? csvFileName)
    {
        if (headers is { Count: > 0 })
        {
            var set = new HashSet<string>(headers.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var a in _rules.Artifacts)
                if (a.HeaderSignatures.Any(sig => sig.Count > 0 && sig.All(set.Contains)))
                    return a;
        }
        if (!string.IsNullOrWhiteSpace(csvFileName))
        {
            var name = Path.GetFileName(csvFileName.Trim());
            foreach (var a in _rules.Artifacts)
                if (_nameRegex[a.Id].Any(r => r.IsMatch(name)))
                    return a;
        }
        return null;
    }

    private static bool AppliesTo(AdvisoryRule rule, AdvisoryArtifact? artifact)
    {
        var id = artifact?.Id;
        if (id is not null && rule.ExceptArtifacts.Contains(id, StringComparer.OrdinalIgnoreCase)) return false;
        if (rule.Artifacts.Count == 0 || rule.Artifacts.Contains("*"))
            return !rule.KnownArtifactOnly || artifact is not null;
        return id is not null && rule.Artifacts.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Triggered(AdvisoryTrigger when, AdvisoryContext ctx, CopyAssessment copy) => when switch
    {
        AdvisoryTrigger.Always => true,
        AdvisoryTrigger.CopyDetected => copy.Suspected,
        AdvisoryTrigger.CopyNotDetected => !copy.Suspected,
        AdvisoryTrigger.LiveAcquisition => ctx.Acquisition == EvidenceAcquisition.LiveVolume,
        AdvisoryTrigger.HiveDirtyReplayed => ctx.HiveWasDirtyReplayed,
        AdvisoryTrigger.EseDatabaseDirty => ctx.EseDatabaseDirty,
        AdvisoryTrigger.ArchiveExtracted => ctx.Acquisition == EvidenceAcquisition.ExtractedArchive,
        // VMware snapshots and hibernation files are written by a paused / stopped system; a crash dump written at a real bug check
        // is too (all processors frozen), unless it was produced from the running system (acquisition tools, live kernel dumps).
        AdvisoryTrigger.MemorySmear => !string.Equals(ctx.MemoryFormat, "VMware", StringComparison.OrdinalIgnoreCase)
                                       && !string.Equals(ctx.MemoryFormat, "Hibernation", StringComparison.OrdinalIgnoreCase)
                                       && !(string.Equals(ctx.MemoryFormat, "WindowsCrashDump", StringComparison.OrdinalIgnoreCase) && ctx.MemoryLiveDump == false),
        AdvisoryTrigger.MemoryKernelDump => ctx.MemoryDumpKind is { } k
                                            && (k.Equals("Kernel", StringComparison.OrdinalIgnoreCase) || k.Equals("BitmapKernel", StringComparison.OrdinalIgnoreCase)
                                                || k.Equals("Active", StringComparison.OrdinalIgnoreCase)),
        AdvisoryTrigger.RitaShortCapture => ctx.RitaDataSpanHours is { } h && h < 24,
        _ => false,
    };

    private static bool CoverageMatches(AdvisoryRule rule, AdvisoryContext ctx)
    {
        if (rule.ArchiveCoverage.Count == 0) return true;
        var cov = string.IsNullOrWhiteSpace(ctx.ArchiveTimestampCoverage) ? "ModifiedOnly" : ctx.ArchiveTimestampCoverage;
        return rule.ArchiveCoverage.Contains(cov, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map rule column names (trailing * = prefix wildcard) to the CSV's own spelling, in header order.</summary>
    internal static IReadOnlyList<string> Resolve(IReadOnlyList<string> patterns, IReadOnlyList<string> headers)
    {
        if (patterns.Count == 0 || headers.Count == 0) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var raw in headers)
        {
            var h = raw.Trim();
            foreach (var p in patterns)
            {
                var match = p.EndsWith('*')
                    ? h.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
                    : h.Equals(p, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    if (!result.Contains(h, StringComparer.OrdinalIgnoreCase)) result.Add(h);
                    break;
                }
            }
        }
        return result;
    }

    private static string Render(AdvisoryRule rule, AdvisoryArtifact? artifact, CopyAssessment copy)
    {
        var msg = rule.Message
            .Replace("{artifact}", artifact?.Label ?? "this artifact")
            .Replace("{inputNoun}", artifact?.InputNoun ?? "input files");
        if (msg.Contains("{copyEvidence}"))
            msg = msg.Replace("{copyEvidence}", copy.Summary);
        else if (rule.When == AdvisoryTrigger.CopyDetected && copy.Summary.Length > 0)
            msg = msg.TrimEnd() + " " + copy.Summary;
        return Regex.Replace(msg, @"\s{2,}", " ").Trim();
    }
}
