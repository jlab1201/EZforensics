using System.Text.Json;

namespace EzForensics.Core.Settings;

/// <summary>
/// Product name and the per-user data folders of EZ Forensics (formerly EZ Hub).
/// <para>
/// The installer (Velopack) owns <c>%LOCALAPPDATA%\EzForensics</c> (app binaries, <c>current\</c>, <c>packages\</c>,
/// <c>Update.exe</c>) and deletes that whole folder on uninstall, so user data lives in a sibling folder,
/// <c>%LOCALAPPDATA%\EzForensicsData</c>, which updates and uninstall never touch.
/// </para>
/// Environment overrides use the new <c>EZFORENSICS_*</c> names, with the old <c>EZHUB_*</c> names as fallbacks.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "EZ Forensics";
    /// <summary>Velopack pack id (= install folder name under %LOCALAPPDATA%).</summary>
    public const string PackId = "EzForensics";
    public const string DataFolderName = "EzForensicsData";
    public const string LegacyFolderName = "EzHub";

    /// <summary>Value of <c>EZFORENSICS_&lt;name&gt;</c>, else <c>EZHUB_&lt;name&gt;</c>, else null (empty counts as unset).</summary>
    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable("EZFORENSICS_" + name);
        if (!string.IsNullOrWhiteSpace(v)) return v;
        v = Environment.GetEnvironmentVariable("EZHUB_" + name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>True when <c>EZFORENSICS_DATA</c> (or <c>EZHUB_DATA</c>) moves the data root (portable use, UI tests).</summary>
    public static bool IsDataRootOverridden => Env("DATA") != null;

    /// <summary><c>%LOCALAPPDATA%\EzForensicsData</c>, or <c>EZFORENSICS_DATA</c>.</summary>
    public static string DataRoot => Env("DATA") is { } d ? Path.GetFullPath(d) : Path.Combine(LocalAppData, DataFolderName);

    /// <summary>The EZ Hub data folder (<c>%LOCALAPPDATA%\EzHub</c>). Read for migration only; never written or deleted.</summary>
    public static string LegacyRoot => Path.Combine(LocalAppData, LegacyFolderName);

    /// <summary>The installer's app folder (<c>%LOCALAPPDATA%\EzForensics</c>), wiped by uninstall: never store data here.</summary>
    public static string InstallRoot => Path.Combine(LocalAppData, PackId);

    public static string SettingsPath => Env("SETTINGS") is { } s ? s : Path.Combine(DataRoot, "settings.json");
    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string DefaultToolsRoot => Path.Combine(DataRoot, "Tools");
    public static string CacheDir => Path.Combine(DataRoot, "cache");
    public static string FlagsDir => Path.Combine(DataRoot, "flags");
    /// <summary>User EvtxECmd event selection (recommended-events.json schema, ticked events only).</summary>
    public static string EvtxSelectionPath => Path.Combine(DataRoot, "evtx-selection.json");
    /// <summary>User-edited Ingest commands per triage rule (<c>{ "commands": { ruleId: text } }</c>).</summary>
    public static string IngestCommandsPath => Path.Combine(DataRoot, "ingest-commands.json");
    /// <summary>Private .NET runtime (dotnet-install.ps1 -InstallDir), used only when the machine has no .NET 9.</summary>
    public static string PrivateDotnetDir => Env("DOTNET_DIR") is { } d ? Path.GetFullPath(d) : Path.Combine(DataRoot, "dotnet");

    /// <summary>
    /// Folder for a managed component (<c>plaso</c>, <c>zeek</c>): the env override, else the legacy EZ Hub folder while it
    /// still holds that component (Python venvs are not relocatable, so they are never moved), else the new data root.
    /// The legacy folder is only considered for the default data root, so a scratch data root is always clean.
    /// </summary>
    public static string ComponentRoot(string component, string envName, Func<string, bool>? legacyInUse = null)
    {
        if (Env(envName) is { } r) return Path.GetFullPath(r);
        var fresh = Path.Combine(DataRoot, component);
        if (IsDataRootOverridden) return fresh;
        var legacy = Path.Combine(LegacyRoot, component);
        try
        {
            if (!Directory.Exists(fresh) && Directory.Exists(legacy) && (legacyInUse?.Invoke(legacy) ?? true)) return legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return fresh;
    }
}

/// <summary>Outcome of <see cref="DataMigration.Run"/>.</summary>
public sealed record MigrationResult(bool Migrated, IReadOnlyList<string> Copied, IReadOnlyList<string> Notes);

/// <summary>
/// First run of EZ Forensics on a machine that had EZ Hub: copies <c>settings.json</c> (and the small flag / stats fallback
/// files) from <c>%LOCALAPPDATA%\EzHub</c> to the new data root. Nothing is moved or deleted: the old folder stays as it is,
/// the tools folder and plaso / Zeek setups keep being used where they are (see <see cref="AppPaths.ComponentRoot"/>).
/// </summary>
public static class DataMigration
{
    public const string MarkerName = "migrated-from-ezhub.json";

    public static MigrationResult Run(string? legacyRoot = null, string? dataRoot = null, string? settingsPath = null)
    {
        legacyRoot ??= AppPaths.LegacyRoot;
        dataRoot ??= AppPaths.DataRoot;
        settingsPath ??= Path.Combine(dataRoot, "settings.json");
        var copied = new List<string>();
        var notes = new List<string>();
        var marker = Path.Combine(dataRoot, MarkerName);
        try
        {
            if (File.Exists(marker)) return new MigrationResult(false, copied, notes);
            var oldSettings = Path.Combine(legacyRoot, "settings.json");
            if (!File.Exists(oldSettings)) return new MigrationResult(false, copied, notes);
            if (File.Exists(settingsPath))
            {
                notes.Add("settings.json already exists in the new data folder; the EZ Hub settings were not copied.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
                File.Copy(oldSettings, settingsPath + ".tmp", overwrite: true);
                File.Move(settingsPath + ".tmp", settingsPath, overwrite: false);
                copied.Add(settingsPath);
            }
            foreach (var sub in new[] { "flags", "cache" })
            {
                var src = Path.Combine(legacyRoot, sub);
                if (!Directory.Exists(src)) continue;
                var dst = Path.Combine(dataRoot, sub);
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.EnumerateFiles(src, "*.json"))
                {
                    var t = Path.Combine(dst, Path.GetFileName(f));
                    if (File.Exists(t)) continue;
                    try { File.Copy(f, t); copied.Add(t); }
                    catch (IOException) { }
                }
            }
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(marker, JsonSerializer.Serialize(new
            {
                from = legacyRoot,
                utc = DateTime.UtcNow.ToString("o"),
                copied,
                note = "EZ Hub's folder was left untouched. The tools folder and any plaso / Zeek setup are still used where they are.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            return new MigrationResult(true, copied, notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("Migration from EZ Hub failed: " + ex.Message);
            return new MigrationResult(false, copied, notes);
        }
    }

    /// <summary>
    /// One-time move of EZ Hub's tools folder (<c>%LOCALAPPDATA%\EzHub\Tools</c>) to <c>EzForensicsData\Tools</c> when the settings
    /// still point at it. Returns the new tools root to store (null = leave the setting as it is) and a note for the log.
    /// A tools root set to any other folder is never touched; a non-empty target is never merged into. A failed move (a tool
    /// still open) leaves everything where it was and is tried again on the next start.
    /// </summary>
    public static (string? NewRoot, string? Note) MoveLegacyTools(string toolsRoot, string? legacyTools = null, string? newTools = null)
    {
        legacyTools ??= Path.Combine(AppPaths.LegacyRoot, "Tools");
        newTools ??= AppPaths.DefaultToolsRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(toolsRoot) || !SamePath(toolsRoot, legacyTools)) return (null, null);
            if (!Directory.Exists(legacyTools))
                return (newTools, $"The tools folder {legacyTools} no longer exists; the tools folder is now {newTools}.");
            if (Directory.Exists(newTools))
            {
                if (Directory.EnumerateFileSystemEntries(newTools).Any())
                    return (null, $"{newTools} already has files, so the EZ Hub tools folder {legacyTools} was not moved.");
                Directory.Delete(newTools);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(newTools))!);
            Directory.Move(legacyTools, newTools);
            return (newTools, $"Moved the EZ Hub tools folder {legacyTools} -> {newTools}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not move the EZ Hub tools folder {legacyTools} (tried again on the next start): {ex.Message}");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
