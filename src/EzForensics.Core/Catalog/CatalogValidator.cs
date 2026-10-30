using System.Text.RegularExpressions;

namespace EzForensics.Core.Catalog;

/// <summary>An option line from System.CommandLine help, e.g. "-f, --file &lt;file&gt;".</summary>
public sealed record HelpOption(IReadOnlyList<string> Aliases, string? ArgumentName, string Description)
{
    public bool IsFlag => ArgumentName is null;
}

/// <summary>Difference between a ToolDefinition and the tool's actual --help output.</summary>
public sealed record CatalogDrift(
    string ToolId,
    IReadOnlyList<string> MissingFromHelp,
    IReadOnlyList<string> MissingFromDefinition)
{
    public bool HasDrift => MissingFromHelp.Count > 0 || MissingFromDefinition.Count > 0;

    public override string ToString() => !HasDrift
        ? $"{ToolId}: no drift"
        : $"{ToolId}: in definition but not in --help: [{string.Join(", ", MissingFromHelp)}]; " +
          $"in --help but not in definition: [{string.Join(", ", MissingFromDefinition)}]";
}

/// <summary>Compares tool definitions against <c>Tool.exe --help</c> output (System.CommandLine format).</summary>
public static class CatalogValidator
{
    private static readonly HashSet<string> BuiltInOptions = new(StringComparer.Ordinal)
    {
        "-?", "-h", "--help", "/?", "/h", "--version",
    };

    // "  -f, --file <file>   Description"  /  "  --csv <csv>   Description"  /  "  --mp   Description"
    private static readonly Regex OptionLine = new(
        @"^\s{1,6}(?<names>-{1,2}[^\s,<]+(?:\s*,\s*-{1,2}[^\s,<]+)*)(?:\s+<(?<arg>[^>]+)>)?(?:\s{2,}(?<desc>.*))?$",
        RegexOptions.Compiled);

    public static IReadOnlyList<HelpOption> ParseHelp(string helpText)
    {
        var result = new List<HelpOption>();
        bool inOptions = false;
        foreach (var raw in helpText.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                // Section header ("Options:", "Usage:", "Commands:" ...).
                inOptions = line.TrimEnd(':').Equals("Options", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOptions) continue;

            var m = OptionLine.Match(line);
            if (!m.Success) continue; // continuation line of a wrapped description
            var aliases = m.Groups["names"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            result.Add(new HelpOption(
                aliases,
                m.Groups["arg"].Success ? m.Groups["arg"].Value : null,
                m.Groups["desc"].Success ? m.Groups["desc"].Value.Trim() : ""));
        }
        return result;
    }

    public static CatalogDrift Validate(ToolDefinition def, string helpText)
    {
        var options = ParseHelp(helpText)
            .Where(o => !o.Aliases.All(BuiltInOptions.Contains))
            .ToList();
        var helpAliases = new HashSet<string>(options.SelectMany(o => o.Aliases), StringComparer.Ordinal);
        var defFlags = new HashSet<string>(def.Parameters.Select(p => p.Flag), StringComparer.Ordinal);

        var missingFromHelp = def.Parameters
            .Select(p => p.Flag)
            .Where(f => !helpAliases.Contains(f))
            .Distinct()
            .ToList();
        var missingFromDef = options
            .Where(o => !o.Aliases.Any(defFlags.Contains))
            .Select(o => string.Join(", ", o.Aliases))
            .ToList();

        return new CatalogDrift(def.Id, missingFromHelp, missingFromDef);
    }
}
