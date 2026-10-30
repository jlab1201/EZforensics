using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Tools;

public enum RemoteFileStatus { UpToDate, New, Changed }

public sealed record RemoteToolFile(string Name, string Url, string? ETag, long Size, bool IsNet9, RemoteFileStatus Status);

public sealed record UpdateProgress(string Name, int FileIndex, int FileCount, long BytesReceived, long? TotalBytes, string Phase);

public sealed record RemoteDetail(string Name, string Sha1, string Url, long Size, bool IsNet9);

public sealed class ToolUpdater
{
    public const string IndexUrl = "https://tools.ericzimmermanstools.com";
    public const string DetailsFileName = "!!!RemoteFileDetails.csv";
    private const string B2Prefix = "https://f001.backblazeb2.com/file/EricZimmermanTools/";
    private const string DownloadPrefix = "https://download.ericzimmermanstools.com/";

    private static readonly Regex LinkRegex = new(
        @"\b(https)://[-A-Z0-9+&@#/%?=~_|$!:,.;]*[A-Z0-9+&@#/%=~_|$].(zip|txt)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] Excluded = { "kape.zip", "All.zip", "All_9.zip" };

    private readonly HttpClient _http;

    public ToolUpdater(HttpClient http)
    {
        _http = http;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("EzForensics/1.0");
    }

    // ---------- parsing ----------

    /// <summary>Extract the download URLs from the index page. netVersion: 4, 9 or 0 (both).</summary>
    public static IReadOnlyList<string> ParseLinks(string html, int netVersion)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in LinkRegex.Matches(html))
        {
            var url = m.Value.Replace(B2Prefix, DownloadPrefix, StringComparison.OrdinalIgnoreCase);
            var name = FileNameOf(url);
            if (Excluded.Any(e => e.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            bool net9 = IsNet9Url(url);
            bool always = name.Equals("Get-ZimmermanTools.zip", StringComparison.OrdinalIgnoreCase);
            if (!always)
            {
                if (netVersion == 9 && !net9) continue;
                if (netVersion == 4 && net9) continue;
            }
            if (seen.Add(url)) result.Add(url);
        }
        return result;
    }

    public static bool IsNet9Url(string url) => url.Contains("/net9/", StringComparison.OrdinalIgnoreCase);

    public static string FileNameOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    // ---------- RemoteFileDetails.csv ----------

    public static IReadOnlyList<RemoteDetail> ReadDetails(string csvPath)
    {
        var list = new List<RemoteDetail>();
        if (!File.Exists(csvPath)) return list;
        var lines = File.ReadAllText(csvPath, Encoding.UTF8);
        var rows = ParseCsv(lines);
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Count < 5) continue;
            long.TryParse(r[3], out var size);
            bool.TryParse(r[4], out var net9);
            list.Add(new RemoteDetail(r[0], r[1], r[2], size, net9));
        }
        return list;
    }

    public static void WriteDetails(string csvPath, IEnumerable<RemoteDetail> details)
    {
        var sb = new StringBuilder();
        sb.Append("\"Name\",\"SHA1\",\"URL\",\"Size\",\"IsNet9\"\r\n");
        foreach (var d in details)
            sb.Append(Q(d.Name)).Append(',').Append(Q(d.Sha1)).Append(',').Append(Q(d.Url)).Append(',')
              .Append(Q(d.Size.ToString())).Append(',').Append(Q(d.IsNet9 ? "True" : "False")).Append("\r\n");
        var tmp = csvPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, csvPath, overwrite: true);
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQ = false;
                }
                else cur.Append(c);
            }
            else if (c == '"') { inQ = true; any = true; }
            else if (c == ',') { row.Add(cur.ToString()); cur.Clear(); any = true; }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                if (any || cur.Length > 0) { row.Add(cur.ToString()); rows.Add(row); }
                row = new List<string>(); cur.Clear(); any = false;
            }
            else { cur.Append(c); any = true; }
        }
        if (any || cur.Length > 0) { row.Add(cur.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>Compare remote entries (Name/ETag/Url/Size/IsNet9) with the local details file.</summary>
    public static List<RemoteToolFile> Diff(IEnumerable<RemoteToolFile> remote, IReadOnlyList<RemoteDetail> local)
    {
        var byUrl = local.GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var res = new List<RemoteToolFile>();
        foreach (var r in remote)
        {
            RemoteFileStatus st;
            if (!byUrl.TryGetValue(r.Url, out var l)) st = RemoteFileStatus.New;
            else if (string.IsNullOrEmpty(r.ETag) || !string.Equals(l.Sha1, r.ETag, StringComparison.OrdinalIgnoreCase))
                st = RemoteFileStatus.Changed;
            else st = RemoteFileStatus.UpToDate;
            res.Add(r with { Status = st });
        }
        return res;
    }

    // ---------- network ----------

    public async Task<List<RemoteToolFile>> CheckAsync(string root, int netVersion, CancellationToken ct)
    {
        var html = await _http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
        var urls = ParseLinks(html, netVersion);

        using var gate = new SemaphoreSlim(8);
        var tasks = urls.Select(async url =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string? etag = null; long size = 0;
                for (int attempt = 1; attempt <= 3 && etag is null; attempt++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Head, url);
                        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            etag = resp.Headers.ETag?.Tag?.Trim('"');
                            size = resp.Content.Headers.ContentLength ?? 0;
                            if (etag is null) break;
                        }
                    }
                    catch (HttpRequestException) { }
                    if (etag is null) await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false); // transient CDN error
                }
                return new RemoteToolFile(FileNameOf(url), url, etag, size, IsNet9Url(url), RemoteFileStatus.New);
            }
            finally { gate.Release(); }
        }).ToList();
        var remote = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();

        var local = ReadDetails(Path.Combine(root, DetailsFileName));
        return Diff(remote, local);
    }

    public async Task DownloadAsync(IEnumerable<RemoteToolFile> selected, string root, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var files = selected.ToList();
        Directory.CreateDirectory(root);
        var csvPath = Path.Combine(root, DetailsFileName);
        var details = ReadDetails(csvPath).ToList();

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            var targetDir = f.IsNet9 ? Path.Combine(root, "net9") : root;
            Directory.CreateDirectory(targetDir);
            var zipPath = Path.Combine(targetDir, f.Name);
            var etagSeen = f.ETag;
            long sizeSeen = f.Size;

            progress?.Report(new UpdateProgress(f.Name, i + 1, files.Count, 0, f.Size > 0 ? f.Size : null, "Downloading"));
            var partPath = zipPath + ".part";
            for (int attempt = 1; ; attempt++)
            {
                // A stalled connection (no bytes for StallTimeout) is cancelled and retried instead of hanging forever.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    stall.CancelAfter(StallTimeout);
                    using (var resp = await _http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        if (string.IsNullOrEmpty(etagSeen)) etagSeen = resp.Headers.ETag?.Tag?.Trim('"');
                        if (sizeSeen == 0) sizeSeen = resp.Content.Headers.ContentLength ?? 0;
                        long? total = resp.Content.Headers.ContentLength ?? (f.Size > 0 ? f.Size : null);
                        await using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                        await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        var buf = new byte[81920];
                        long got = 0; int n;
                        stall.CancelAfter(StallTimeout);
                        while ((n = await src.ReadAsync(buf, stall.Token).ConfigureAwait(false)) > 0)
                        {
                            stall.CancelAfter(StallTimeout);
                            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                            got += n;
                            progress?.Report(new UpdateProgress(f.Name, i + 1, files.Count, got, total, "Downloading"));
                        }
                        if (total is > 0 && got != total)
                            throw new IOException($"The connection closed early ({got:N0} of {total:N0} bytes).", new System.Net.Sockets.SocketException(10054));
                    }
                    File.Move(partPath, zipPath, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested && IsTransient(ex))
                {
                    TryDelete(partPath);
                    progress?.Report(new UpdateProgress(f.Name, i + 1, files.Count, 0, null, $"Retrying ({attempt + 1}/{MaxAttempts})"));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt * attempt)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    TryDelete(partPath);
                    throw new IOException($"The download of {f.Name} stalled (no data for {StallTimeout.TotalSeconds:0} s).", new System.Net.Sockets.SocketException(10060));
                }
                catch { TryDelete(partPath); throw; }
            }

            if (f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new UpdateProgress(f.Name, i + 1, files.Count, 0, null, "Extracting"));
                ExtractZip(zipPath, targetDir);
                File.Delete(zipPath);
            }

            details.RemoveAll(d => d.Url.Equals(f.Url, StringComparison.OrdinalIgnoreCase));
            details.Add(new RemoteDetail(f.Name, etagSeen ?? "", f.Url, sizeSeen, f.IsNet9));
            WriteDetails(csvPath, details);
            progress?.Report(new UpdateProgress(f.Name, i + 1, files.Count, f.Size, f.Size, "Done"));
        }
    }

    public const int MaxAttempts = 5;
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Connection resets, early closes, stalls, 5xx and 429 are worth another try; 404 and disk errors are not.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode is null || (int)h.StatusCode >= 500 || (int)h.StatusCode == 429,
        OperationCanceledException => true, // stall timeout (caller excludes user cancellation)
        IOException io => IsNetworkIo(io),
        _ => false,
    };

    /// <summary>An IOException raised by the network (socket reset/timeout, HTTP body), not by the disk.</summary>
    public static bool IsNetworkIo(IOException io) =>
        io is HttpIOException || io.InnerException is System.Net.Sockets.SocketException or HttpIOException;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public static void ExtractZip(string zipPath, string targetDir)
    {
        var full = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(targetDir, e.FullName));
            if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
        }
    }
}
