using System.Text;

namespace EzForensics.Core.Execution;

/// <summary>
/// The header facts at the top/bottom of a job log ("# EzForensics job", "# Started", "# Command", "# Exit code", "# Finished"). A multi-line
/// command is written as "# Command: &lt;first line&gt;" plus one "#| &lt;line&gt;" continuation per further line, so it can be read back
/// and never leaks into the process output shown as the Log.
/// </summary>
public static class JobLogFormat
{
    /// <summary>First line of every job log, followed by the job id.</summary>
    public const string JobPrefix = "# EzForensics job ";
    /// <summary>The first line of job logs written before the rename (EZ Hub); still recognised as a header line.</summary>
    public const string LegacyJobPrefix = "# EzHub job ";
    public const string CommandPrefix = "# Command: ";
    public const string ContinuationPrefix = "#| ";

    /// <summary>The log lines for the command header (one line for a single-line command).</summary>
    public static IEnumerable<string> CommandHeaderLines(string command)
    {
        var parts = command.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        yield return CommandPrefix + parts[0];
        for (int i = 1; i < parts.Length; i++) yield return ContinuationPrefix + parts[i];
    }

    /// <summary>True for a header line that belongs in the metadata, not in the Log.</summary>
    public static bool IsHeaderLine(string line) =>
        line.StartsWith(JobPrefix, StringComparison.Ordinal)
        || line.StartsWith(LegacyJobPrefix, StringComparison.Ordinal)
        || line.StartsWith("# Started (UTC)", StringComparison.Ordinal)
        || line.StartsWith("# Finished (UTC)", StringComparison.Ordinal)
        || line.StartsWith("# Exit code", StringComparison.Ordinal)
        || line.StartsWith(CommandPrefix, StringComparison.Ordinal)
        || line.StartsWith(ContinuationPrefix, StringComparison.Ordinal) || line == "#|";

    /// <summary>Splits a whole log file into the command (null when absent) and the process output without header lines.</summary>
    public static (string? Command, string Body) Split(string text)
    {
        var cmd = new StringBuilder();
        bool hasCmd = false;
        var body = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith(CommandPrefix, StringComparison.Ordinal)) { hasCmd = true; if (cmd.Length > 0) cmd.Append('\n'); cmd.Append(raw[CommandPrefix.Length..]); }
            else if (raw.StartsWith(ContinuationPrefix, StringComparison.Ordinal)) cmd.Append('\n').Append(raw[ContinuationPrefix.Length..]);
            else if (raw == "#|") cmd.Append('\n');
            else if (!IsHeaderLine(raw)) body.Append(raw).Append('\n');
        }
        var b = body.ToString();
        if (b.EndsWith("\n\n")) b = b[..^1];
        return (hasCmd ? cmd.ToString() : null, b);
    }
}
