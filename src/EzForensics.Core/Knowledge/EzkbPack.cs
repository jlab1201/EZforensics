using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EzForensics.Core.Knowledge;

/// <summary>The <c>manifest.json</c> of a pack.</summary>
public sealed record EzkbManifest(int FormatVersion, string Name, string Created, string? EmbeddingModel, int Dims, int Notes, int Chunks, bool HasIndex);

/// <summary>
/// A knowledge pack (.ezkb): a zip with manifest.json, notes/&lt;relative path&gt;.md and optionally index/chunks.jsonl plus
/// index/embeddings.bin (float32 little-endian, one vector per chunk line). Import reuses the prebuilt index when it fits the current embedder.
/// </summary>
public static class EzkbPack
{
    public const int FormatVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string ChunksEntry = "index/chunks.jsonl";
    private const string VectorsEntry = "index/embeddings.bin";

    private sealed record ChunkLine(string Path, string Hash, int Ord, string Heading, string Text);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Builds a pack from a vault folder. With an embedder the pack carries the prebuilt index; without one, notes only.</summary>
    public static EzkbManifest Write(string vaultFolder, string outPath, string name, IEmbedder? embedder,
        IProgress<(int done, int total, string item)>? progress = null, CancellationToken ct = default)
    {
        var notes = MarkdownVaultReader.ReadFolder(vaultFolder);
        var files = new List<(VaultNote Note, string Md)>();
        foreach (var n in notes) files.Add((n, File.ReadAllText(Path.Combine(vaultFolder, n.RelPath.Replace('/', Path.DirectorySeparatorChar)))));

        var perNote = new List<(VaultNote Note, string Md, List<ChunkPiece> Chunks)>();
        foreach (var (n, md) in files) perNote.Add((n, md, Chunker.Chunk(n.Title, n.Body)));

        float[][]? vectors = null;
        if (embedder != null)
        {
            var texts = perNote.SelectMany(p => p.Chunks.Select(c => c.EmbedText)).ToList();
            var all = new List<float[]>(texts.Count);
            for (var i = 0; i < texts.Count; i += KnowledgeImporter.EmbedBatch)
            {
                ct.ThrowIfCancellationRequested();
                all.AddRange(embedder.Embed(texts.GetRange(i, Math.Min(KnowledgeImporter.EmbedBatch, texts.Count - i)), ct));
                progress?.Report((Math.Min(i + KnowledgeImporter.EmbedBatch, texts.Count), texts.Count, "embedding"));
            }
            vectors = all.ToArray();
        }
        return WriteZip(outPath, name, perNote, vectors, embedder?.ModelId, embedder?.Dims ?? 0);
    }

    /// <summary>Builds a pack from a source already in the store (notes rebuilt with frontmatter, prebuilt chunks and vectors).</summary>
    public static EzkbManifest Write(KnowledgeStore store, string sourceName, string outPath)
    {
        var sid = store.FindSource(sourceName) ?? throw new InvalidOperationException("Unknown source: " + sourceName);
        var chunks = store.Chunks(sid, "accepted").GroupBy(c => c.NoteId).ToDictionary(g => g.Key, g => g.ToList());
        var perNote = new List<(VaultNote, string, List<ChunkPiece>)>();
        var vectors = new List<float[]>();
        var withVectors = true;
        foreach (var (id, _, _, _) in store.Notes(sid, "accepted"))
        {
            var info = store.Note(id)!.Value;
            var md = BuildMarkdown(info.Title, info.Tags, info.Body);
            var parsed = MarkdownVaultReader.Parse(info.Path, md);
            var rows = chunks.GetValueOrDefault(id) ?? new();
            perNote.Add((parsed, md, rows.Select(r => new ChunkPiece(r.Ord, r.Heading, r.Text, "")).ToList()));
            foreach (var r in rows) { if (r.Embedding == null) withVectors = false; else vectors.Add(r.Embedding); }
        }
        var model = withVectors && vectors.Count > 0 ? store.GetMeta("embed_model") : null;
        return WriteZip(outPath, sourceName, perNote, model != null ? vectors.ToArray() : null, model, vectors.FirstOrDefault()?.Length ?? 0);
    }

    private static string BuildMarkdown(string title, string tags, string body)
    {
        var sb = new StringBuilder("---\n");
        sb.Append("title: ").Append(JsonSerializer.Serialize(title)).Append('\n');
        var t = tags.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length > 0) sb.Append("tags: [").Append(string.Join(", ", t.Select(x => JsonSerializer.Serialize(x)))).Append("]\n");
        return sb.Append("---\n\n").Append(body).ToString();
    }

    private static EzkbManifest WriteZip(string outPath, string name, List<(VaultNote Note, string Md, List<ChunkPiece> Chunks)> notes,
        float[][]? vectors, string? model, int dims)
    {
        var chunkCount = notes.Sum(n => n.Chunks.Count);
        var manifest = new EzkbManifest(FormatVersion, name, DateTime.UtcNow.ToString("o"), vectors != null ? model : null, vectors != null ? dims : 0,
            notes.Count, chunkCount, true);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(outPath)) File.Delete(outPath);
        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        Put(zip, ManifestEntry, JsonSerializer.Serialize(manifest, Json));
        foreach (var n in notes) Put(zip, "notes/" + n.Note.RelPath, n.Md);

        var jsonl = new StringBuilder();
        foreach (var n in notes)
            foreach (var c in n.Chunks)
                jsonl.Append(JsonSerializer.Serialize(new ChunkLine(n.Note.RelPath, n.Note.Hash, c.Ord, c.Heading, c.Text), JsonLine)).Append('\n');
        Put(zip, ChunksEntry, jsonl.ToString());

        if (vectors != null)
        {
            var e = zip.CreateEntry(VectorsEntry, CompressionLevel.Optimal);
            using var s = e.Open();
            foreach (var v in vectors) s.Write(MemoryMarshal.AsBytes(v.AsSpan()));
        }
        return manifest;
    }

    private static void Put(ZipArchive zip, string entry, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    public static EzkbManifest ReadManifest(string packPath)
    {
        using var zip = ZipFile.OpenRead(packPath);
        return ReadManifest(zip);
    }

    private static EzkbManifest ReadManifest(ZipArchive zip)
    {
        var e = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Not a knowledge pack: manifest.json is missing.");
        using var r = new StreamReader(e.Open());
        var m = JsonSerializer.Deserialize<EzkbManifest>(r.ReadToEnd(), JsonLine) ?? throw new InvalidDataException("Bad manifest.");
        if (m.FormatVersion > FormatVersion) throw new InvalidDataException($"Pack format {m.FormatVersion} is newer than this app supports.");
        return m;
    }

    /// <summary>Imports a pack as a source named <paramref name="name"/> (default: the name in the manifest).</summary>
    public static Task<ImportResult> ImportAsync(string packPath, KnowledgeImporter importer, string? name,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct, IntakeOptions? intake = null) => Task.Run(() =>
    {
        using var zip = ZipFile.OpenRead(packPath);
        var manifest = ReadManifest(zip);
        var notes = new List<VaultNote>();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("notes/", StringComparison.Ordinal) && e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            notes.Add(MarkdownVaultReader.Parse(e.FullName["notes/".Length..], r.ReadToEnd()));
        }
        notes.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));

        Dictionary<string, PrebuiltNote>? prebuilt = null;
        float[][]? vecs = null;
        if (zip.GetEntry(ChunksEntry) is { } ce)
        {
            var lines = new List<ChunkLine>();
            using (var r = new StreamReader(ce.Open(), Encoding.UTF8))
                for (string? line; (line = r.ReadLine()) != null;)
                    if (line.Length > 0 && JsonSerializer.Deserialize<ChunkLine>(line, JsonLine) is { } cl) lines.Add(cl);

            if (manifest.EmbeddingModel != null && manifest.Dims > 0 && zip.GetEntry(VectorsEntry) is { } ve)
            {
                using var ms = new MemoryStream();
                using (var s = ve.Open()) s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length == lines.Count * manifest.Dims * 4)
                {
                    vecs = new float[lines.Count][];
                    for (var i = 0; i < vecs.Length; i++)
                    {
                        vecs[i] = new float[manifest.Dims];
                        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(i * manifest.Dims * 4, manifest.Dims * 4)).CopyTo(vecs[i]);
                    }
                }
            }
            prebuilt = new();
            for (var i = 0; i < lines.Count;)
            {
                var j = i;
                while (j < lines.Count && lines[j].Path == lines[i].Path) j++;
                var pieces = lines.GetRange(i, j - i).Select(l => new ChunkPiece(l.Ord, l.Heading, l.Text, "")).ToList();
                var note = notes.FirstOrDefault(n => n.RelPath == lines[i].Path);
                if (note != null)
                {
                    // the embedded text is rebuilt so a later re-embed (other model) works from the same chunks
                    pieces = pieces.Select(p => p with { EmbedText = (p.Heading.Length > 0 ? $"{note.Title} › {p.Heading}\n" : $"{note.Title}\n") + p.Text }).ToList();
                    prebuilt[lines[i].Path] = new PrebuiltNote(lines[i].Hash, pieces, vecs?[i..j]);
                }
                i = j;
            }
        }
        return importer.ImportNotes(string.IsNullOrWhiteSpace(name) ? manifest.Name : name!, "pack", packPath, notes, prebuilt,
            vecs != null ? manifest.EmbeddingModel : null, progress, ct, intake: intake);
    }, ct);
}
