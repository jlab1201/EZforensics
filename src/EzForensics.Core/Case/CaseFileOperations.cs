using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EzForensics.Core.Execution;
using EzForensics.Core.Platforms;
using EzForensics.Core.Results;

namespace EzForensics.Core.Case;

/// <summary>A file operation refused by <see cref="CaseFileOperations"/> (outside the case, protected file, invalid name, ...).</summary>
public sealed class CaseFileOperationException : InvalidOperationException
{
    public CaseFileOperationException(string message) : base(message) { }
}

/// <summary>One input of a derived output (path relative to the case folder).</summary>
public sealed record DerivedOutputInput(string Path, string? Host, string Sha256);

/// <summary>An output the user derived from case files (Merge into new file). Appended to <c>&lt;case&gt;\logs\derived_outputs.json</c>.</summary>
public sealed record DerivedOutputRecord
{
    public string Kind { get; init; } = "merge";
    /// <summary>Output path relative to the case folder.</summary>
    public required string Output { get; init; }
    public required string OutputSha256 { get; init; }
    public long Rows { get; init; }
    public IReadOnlyList<DerivedOutputInput> Inputs { get; init; } = Array.Empty<DerivedOutputInput>();
    public DateTime CreatedUtc { get; init; }
    public string User { get; init; } = "";
    public string App { get; init; } = "EZ Forensics";
}

/// <summary>
/// File operations of the Analyze case tree (rename, move, copy, duplicate, new folder, delete, merge). Every operation:
/// stays inside the case folder (anything else is refused), never touches run manifests (<c>EzForensics_manifest_*.json</c>, older <c>EzHub_manifest_*.json</c>),
/// <c>case.json</c>, the <c>logs</c> folder or the hidden <c>.ezstaging</c> folder, carries a CSV's sidecars
/// (<c>&lt;stem&gt;.ezflags.json</c>, <c>&lt;name&gt;.csv.*</c>) along, never overwrites (a taken name gets <c>_2</c>, <c>_3</c>, ...)
/// and appends a line to <c>&lt;case&gt;\logs\file_operations.log</c>
/// (<c>UTC time \t user \t op \t from \t to \t sha256</c>; files are hashed when copied or deleted, up to <see cref="MaxHashBytes"/>).
/// </summary>
public sealed class CaseFileOperations
{
    public const string LogFileName = "file_operations.log";
    public const string DerivedOutputsFileName = "derived_outputs.json";
    public const string LogsFolderName = "logs";
    /// <summary>Hidden case-level cache folder (combined views). Hidden from the tree like <c>.ezstaging</c>.</summary>
    public const string CacheFolderName = ".ezcache";
    public const string CombinedFolderName = "combined";
    public const long MaxHashBytes = 256L * 1024 * 1024;
    /// <summary>Default folder / file name stem of merged outputs.</summary>
    public const string MergedName = "Merged";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Action<string, bool> _recycle;
    private readonly Func<DateTime> _clock;

    /// <param name="caseDir">The case folder (the boundary of every operation).</param>
    /// <param name="recycle">Deletes a path (<c>true</c> = directory). The app sends it to the Recycle Bin; never a hard delete in the UI.</param>
    /// <param name="user">User name for the log (default <see cref="Environment.UserName"/>).</param>
    public CaseFileOperations(string caseDir, Action<string, bool> recycle, string? user = null, Func<DateTime>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(caseDir)) throw new ArgumentException("No case folder.", nameof(caseDir));
        CaseDir = Norm(Path.GetFullPath(caseDir));
        _prefix = CaseDir.EndsWith(Path.DirectorySeparatorChar) ? CaseDir : CaseDir + Path.DirectorySeparatorChar;
        _recycle = recycle ?? throw new ArgumentNullException(nameof(recycle));
        User = string.IsNullOrWhiteSpace(user) ? SafeUserName() : user;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The case folder, full path without a trailing separator, except for a drive root (<c>D:\</c>).</summary>
    public string CaseDir { get; }
    /// <summary><see cref="CaseDir"/> with exactly one trailing separator (prefix of every path inside the case).</summary>
    private readonly string _prefix;

    /// <summary>Removes trailing separators, but keeps the one of a root (<c>D:\</c> stays <c>D:\</c>, never the drive-relative <c>D:</c>).</summary>
    private static string Norm(string full)
    {
        var t = full.TrimEnd('\\', '/');
        var root = Path.GetPathRoot(full);
        return !string.IsNullOrEmpty(root) && t.Length <= root.TrimEnd('\\', '/').Length ? root : t;
    }
    public string User { get; }
    public string LogPath => Path.Combine(CaseDir, LogsFolderName, LogFileName);
    public string DerivedOutputsPath => Path.Combine(CaseDir, LogsFolderName, DerivedOutputsFileName);

    // ------------------------------------------------------------ bounds and protected paths

    /// <summary>True when <paramref name="path"/> is inside the case folder (the case folder itself excluded).</summary>
    public bool IsInsideCase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Norm(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return !IsCaseRoot(full) && full.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCaseRoot(string full) => string.Equals(full, CaseDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-relative path ("" for the case folder).</summary>
    public string Relative(string full) =>
        IsCaseRoot(full) ? "" : full.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) ? full[_prefix.Length..] : full;

    /// <summary>True for names of protected case files: run manifests and the case marker.</summary>
    public static bool IsProtectedFileName(string name) =>
        name.Equals(CaseLayout.MarkerFileName, StringComparison.OrdinalIgnoreCase)
        || CaseIndex.IsManifestFileName(name);

    /// <summary>The first segment of a case-relative path is <c>logs</c>, <c>.ezstaging</c>, <c>.ezcache</c> or <c>.ezassistant</c>.</summary>
    private bool IsInReservedFolder(string full)
    {
        var rel = Relative(full);
        if (rel.Length == 0) return false;
        var first = rel.Split('\\', '/')[0];
        return first.Equals(LogsFolderName, StringComparison.OrdinalIgnoreCase)
               || first.Equals(OutputPlan.StagingFolderName, StringComparison.OrdinalIgnoreCase)
               || first.Equals(CacheFolderName, StringComparison.OrdinalIgnoreCase)
               || first.Equals(Assistant.AssistantConsent.FolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalises <paramref name="path"/> and refuses anything an operation may not take as its source.</summary>
    public string CheckSource(string path, string op)
    {
        var full = Full(path);
        if (IsCaseRoot(full)) throw new CaseFileOperationException($"{op}: the case folder itself cannot be changed.");
        if (!IsInsideCase(full)) throw new CaseFileOperationException($"{op}: {path} is outside the case folder {CaseDir}.");
        if (IsInReservedFolder(full)) throw new CaseFileOperationException($"{op}: {Relative(full)} is in a folder EZ Forensics manages (logs, .ezstaging, .ezcache).");
        if (IsProtectedFileName(Path.GetFileName(full))) throw new CaseFileOperationException($"{op}: {Path.GetFileName(full)} is a run manifest / case file and cannot be changed.");
        if (OutputNaming.IsEzForensicsSidecar(full)) throw new CaseFileOperationException($"{op}: {Path.GetFileName(full)} is a sidecar; it follows its CSV.");
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException($"{op}: not found.", full);
        CheckNoReparsePoint(full, op);
        if (Directory.Exists(full) && ContainsProtected(full))
            throw new CaseFileOperationException($"{op}: {Relative(full)} contains run manifests or case.json.");
        return full;
    }

    /// <summary>Normalises <paramref name="dir"/> and refuses a destination folder outside the case or in a managed folder.</summary>
    public string CheckDestinationDir(string dir, string op)
    {
        var full = Full(dir);
        if (!IsCaseRoot(full) && !IsInsideCase(full)) throw new CaseFileOperationException($"{op}: {dir} is outside the case folder {CaseDir}.");
        if (IsInReservedFolder(full)) throw new CaseFileOperationException($"{op}: cannot write into {Relative(full)} (managed by EZ Forensics).");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"{op}: folder not found: {full}");
        CheckNoReparsePoint(full, op);
        return full;
    }

    /// <summary>
    /// Refuses <paramref name="full"/> when it, or any folder between the case folder and it, is a reparse point (junction, symbolic link,
    /// mount point): such a path may lead outside the case, so the textual bounds check says nothing about where the operation lands.
    /// </summary>
    private void CheckNoReparsePoint(string full, string op)
    {
        if (!IsInsideCase(full)) return;
        var parts = Relative(full).Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        var p = CaseDir;
        foreach (var part in parts)
        {
            p = Path.Combine(p, part);
            if (IsReparsePoint(p))
                throw new CaseFileOperationException($"{op}: {Relative(p)} is a link (junction / symbolic link); EZ Forensics does not operate through links because they can point outside the case.");
        }
    }

    /// <summary>True when <paramref name="path"/> exists and is a reparse point (junction, symbolic link, mount point...).</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>The first reparse point inside <paramref name="dir"/> (never followed), or null.</summary>
    public static string? FirstReparsePointInside(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(d).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) return e.FullName;
                if ((e.Attributes & FileAttributes.Directory) != 0) stack.Push(e.FullName);
            }
        }
        return null;
    }

    private void CheckNoReparsePointInside(string dir, string op)
    {
        if (FirstReparsePointInside(dir) is { } link)
            throw new CaseFileOperationException($"{op}: {Relative(dir)} contains a link ({Relative(link)}); EZ Forensics does not {op.ToLowerInvariant()} folders with junctions / symbolic links inside. Remove the link first.");
    }

    private static bool ContainsProtected(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.json", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Any(f => IsProtectedFileName(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new CaseFileOperationException("No path.");
        try { return Norm(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new CaseFileOperationException($"Invalid path: {path}"); }
    }

    // ------------------------------------------------------------ names

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();
    private static readonly Regex DeviceName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates a new name for a file or folder; returns the final name (a CSV keeps <c>.csv</c>: it is appended when missing)
    /// or throws <see cref="CaseFileOperationException"/> with the reason.
    /// </summary>
    public static string ValidateName(string? name, bool isCsv, bool isDirectory)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new CaseFileOperationException("The name is empty.");
        if (n.IndexOfAny(InvalidNameChars) >= 0) throw new CaseFileOperationException("The name contains a character Windows does not allow (\\ / : * ? \" < > |).");
        if (n is "." or "..") throw new CaseFileOperationException("Invalid name.");
        if (isCsv && !n.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) n += ".csv";
        if (isCsv && n.Length == 4) throw new CaseFileOperationException("The name is empty.");
        if (n.EndsWith('.') || n.EndsWith(' ')) throw new CaseFileOperationException("A name cannot end with a dot or a space.");
        if (DeviceName.IsMatch(n.Split('.')[0])) throw new CaseFileOperationException($"{n} is a reserved Windows device name.");
        if (n.Length > 200) throw new CaseFileOperationException("The name is too long (200 characters at most).");
        if (IsProtectedFileName(n)) throw new CaseFileOperationException($"{n} is reserved for run manifests / the case marker.");
        if (OutputNaming.IsEzForensicsSidecar(n)) throw new CaseFileOperationException($"{n} is reserved for EZ Forensics sidecars.");
        if (isDirectory && n.StartsWith('.')) throw new CaseFileOperationException("Folder names starting with a dot are reserved (hidden folders).");
        return n;
    }

    /// <summary>
    /// Turns a typed name into a layout v2 output name (<see cref="ArtifactOutputLayout.IsValidOutputName"/>): alphanumeric segments joined by
    /// <c>_</c>, first letter upper case, <c>.csv</c>. <c>my merged (2).csv</c> → <c>My_merged_2.csv</c>; nothing usable → <c>Merged.csv</c>.
    /// </summary>
    public static string SanitizeOutputName(string? name)
    {
        var stem = name ?? "";
        if (stem.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        var segs = Regex.Split(stem, "[^A-Za-z0-9]+").Where(s => s.Length > 0).ToList();
        if (segs.Count == 0) return MergedName + ".csv";
        if (!char.IsAsciiLetter(segs[0][0])) segs.Insert(0, MergedName);
        segs[0] = char.ToUpperInvariant(segs[0][0]) + segs[0][1..];
        var result = string.Join('_', segs) + ".csv";
        return ArtifactOutputLayout.IsValidOutputName(result) ? result : MergedName + ".csv";
    }

    // ------------------------------------------------------------ sidecars and unique names

    /// <summary>
    /// Sidecars of a CSV that exist on disk: <c>&lt;stem&gt;.ezflags.json</c> (row flags) and every <c>&lt;name&gt;.csv.*</c> file
    /// (<c>.ezstats.json</c> tree stats and any other helper file). Empty for non-CSV paths.
    /// </summary>
    public static IReadOnlyList<string> SidecarsOf(string csvPath)
    {
        var full = Path.GetFullPath(csvPath);
        if (!full.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        var list = new List<string>();
        var flags = RowFlagStore.SidecarPathFor(full);
        if (File.Exists(flags)) list.Add(flags);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                var fn = Path.GetFileName(f);
                if (fn.Length > name.Length + 1 && fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) list.Add(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Where a sidecar of <paramref name="csv"/> goes when the CSV becomes <paramref name="newCsv"/>.</summary>
    public static string SidecarTarget(string csv, string sidecar, string newCsv)
    {
        var newDir = Path.GetDirectoryName(Path.GetFullPath(newCsv))!;
        var sideName = Path.GetFileName(sidecar);
        var oldName = Path.GetFileName(csv);
        if (sideName.StartsWith(oldName + ".", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(newDir, Path.GetFileName(newCsv) + sideName[oldName.Length..]);
        // <stem>.ezflags.json
        var oldStem = Path.GetFileNameWithoutExtension(csv);
        return Path.Combine(newDir, Path.GetFileNameWithoutExtension(newCsv) + sideName[oldStem.Length..]);
    }

    /// <summary>
    /// A free target: <paramref name="path"/> itself, else <c>Name_2.ext</c>, <c>Name_3.ext</c>, ... (folders: <c>Name_2</c>). For a CSV the
    /// sidecar names it would take must be free too.
    /// </summary>
    public static string UniqueTarget(string path, bool isDirectory, IReadOnlyList<string>? sidecarsOf = null, string? sourceCsv = null)
    {
        var full = Path.GetFullPath(path);
        bool Free(string p)
        {
            if (File.Exists(p) || Directory.Exists(p)) return false;
            if (sidecarsOf != null && sourceCsv != null)
                foreach (var s in sidecarsOf)
                {
                    var t = SidecarTarget(sourceCsv, s, p);
                    if (File.Exists(t) || Directory.Exists(t)) return false;
                }
            return true;
        }
        if (Free(full)) return full;
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string ext = isDirectory ? "" : Path.GetExtension(name);
        // Name_2 -> Name_3 rather than Name_2_2
        var m = Regex.Match(stem, @"^(?<b>.+)_(?<n>[0-9]+)$");
        int start = 2;
        if (m.Success && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var cur) && cur < 100_000)
        {
            stem = m.Groups["b"].Value;
            start = cur + 1;
        }
        for (int n = start; n < start + 100_000; n++)
        {
            var p = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (Free(p)) return p;
        }
        throw new IOException("No free name for " + full);
    }

    // ------------------------------------------------------------ operations

    /// <summary>Renames a file (with its sidecars) or folder in place. Returns the new path (a taken name gets <c>_2</c>).</summary>
    public string Rename(string path, string newName)
    {
        var src = CheckSource(path, "Rename");
        bool isDir = Directory.Exists(src);
        bool isCsv = !isDir && src.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        var name = ValidateName(newName, isCsv, isDir);
        var parent = Path.GetDirectoryName(src)!;
        if (string.Equals(parent, CaseDir, StringComparison.OrdinalIgnoreCase) && isDir && CaseIndex.CaseLevelFolders.Contains(name)
            && !string.Equals(Path.GetFileName(src), name, StringComparison.OrdinalIgnoreCase))
            throw new CaseFileOperationException($"{name} is a case-level folder name.");
        var target = Path.Combine(parent, name);
        if (string.Equals(target, src, StringComparison.Ordinal)) return src;
        // a case-only change (foo.csv -> Foo.csv) is a rename to the same file: no collision
        bool caseOnly = string.Equals(target, src, StringComparison.OrdinalIgnoreCase);
        return MoveCore(src, caseOnly ? target : null, parent, name, "RENAME");
    }

    /// <summary>Moves a file (with its sidecars) or folder into <paramref name="destDir"/> (Cut + Paste). Returns the new path.</summary>
    public string Move(string path, string destDir)
    {
        var src = CheckSource(path, "Move");
        var dest = CheckDestinationDir(destDir, "Move");
        if (Directory.Exists(src) && (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase)
                                      || dest.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new CaseFileOperationException("Move: a folder cannot be moved into itself.");
        if (string.Equals(Norm(Path.GetDirectoryName(src)!), dest, StringComparison.OrdinalIgnoreCase)) return src; // already there
        CheckCaseLevelName(src, dest, "Move");
        return MoveCore(src, null, dest, Path.GetFileName(src), "MOVE");
    }

    private string MoveCore(string src, string? exactTarget, string destDir, string name, string op)
    {
        bool isDir = Directory.Exists(src);
        var sidecars = isDir ? Array.Empty<string>() : SidecarsOf(src);
        var target = exactTarget ?? UniqueTarget(Path.Combine(destDir, name), isDir, sidecars, src);
        if (isDir)
        {
            if (exactTarget != null)
            {
                // case-only folder rename: go through a temporary name
                var tmp = UniqueTarget(target + "_tmp", true);
                Directory.Move(src, tmp);
                Directory.Move(tmp, target);
            }
            else Directory.Move(src, target);
            Log(op, src, target, "-");
            return target;
        }
        var moves = new List<(string From, string To)> { (src, target) };
        foreach (var s in sidecars) moves.Add((s, SidecarTarget(src, s, target)));
        var done = new List<(string From, string To)>();
        try
        {
            foreach (var (from, to) in moves)
            {
                File.Move(from, to, overwrite: false);
                done.Add((from, to));
            }
        }
        catch
        {
            // roll back what moved, so a CSV never loses its sidecars
            foreach (var (from, to) in Enumerable.Reverse(done))
                try { File.Move(to, from, overwrite: false); }
                catch (Exception rex) { EzForensics.Core.Diagnostics.CoreLog.Warn($"Rollback could not move {to} back to {from}", rex); }
            throw;
        }
        foreach (var (from, to) in moves) Log(op, from, to, "-");
        return target;
    }

    /// <summary>Copies a file (with its sidecars) or folder into <paramref name="destDir"/>. Returns the new path (never overwrites).</summary>
    public string Copy(string path, string destDir)
    {
        var src = CheckSource(path, "Copy");
        var dest = CheckDestinationDir(destDir, "Copy");
        if (Directory.Exists(src) && (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase)
                                      || dest.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new CaseFileOperationException("Copy: a folder cannot be copied into itself.");
        CheckCaseLevelName(src, dest, "Copy");
        return CopyCore(src, Path.Combine(dest, Path.GetFileName(src)), "COPY");
    }

    /// <summary>A folder dropped into the case root may not take a case-level folder name (Timeline, logs, SystemInfo...).</summary>
    private void CheckCaseLevelName(string src, string dest, string op)
    {
        var name = Path.GetFileName(src);
        if (IsCaseRoot(dest) && Directory.Exists(src) && CaseIndex.CaseLevelFolders.Contains(name))
            throw new CaseFileOperationException($"{op}: {name} is a case-level folder name; it cannot be placed at the top of the case.");
    }

    /// <summary>Copies a file (with its sidecars) or folder next to itself as <c>Name_2</c>. Returns the new path.</summary>
    public string Duplicate(string path)
    {
        var src = CheckSource(path, "Duplicate");
        return CopyCore(src, src, "DUPLICATE");
    }

    private string CopyCore(string src, string wanted, string op)
    {
        bool isDir = Directory.Exists(src);
        if (isDir)
        {
            CheckNoReparsePointInside(src, op == "COPY" ? "Copy" : "Duplicate");
            var target = UniqueTarget(wanted, true);
            CopyDirectory(src, target, op);
            Log(op, src, target, "-");
            return target;
        }
        var sidecars = SidecarsOf(src);
        var t = UniqueTarget(wanted, false, sidecars, src);
        CopyFile(src, t, op);
        foreach (var s in sidecars) CopyFile(s, SidecarTarget(src, s, t), op);
        return t;
    }

    private void CopyFile(string from, string to, string op)
    {
        File.Copy(from, to, overwrite: false);
        Log(op, from, to, HashForLog(to));
    }

    private void CopyDirectory(string from, string to, string op)
    {
        Directory.CreateDirectory(to);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in Directory.EnumerateFiles(from, "*", opts)) CopyFile(f, Path.Combine(to, Path.GetFileName(f)), op);
        foreach (var d in Directory.EnumerateDirectories(from, "*", opts)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)), op);
    }

    /// <summary>Creates a new (empty) folder in <paramref name="parentDir"/>. Returns its path (<c>New folder_2</c> when taken).</summary>
    public string CreateFolder(string parentDir, string name = "New folder")
    {
        var parent = CheckDestinationDir(parentDir, "New folder");
        var n = ValidateName(name, false, true);
        if (string.Equals(parent, CaseDir, StringComparison.OrdinalIgnoreCase) && CaseIndex.CaseLevelFolders.Contains(n))
            throw new CaseFileOperationException($"{n} is a case-level folder name.");
        var target = UniqueTarget(Path.Combine(parent, n), true);
        Directory.CreateDirectory(target);
        Log("NEWFOLDER", "-", target, "-");
        return target;
    }

    /// <summary>
    /// Deletes files (with their sidecars) and folders through the recycle callback (the app: Recycle Bin). Every file is hashed and
    /// logged first. All paths are checked before anything is deleted.
    /// </summary>
    public void Delete(IEnumerable<string> paths)
    {
        var list = paths.Select(p => CheckSource(p, "Delete")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // a folder's own descendants go with it
        list = list.Where(p => !list.Any(o => !ReferenceEquals(o, p) && p.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        // never delete through a link: a junction inside a folder could make the Recycle Bin operation reach files outside the case
        foreach (var p in list) if (Directory.Exists(p)) CheckNoReparsePointInside(p, "Delete");
        int done = 0;
        try
        {
            foreach (var p in list)
            {
                if (Directory.Exists(p))
                {
                    // hashes first (the files are gone afterwards); logged only once the folder is really recycled
                    var hashes = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                        .Select(f => (f, HashForLog(f))).ToList();
                    _recycle(p, true);
                    foreach (var (f, h) in hashes) Log("DELETE", f, "-", h);
                    Log("DELETE", p, "-", "-");
                    done++;
                    continue;
                }
                var sidecars = SidecarsOf(p);
                var hash = HashForLog(p);
                _recycle(p, false);
                Log("DELETE", p, "-", hash);
                foreach (var s in sidecars)
                {
                    var sh = HashForLog(s);
                    _recycle(s, false);
                    Log("DELETE", s, "-", sh);
                }
                done++;
            }
        }
        catch (Exception ex) when (done > 0 && ex is not CaseFileOperationException)
        {
            throw new CaseFileOperationException($"Delete stopped after {done} of {list.Count} item(s) were sent to the Recycle Bin: {ex.Message}");
        }
    }

    /// <summary>
    /// Why items under <paramref name="path"/> cannot safely go to the Recycle Bin, or null when they can. Windows deletes files on network
    /// shares (UNC paths, mapped drives) and on removable / optical / RAM drives permanently, without a Recycle Bin, so the app refuses to
    /// delete there rather than destroy anything. <paramref name="driveTypeOf"/> replaces <see cref="DriveInfo.DriveType"/> (unit tests).
    /// </summary>
    public static string? RecycleRefusal(string path, Func<string, DriveType>? driveTypeOf = null)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "the path is invalid."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return "the case is on a network share; Windows deletes files there permanently (no Recycle Bin). Delete them in Explorer if you really mean to.";
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return "the drive of the case folder is unknown.";
        DriveType type;
        try { type = driveTypeOf != null ? driveTypeOf(root) : new DriveInfo(root).DriveType; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return "the drive of the case folder could not be checked."; }
        return type == DriveType.Fixed
            ? null
            : $"the case is on a {(type == DriveType.Network ? "network" : type.ToString().ToLowerInvariant())} drive ({root.TrimEnd('\\')}); Windows deletes files there permanently (no Recycle Bin). Delete them in Explorer if you really mean to.";
    }

    // ------------------------------------------------------------ merge (derived output)

    /// <summary><c>&lt;case&gt;\&lt;host or Merged&gt;\Merged\Merged_&lt;n&gt;.csv</c> with the first free n (from 1).</summary>
    public static string DefaultMergePath(string caseDir, string? host)
    {
        var top = string.IsNullOrWhiteSpace(host) ? MergedName : ArtifactOutputLayout.SafeHostFolder(host);
        var dir = Path.Combine(Path.GetFullPath(caseDir), top, MergedName);
        for (int n = 1; ; n++)
        {
            var p = Path.Combine(dir, $"{MergedName}_{n}.csv");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>
    /// Merges <paramref name="inputs"/> (all inside the case) into a new CSV inside the case (<see cref="CsvMerger"/>; the file name is
    /// sanitised to a layout v2 output name, a taken name gets <c>_2</c>), records it in <c>logs\derived_outputs.json</c> and the operations log.
    /// </summary>
    public async Task<(string Path, CsvMergeResult Result)> MergeAsync(IReadOnlyList<CsvMergeInput> inputs, string outputPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (inputs.Count < 1) throw new CaseFileOperationException("Merge: no input files.");
        var checkedInputs = inputs.Select(i => i with { Path = CheckSource(i.Path, "Merge") }).ToList();
        if (checkedInputs.Any(i => Directory.Exists(i.Path))) throw new CaseFileOperationException("Merge: select CSV files only.");
        var outFull = Full(outputPath);
        var dir = Path.GetDirectoryName(outFull)!;
        if (!IsInsideCase(dir) && !IsCaseRoot(dir)) throw new CaseFileOperationException($"Merge: the output must be inside the case folder {CaseDir}.");
        if (IsInReservedFolder(dir)) throw new CaseFileOperationException("Merge: cannot write into logs, .ezstaging or .ezcache.");
        CheckNoReparsePoint(dir, "Merge");
        Directory.CreateDirectory(dir);
        var target = UniqueTarget(Path.Combine(dir, SanitizeOutputName(Path.GetFileName(outFull))), false);
        var result = await CsvMerger.MergeAsync(checkedInputs, target, new CsvMergeOptions(), progress, ct).ConfigureAwait(false);
        var record = new DerivedOutputRecord
        {
            Output = Relative(target), OutputSha256 = result.OutputSha256, Rows = result.RowsWritten, CreatedUtc = _clock(), User = User,
            Inputs = checkedInputs.Select(i => new DerivedOutputInput(Relative(i.Path), i.Host,
                result.InputSha256.TryGetValue(i.Path, out var h) ? h : "")).ToList(),
        };
        RecordDerivedOutput(record);
        foreach (var i in record.Inputs) Log("MERGE-INPUT", i.Path, record.Output, i.Sha256);
        Log("MERGE", string.Join(" + ", record.Inputs.Select(i => i.Path)), target, result.OutputSha256);
        return (target, result);
    }

    // Read-modify-write of derived_outputs.json: two merges finishing together must not drop each other's record.
    private static readonly object DerivedOutputsGate = new();

    /// <summary>
    /// Appends <paramref name="record"/> to <c>logs\derived_outputs.json</c> (a JSON array; rewritten atomically). A file that cannot be
    /// parsed is never overwritten: it is kept as <c>derived_outputs.corrupt-&lt;UTC time&gt;.json</c> and a new file is started.
    /// </summary>
    public void RecordDerivedOutput(DerivedOutputRecord record)
    {
        lock (DerivedOutputsGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DerivedOutputsPath)!);
            var list = TryReadDerivedOutputs(DerivedOutputsPath, out var parsed).ToList();
            if (!parsed)
            {
                var backup = UniqueTarget(Path.Combine(Path.GetDirectoryName(DerivedOutputsPath)!,
                    $"derived_outputs.corrupt-{_clock().ToUniversalTime():yyyyMMddTHHmmssZ}.json"), false);
                File.Move(DerivedOutputsPath, backup);
                Log("DERIVED-BACKUP", DerivedOutputsPath, backup, HashForLog(backup));
            }
            list.Add(record);
            var tmp = DerivedOutputsPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json), new UTF8Encoding(false));
                File.Move(tmp, DerivedOutputsPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    public static IReadOnlyList<DerivedOutputRecord> ReadDerivedOutputs(string caseDir) =>
        TryReadDerivedOutputs(Path.Combine(caseDir, LogsFolderName, DerivedOutputsFileName), out _);

    /// <summary>The records; <paramref name="parsed"/> is false when the file exists but is not a valid record list (then empty).</summary>
    private static IReadOnlyList<DerivedOutputRecord> TryReadDerivedOutputs(string path, out bool parsed)
    {
        parsed = true;
        if (!File.Exists(path)) return Array.Empty<DerivedOutputRecord>();
        try { return JsonSerializer.Deserialize<List<DerivedOutputRecord>>(File.ReadAllText(path), Json) ?? new List<DerivedOutputRecord>(); }
        catch (JsonException) { parsed = false; return Array.Empty<DerivedOutputRecord>(); }
    }

    // ------------------------------------------------------------ combined view cache

    /// <summary><c>&lt;case&gt;\.ezcache\combined</c>.</summary>
    public static string CombinedCacheDir(string caseDir) => Path.Combine(Path.GetFullPath(caseDir), CacheFolderName, CombinedFolderName);

    /// <summary>Cache key of a combined view: SHA-256 (16 hex) over the paths (in order), sizes and last-write times.</summary>
    public static string CombinedKey(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            var fi = new FileInfo(p);
            sb.Append(Path.GetFullPath(p).ToUpperInvariant()).Append('|')
              .Append(fi.Exists ? fi.Length : -1).Append('|')
              .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>Creates the hidden <c>.ezcache</c> folder (and <c>combined</c>) and returns the combined folder.</summary>
    public static string EnsureCombinedCacheDir(string caseDir)
    {
        var cache = Path.Combine(Path.GetFullPath(caseDir), CacheFolderName);
        var di = Directory.CreateDirectory(cache);
        try { if ((di.Attributes & FileAttributes.Hidden) == 0) di.Attributes |= FileAttributes.Hidden; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Directory.CreateDirectory(Path.Combine(cache, CombinedFolderName)).FullName;
    }

    /// <summary>Keeps the <paramref name="keep"/> most recently used combined views (and their sidecars); deletes older ones. Cache only.</summary>
    public static void PruneCombinedCache(string caseDir, int keep, string? except = null)
    {
        var dir = CombinedCacheDir(caseDir);
        if (!Directory.Exists(dir)) return;
        var csvs = new DirectoryInfo(dir).EnumerateFiles("*.csv").OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc).ToList();
        foreach (var f in csvs.Skip(keep))
        {
            if (except != null && string.Equals(f.FullName, Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var s in SidecarsOf(f.FullName)) File.Delete(s);
                f.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------ log

    /// <summary>Appends one tab-separated line to <c>logs\file_operations.log</c>.</summary>
    public void Log(string op, string from, string to, string sha256)
    {
        var line = string.Join('\t', _clock().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), User, op,
            Clean(from), Clean(to), string.IsNullOrEmpty(sha256) ? "-" : sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var w = new StreamWriter(fs, new UTF8Encoding(false));
        w.Write(line + "\r\n");
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Lower-case SHA-256 of a file up to <see cref="MaxHashBytes"/>; <c>skipped:&gt;256MB</c> / <c>error:...</c> otherwise.</summary>
    public static string HashForLog(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "-";
            if (fi.Length > MaxHashBytes) return "skipped:>256MB";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "error:" + ex.GetType().Name; }
    }

    private static string SafeUserName()
    {
        try
        {
            var d = Environment.UserDomainName;
            var u = Environment.UserName;
            return string.IsNullOrEmpty(d) ? u : d + "\\" + u;
        }
        catch (Exception) { return "unknown"; }
    }
}
