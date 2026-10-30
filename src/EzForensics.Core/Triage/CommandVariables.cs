namespace EzForensics.Core.Triage;

/// <summary>App-managed command variables ({eventIds}, {dateFormat}, {batchFile}): expanded before a job is queued.</summary>
public static class CommandVariables
{
    /// <summary>
    /// Replaces <c>{token}</c> with <paramref name="value"/>. When the value is null/blank every argument containing the token is dropped,
    /// together with the preceding argument when that is one of <paramref name="flags"/> (case-insensitive). Returns the same list when
    /// the token is absent.
    /// </summary>
    public static IReadOnlyList<string> Expand(IReadOnlyList<string> args, string token, string? value, params string[] flags)
    {
        var tok = "{" + token + "}";
        if (!args.Any(a => a.Contains(tok, StringComparison.Ordinal))) return args;
        var blank = string.IsNullOrWhiteSpace(value);
        var res = new List<string>(args.Count);
        foreach (var a in args)
        {
            if (!a.Contains(tok, StringComparison.Ordinal)) { res.Add(a); continue; }
            if (!blank) { res.Add(a.Replace(tok, value, StringComparison.Ordinal)); continue; }
            if (res.Count > 0 && flags.Any(f => f.Equals(res[^1], StringComparison.OrdinalIgnoreCase))) res.RemoveAt(res.Count - 1);
        }
        return res;
    }

    /// <summary>Built-in RECmd batch file; {toolDir} is expanded by the job queue.</summary>
    public const string BuiltInBatchFile = @"{toolDir}\BatchExamples\DFIRBatch.reb";

    /// <summary>The value for {batchFile}: the configured file when it exists, otherwise the built-in one (<paramref name="warn"/> reports a missing file).</summary>
    public static string ResolveBatchFile(string? setting, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(setting)) return BuiltInBatchFile;
        var p = setting.Trim();
        bool exists;
        try { exists = File.Exists(p); } catch (Exception) { exists = false; }
        if (exists) return p;
        warn?.Invoke($"RECmd batch file \"{p}\" was not found; using the built-in DFIRBatch.reb.");
        return BuiltInBatchFile;
    }
}
