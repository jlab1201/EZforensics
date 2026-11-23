using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace EzForensics.Core.Explore.Search;

/// <summary>
/// Agent Ransack-style file search over folders (read-only: evidence files are only opened for reading, never executed).
/// Folders are walked depth-first with manual recursion; junctions and symbolic links are listed but never entered; one unreadable
/// file or folder is counted in <see cref="SearchProgress.SkippedByReason"/> / <see cref="Skipped"/> and the search goes on.
/// Name and metadata filters run first; content is searched only for the survivors, a few files in parallel. Results stream as found
/// (order is not guaranteed). Use one instance per search.
/// </summary>
public sealed class FileSearcher
{
    /// <summary>Most skipped items kept in <see cref="Skipped"/> (the counts in progress are never capped).</summary>
    public const int MaxSkippedKept = 1000;

    private static readonly EnumerationOptions Enum = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    private readonly ConcurrentQueue<SkippedItem> _skipped = new();
    private int _skippedKept;
    private Stats _stats = new();

    /// <summary>Items that were not (fully) searched, with why (the first <see cref="MaxSkippedKept"/>).</summary>
    public IReadOnlyList<SkippedItem> Skipped => _skipped.ToArray();

    /// <summary>Progress interval (default 100 ms).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Streams matches. Throws <see cref="ArgumentException"/> when <see cref="SearchValidation.Validate"/> finds errors, and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled. Stops after <see cref="SearchOptions.MaxResults"/>.
    /// </summary>
    public async IAsyncEnumerable<SearchResult> SearchAsync(SearchOptions options, IProgress<SearchProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var errors = SearchValidation.Validate(options);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(options));

        _stats = new Stats();
        var stats = _stats;
        var nameMatch = CreateNameMatcher(options);
        var excluded = CreateExcludeMatcher(options.ExcludeFolders);
        var matcher = options.HasContentCriteria ? ContentMatcher.CreateFactory(options) : null;
        var reporter = new Reporter(progress, stats, ProgressInterval);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var output = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(1024) { SingleReader = true });
        var work = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(256) { SingleWriter = true });

        var ctx = new WalkContext(options, nameMatch, excluded, matcher != null, work.Writer, output.Writer, stats, reporter, this, token);
        var producer = Task.Run(async () =>
        {
            try { await ProduceAsync(ctx).ConfigureAwait(false); }
            finally { work.Writer.TryComplete(); }
        }, token);

        int dop = Math.Clamp(options.MaxParallelism, 1, 16);
        var workers = matcher == null
            ? Array.Empty<Task>()
            : Enumerable.Range(0, dop).Select(_ => Task.Run(async () =>
            {
                await foreach (var e in work.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref stats.ContentSearched);
                    var r = ContentScanner.Scan(e, options, matcher, token);
                    if (r.Skip is { } why) AddSkip(e.FullPath, why, r.Message);
                    else if (r.Result != null) await output.Writer.WriteAsync(r.Result, token).ConfigureAwait(false);
                    reporter.Maybe();
                }
            }, token)).ToArray();

        var all = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(workers.Append(producer)).ConfigureAwait(false);
                output.Writer.TryComplete();
            }
            catch (Exception ex) { output.Writer.TryComplete(ex); }
        });

        try
        {
            await foreach (var r in output.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var m = Interlocked.Increment(ref stats.Matches);
                yield return r;
                reporter.Maybe();
                if (m >= options.MaxResults) { stats.LimitReached = true; break; }
            }
        }
        finally
        {
            cts.Cancel();
            try { await all.ConfigureAwait(false); } catch { /* reported through the reader */ }
            stats.Cancelled = ct.IsCancellationRequested;
            stats.CurrentFolder = null;
            reporter.Report(complete: true);
        }
    }

    // ---------------- Enumeration ----------------

    private sealed record WalkContext(
        SearchOptions O, Func<string, bool> NameMatch, Func<string, string, bool> Excluded, bool Content,
        ChannelWriter<FileEntry> Work, ChannelWriter<SearchResult> Output, Stats Stats, Reporter Reporter, FileSearcher Owner, CancellationToken Ct);

    private static async Task ProduceAsync(WalkContext c)
    {
        foreach (var root in NormalizeRoots(c.O))
        {
            c.Ct.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                if (FileEntry.FromPath(root) is { } fe)
                {
                    Interlocked.Increment(ref c.Stats.FilesScanned);
                    await ConsiderAsync(c, fe).ConfigureAwait(false);
                }
                else c.Owner.AddSkip(root, SkipReason.FileUnreadable, "The file's details could not be read.");
            }
            else if (Directory.Exists(root)) await WalkAsync(c, root).ConfigureAwait(false);
            else c.Owner.AddSkip(root, SkipReason.RootMissing, "Not found.");
        }
    }

    /// <summary>Full paths, without duplicates and (when subfolders are searched) without roots inside another folder root.</summary>
    internal static List<string> NormalizeRoots(SearchOptions o)
    {
        var list = new List<string>();
        foreach (var r in o.Roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string full;
            try { full = Path.GetFullPath(r.Trim()); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            var root = Path.GetPathRoot(full);
            if (full.Length > (root?.Length ?? 0)) full = full.TrimEnd('\\', '/');
            if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
        }
        if (!o.IncludeSubfolders) return list;
        return list.Where(r => !list.Any(other => !ReferenceEquals(other, r) && Directory.Exists(other) && IsUnder(r, other))).ToList();
    }

    private static bool IsUnder(string path, string folder)
    {
        var f = folder.EndsWith('\\') || folder.EndsWith('/') ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WalkAsync(WalkContext c, string rootDir)
    {
        var o = c.O;
        var stack = new Stack<string>();
        stack.Push(rootDir);
        var subdirs = new List<string>();
        while (stack.Count > 0)
        {
            c.Ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            Interlocked.Increment(ref c.Stats.FoldersScanned);
            c.Stats.CurrentFolder = dir;
            c.Reporter.Maybe();
            subdirs.Clear();

            IEnumerator<FileSystemInfo>? it = null;
            try { it = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Enum).GetEnumerator(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                c.Owner.AddSkip(dir, SkipReason.FolderUnreadable, ex.Message);
                continue;
            }

            using (it)
            {
                while (true)
                {
                    FileSystemInfo info;
                    try
                    {
                        if (!it.MoveNext()) break;
                        info = it.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        c.Owner.AddSkip(dir, SkipReason.FolderUnreadable, ex.Message);
                        break;
                    }
                    c.Ct.ThrowIfCancellationRequested();

                    FileEntry entry;
                    try { entry = FileEntry.From(info); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        entry = new FileEntry(info.FullName, info.Name, info is DirectoryInfo, 0, default, default, default, 0);
                    }

                    if (!o.IncludeHidden && entry.IsHidden) continue;
                    if (!o.IncludeSystem && entry.IsSystem) continue;

                    if (entry.IsDirectory)
                    {
                        if (c.Excluded(entry.Name, entry.FullPath)) continue;
                        bool link = entry.IsReparsePoint && IsFolderLink(info);
                        if (link) c.Owner.AddSkip(entry.FullPath, SkipReason.LinkNotFollowed, "Junction or symbolic link: listed, not entered.");
                        else if (o.IncludeSubfolders) subdirs.Add(entry.FullPath);
                        if (o.Target != SearchTarget.Files) await ConsiderAsync(c, entry).ConfigureAwait(false);
                    }
                    else
                    {
                        Interlocked.Increment(ref c.Stats.FilesScanned);
                        if (o.Target != SearchTarget.Folders) await ConsiderAsync(c, entry).ConfigureAwait(false);
                    }
                }
            }

            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
    }

    /// <summary>True for a junction or symbolic link (has a link target); other reparse folders (cloud placeholders) are ordinary.</summary>
    private static bool IsFolderLink(FileSystemInfo di)
    {
        try { return di.LinkTarget != null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static async ValueTask ConsiderAsync(WalkContext c, FileEntry e)
    {
        if (!PassesFilters(e, c.O) || !c.NameMatch(e.Name)) return;
        if (e.IsDirectory || !c.Content) await c.Output.WriteAsync(SearchResult.From(e), c.Ct).ConfigureAwait(false);
        else await c.Work.WriteAsync(e, c.Ct).ConfigureAwait(false);
    }

    // ---------------- Matching ----------------

    internal static bool PassesFilters(FileEntry e, SearchOptions o)
    {
        if (!e.IsDirectory)
        {
            if (o.MinSize is { } min && e.Length < min) return false;
            if (o.MaxSize is { } max && e.Length > max) return false;
        }
        if (!InRange(e.ModifiedUtc, o.ModifiedAfter, o.ModifiedBefore)) return false;
        if (!InRange(e.CreatedUtc, o.CreatedAfter, o.CreatedBefore)) return false;
        if (!InRange(e.AccessedUtc, o.AccessedAfter, o.AccessedBefore)) return false;
        if ((e.Attributes & o.AttributesMustHave) != o.AttributesMustHave) return false;
        if ((e.Attributes & o.AttributesMustNotHave) != 0) return false;
        if (!o.IncludeHidden && e.IsHidden) return false;
        if (!o.IncludeSystem && e.IsSystem) return false;
        return true;

        static bool InRange(DateTime t, DateTime? after, DateTime? before) =>
            (after is not { } a || t >= a) && (before is not { } b || t <= b);
    }

    /// <summary>Name predicate for <paramref name="o"/> (see <see cref="NameMatchMode"/>); options are assumed valid.</summary>
    public static Func<string, bool> CreateNameMatcher(SearchOptions o)
    {
        if (!o.HasNameCriteria) return _ => true;
        var text = o.NameText!;
        if (o.NameAsFilter) return n => NameFilter.Matches(n, text);
        var cmp = o.NameMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (o.NameMode)
        {
            case NameMatchMode.Regex:
                var rx = new Regex(text, RegexOptions.CultureInvariant | (o.NameMatchCase ? 0 : RegexOptions.IgnoreCase), ContentMatcher.RegexTimeout);
                return n =>
                {
                    try { return rx.IsMatch(n); }
                    catch (RegexMatchTimeoutException) { return false; }
                };
            case NameMatchMode.Wildcard:
            {
                if (!SearchExpression.TryParse(text, true, true, out var expr, out var err)) throw new ArgumentException(err);
                bool ignoreCase = !o.NameMatchCase;
                return n => expr!.Evaluate(t => t.IndexOfAny(Wild) >= 0
                    ? FileSystemName.MatchesSimpleExpression(t, n, ignoreCase)
                    : n.Contains(t, cmp));
            }
            default:
            {
                if (!SearchExpression.TryParse(text, false, false, out var expr, out var err)) throw new ArgumentException(err);
                return n => expr!.Evaluate(t => n.Contains(t, cmp));
            }
        }
    }

    private static readonly char[] Wild = { '*', '?' };

    /// <summary>(name, fullPath) predicate for <see cref="SearchOptions.ExcludeFolders"/>.</summary>
    internal static Func<string, string, bool> CreateExcludeMatcher(IReadOnlyList<string> patterns)
    {
        var names = new List<string>();
        var paths = new List<string>();
        foreach (var raw in patterns)
        {
            var p = raw?.Trim().Trim('"') ?? "";
            if (p.Length == 0) continue;
            // FileSystemName treats '\' as an escape character: double it so path separators match literally.
            if (p.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) paths.Add(p.Replace('/', '\\').TrimEnd('\\').Replace(@"\", @"\\"));
            else names.Add(p);
        }
        if (names.Count == 0 && paths.Count == 0) return (_, _) => false;
        return (name, full) =>
        {
            foreach (var n in names) if (FileSystemName.MatchesSimpleExpression(n, name, true)) return true;
            foreach (var p in paths) if (FileSystemName.MatchesSimpleExpression(p, full, true)) return true;
            return false;
        };
    }

    // ---------------- Bookkeeping ----------------

    private void AddSkip(string path, SkipReason reason, string? message)
    {
        _stats.AddSkip(reason);
        if (Interlocked.Increment(ref _skippedKept) <= MaxSkippedKept) _skipped.Enqueue(new SkippedItem(path, reason, message));
    }

    private sealed class Stats
    {
        public long FoldersScanned, FilesScanned, ContentSearched, Matches;
        public volatile string? CurrentFolder;
        public volatile bool LimitReached, Cancelled;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly long[] _skips = new long[System.Enum.GetValues<SkipReason>().Length];

        public void AddSkip(SkipReason r) => Interlocked.Increment(ref _skips[(int)r]);

        public SearchProgress Snapshot(bool complete)
        {
            var by = new Dictionary<SkipReason, long>();
            long total = 0;
            for (int i = 0; i < _skips.Length; i++)
            {
                var v = Interlocked.Read(ref _skips[i]);
                if (v == 0) continue;
                by[(SkipReason)i] = v;
                total += v;
            }
            return new SearchProgress(Interlocked.Read(ref FoldersScanned), Interlocked.Read(ref FilesScanned), Interlocked.Read(ref ContentSearched),
                Interlocked.Read(ref Matches), total, by, CurrentFolder, Clock.Elapsed, complete, LimitReached, Cancelled);
        }
    }

    private sealed class Reporter
    {
        private readonly IProgress<SearchProgress>? _p;
        private readonly Stats _s;
        private readonly long _intervalMs;
        private long _last = -1;

        public Reporter(IProgress<SearchProgress>? p, Stats s, TimeSpan interval)
        {
            _p = p; _s = s; _intervalMs = (long)interval.TotalMilliseconds;
        }

        public void Maybe()
        {
            if (_p == null) return;
            long now = Environment.TickCount64, last = Interlocked.Read(ref _last);
            if (last >= 0 && now - last < _intervalMs) return;
            if (Interlocked.CompareExchange(ref _last, now, last) != last) return;
            _p.Report(_s.Snapshot(false));
        }

        public void Report(bool complete) => _p?.Report(_s.Snapshot(complete));
    }
}
