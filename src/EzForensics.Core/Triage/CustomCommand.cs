using System.Text.RegularExpressions;
using EzForensics.Core.Commands;

namespace EzForensics.Core.Triage;

/// <summary>Outcome of parsing a user-typed command: the arg tokens (exe removed), blocking errors and hints.</summary>
public sealed record CustomCommandResult(IReadOnlyList<string> Args, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Custom Ingest commands: parsing the typed text into rule args and swapping them into the embedded rules.</summary>
public static partial class CustomCommand
{
    private static readonly HashSet<string> KnownTokens = new(StringComparer.Ordinal)
        { "file", "dir", "out", "root", "softwareHive", "toolDir", "eventIds", "dateFormat", "batchFile" };

    private static readonly Dictionary<string, string> PathFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-f"] = "{file}", ["-d"] = "{dir}", ["--csv"] = "{out}", ["--json"] = "{out}", ["--html"] = "{out}",
        ["--xml"] = "{out}", ["--body"] = "{out}", ["-r"] = "{softwareHive}",
    };

    private static bool LooksLikeFixedPath(string v)
    {
        if (v.StartsWith(@"\\", StringComparison.Ordinal) || Regex.IsMatch(v, @"^[A-Za-z]:")) return true;
        try { return Path.IsPathRooted(v) && v.Length > 1; } catch (ArgumentException) { return false; }
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRx();

    /// <summary>The rule's own command as editable text: <c>ExeName arg arg ...</c> (Windows quoting).</summary>
    public static string DefaultText(TriageRule rule)
        => string.Join(" ", new[] { rule.ExeName }.Concat(rule.Args).Select(BuiltCommand.QuoteWindows));

    public static CustomCommandResult Parse(string? text, TriageRule rule)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var t = (text ?? "").Trim();
        if (t.StartsWith("& ", StringComparison.Ordinal)) t = t[2..].TrimStart();

        var tokens = Tokenize(t).Select(MkapeImporter.Convert).ToList();
        if (tokens.Count > 0 && IsExe(tokens[0], rule)) tokens.RemoveAt(0);

        if (tokens.Count == 0)
        {
            errors.Add(string.IsNullOrWhiteSpace(t) ? "The command is empty." : "No arguments after the executable.");
            return new CustomCommandResult(tokens, errors, warnings);
        }

        var all = string.Join("\n", tokens);
        if (!all.Contains("{out}", StringComparison.Ordinal))
            errors.Add("{out} is required: results are collected from the output folder.");
        foreach (var name in TokenRx().Matches(all).Select(m => m.Groups[1].Value).Distinct())
            if (!KnownTokens.Contains(name)) errors.Add($"Unknown placeholder {{{name}}}.");

        var file = all.Contains("{file}", StringComparison.Ordinal);
        var dir = all.Contains("{dir}", StringComparison.Ordinal);
        if (!file && !dir)
            errors.Add(rule.Mode == RuleMode.Dir
                ? "Add {dir}: the matched folder is not passed to the tool."
                : "Add {file}: the matched file is not passed to the tool.");
        else if (rule.Mode == RuleMode.Dir && file) warnings.Add("This rule matches a folder: {dir} is the matched folder, {file} is the same path.");
        else if (rule.Mode == RuleMode.File && dir && !file) warnings.Add("This rule matches a file: {dir} is that file's folder; use {file} for the file itself.");

        var defaultVars = TokenRx().Matches(string.Join("\n", rule.Args)).Select(m => m.Groups[1].Value).Distinct()
            .Where(n => n is not ("file" or "dir" or "out" or "toolDir"));
        foreach (var v in defaultVars)
        {
            if (all.Contains("{" + v + "}", StringComparison.Ordinal)) continue;
            warnings.Add(v switch
            {
                "softwareHive" => "The default passes the SOFTWARE hive with {softwareHive}; without it the tool cannot resolve some values.",
                _ => $"The default uses {{{v}}}; it is missing here.",
            });
        }

        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var flag = tokens[i];
            if (!PathFlags.TryGetValue(flag, out var suggest)) continue;
            var val = tokens[i + 1];
            if (val.Contains('{') || !LooksLikeFixedPath(val)) continue;
            warnings.Add($"\"{val}\" after {flag} is a fixed path: use {suggest} so each run gets its own.");
        }
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (!tokens[i].Equals("--dt", StringComparison.OrdinalIgnoreCase)) continue;
            var v = tokens[i + 1];
            if (v.Contains("{dateFormat}", StringComparison.Ordinal) || v.Contains('{')) continue;   // value unknown at parse time
            if (!EzForensics.Core.Results.CsvValueParser.IsAnalyzeDateFormat(v)) { warnings.Add(EzForensics.Core.Results.CsvValueParser.DateFormatWarning); break; }
        }
        if (rule.PerMatch && tokens.Any(a => a.Equals("--csvf", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("This rule runs once per match (e.g. per hive/user): a fixed --csvf name makes every run write the same file. Leave --csvf out and EzForensics names each file.");
        return new CustomCommandResult(tokens, errors, warnings);
    }

    /// <summary>Splits on whitespace, honouring "..." and (at a token start) PowerShell '...' quoting; the quotes are removed.</summary>
    private static List<string> Tokenize(string cmd)
    {
        const char Sq = (char)39, Dq = (char)34;
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        char q = (char)0;
        bool has = false;
        for (var i = 0; i < cmd.Length; i++)
        {
            var c = cmd[i];
            if (q != (char)0)
            {
                if (c == q)
                {
                    if (q == Sq && i + 1 < cmd.Length && cmd[i + 1] == Sq) { sb.Append(Sq); i++; }
                    else q = (char)0;
                }
                else sb.Append(c);
            }
            else if (c == Dq || (c == Sq && sb.Length == 0 && !has)) { q = c; has = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (has || sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else sb.Append(c);
        }
        if (has || sb.Length > 0) res.Add(sb.ToString());
        return res;
    }

    private static bool IsExe(string tok, TriageRule rule)
    {
        string name;
        try { name = Path.GetFileName(tok); }
        catch (ArgumentException) { name = tok[(tok.LastIndexOfAny(new[] { '\\', '/' }) + 1)..]; }
        if (name.Length == 0) return false;
        var exe = rule.ExeName;
        return name.Equals(exe, StringComparison.OrdinalIgnoreCase)
            || name.Equals(exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCustom(CustomCommandStore store, string ruleId) => store.Get(ruleId) is not null;

    /// <summary>Swaps in the stored args of every rule that has a valid custom command; invalid ones keep the original and are listed in <paramref name="problems"/>.</summary>
    public static IReadOnlyList<TriageRule> Apply(IReadOnlyList<TriageRule> rules, CustomCommandStore store, ICollection<string>? problems = null)
    {
        var result = new List<TriageRule>(rules.Count);
        foreach (var r in rules)
        {
            var text = store.Get(r.Id);
            if (text == null) { result.Add(r); continue; }
            var parsed = Parse(text, r);
            if (parsed.IsValid) result.Add(Clone(r, parsed.Args.ToList()));
            else
            {
                problems?.Add($"{(string.IsNullOrWhiteSpace(r.Label) ? r.Id : r.Label)}: {parsed.Errors[0]}");
                result.Add(r);
            }
        }
        return result;
    }

    private static TriageRule Clone(TriageRule r, List<string> args) => new()
    {
        Id = r.Id, Label = r.Label, ToolId = r.ToolId, ExeName = r.ExeName, Category = r.Category,
        Paths = r.Paths, Mode = r.Mode, Mask = r.Mask, Recurse = r.Recurse, PerMatch = r.PerMatch,
        Args = args, DefaultSelected = r.DefaultSelected, Exclusive = r.Exclusive, RequiresAdmin = r.RequiresAdmin,
        LiveVolumeMatch = r.LiveVolumeMatch, Output = r.Output,
    };
}
