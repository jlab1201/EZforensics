using System.Text;
using System.Text.Json;

namespace EzForensics.Core.Knowledge;

/// <summary>Sends a prompt to the AI and returns its raw text reply.</summary>
public delegate Task<string> KnowledgeCompletion(string prompt, CancellationToken ct);

public sealed record LabelRunResult(int Labelled, int Failed);

/// <summary>Asks the AI for a summary, category, type and tags of notes, 6 per call. Never throws on AI failure; a failed batch sets ai_state='failed'.</summary>
public sealed class KnowledgeLabeler(KnowledgeCompletion complete, string modelName, TimeSpan? perCallTimeout = null)
{
    /// <summary>Local models (qwen3 with thinking) need minutes for one batch of 6; the HttpClient allows 10.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);
    public const int BatchSize = 6;
    public const int BodyChars = 4000;
    private readonly TimeSpan _timeout = perCallTimeout ?? DefaultTimeout;

    /// <summary>Labels the notes in batches. The store is used by the calling flow only (one batch at a time). Cancelling throws <see cref="OperationCanceledException"/> and keeps finished batches.</summary>
    public async Task<LabelRunResult> LabelAsync(KnowledgeStore store, IReadOnlyList<long> noteIds, IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        int labelled = 0, failed = 0, done = 0;
        var total = noteIds.Count;
        for (var start = 0; start < total; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = noteIds.Skip(start).Take(BatchSize).ToList();
            var ids = new List<long>();
            var items = new List<(string Title, string Body, IReadOnlyList<string> Hints)>();
            var existing = new List<string[]>();
            foreach (var id in batch)
            {
                var n = store.Note(id);
                if (n == null) { failed++; continue; }
                var body = n.Value.Body.Length > BodyChars ? n.Value.Body[..BodyChars] : n.Value.Body;
                ids.Add(id);
                items.Add((n.Value.Title, body, store.ConceptLabels(id, 10)));
                existing.Add(n.Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            if (ids.Count > 0)
            {
                List<(int Index, string? Summary, string? Category, string? Type, List<string>? Tags)>? parsed = null;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_timeout);
                    var reply = await complete(BuildPrompt(items), cts.Token).ConfigureAwait(false);
                    parsed = Parse(reply);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { parsed = null; }

                var byIndex = new Dictionary<int, (int Index, string? Summary, string? Category, string? Type, List<string>? Tags)>();
                if (parsed != null) foreach (var p in parsed) byIndex.TryAdd(p.Index, p);

                var bad = new List<long>();
                var at = DateTime.UtcNow.ToString("o");
                for (var i = 0; i < ids.Count; i++)
                {
                    if (!byIndex.TryGetValue(i, out var p)) { bad.Add(ids[i]); continue; }
                    var tags = KnowledgeLabels.Tags(existing[i].Concat(p.Tags ?? new List<string>()));
                    var summary = string.IsNullOrWhiteSpace(p.Summary) ? null : p.Summary.Trim();
                    store.SetLabels(ids[i], summary, KnowledgeLabels.Category(p.Category), KnowledgeLabels.Type(p.Type), tags, modelName, at);
                    labelled++;
                }
                if (bad.Count > 0) { store.SetAiFailed(bad); failed += bad.Count; }
            }

            done += batch.Count;
            progress?.Report((done, total));
        }
        return new LabelRunResult(labelled, failed);
    }

    internal static string BuildPrompt(IReadOnlyList<(string Title, string Body, IReadOnlyList<string> Hints)> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are labelling DFIR knowledge notes for an examiner's knowledge base.");
        sb.AppendLine();
        sb.AppendLine("For each note give: a summary, one category, one type and tags.");
        sb.AppendLine();
        sb.AppendLine("Categories (pick exactly one):");
        foreach (var (c, d) in new[]
        {
            ("Windows artifacts", "registry, prefetch, shellbags, amcache, LNK, jump lists and other Windows host artifacts"),
            ("Event logs", "Windows event logs, Sysmon, event IDs and log analysis"),
            ("Memory", "memory acquisition and analysis, Volatility"),
            ("Disk & file system", "disk images, NTFS, MFT, USN, carving, file systems"),
            ("Network", "packet captures, Zeek, flows, DNS, beaconing"),
            ("Malware", "malware analysis, persistence, behaviour, YARA"),
            ("Cloud & identity", "cloud logs, Azure, AWS, Microsoft 365, Active Directory, authentication"),
            ("Threat intel", "threat actors, IOCs, MITRE ATT&CK, campaigns"),
            ("Tools", "how to use a specific forensic tool or command line"),
            ("Methodology", "investigation workflow, triage, timelines, analysis approach"),
            ("Reporting", "findings, report writing, evidence handling"),
            ("General", "anything that fits none of the above"),
        }) sb.AppendLine($"- {c}: {d}");
        sb.AppendLine();
        sb.AppendLine("Types (pick exactly one):");
        sb.AppendLine("- Learned by doing: the examiner's own notes from a case, lab or practice");
        sb.AppendLine("- Reference: explains how something works, facts to look up");
        sb.AppendLine("- Template / checklist: a reusable procedure, checklist or template");
        sb.AppendLine();
        sb.AppendLine("Summary: 80-180 words of plain prose, what the note teaches, no markdown, no meta narration.");
        sb.AppendLine("Tags: 3-6 short kebab-case tags.");
        sb.AppendLine();
        for (var i = 0; i < items.Count; i++)
        {
            sb.AppendLine($"[{i}]");
            sb.AppendLine($"title: {items[i].Title}");
            sb.AppendLine($"concepts: {string.Join(", ", items[i].Hints)}");
            sb.AppendLine("body:");
            sb.AppendLine(items[i].Body);
            sb.AppendLine();
        }
        sb.AppendLine("Respond with ONLY a JSON array: [{\"index\":0,\"summary\":\"…\",\"category\":\"…\",\"type\":\"…\",\"tags\":[\"…\"]}]");
        return sb.ToString();
    }

    internal static List<(int Index, string? Summary, string? Category, string? Type, List<string>? Tags)>? Parse(string reply)
    {
        var doc = JsonArrayText.Extract(reply);
        if (doc == null) return null;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<(int, string?, string?, string?, List<string>?)>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                int? index = null; string? summary = null, category = null, type = null; List<string>? tags = null;
                foreach (var p in e.EnumerateObject())
                {
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "index":
                            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) index = n;
                            else if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var m)) index = m;
                            break;
                        case "summary": if (p.Value.ValueKind == JsonValueKind.String) summary = p.Value.GetString(); break;
                        case "category": if (p.Value.ValueKind == JsonValueKind.String) category = p.Value.GetString(); break;
                        case "type": if (p.Value.ValueKind == JsonValueKind.String) type = p.Value.GetString(); break;
                        case "tags":
                            if (p.Value.ValueKind == JsonValueKind.Array)
                                tags = p.Value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList();
                            break;
                    }
                }
                if (index is { } ix) list.Add((ix, summary, category, type, tags));
            }
            return list;
        }
    }
}
