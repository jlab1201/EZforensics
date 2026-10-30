using System.Text.RegularExpressions;

namespace EzForensics.Core.Platforms;

/// <summary>
/// A native (in-process, C#) parser for one artifact of one platform. Parsers are stateless and thread safe: the same instance may
/// run for several system roots in parallel. They only read <see cref="ArtifactParseContext.Inputs"/> (and, when needed, other
/// files below <see cref="ArtifactParseContext.SystemRoot"/> through <see cref="EvidencePath"/>), never the analyst's machine, and only
/// write inside <see cref="ArtifactParseContext.OutputDir"/>.
/// </summary>
public interface IArtifactParser
{
    /// <summary>Stable id, lower-case, <c>&lt;platform&gt;-&lt;artifact&gt;</c> (e.g. <c>linux-passwd</c>). Recorded in the run manifest.</summary>
    string Id { get; }

    OsFamily Platform { get; }

    /// <summary>Case sub-folder, e.g. <c>Accounts</c>, <c>EventLogs</c>, <c>Persistence</c> (see docs/LINUX_PARSERS.md).</summary>
    string Category { get; }

    /// <summary>Main output file name: <c>&lt;Artifact&gt;[_&lt;Qualifier&gt;].csv</c>, PascalCase, no tool prefix (e.g. <c>Users.csv</c>, <c>Logins_Failed.csv</c>).</summary>
    string OutputName { get; }

    /// <summary>Input globs relative to the system root, '/' separated (<c>*</c>, <c>?</c>, <c>**</c>; case-insensitive). See <see cref="EvidencePath.Glob"/>.</summary>
    IEnumerable<string> InputGlobs { get; }

    Task<ArtifactParseResult> ParseAsync(ArtifactParseContext ctx, CancellationToken ct);
}

public sealed record ArtifactParseProgress(string? CurrentFile, int FilesDone, int FilesTotal, long Rows);

/// <summary>Everything a parser needs for one run.</summary>
public sealed class ArtifactParseContext
{
    /// <summary>The evidence system root (the folder that plays <c>/</c>).</summary>
    public required string SystemRoot { get; init; }
    /// <summary>The files matched by the parser's <see cref="IArtifactParser.InputGlobs"/>, ordered by relative path.</summary>
    public required IReadOnlyList<EvidenceFile> Inputs { get; init; }
    /// <summary>Folder the parser writes into (created by the caller): <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;</c>.</summary>
    public required string OutputDir { get; init; }
    /// <summary>Host name used for the output layout and any Host column ("Unknown" when not known).</summary>
    public string HostName { get; init; } = ArtifactOutputLayout.UnknownHost;
    /// <summary>Operating-system detection for the root, when the caller has it.</summary>
    public OsDetection? Os { get; init; }
    public IProgress<ArtifactParseProgress>? Progress { get; init; }

    public string OutputPath(string fileName) => Path.Combine(OutputDir, fileName);
}

/// <summary>What a parser produced. Warnings are shown in the job log and recorded in the run manifest; they never fail the run.</summary>
public sealed class ArtifactParseResult
{
    public List<string> OutputFiles { get; } = new();
    public long Rows { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>A parser whose inputs exist in a system root.</summary>
public sealed record ArtifactParserMatch(IArtifactParser Parser, IReadOnlyList<EvidenceFile> Inputs);

/// <summary>Case folder layout for native parsers: <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;.csv</c>.</summary>
public static class ArtifactOutputLayout
{
    public const string UnknownHost = "Unknown";

    private static readonly Regex OutputNamePattern = new(@"^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*\.csv$", RegexOptions.CultureInvariant);
    private static readonly Regex CategoryPattern = new(@"^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);

    /// <summary><c>Users.csv</c>, <c>Logins_Failed.csv</c>, <c>ShellHistory_Bash.csv</c>: yes. <c>linux_users.csv</c>, <c>EzForensics_Users.csv</c> style tool prefixes are rejected by the registry.</summary>
    public static bool IsValidOutputName(string? name) => name != null && OutputNamePattern.IsMatch(name);

    public static bool IsValidCategory(string? category) => category != null && CategoryPattern.IsMatch(category);

    /// <summary>A host name safe as one folder name (invalid characters become '_', empty becomes <see cref="UnknownHost"/>).</summary>
    public static string SafeHostFolder(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return UnknownHost;
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var chars = host.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim('.', ' ');
        if (s.Length == 0) return UnknownHost;
        if (s.Length > 64) s = s[..64];
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, "^(COM|LPT)[0-9]$")) s = "_" + s;   // Windows device names
        return s;
    }

    public static string DirectoryFor(string caseDir, string? hostName, string category) =>
        Path.Combine(caseDir, SafeHostFolder(hostName), category);
}
