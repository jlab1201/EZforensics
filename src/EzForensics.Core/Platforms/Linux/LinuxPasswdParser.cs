using System.Globalization;

namespace EzForensics.Core.Platforms.Linux;

/// <summary>
/// Reference native parser: <c>etc/passwd</c> -> <c>&lt;Host&gt;\Accounts\Users.csv</c>.
/// Columns: UserName, Uid, Gid, Gecos, Home, Shell, IsSystemAccount, HasLoginShell.
/// <list type="bullet">
/// <item>IsSystemAccount: UID below <c>UID_MIN</c> from the evidence's <c>etc/login.defs</c> (default 1000), or the overflow /
///   nobody UIDs 65534 and 4294967294.</item>
/// <item>HasLoginShell: the shell is not a known no-login shell (nologin, false, sync, shutdown, halt). An empty shell field means
///   <c>/bin/sh</c> to login(1), so it counts as a login shell.</item>
/// <item>Warnings (hints, not verdicts): UID 0 accounts other than root, a password hash stored in passwd itself, duplicate
///   user names or UIDs, malformed lines, NIS compat lines.</item>
/// </list>
/// </summary>
public sealed class LinuxPasswdParser : IArtifactParser
{
    public const string ParserId = "linux-passwd";
    public static readonly string[] Headers = { "UserName", "Uid", "Gid", "Gecos", "Home", "Shell", "IsSystemAccount", "HasLoginShell" };
    public const long DefaultUidMin = 1000;

    private static readonly string[] NoLoginShellNames = { "nologin", "false", "sync", "shutdown", "halt" };

    public string Id => ParserId;
    public OsFamily Platform => OsFamily.Linux;
    public string Category => "Accounts";
    public string OutputName => "Users.csv";
    public IEnumerable<string> InputGlobs => new[] { "etc/passwd" };

    public async Task<ArtifactParseResult> ParseAsync(ArtifactParseContext ctx, CancellationToken ct)
    {
        var result = new ArtifactParseResult();
        var uidMin = ReadUidMin(ctx.SystemRoot, result.Warnings);
        var inputs = ctx.Inputs;
        if (inputs.Count == 0)
        {
            result.Warnings.Add("No etc/passwd in the evidence.");
            return result;
        }

        using var writer = new ArtifactCsvWriter(ctx.OutputPath(OutputName), Headers);
        int done = 0;
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Progress?.Report(new ArtifactParseProgress(input.RelativePath, done, inputs.Count, writer.Rows));
            if (input.FullPath == null)
            {
                result.Warnings.Add($"{input.RelativePath} is a link to '{input.LinkTarget}' that is not in the evidence; not parsed.");
                done++;
                continue;
            }
            if (input.IsLink) result.Warnings.Add($"{input.RelativePath} is a link to '{input.LinkTarget}'.");

            string text;
            try
            {
                using var fs = new FileStream(input.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{input.RelativePath}: cannot read ({ex.Message}).");
                done++;
                continue;
            }

            foreach (var e in Parse(text, input.RelativePath, uidMin, result.Warnings))
                writer.WriteRow(e.UserName, e.Uid, e.Gid, e.Gecos, e.Home, e.Shell, YesNo(e.IsSystemAccount), YesNo(e.HasLoginShell));
            done++;
        }

        result.Rows = writer.Rows;
        result.OutputFiles.Add(writer.Commit());
        ctx.Progress?.Report(new ArtifactParseProgress(null, done, inputs.Count, result.Rows));
        return result;
    }

    public sealed record PasswdEntry(string UserName, string Uid, string Gid, string Gecos, string Home, string Shell,
        bool? IsSystemAccount, bool HasLoginShell, int LineNumber);

    /// <summary>Parses passwd text (pure; used by the tests). Warnings are appended to <paramref name="warnings"/>.</summary>
    public static IReadOnlyList<PasswdEntry> Parse(string text, string sourceName, long uidMin, List<string> warnings)
    {
        var list = new List<PasswdEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var uids = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (line[0] == '+' || line[0] == '-')
            {
                warnings.Add($"{sourceName}:{lineNo}: NIS compat entry '{line}' (accounts come from NIS/LDAP).");
                continue;
            }
            var f = line.Split(':');
            if (f.Length != 7)
            {
                warnings.Add($"{sourceName}:{lineNo}: expected 7 fields, found {f.Length}; line skipped.");
                continue;
            }

            string user = f[0], pw = f[1], uid = f[2], gid = f[3], gecos = f[4], home = f[5], shell = f[6];
            bool? system = null;
            if (long.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var uidNum))
                system = IsSystemUid(uidNum, uidMin);
            else
                warnings.Add($"{sourceName}:{lineNo}: user '{user}' has a non-numeric UID '{uid}'.");

            if (uidNum == 0 && system != null && user != "root")
                warnings.Add($"{sourceName}:{lineNo}: '{user}' has UID 0 (root privileges).");
            if (pw.Length > 0 && pw != "x" && pw != "*" && !pw.StartsWith('!'))
                warnings.Add($"{sourceName}:{lineNo}: '{user}' has a password hash in passwd instead of shadow.");
            else if (pw.Length == 0)
                warnings.Add($"{sourceName}:{lineNo}: '{user}' has an empty password field (no password).");
            if (!names.Add(user))
                warnings.Add($"{sourceName}:{lineNo}: duplicate user name '{user}'.");
            if (uids.TryGetValue(uid, out var other))
                warnings.Add($"{sourceName}:{lineNo}: '{user}' shares UID {uid} with '{other}'.");
            else uids[uid] = user;

            list.Add(new PasswdEntry(user, uid, gid, gecos, home, shell, system, HasLoginShell(shell), lineNo));
        }
        return list;
    }

    public static bool IsSystemUid(long uid, long uidMin) => uid < uidMin || uid == 65534 || uid == 4294967294;

    public static bool HasLoginShell(string shell)
    {
        var s = shell.Trim();
        if (s.Length == 0) return true;
        var name = s[(s.LastIndexOf('/') + 1)..];
        return !NoLoginShellNames.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>UID_MIN from the evidence's etc/login.defs, else <see cref="DefaultUidMin"/>.</summary>
    public static long ReadUidMin(string systemRoot, List<string>? warnings = null)
    {
        var f = EvidencePath.ResolveFile(systemRoot, "etc/login.defs");
        var text = f == null ? null : EvidencePath.ReadText(f, 256 * 1024);
        if (text == null) return DefaultUidMin;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "UID_MIN")
            {
                if (long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
                warnings?.Add($"etc/login.defs: UID_MIN '{parts[1]}' is not a number; using {DefaultUidMin}.");
                return DefaultUidMin;
            }
        }
        return DefaultUidMin;
    }

    private static string YesNo(bool? b) => b == null ? "" : b.Value ? "Yes" : "No";
}
