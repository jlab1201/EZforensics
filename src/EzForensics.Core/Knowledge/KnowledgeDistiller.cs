using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EzForensics.Core.Analysis;

namespace EzForensics.Core.Knowledge;

public sealed record DistillResult(int Created, int Sections, int FailedSections);

/// <summary>
/// Splits a long document into sections of about 6,000 characters and asks the AI for atomic, single-idea notes per section.
/// Each one is stored as a pending child of the document (parent_id plus a structure link), through the importer's intake path so it gets
/// chunks, embeddings, concepts and duplicate flags. The parent is never changed. Accepted or rejected children are never touched.
/// </summary>
public sealed class KnowledgeDistiller(KnowledgeCompletion complete, string modelName, TimeSpan? perCallTimeout = null)
{
    public const int SectionChars = 6000;
    public const int MaxWords = 250;
    private const int MinSectionChars = 1500;
    private readonly TimeSpan _timeout = perCallTimeout ?? KnowledgeLabeler.DefaultTimeout;

    /// <summary>Children of the note that are still pending (waiting for review).</summary>
    public int PendingChildren(KnowledgeStore store, long parentId) =>
        store.Children(parentId).Count(id => store.Meta(id)?.Status == "pending");

    private sealed record Draft(string Title, string Body, string? Category, string? Type, List<string> Tags);

    /// <summary>
    /// Distils the note. Throws <see cref="InvalidOperationException"/> ("has pending notes") when it already has pending children and
    /// <paramref name="replacePending"/> is false. A failed section counts in <see cref="DistillResult.FailedSections"/> and the rest go on.
    /// Cancelling keeps what finished and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<DistillResult> DistillAsync(KnowledgeStore store, KnowledgeImporter importer, long parentId, bool replacePending,
        IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var meta = store.Meta(parentId) ?? throw new InvalidOperationException("The note no longer exists.");
        var note = store.Note(parentId) ?? throw new InvalidOperationException("The note no longer exists.");
        var src = store.Sources().FirstOrDefault(s => s.Id == meta.SourceId) ?? throw new InvalidOperationException("The note's source no longer exists.");

        var pending = store.Children(parentId).Where(id => store.Meta(id)?.Status == "pending").ToList();
        if (pending.Count > 0 && !replacePending) throw new InvalidOperationException("has pending notes");

        var sections = Split(note.Body);
        var drafts = new List<Draft>();
        int failed = 0, done = 0;
        OperationCanceledException? cancelled = null;
        // pending children are replaced only once the AI has been asked at least once, so an immediate cancel changes nothing
        foreach (var section in sections)
        {
            try { ct.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException e) { cancelled = e; break; }
            List<Draft>? got = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                got = ParseDrafts(await complete(BuildPrompt(meta.Title, section), cts.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException e) when (ct.IsCancellationRequested) { cancelled = e; break; }
            catch (Exception) { got = null; }
            if (got == null) failed++; else drafts.AddRange(got);
            progress?.Report((++done, sections.Count));
        }

        var created = 0;
        if (drafts.Count > 0 || (cancelled == null && failed < sections.Count))
        {
            foreach (var id in pending) store.DeleteNote(id);

            var taken = store.NotesOf(meta.SourceId).Keys.ToHashSet(StringComparer.Ordinal);
            var notes = new List<VaultNote>();
            var byPath = new Dictionary<string, Draft>(StringComparer.Ordinal);
            var n = 0;
            foreach (var d in drafts)
            {
                string path;
                do path = $"{meta.Path}#distilled-{++n}"; while (taken.Contains(path));
                taken.Add(path);
                byPath[path] = d;
                notes.Add(new VaultNote(path, d.Title, "", Array.Empty<string>(), HashOf(parentId, d.Body), d.Body, Array.Empty<VaultLink>()));
            }

            if (notes.Count > 0)
            {
                var intake = new IntakeOptions { AddedBy = CurrentIdentity.Name };
                importer.ImportNotes(src.Name, src.Kind, src.Path, notes, null, null, null, CancellationToken.None, buildLinks: false, intake: intake);
                var ids = store.NotesOf(meta.SourceId);
                var at = DateTime.UtcNow.ToString("o");
                var links = new List<(long, long, string)>();
                foreach (var (path, d) in byPath)
                {
                    if (!ids.TryGetValue(path, out var row)) continue; // an identical body already known: no new note
                    store.SetParent(row.Id, parentId);
                    store.SetLabels(row.Id, null, KnowledgeLabels.Category(d.Category), KnowledgeLabels.Type(d.Type), KnowledgeLabels.Tags(d.Tags), modelName, at);
                    links.Add((row.Id, parentId, LinkKinds.Structure));
                    created++;
                }
                store.AddLinks(links);
            }
        }

        if (cancelled != null) throw cancelled;
        return new DistillResult(created, sections.Count, failed);
    }

    private static string HashOf(long parentId, string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("distilled:" + parentId + ":" + body))).ToLowerInvariant();

    /// <summary>How many AI calls distilling a document of this body takes (one per section).</summary>
    public static int SectionCount(string? body) => string.IsNullOrWhiteSpace(body) ? 0 : Split(body).Count;

    /// <summary>Sections of about <see cref="SectionChars"/> characters: cut at a heading when one falls late in the window, else at a paragraph or line break.</summary>
    internal static List<string> Split(string body)
    {
        var list = new List<string>();
        var text = body.Replace("\r\n", "\n");
        var at = 0;
        while (at < text.Length)
        {
            if (text.Length - at <= SectionChars) { list.Add(text[at..]); break; }
            var window = text.Substring(at, SectionChars);
            var cut = -1;
            var h = window.LastIndexOf("\n#", StringComparison.Ordinal);
            if (h >= MinSectionChars) cut = h + 1; // keep the heading with the section that follows
            if (cut < 0) { var p = window.LastIndexOf("\n\n", StringComparison.Ordinal); if (p >= MinSectionChars) cut = p + 2; }
            if (cut < 0) { var p = window.LastIndexOf('\n'); if (p >= MinSectionChars) cut = p + 1; }
            if (cut < 0) cut = SectionChars;
            var piece = text.Substring(at, cut);
            if (piece.Trim().Length > 0) list.Add(piece);
            at += cut;
        }
        if (list.Count == 0) list.Add(text);
        return list;
    }

    internal static string BuildPrompt(string title, string section)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are splitting a DFIR document into notes for an examiner's knowledge base.");
        sb.AppendLine();
        sb.AppendLine($"Document: {title}");
        sb.AppendLine();
        sb.AppendLine("Write notes that are atomic, self-contained, one idea each, in plain prose of at most 250 words (no markdown headings, no references to \"this document\").");
        sb.AppendLine("Return [] if nothing in the section is worth keeping.");
        sb.AppendLine();
        sb.AppendLine("Categories (pick exactly one): " + string.Join(", ", KnowledgeLabels.Categories));
        sb.AppendLine("Types (pick exactly one): " + string.Join(", ", KnowledgeLabels.Types));
        sb.AppendLine("Tags: 3-6 short kebab-case tags.");
        sb.AppendLine();
        sb.AppendLine("Section:");
        sb.AppendLine(section);
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON array: [{\"title\":\"…\",\"body\":\"…\",\"category\":\"…\",\"type\":\"…\",\"tags\":[\"…\"]}]");
        return sb.ToString();
    }

    private static List<Draft>? ParseDrafts(string reply)
    {
        var doc = JsonArrayText.Extract(reply);
        if (doc == null) return null;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Draft>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string? title = null, body = null, category = null, type = null; var tags = new List<string>();
                foreach (var p in e.EnumerateObject())
                {
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "title": if (p.Value.ValueKind == JsonValueKind.String) title = p.Value.GetString(); break;
                        case "body": if (p.Value.ValueKind == JsonValueKind.String) body = p.Value.GetString(); break;
                        case "category": if (p.Value.ValueKind == JsonValueKind.String) category = p.Value.GetString(); break;
                        case "type": if (p.Value.ValueKind == JsonValueKind.String) type = p.Value.GetString(); break;
                        case "tags":
                            if (p.Value.ValueKind == JsonValueKind.Array)
                                tags = p.Value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList();
                            break;
                    }
                }
                body = LimitWords(body?.Trim() ?? "");
                if (body.Length == 0) continue;
                title = string.IsNullOrWhiteSpace(title) ? string.Join(' ', body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8)) : title.Trim();
                list.Add(new Draft(title, body, category, type, tags));
            }
            return list;
        }
    }

    private static string LimitWords(string body)
    {
        var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaxWords ? body : string.Join(' ', words.Take(MaxWords));
    }
}
