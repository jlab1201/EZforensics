using System.Text.RegularExpressions;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Catches Spectre growing a new external tool: every binary Spectre's modules declare (module.yaml tools / optional_tools) or config.py
/// resolves (_which_or) must be in <see cref="SpectreEngineTools"/>, with the same required / optional flag. Skipped when the Spectre
/// repo is not around (<c>EZFORENSICS_SPECTRE_SRC</c> or a sibling "Spectre" folder).
/// </summary>
/// <summary>A fact that shows as skipped (not passed) when the Spectre repo is missing.</summary>
public sealed class SpectreRepoFactAttribute : FactAttribute
{
    public SpectreRepoFactAttribute()
    {
        if (SpectreEngineSetup.FindSource() == null) Skip = "Spectre repo not found (set EZFORENSICS_SPECTRE_SRC or check out a sibling 'Spectre' folder)";
    }
}

public class SpectreEngineToolsDriftTests
{
    [SpectreRepoFact]
    public void Every_tool_Spectre_declares_is_in_the_manifest()
    {
        var repo = SpectreEngineSetup.FindSource();
        repo.Should().NotBeNull();
        var problems = new List<string>();

        foreach (var yaml in Directory.GetFiles(Path.Combine(repo, "src", "spectre", "modules"), "module.yaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(yaml);
            var module = Regex.Match(text, @"^name:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
            foreach (var (key, required) in new[] { ("tools", true), ("optional_tools", false) })
                foreach (var bin in List(text, key))
                    Check(bin, required, $"module '{module}' ({key} in {Path.GetRelativePath(repo, yaml)})", problems);
        }

        var config = File.ReadAllText(Path.Combine(repo, "src", "spectre", "config.py"));
        foreach (Match m in Regex.Matches(config, @"_which_or\(\s*""([^""]+)"""))
            Check(m.Groups[1].Value, null, "src/spectre/config.py (_which_or)", problems);
        if (Regex.IsMatch(config, @"def _resolve_zeek")) Check("zeek", null, "src/spectre/config.py (_resolve_zeek)", problems);
        if (Regex.IsMatch(config, @"MEMPROCFS_BIN")) Check("memprocfs", null, "src/spectre/config.py (MEMPROCFS_BIN)", problems);

        problems.Should().BeEmpty("add or fix the entry in src/EzForensics.Core/Spectre/SpectreEngineTools.cs (id, binaries, modules, Required, install kind, probe)");
    }

    /// <summary>required: true = listed under tools, false = under optional_tools, null = only has to exist.</summary>
    private static void Check(string bin, bool? required, string where, List<string> problems)
    {
        var tool = SpectreEngineTools.All.FirstOrDefault(t => t.Binaries.Contains(bin, StringComparer.OrdinalIgnoreCase));
        if (tool == null) problems.Add($"'{bin}' is used by {where} but no tool in SpectreEngineTools provides it: add a tool with Binaries containing \"{bin}\".");
        else if (required is { } r && tool.Required != r)
            problems.Add($"'{bin}' is {(r ? "required" : "optional")} in {where} but '{tool.Id}' is {(tool.Required ? "Required" : "Optional")} in SpectreEngineTools: set Required = {r.ToString().ToLowerInvariant()}.");
    }

    /// <summary>The items of <c>key: [a, b]</c> (inline list on one line).</summary>
    private static IEnumerable<string> List(string yaml, string key)
    {
        var m = Regex.Match(yaml, @"^" + key + @":\s*\[([^\]]*)\]", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Trim('"', '\'')) : Array.Empty<string>();
    }
}
