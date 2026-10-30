using System.Text.RegularExpressions;

namespace EzForensics.Core.Persistence;

/// <summary>
/// Simple, explainable red flags for an autostart entry. They are hints for triage, never verdicts: legitimate software
/// also lives in AppData or ProgramData, and administrators use PowerShell.
/// </summary>
public static class PersistenceHeuristics
{
    public const string UserWritable = "UserWritablePath", EncodedPowerShell = "EncodedPowerShell", NetworkPath = "NetworkPath",
        ImageMissing = "ImageMissingInSource", ScriptHost = "ScriptOrLolbin", HiddenTask = "HiddenTask", TaskXmlMissing = "TaskXmlMissing",
        IfeoDebugger = "IFEODebugger", SilentExit = "SilentProcessExitMonitor", AppInitLoaded = "AppInitEnabled", ComShadowsHklm = "ShadowsHKLMClass",
        WmiConsumer = "WMIConsumer", NonDefaultValue = "NonDefaultValue";

    /// <summary>Command lines come from untrusted evidence: a match that runs longer than this counts as no match.</summary>
    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(250);

    private static bool Is(Regex rx, string text)
    {
        try { return rx.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static readonly Regex UserWritableRx = new(
        @"(\\AppData\\|\\Temp\\|\\Tmp\\|\\ProgramData\\|\\Users\\Public\\|\\Downloads\\|\\Desktop\\|\\\$Recycle\.Bin\\|\\PerfLogs\\|\\Windows\\Tasks\\|%temp%|%tmp%|%appdata%|%localappdata%|%programdata%|%public%|%userprofile%\\(Downloads|Desktop|AppData))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RxTimeout);

    private static readonly Regex EncodedRx = new(
        @"(powershell|pwsh)(\.exe)?\b.*\s-(e|ec|en|enc|enco|encod|encode|encoded|encodedc|encodedcommand)\s+[A-Za-z0-9+/=]{16,}|FromBase64String",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RxTimeout);

    private static readonly Regex NetworkRx = new(@"(^|[\s""'=])\\\\[\w.\-$]+|https?://|ftp://", RegexOptions.IgnoreCase | RegexOptions.Compiled, RxTimeout);

    /// <summary>Script hosts and LOLBins often abused to start a payload from an autostart entry.</summary>
    public static readonly string[] Lolbins =
    {
        "rundll32", "regsvr32", "mshta", "wscript", "cscript", "powershell", "pwsh", "certutil", "bitsadmin", "installutil", "regasm", "regsvcs",
        "msbuild", "forfiles", "wmic", "msxsl", "odbcconf", "cmstp", "hh", "scriptrunner", "pcalua", "conhost", "msiexec", "cmd",
    };

    private static readonly Regex LolbinRx = new(
        @"(^|[\\/\s""'])(" + string.Join("|", Lolbins.Select(Regex.Escape)) + @")(\.exe)?([\s""',]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RxTimeout);

    /// <summary>Adds the generic flags for a command line / image path. <paramref name="checker"/> enables the missing-image check.</summary>
    public static void Apply(PersistenceEntry e, OfflineChecker? checker)
    {
        var text = (e.Command + " " + e.ImagePath).Trim();
        if (text.Length == 0) return;
        if (Is(UserWritableRx, text)) e.Flag(UserWritable);
        if (Is(EncodedRx, text)) e.Flag(EncodedPowerShell);
        if (Is(NetworkRx, text)) e.Flag(NetworkPath);
        Match m;
        try { m = LolbinRx.Match(text); }
        catch (RegexMatchTimeoutException) { m = Match.Empty; }
        if (m.Success && !IsBenignCmd(m.Groups[2].Value, text) && !IsWindowsHostedDll(m.Groups[2].Value, e.Command))
            e.Flag(ScriptHost + ":" + m.Groups[2].Value.ToLowerInvariant());
        if (checker != null && !string.IsNullOrEmpty(e.ImagePath) && checker.IsMissing(e.ImagePath, e.User)) e.Flag(ImageMissing);
    }

    private static readonly Regex WindowsHostRx = new(
        @"^""?(%windir%|%systemroot%|C:\\Windows)\\(system32|syswow64)\\(rundll32|msiexec|conhost|regsvr32|svchost)\.exe""?(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RxTimeout);
    private static readonly Regex PathRx = new(@"[A-Za-z]:\\|%[A-Za-z()0-9]+%\\|\\\\", RegexOptions.Compiled, RxTimeout);
    private static readonly Regex WindowsPathRx = new(@"^(%windir%|%systemroot%|C:\\Windows)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled, RxTimeout);

    /// <summary>rundll32 / msiexec / regsvr32 started from the Windows folder on a DLL that is also in the Windows folder (or a bare
    /// name, loaded from System32): how Windows itself uses them, so not flagged. Anything else (user paths, URLs, scripts) still is.</summary>
    public static bool IsWindowsHostedDll(string bin, string command)
    {
        if (bin is not ("rundll32" or "msiexec" or "conhost" or "regsvr32") || !Is(WindowsHostRx, command.Trim())) return false;
        string rest;
        try { rest = WindowsHostRx.Replace(command.Trim(), "", 1); }
        catch (RegexMatchTimeoutException) { return false; }
        try
        {
            foreach (Match p in PathRx.Matches(rest))
            {
                var tail = rest[p.Index..].TrimStart('"');
                if (!WindowsPathRx.IsMatch(tail)) return false;
            }
        }
        catch (RegexMatchTimeoutException) { return false; }
        return !rest.Contains("://", StringComparison.Ordinal);
    }

    // "cmd" alone is too common in legitimate Active Setup / RunOnce entries to flag unless it runs something else.
    private static bool IsBenignCmd(string bin, string text) =>
        bin.Equals("cmd", StringComparison.OrdinalIgnoreCase) && !CmdRuns(text);

    private static bool CmdRuns(string text)
    {
        try { return Regex.IsMatch(text, @"\bcmd(\.exe)?\s+/[ck]\s+\S", RegexOptions.IgnoreCase, RxTimeout); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>The executable or DLL a command line starts: the quoted part, else the shortest prefix ending in a known extension.</summary>
    public static string ImageFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }
        try
        {
            var m = Regex.Match(c, @"^.*?\.(exe|dll|sys|com|bat|cmd|ps1|vbs|vbe|js|jse|wsf|hta|scr|cpl|ocx|msi|lnk|pif)(?=$|[\s,])", RegexOptions.IgnoreCase, RxTimeout);
            if (m.Success) return m.Value;
        }
        catch (RegexMatchTimeoutException) { }
        var sp = c.IndexOf(' ');
        return sp > 0 ? c[..sp] : c;
    }
}

/// <summary>Maps Windows paths written in the evidence (environment variables, <c>\SystemRoot\</c>, <c>\??\C:\</c>, drive letters) onto the offline system root.</summary>
public static class OfflinePath
{
    public static string? Resolve(string path, string systemRoot, string? user = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = @"%SystemRoot%\" + p[12..];
        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase)) p = @"%SystemRoot%\" + p;
        var profile = string.IsNullOrEmpty(user) ? null : Path.Combine(systemRoot, "Users", user);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Path.Combine(systemRoot, "Windows"),
            ["windir"] = Path.Combine(systemRoot, "Windows"),
            ["SystemDrive"] = systemRoot.TrimEnd('\\'),
            ["ProgramFiles"] = Path.Combine(systemRoot, "Program Files"),
            ["ProgramW6432"] = Path.Combine(systemRoot, "Program Files"),
            ["ProgramFiles(x86)"] = Path.Combine(systemRoot, "Program Files (x86)"),
            ["CommonProgramFiles"] = Path.Combine(systemRoot, "Program Files", "Common Files"),
            ["CommonProgramFiles(x86)"] = Path.Combine(systemRoot, "Program Files (x86)", "Common Files"),
            ["ProgramData"] = Path.Combine(systemRoot, "ProgramData"),
            ["ALLUSERSPROFILE"] = Path.Combine(systemRoot, "ProgramData"),
            ["PUBLIC"] = Path.Combine(systemRoot, "Users", "Public"),
        };
        if (profile != null)
        {
            vars["USERPROFILE"] = profile;
            vars["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
            vars["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
            vars["TEMP"] = vars["TMP"] = Path.Combine(profile, "AppData", "Local", "Temp");
        }
        var unresolved = false;
        try
        {
            p = Regex.Replace(p, "%([^%]+)%", m =>
            {
                if (vars.TryGetValue(m.Groups[1].Value, out var v)) return v;
                unresolved = true;
                return m.Value;
            }, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }
        catch (RegexMatchTimeoutException) { return null; }
        if (unresolved) return null;
        // Evidence paths are Windows paths; on another host OS the separators are mapped so the offline check still works.
        static string Host(string s) => Path.DirectorySeparatorChar == '\\' ? s : s.Replace('\\', Path.DirectorySeparatorChar);
        string? result = null;
        if (p.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase)) result = Host(p); // expanded variable
        else if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\') result = Path.Combine(systemRoot, Host(p[3..]));
        return result != null && StaysInside(result, systemRoot) ? result : null; // relative names (found through PATH), UNC paths: not checkable offline
    }

    /// <summary>The evidence names the path: a ".." must not lead out of the evidence root onto the analyst's own disk.</summary>
    private static bool StaysInside(string resolved, string systemRoot)
    {
        try
        {
            var root = Path.GetFullPath(systemRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(resolved).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static readonly string[] BinaryExt = { ".exe", ".dll", ".sys", ".ocx", ".cpl", ".scr", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js" };

    /// <summary>A missing file is reported only when its folder exists in the source and holds other programs or scripts, so partial
    /// (KAPE) collections that copied only the hives and logs out of System32 or never copied Program Files do not flag everything.</summary>
    public static bool CanCheck(string resolved)
    {
        try
        {
            var dir = Path.GetDirectoryName(resolved);
            if (dir == null || !Directory.Exists(dir)) return false;
            return Directory.EnumerateFiles(dir).Take(5000).Any(f => BinaryExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}

/// <summary>Missing-image check against the offline system root, with the folder test (<see cref="OfflinePath.CanCheck"/>) cached.</summary>
public sealed class OfflineChecker
{
    private readonly Dictionary<string, bool> _dirs = new(StringComparer.OrdinalIgnoreCase);
    public OfflineChecker(string systemRoot) => SystemRoot = systemRoot;
    public string SystemRoot { get; }

    public bool IsMissing(string imagePath, string? user)
    {
        var resolved = OfflinePath.Resolve(imagePath, SystemRoot, user);
        if (resolved == null) return false;
        var dir = Path.GetDirectoryName(resolved) ?? "";
        if (!_dirs.TryGetValue(dir, out var can)) _dirs[dir] = can = OfflinePath.CanCheck(resolved);
        try { return can && !File.Exists(resolved); } catch { return false; }
    }
}