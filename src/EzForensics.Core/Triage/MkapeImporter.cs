using System.Text;
using YamlDotNet.Serialization;

namespace EzForensics.Core.Triage;

/// <summary>
/// Basic converter from KAPE .mkape modules (MIT, KapeFiles) to triage rules.
/// Only the Processors section is read. %sourceDirectory% -> {dir}, %sourceFile% -> {file},
/// %destinationDirectory% -> {out}, %binaryDirectory% -> {toolDir}. Source paths are not part of
/// .mkape, so imported rules have empty Paths (the caller must assign them) and are not selected by default.
/// </summary>
public static class MkapeImporter
{
    private sealed class Module
    {
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Id { get; set; }
        public List<Processor>? Processors { get; set; }
    }

    private sealed class Processor
    {
        public string? Executable { get; set; }
        public string? CommandLine { get; set; }
        public string? ExportFormat { get; set; }
    }

    public static IReadOnlyList<TriageRule> Parse(string yaml, string? fallbackName = null)
    {
        var de = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var mod = de.Deserialize<Module>(yaml) ?? new Module();
        var rules = new List<TriageRule>();
        int n = 0;
        foreach (var p in mod.Processors ?? new())
        {
            if (string.IsNullOrWhiteSpace(p.Executable)) continue;
            var exe = Path.GetFileName(p.Executable.Trim().Trim('"'));
            var toolId = Path.GetFileNameWithoutExtension(exe);
            var args = Tokenize(p.CommandLine ?? "").Select(Convert).ToList();
            var baseId = mod.Id ?? fallbackName ?? toolId;
            rules.Add(new TriageRule
            {
                Id = n == 0 ? $"mkape:{toolId}:{baseId}" : $"mkape:{toolId}:{baseId}:{n}",
                Label = (mod.Description ?? toolId).Trim(),
                ToolId = toolId,
                ExeName = exe,
                Category = string.IsNullOrWhiteSpace(mod.Category) ? "Imported" : mod.Category!,
                Mode = args.Any(a => a.Contains("{file}")) ? RuleMode.File : RuleMode.Dir,
                Args = args,
                DefaultSelected = false,
            });
            n++;
        }
        return rules;
    }

    public static IReadOnlyList<TriageRule> ImportFolder(string folder)
    {
        var all = new List<TriageRule>();
        foreach (var f in Directory.EnumerateFiles(folder, "*.mkape", SearchOption.AllDirectories))
        {
            try { all.AddRange(Parse(File.ReadAllText(f), Path.GetFileNameWithoutExtension(f))); }
            catch (Exception ex) when (ex is IOException or YamlDotNet.Core.YamlException) { }
        }
        return all;
    }

    public static string Convert(string token)
        => token
            .Replace("%sourceDirectory%", "{dir}", StringComparison.OrdinalIgnoreCase)
            .Replace("%sourceFile%", "{file}", StringComparison.OrdinalIgnoreCase)
            .Replace("%destinationDirectory%", "{out}", StringComparison.OrdinalIgnoreCase)
            .Replace("%binaryDirectory%", "{toolDir}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a command line on whitespace, honouring double quotes (quotes are removed).</summary>
    public static List<string> Tokenize(string cmd)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false, has = false;
        foreach (var c in cmd)
        {
            if (c == '"') { inQ = !inQ; has = true; }
            else if (char.IsWhiteSpace(c) && !inQ)
            {
                if (has || sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else sb.Append(c);
        }
        if (has || sb.Length > 0) res.Add(sb.ToString());
        return res;
    }
}
