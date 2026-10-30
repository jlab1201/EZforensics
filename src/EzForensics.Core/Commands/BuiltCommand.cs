using System.Text;

namespace EzForensics.Core.Commands;

/// <summary>
/// Result of <see cref="CommandBuilder.Build"/>. <see cref="Args"/> is a pure argv list (no quoting) meant
/// for <c>ProcessStartInfo.ArgumentList</c>; the string forms are for display and copy/paste only.
/// </summary>
public sealed record BuiltCommand(IReadOnlyList<string> Args, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>cmd/CreateProcess-style command line: args with spaces or quotes are double-quoted.</summary>
    public string ToDisplayString(string exePath) =>
        string.Join(" ", new[] { exePath }.Concat(Args).Select(QuoteWindows));

    /// <summary>PowerShell form: <c>&amp; 'exe' 'arg' ...</c> with single-quote escaping (so <c>$MFT</c> stays literal).</summary>
    public string ToPowerShell(string exePath) =>
        "& " + string.Join(" ", new[] { exePath }.Concat(Args).Select(QuotePowerShell));

    /// <summary>
    /// A PowerShell single-quoted string. PowerShell also ends such a string at the typographic quotes ‘ ’ ‚ ‛ (U+2018-U+201B), so
    /// those are doubled too; otherwise a file name such as <c>it’s; calc.exe</c> would run code when the command is pasted.
    /// </summary>
    public static string QuotePowerShell(string arg)
    {
        var sb = new StringBuilder(arg.Length + 2).Append('\'');
        foreach (var c in arg)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Quotes per CommandLineToArgvW rules, only when needed.</summary>
    public static string QuoteWindows(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
