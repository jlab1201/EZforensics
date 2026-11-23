using System.Diagnostics;
using EzForensics.Core.Knowledge;
using FluentAssertions;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests.Knowledge;

/// <summary>Runs against the real model when models\bge-small-en-v1.5 exists at the repo root (build/get-embedding-model.ps1); otherwise they pass without checking.</summary>
public class OnnxEmbedderTests
{
    private readonly ITestOutputHelper _out;
    public OnnxEmbedderTests(ITestOutputHelper output) => _out = output;

    private static string? ModelDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "models", "bge-small-en-v1.5");
            if (File.Exists(Path.Combine(p, "model_quantized.onnx"))) return p;
        }
        return null;
    }

    [Fact]
    public void Missing_model_files_give_null_not_a_crash()
    {
        OnnxEmbedder.TryCreate(Path.Combine(Path.GetTempPath(), "no_such_model_dir")).Should().BeNull();
    }

    [Fact]
    public void Real_model_embeds_unit_vectors_and_ranks_related_text_higher()
    {
        if (ModelDir() is not { } dir) return;
        using var e = OnnxEmbedder.TryCreate(dir)!;
        var docs = e.Embed(new[]
        {
            "Prefetch files record that a program was executed and when it last ran.",
            "Zeek writes conn.log and dns.log for network connections.",
        }, default);
        docs.Should().HaveCount(2);
        docs[0].Should().HaveCount(384);
        Math.Abs(docs[0].Sum(x => x * x) - 1f).Should().BeLessThan(0.01f);
        var q = e.EmbedQuery("which artifact proves a program ran", default);
        var s0 = q.Zip(docs[0]).Sum(p => p.First * p.Second);
        var s1 = q.Zip(docs[1]).Sum(p => p.First * p.Second);
        s0.Should().BeGreaterThan(s1);
    }

    [Fact]
    public async Task Real_model_end_to_end_search_and_timing()
    {
        if (ModelDir() is not { } dir) return;
        using var e = OnnxEmbedder.TryCreate(dir)!;
        using var v = new TestVault();
        using var store = new KnowledgeStore(v.DbPath);
        // pad the fixture with generated notes so the store holds a few hundred chunks
        for (var i = 0; i < 60; i++)
            File.WriteAllText(Path.Combine(v.Vault, $"gen{i}.md"), $"# Generated {i}\n\n" + string.Join("\n\n", Enumerable.Range(1, 4)
                .Select(s => $"## Section {s}\n\nHost {i} section {s}: " + string.Concat(Enumerable.Repeat("registry run keys and scheduled tasks give persistence. ", 12)))));
        var sw = Stopwatch.StartNew();
        await new KnowledgeImporter(store, e).ImportFolderAsync(v.Vault, "real", null, default);
        _out.WriteLine($"import {store.NoteCount()} notes / {store.ChunkCount()} chunks: {sw.ElapsedMilliseconds} ms");

        var r = new HybridRetriever(store, e);
        r.Search("warm up");
        sw.Restart();
        const int runs = 20;
        KnowledgeResult res = null!;
        for (var i = 0; i < runs; i++) res = r.Search("what proves that a program was executed on Windows");
        _out.WriteLine($"search: {sw.ElapsedMilliseconds / (double)runs:F1} ms per query");
        res.Hits.Take(3).Select(h => h.NoteTitle).Should().Contain(t => t == "Prefetch" || t == "Amcache" || t == "Shimcache");
    }
}
