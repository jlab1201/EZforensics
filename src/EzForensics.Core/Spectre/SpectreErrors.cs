namespace EzForensics.Core.Spectre;

/// <summary>Turns Spectre failures into one short plain sentence for the UI. Raw replies and JSON never reach the screen.</summary>
public static class SpectreErrors
{
    /// <summary>Where the full detail goes (the app sets this to its log).</summary>
    public static Action<string>? Log { get; set; }

    public const string Suffix = " Details are in app.log.";

    /// <summary>The engine does not have the tool that was called (an older Spectre).</summary>
    public static bool IsUnknownTool(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("unknown tool", StringComparison.OrdinalIgnoreCase) || msg.Contains("tool not found", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("no such tool", StringComparison.OrdinalIgnoreCase) || msg.Contains("method not found", StringComparison.OrdinalIgnoreCase);
    }

    public static string Friendly(Exception ex)
    {
        var msg = ex.Message ?? "";
        string s;
        if (ex is SpectreToolException { RawReply: not null } || msg.StartsWith("Unreadable reply", StringComparison.OrdinalIgnoreCase))
            s = "Spectre sent a reply this app could not read.";
        else if (IsUnknownTool(ex))
            return "This Spectre version can't do this yet. Update Spectre on the Tools page." + Suffix;
        else if (ex is TimeoutException || ex is OperationCanceledException || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            s = "Spectre took too long to answer.";
        else if (ex is IOException && (msg.Contains("not available", StringComparison.OrdinalIgnoreCase) || msg.Contains("stopped", StringComparison.OrdinalIgnoreCase)))
            s = "Spectre is not available right now.";
        else
            s = FirstLine(msg);
        return s + Suffix;
    }

    private static string FirstLine(string msg)
    {
        var line = msg.Split('\n', '\r').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        // a raw reply pasted into the message ("error: {...}") is cut off; only the words before it are shown
        var raw = line.IndexOfAny(new[] { '{', '[', '<' });
        if (raw >= 0) line = line[..raw].TrimEnd(' ', ':', '-', '(', '=', ',');
        if (line.Length < 4) return "Spectre reported a problem.";
        if (line.Length > 160) line = line[..157].TrimEnd() + "...";
        return line.EndsWith('.') ? line : line + ".";
    }
}
