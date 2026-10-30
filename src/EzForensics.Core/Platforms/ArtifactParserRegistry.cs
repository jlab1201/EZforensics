using EzForensics.Core.Platforms.Linux;

namespace EzForensics.Core.Platforms;

/// <summary>
/// The set of native artifact parsers. Register validates the contract (unique id, known platform, category and output-name
/// conventions, unique output per platform + category); Discover finds which parsers have inputs in a system root.
/// Thread safe for concurrent reads after registration.
/// </summary>
public sealed class ArtifactParserRegistry
{
    private readonly List<IArtifactParser> _parsers = new();
    private readonly object _gate = new();

    /// <summary>Every built-in parser.</summary>
    public static ArtifactParserRegistry CreateDefault()
    {
        var r = new ArtifactParserRegistry();
        r.Register(new LinuxPasswdParser());
        return r;
    }

    public IReadOnlyList<IArtifactParser> All
    {
        get { lock (_gate) return _parsers.ToList(); }
    }

    public void Register(IArtifactParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (string.IsNullOrWhiteSpace(parser.Id) || parser.Id != parser.Id.ToLowerInvariant() || parser.Id.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Parser id '{parser.Id}' must be lower-case without spaces.", nameof(parser));
        if (parser.Platform == OsFamily.Unknown)
            throw new ArgumentException($"Parser '{parser.Id}' must target a platform.", nameof(parser));
        if (!ArtifactOutputLayout.IsValidCategory(parser.Category))
            throw new ArgumentException($"Parser '{parser.Id}': category '{parser.Category}' must be PascalCase.", nameof(parser));
        if (!ArtifactOutputLayout.IsValidOutputName(parser.OutputName))
            throw new ArgumentException($"Parser '{parser.Id}': output name '{parser.OutputName}' must be <Artifact>[_<Qualifier>].csv.", nameof(parser));
        var globs = parser.InputGlobs?.ToList() ?? new List<string>();
        if (globs.Count == 0 || globs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Parser '{parser.Id}' needs at least one input glob.", nameof(parser));
        if (globs.Any(g => EvidencePath.IsRooted(g) || EvidencePath.Split(g).Contains("..")))
            throw new ArgumentException($"Parser '{parser.Id}': input globs must be relative to the system root and must not use '..'.", nameof(parser));

        lock (_gate)
        {
            if (_parsers.Any(p => string.Equals(p.Id, parser.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"A parser with id '{parser.Id}' is already registered.");
            var clash = _parsers.FirstOrDefault(p => p.Platform == parser.Platform &&
                string.Equals(p.Category, parser.Category, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.OutputName, parser.OutputName, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                throw new InvalidOperationException($"Parsers '{clash.Id}' and '{parser.Id}' would both write {parser.Category}\\{parser.OutputName}.");
            _parsers.Add(parser);
        }
    }

    public IArtifactParser? Find(string id)
    {
        lock (_gate) return _parsers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<IArtifactParser> ForPlatform(OsFamily platform)
    {
        lock (_gate) return _parsers.Where(p => p.Platform == platform).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Parsers of <paramref name="platform"/> with at least one input in <paramref name="systemRoot"/>, with those inputs.</summary>
    public IReadOnlyList<ArtifactParserMatch> Discover(string systemRoot, OsFamily platform, CancellationToken ct = default)
    {
        var list = new List<ArtifactParserMatch>();
        foreach (var p in ForPlatform(platform))
        {
            ct.ThrowIfCancellationRequested();
            var inputs = FindInputs(p, systemRoot);
            if (inputs.Count > 0) list.Add(new ArtifactParserMatch(p, inputs));
        }
        return list;
    }

    /// <summary>Detects the platform of <paramref name="systemRoot"/> and discovers its parsers.</summary>
    public (OsDetection Os, IReadOnlyList<ArtifactParserMatch> Matches) Discover(string systemRoot, CancellationToken ct = default)
    {
        var os = OsFamilyDetector.Detect(systemRoot);
        return (os, os.Family == OsFamily.Unknown ? Array.Empty<ArtifactParserMatch>() : Discover(systemRoot, os.Family, ct));
    }

    public static IReadOnlyList<EvidenceFile> FindInputs(IArtifactParser parser, string systemRoot)
    {
        var all = new SortedDictionary<string, EvidenceFile>(StringComparer.Ordinal);
        foreach (var g in parser.InputGlobs)
            foreach (var f in EvidencePath.Glob(systemRoot, g))
                all.TryAdd(f.RelativePath, f);
        return all.Values.ToList();
    }

    /// <summary>
    /// Runs one match: creates <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;</c> and calls the parser. The host defaults to the
    /// evidence's recorded host name.
    /// </summary>
    public static Task<ArtifactParseResult> RunAsync(ArtifactParserMatch match, string systemRoot, string caseDir, OsDetection? os = null,
        string? hostName = null, IProgress<ArtifactParseProgress>? progress = null, CancellationToken ct = default)
    {
        var host = ArtifactOutputLayout.SafeHostFolder(hostName ?? os?.HostName);
        var outDir = ArtifactOutputLayout.DirectoryFor(caseDir, host, match.Parser.Category);
        Directory.CreateDirectory(outDir);
        var ctx = new ArtifactParseContext
        {
            SystemRoot = systemRoot, Inputs = match.Inputs, OutputDir = outDir, HostName = host, Os = os, Progress = progress,
        };
        return match.Parser.ParseAsync(ctx, ct);
    }
}
