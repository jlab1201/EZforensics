using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Persistence;

/// <summary>
/// WMI permanent event subscriptions in an offline CIM repository (<c>Windows\System32\wbem\Repository\OBJECTS.DATA</c>).
/// Re-implements the string-carving approach of PyWMIPersistenceFinder (David Pany, Mandiant, MIT licence,
/// https://github.com/davidpany/WMI_Forensics): find <c>__FilterToConsumerBinding</c> instances, take the consumer and filter
/// names from their <c>...EventConsumer.Name="..."</c> / <c>__EventFilter.Name="..."</c> references, then carve the consumer
/// (command line or script) and the filter (WQL query) records that carry those names. The repository structure is not parsed,
/// so stale copies in unallocated pages can show up too (that is useful: deleted subscriptions are found as well).
/// </summary>
public static class WmiPersistence
{
    private const int Chunk = 16 << 20, Overlap = 256 << 10;
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly Regex ConsumerRef = new(@"(\w*EventConsumer)\.Name=""([^""\x00]{1,256})""", RegexOptions.Compiled);
    private static readonly Regex FilterRef = new(@"_EventFilter\.Name=""([^""\x00]{1,256})""", RegexOptions.Compiled);

    /// <summary>Pairs that ship with Windows / SCCM and are usually legitimate.</summary>
    public static readonly (string Consumer, string Filter)[] KnownLegitimate =
    {
        ("BVTConsumer", "BVTFilter"), ("SCM Event Log Consumer", "SCM Event Log Filter"),
    };

    public static string RepositoryPath(string systemRoot) => Path.Combine(systemRoot, "Windows", "System32", "wbem", "Repository", "OBJECTS.DATA");

    public static List<WmiFinding> Scan(string objectsData, CancellationToken ct = default)
    {
        using var fs = new FileStream(objectsData, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Scan(fs, ct);
    }

    /// <summary>Scans the repository in 16 MB chunks (with overlap) so large repositories stay within bounded memory.</summary>
    public static List<WmiFinding> Scan(Stream s, CancellationToken ct = default)
    {
        // Pass 1: bindings.
        var bindings = new List<(string ConsumerType, string Consumer, string Filter)>();
        ForEachChunk(s, ct, text =>
        {
            int i = 0;
            while ((i = text.IndexOf("_FilterToConsumerBinding", i, StringComparison.Ordinal)) >= 0)
            {
                var win = text.Substring(i, Math.Min(2048, text.Length - i));
                var c = ConsumerRef.Match(win);
                var f = FilterRef.Match(win);
                if (c.Success && f.Success)
                {
                    var b = (c.Groups[1].Value, c.Groups[2].Value, f.Groups[1].Value);
                    if (!bindings.Contains(b)) bindings.Add(b);
                }
                i += 24;
            }
        });
        if (bindings.Count == 0) return new List<WmiFinding>();

        // Pass 2: consumer and filter records carrying those names.
        var consumers = new Dictionary<string, (string Type, string? Engine, string Command, long Offset)>(StringComparer.Ordinal);
        var filters = new Dictionary<string, (string? Ns, string Query, long Offset)>(StringComparer.Ordinal);
        var consumerNames = bindings.Select(b => b.Consumer).Distinct().ToList();
        var filterNames = bindings.Select(b => b.Filter).Distinct().ToList();
        long chunkStart = 0;
        ForEachChunk(s, ct, text =>
        {
            foreach (var name in consumerNames)
            {
                if (consumers.ContainsKey(name)) continue;
                foreach (var at in Occurrences(text, "\0" + name + "\0"))
                {
                    var before = text.Substring(Math.Max(0, at - 512), Math.Min(512, at));
                    var cls = new[] { "CommandLineEventConsumer", "ActiveScriptEventConsumer", "NTEventLogEventConsumer", "LogFileEventConsumer", "SMTPEventConsumer" }
                        .Select(c => (c, before.LastIndexOf(c + "\0", StringComparison.Ordinal))).Where(x => x.Item2 >= 0).OrderByDescending(x => x.Item2).FirstOrDefault();
                    if (cls.c == null) continue;
                    var fields = Fields(text, at + 1 + name.Length, 6);
                    string? engine = null; string command;
                    if (cls.c == "ActiveScriptEventConsumer")
                    {
                        engine = fields.FirstOrDefault(f => f is "VBScript" or "JScript" or "vbscript" or "jscript");
                        command = fields.Where(f => f != engine).OrderByDescending(f => f.Length).FirstOrDefault() ?? "";
                    }
                    else if (cls.c == "CommandLineEventConsumer")
                        command = fields.FirstOrDefault(f => f.Contains('\\') || f.Contains(' ') || f.Contains(".exe", StringComparison.OrdinalIgnoreCase)) ?? string.Join(" ", fields);
                    else command = string.Join(" | ", fields.Where(f => f.Length > 1));
                    consumers[name] = (cls.c, engine, command.Trim(), chunkStart + at);
                    break;
                }
            }
            foreach (var name in filterNames)
            {
                if (filters.ContainsKey(name)) continue;
                foreach (var at in Occurrences(text, "\0" + name + "\0"))
                {
                    var before = text.Substring(Math.Max(0, at - 512), Math.Min(512, at));
                    if (before.LastIndexOf("__EventFilter\0", StringComparison.Ordinal) < 0) continue;
                    var fields = Fields(text, at + 1 + name.Length, 3);
                    var query = fields.FirstOrDefault(f => f.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
                    if (query == null) continue;
                    var nsMatch = Regex.Match(before, @"\x00(root\\[\w\\]+)\x00", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
                    filters[name] = (nsMatch.Success ? nsMatch.Groups[1].Value : null, query.Trim(), chunkStart + at);
                    break;
                }
            }
        }, start => chunkStart = start);

        return bindings.Select(b =>
        {
            consumers.TryGetValue(b.Consumer, out var c);
            filters.TryGetValue(b.Filter, out var f);
            return new WmiFinding
            {
                ConsumerType = c.Type ?? b.ConsumerType, ConsumerName = b.Consumer, FilterName = b.Filter, Command = c.Command ?? "",
                ScriptingEngine = c.Engine, FilterQuery = f.Query ?? "", Namespace = f.Ns,
                LikelyLegitimate = KnownLegitimate.Any(k => k.Consumer == b.Consumer && k.Filter == b.Filter),
                ConsumerOffset = c.Type != null ? c.Offset : -1, FilterOffset = f.Query != null ? f.Offset : -1,
            };
        }).ToList();
    }

    private static IEnumerable<int> Occurrences(string text, string needle)
    {
        int i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { yield return i; i += needle.Length; }
    }

    /// <summary>The next null-separated printable strings after <paramref name="from"/> (each WMI string is a flag byte + text + NUL).</summary>
    private static List<string> Fields(string text, int from, int max)
    {
        var list = new List<string>();
        int p = from;
        while (list.Count < max && p < text.Length)
        {
            while (p < text.Length && text[p] == '\0') p++;
            int start = p;
            while (p < text.Length && text[p] != '\0' && p - start < 1 << 20) p++;
            if (p == start) break;
            var s = text[start..p];
            if (s.Count(ch => ch < 0x20 && ch is not ('\r' or '\n' or '\t')) > s.Length / 10) break; // binary: end of the record
            list.Add(s);
        }
        return list;
    }

    private static void ForEachChunk(Stream s, CancellationToken ct, Action<string> onText, Action<long>? onStart = null)
    {
        s.Position = 0;
        var buf = new byte[Chunk + Overlap];
        long baseAbs = 0; // absolute offset of buf[0]
        int carried = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = 0, n;
            while (carried + read < buf.Length && (n = s.Read(buf, carried + read, buf.Length - carried - read)) > 0) read += n;
            int len = carried + read;
            if (len == 0 || (read == 0 && carried > 0)) break;
            onStart?.Invoke(baseAbs);
            onText(Latin1.GetString(buf, 0, len));
            if (len < buf.Length) break; // end of stream
            Buffer.BlockCopy(buf, len - Overlap, buf, 0, Overlap);
            baseAbs += len - Overlap;
            carried = Overlap;
        }
    }

    public static PersistenceEntry ToEntry(WmiFinding w, string objectsData)
    {
        var e = new PersistenceEntry
        {
            Category = PersistenceCategory.Wmi,
            Location = @"Windows\System32\wbem\Repository\OBJECTS.DATA" + (w.Namespace != null ? $" ({w.Namespace})" : ""),
            EntryName = $"{w.ConsumerName} <- {w.FilterName}",
            Command = w.Command,
            ImagePath = w.ConsumerType == "CommandLineEventConsumer" ? PersistenceHeuristics.ImageFromCommand(w.Command) : "",
            Enabled = "",
            Source = objectsData,
            Notes = string.Join("; ", new[]
            {
                "Consumer=" + w.ConsumerType,
                w.ScriptingEngine != null ? "Engine=" + w.ScriptingEngine : null,
                w.FilterQuery.Length > 0 ? "Filter query=" + w.FilterQuery : "Filter query not found",
                w.LikelyLegitimate ? "Known Windows/SCCM subscription (usually legitimate)" : null,
                w.ConsumerOffset >= 0 ? $"Consumer at offset 0x{w.ConsumerOffset:X}" : null,
            }.Where(x => x != null)),
        };
        if (!w.LikelyLegitimate) e.Flag(PersistenceHeuristics.WmiConsumer);
        return e;
    }
}
