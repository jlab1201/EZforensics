using EzForensics.Core.SystemInfo;

namespace EzForensics.Core.Persistence;

/// <summary>Startup folders: <c>ProgramData\...\Startup</c> (all users) and each profile's <c>AppData\Roaming\...\Startup</c>, with .lnk targets.</summary>
public static class StartupFolders
{
    public static readonly string CommonRelative = Path.Combine("ProgramData", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    public static readonly string UserRelative = Path.Combine("AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    /// <param name="approvedHive">NTUSER.DAT (per user, <paramref name="hkcu"/> true) or SOFTWARE (all users) for the StartupApproved\StartupFolder state.</param>
    public static void Read(string dir, string user, PersistenceResult r, OfflineChecker? chk, IRegistryHive? approvedHive, bool hkcu)
    {
        if (!Directory.Exists(dir)) return;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 4 };
        foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
        {
            var name = Path.GetFileName(f);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var fi = new FileInfo(f);
            var e = new PersistenceEntry
            {
                Category = PersistenceCategory.StartupFolder,
                Location = user.Length == 0 ? "Startup folder (All Users)" : "Startup folder",
                EntryName = name,
                Command = f,
                ImagePath = f,
                User = user,
                Source = f,
                FileCreatedUtc = Safe(() => fi.CreationTimeUtc),
                FileModifiedUtc = Safe(() => fi.LastWriteTimeUtc),
                Notes = $"Size={fi.Length:N0} bytes; file times are those of the evidence copy",
            };
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && LnkTarget.Read(f) is { } lnk)
            {
                e.Command = ((lnk.Target ?? lnk.RelativePath ?? "") + (string.IsNullOrEmpty(lnk.Arguments) ? "" : " " + lnk.Arguments)).Trim();
                e.ImagePath = lnk.Target ?? lnk.RelativePath ?? "";
                e.Notes += "; .lnk target" + (lnk.WorkingDirectory is { Length: > 0 } wd ? "; WorkingDir=" + wd : "")
                           + (lnk.TargetModifiedUtc is { } tm ? $"; target modified {tm.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC" : "");
            }
            if (RegistryAseps.StartupFolderState(approvedHive, hkcu, name) is { } st)
            {
                e.Enabled = st.Enabled ? "Yes" : "No";
                if (!st.Enabled) e.Notes += "; disabled in StartupApproved" + (st.DisabledUtc is { } t ? $" at {t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC" : "");
            }
            else e.Enabled = "Yes";
            PersistenceHeuristics.Apply(e, chk);
            r.Entries.Add(e);
        }
    }

    private static DateTime? Safe(Func<DateTime> f)
    {
        try { var d = f(); return d.Year < 1981 ? null : d; } catch { return null; }
    }
}
