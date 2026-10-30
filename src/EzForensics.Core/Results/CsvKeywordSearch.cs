using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace EzForensics.Core.Results;

/// <summary>A file that matched a keyword. <see cref="HitCount"/> counts matching lines, capped at <see cref="CsvKeywordSearch.MaxCount"/>.</summary>
/// <param name="FirstHitRow">1-based physical line number of the first matching line (the header is line 1); 0 when only the file name matched.</param>
public sealed record KeywordSearchHit(string Path, int HitCount, long FirstHitRow, bool FileNameMatch, bool CountCapped);

public sealed record KeywordSearchProgress(int FilesDone, int FilesTotal, string CurrentPath);

public static class CsvKeywordSearch
{
    public const int MaxCount = 10_000;

    /// <summary>
    /// Case-insensitive ordinal substring search over the raw text of each CSV (line streamed, never fully loaded) and its file name.
    /// Files are scanned in parallel; hits are yielded as files finish. Files that do not match are not yielded.
    /// Unreadable files are skipped. Throws <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public static async IAsyncEnumerable<KeywordSearchHit> SearchAsync(
        IEnumerable<string> csvPaths, string keyword, IProgress<KeywordSearchProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(csvPaths);
        var paths = csvPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrEmpty(keyword) || paths.Count == 0) yield break;

        var channel = Channel.CreateUnbounded<KeywordSearchHit>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int done = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                var opts = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    CancellationToken = cts.Token,
                };
                await Parallel.ForEachAsync(paths, opts, async (path, token) =>
                {
                    var hit = await Task.Run(() => ScanFile(path, keyword, token), token).ConfigureAwait(false);
                    if (hit != null) await channel.Writer.WriteAsync(hit, token).ConfigureAwait(false);
                    progress?.Report(new KeywordSearchProgress(Interlocked.Increment(ref done), paths.Count, path));
                }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var hit in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return hit;
        }
        finally
        {
            cts.Cancel();
            try { await producer.ConfigureAwait(false); } catch { /* surfaced through the channel */ }
        }
    }

    /// <summary>Convenience: collects every hit, ordered by descending hit count then path.</summary>
    public static async Task<IReadOnlyList<KeywordSearchHit>> SearchToListAsync(
        IEnumerable<string> csvPaths, string keyword, IProgress<KeywordSearchProgress>? progress = null, CancellationToken ct = default)
    {
        var list = new List<KeywordSearchHit>();
        await foreach (var h in SearchAsync(csvPaths, keyword, progress, ct).ConfigureAwait(false)) list.Add(h);
        return list.OrderByDescending(h => h.HitCount).ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static KeywordSearchHit? ScanFile(string path, string keyword, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool nameMatch = System.IO.Path.GetFileName(path).Contains(keyword, StringComparison.OrdinalIgnoreCase);
        int count = 0;
        long first = 0, line = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            using var sr = new StreamReader(fs, new UTF8Encoding(false), true, 65536);
            string? l;
            while ((l = sr.ReadLine()) != null)
            {
                line++;
                if ((line & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (l.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    if (count == 0) first = line;
                    if (++count >= MaxCount) break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable file: report only a file-name match
        }
        if (count == 0 && !nameMatch) return null;
        return new KeywordSearchHit(path, count, first, nameMatch, count >= MaxCount);
    }
}
