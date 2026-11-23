using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;
using EzForensics.Core.Tools;

namespace EzForensics.Core.Setup;

/// <summary>What one revert target is.</summary>
public enum RevertKind
{
    /// <summary>A folder (or file) the setup created: deleted with everything in it.</summary>
    Path,
    /// <summary>A folder the setup created that only holds other targets: deleted once it is empty.</summary>
    EmptyFolder,
    /// <summary>EZ Forensics' private WSL distro (<c>wsl --unregister</c>), when this setup created it.</summary>
    WslDistro,
}

/// <summary>One thing a first setup installed and Cancel removes again (saved in <see cref="SetupResumeState.Installed"/> across the restart).</summary>
public sealed class RevertTarget
{
    public RevertKind Kind { get; set; }
    /// <summary>Full path (the distro's install folder for <see cref="RevertKind.WslDistro"/>).</summary>
    public string Path { get; set; } = "";
    /// <summary>The step that installed it (log text).</summary>
    public string What { get; set; } = "";

    public override string ToString() => $"{Kind} {Path} ({What})";
}

/// <summary>Where the setup steps install (all under the data root unless an environment override moves a component).</summary>
public sealed class SetupRevertPaths
{
    public required string DataRoot { get; init; }
    public required string ToolsRoot { get; init; }
    public required string DotnetDir { get; init; }
    public required string PlasoRoot { get; init; }
    public required string PythonDir { get; init; }
    public required string PlasoVenv { get; init; }
    public required string VolatilityRoot { get; init; }
    /// <summary>legacy: a managed Sysinternals Autoruns install from before Autoruns was removed (&lt;ToolsRoot&gt;\Sysinternals\Autoruns).</summary>
    public string AutorunsRoot => System.IO.Path.Combine(ToolsRoot, "Sysinternals", "Autoruns");
    public required string WslRoot { get; init; }
    public required string DistroInstallDir { get; init; }

    public string ToolsNet9 => System.IO.Path.Combine(ToolsRoot, "net9");
    public string VolatilityVenv => System.IO.Path.Combine(VolatilityRoot, "venv");
    public string VolatilityCache => System.IO.Path.Combine(VolatilityRoot, "cache");
    public string VolatilitySymbols => System.IO.Path.Combine(VolatilityRoot, "symbols");

    /// <summary>The real locations (same lookups as <see cref="DefaultSetupExecutor"/>).</summary>
    public static SetupRevertPaths Current(AppSettings settings)
    {
        var plaso = new PlasoLocator();
        return new SetupRevertPaths
        {
            DataRoot = AppPaths.DataRoot,
            ToolsRoot = settings.ToolsRoot,
            DotnetDir = RuntimeProbe.PrivateDotnetRoot,
            PlasoRoot = PlasoLocator.ManagedRoot,
            PythonDir = plaso.PrivatePythonDir,
            PlasoVenv = plaso.ManagedVenv,
            VolatilityRoot = VolatilityLocator.ManagedRoot,
            WslRoot = EzDistro.WslRoot,
            DistroInstallDir = EzDistro.InstallDir,
        };
    }

    /// <summary>Folders whose top-level entries are recorded one by one (several steps add to them).</summary>
    internal IEnumerable<string> Containers => new[] { ToolsRoot, ToolsNet9, VolatilitySymbols };

    /// <summary>Folders recorded as existing or not.</summary>
    internal IEnumerable<string> Folders => new[]
    {
        ToolsRoot, ToolsNet9, DotnetDir, PlasoRoot, PythonDir, PlasoVenv, VolatilityRoot, VolatilityVenv, VolatilityCache, VolatilitySymbols,
        AutorunsRoot, System.IO.Path.GetDirectoryName(AutorunsRoot) ?? AutorunsRoot, WslRoot, DistroInstallDir,
    };
}

/// <summary>What existed before the first setup ran (taken once, before its first step).</summary>
public sealed class SetupSnapshot
{
    public HashSet<string> Existing { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SymbolPackInstalled { get; init; }
    public bool EzDistroExisted { get; init; }

    public bool Existed(string path) => Existing.Contains(SetupRevert.Norm(path));

    /// <param name="ezDistroExists">The private distro is registered now (unknown = true: it is then never removed).</param>
    public static SetupSnapshot Take(SetupRevertPaths paths, bool ezDistroExists)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in paths.Folders)
            if (Directory.Exists(f) || File.Exists(f)) set.Add(SetupRevert.Norm(f));
        foreach (var c in paths.Containers)
        {
            if (!Directory.Exists(c)) continue;
            try { foreach (var e in Directory.EnumerateFileSystemEntries(c)) set.Add(SetupRevert.Norm(e)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new SetupSnapshot
        {
            Existing = set,
            SymbolPackInstalled = VolatilitySetup.SymbolPackInstalled(paths.VolatilitySymbols),
            EzDistroExisted = ezDistroExists,
        };
    }
}

/// <summary>Outcome of <see cref="SetupRevert.RunAsync"/>.</summary>
public sealed record SetupRevertResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Failed)
{
    public bool Ok => Failed.Count == 0;
}

/// <summary>
/// Cancel of the first setup: removes what this setup installed and nothing else. A step counts only when it ran in this setup
/// (Done, or stopped part-way: Failed / Cancelled / Action needed); <see cref="SetupStepState.AlreadyInstalled"/>, skipped and queued
/// steps never do. Of a step's folders only those that did not exist before the setup started (<see cref="SetupSnapshot"/>) and that
/// lie under the data root are removed. Windows features (enabling WSL) are never touched; the private WSL distro is unregistered
/// only when this setup created it. Zeek / RITA inside a distro that already existed stay.
/// </summary>
public static class SetupRevert
{
    public static string Norm(string path) => System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>The step ran in this setup (finished or stopped part-way), so what it left behind is this setup's.</summary>
    public static bool Ran(SetupStep s) => s.Kind != SetupStepKind.BuiltIn &&
                                          s.State is SetupStepState.Done or SetupStepState.Failed or SetupStepState.Cancelled or SetupStepState.ActionNeeded or SetupStepState.Running;

    public static bool IsUnder(string path, string root)
    {
        var p = Norm(path);
        var r = Norm(root) + System.IO.Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The targets of <paramref name="steps"/> that exist now (pure apart from reading the file system).</summary>
    public static List<RevertTarget> Targets(IEnumerable<SetupStep> steps, SetupRevertPaths paths, SetupSnapshot before)
    {
        var list = new List<RevertTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyRan = false;

        void Add(RevertKind kind, string path, string what)
        {
            if (!IsUnder(path, paths.DataRoot) || before.Existed(path)) return;
            if (kind != RevertKind.WslDistro && !Directory.Exists(path) && !File.Exists(path)) return;
            if (seen.Add(kind + "|" + Norm(path))) list.Add(new RevertTarget { Kind = kind, Path = Norm(path), What = what });
        }

        void NewEntries(string container, string what, IReadOnlyCollection<string> exclude)
        {
            if (!Directory.Exists(container)) return;
            if (!before.Existed(container)) { Add(RevertKind.Path, container, what); return; }
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(container).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var e in entries)
                if (!exclude.Contains(Norm(e))) Add(RevertKind.Path, e, what);
        }

        // legacy: a leftover Autoruns lives inside the tools folder (Sysinternals\Autoruns): never counted as one of the EZ tools'.
        var autorunsTop = TopUnder(paths.AutorunsRoot, paths.ToolsRoot);
        var toolsExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Norm(paths.ToolsNet9) };
        if (autorunsTop != null) toolsExclude.Add(autorunsTop);

        foreach (var s in steps.Where(Ran))
        {
            anyRan = true;
            var what = s.Title;
            switch (s.Kind)
            {
                case SetupStepKind.InstallDotnet:
                    Add(RevertKind.Path, paths.DotnetDir, what);
                    break;
                case SetupStepKind.DownloadEzTools:
                    NewEntries(paths.ToolsRoot, what, toolsExclude);
                    NewEntries(paths.ToolsNet9, what, Array.Empty<string>());
                    break;
                case SetupStepKind.ProvisionPython:
                    Add(RevertKind.Path, paths.PythonDir, what);
                    break;
                case SetupStepKind.CreatePlasoVenv or SetupStepKind.InstallPlaso:
                    Add(RevertKind.Path, paths.PlasoVenv, what);
                    break;
                case SetupStepKind.InstallVolatility:
                    Add(RevertKind.Path, paths.VolatilityVenv, what);
                    Add(RevertKind.Path, paths.VolatilityCache, what);
                    Add(RevertKind.Path, paths.VolatilitySymbols, what);
                    break;
                case SetupStepKind.InstallVolatilitySymbols:
                    if (!before.SymbolPackInstalled) NewEntries(paths.VolatilitySymbols, what, Array.Empty<string>());
                    break;
                case SetupStepKind.InstallExploreTool:
                    Add(RevertKind.Path, new Explore.ExploreToolLocator().ToolDir(Enum.Parse<Explore.ExploreTool>(
                        s.ItemId switch { SetupIds.ExploreDie => "Die", SetupIds.ExploreExifTool => "ExifTool", _ => "CyberChef" })), what);
                    break;
                case SetupStepKind.InstallEzDistro:
                    if (!before.EzDistroExisted) Add(RevertKind.WslDistro, paths.DistroInstallDir, what);
                    break;
                // EnableWsl (a Windows feature), SyncMaps (inside the tool folders), Zeek / RITA (inside the distro): nothing of their own.
            }
        }

        if (anyRan && Directory.Exists(paths.AutorunsRoot)) Add(RevertKind.Path, paths.AutorunsRoot, "leftover Sysinternals Autoruns (legacy)");

        // Component folders the steps created around their targets: removed once empty.
        if (anyRan)
            foreach (var f in new[] { paths.PlasoRoot, paths.VolatilityRoot, System.IO.Path.GetDirectoryName(paths.AutorunsRoot) ?? paths.AutorunsRoot, paths.WslRoot, paths.ToolsRoot })
                if (Directory.Exists(f)) Add(RevertKind.EmptyFolder, f, "folder created by setup");
        return list;
    }

    /// <summary>Union of two target lists (the ones saved before the restart and the ones of the resumed run).</summary>
    public static List<RevertTarget> Merge(IEnumerable<RevertTarget>? a, IEnumerable<RevertTarget>? b)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (a ?? Array.Empty<RevertTarget>()).Concat(b ?? Array.Empty<RevertTarget>()).Where(t => seen.Add(t.Kind + "|" + Norm(t.Path))).ToList();
    }

    /// <summary>
    /// Removes the targets: the distro first (<paramref name="removeDistro"/>, e.g. <see cref="EzDistro.RemoveAsync"/>), then the folders,
    /// then the emptied component folders (deepest first). Never deletes the data root itself or anything outside it. A folder in use is
    /// retried briefly, then reported in <see cref="SetupRevertResult.Failed"/>.
    /// </summary>
    public static async Task<SetupRevertResult> RunAsync(IReadOnlyList<RevertTarget> targets, string dataRoot,
        Func<CancellationToken, Task<(bool Ok, string Message)>>? removeDistro, Action<string>? log, CancellationToken ct = default)
    {
        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var t in targets.Where(t => t.Kind == RevertKind.WslDistro))
        {
            if (removeDistro == null) { failed.Add($"{t.Path}: the WSL distro cannot be removed here."); continue; }
            log?.Invoke($"Removing the {EzDistro.Name} WSL distro (wsl --unregister {EzDistro.Name})…");
            try
            {
                var (ok, message) = await removeDistro(ct).ConfigureAwait(true);
                log?.Invoke(message);
                if (ok) removed.Add($"WSL distro {EzDistro.Name}"); else failed.Add(message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add($"wsl --unregister {EzDistro.Name}: {ex.Message}"); }
        }
        foreach (var t in targets.Where(t => t.Kind == RevertKind.Path))
        {
            if (!IsUnder(t.Path, dataRoot)) { failed.Add($"{t.Path}: outside the data folder, not removed."); continue; }
            if (!Directory.Exists(t.Path) && !File.Exists(t.Path)) continue;
            log?.Invoke($"Removing {t.Path}");
            var error = await DeleteAsync(t.Path, ct).ConfigureAwait(true);
            if (error == null) removed.Add(t.Path); else failed.Add($"{t.Path}: {error}");
        }
        foreach (var t in targets.Where(t => t.Kind == RevertKind.EmptyFolder).OrderByDescending(t => t.Path.Length))
        {
            if (!IsUnder(t.Path, dataRoot) || !Directory.Exists(t.Path)) continue;
            try
            {
                if (Directory.EnumerateFileSystemEntries(t.Path).Any()) continue;
                Directory.Delete(t.Path);
                removed.Add(t.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new SetupRevertResult(removed, failed);
    }

    /// <summary>Deletes a folder or file; retries a few times (a process stopped by Cancel may still hold a file). Null on success.</summary>
    private static async Task<string?> DeleteAsync(string path, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5) return ex.Message;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(true);
            }
        }
    }

    /// <summary>The first folder below <paramref name="root"/> on the way to <paramref name="path"/> (null when not under it).</summary>
    private static string? TopUnder(string path, string root)
    {
        if (!IsUnder(path, root)) return null;
        var rel = System.IO.Path.GetRelativePath(Norm(root), Norm(path));
        var first = rel.Split(new[] { '\\', '/' }, 2)[0];
        return Norm(System.IO.Path.Combine(root, first));
    }
}
