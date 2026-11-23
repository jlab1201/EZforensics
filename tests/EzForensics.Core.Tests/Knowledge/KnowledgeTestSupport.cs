using System.Text.RegularExpressions;
using EzForensics.Core.Knowledge;

namespace EzForensics.Core.Tests.Knowledge;

/// <summary>Deterministic hashing-trick embedder (64 dims, bag of words) so tests never need the real model.</summary>
public sealed class FakeEmbedder : IEmbedder
{
    public string ModelId { get; }
    public int Dims => 64;
    public int Calls { get; private set; }
    public int TextsEmbedded { get; private set; }

    public FakeEmbedder(string modelId = "fake-hash") => ModelId = modelId;

    public float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Calls++;
        TextsEmbedded += texts.Count;
        return texts.Select(Vec).ToArray();
    }

    public float[] EmbedQuery(string text, CancellationToken ct) => Vec(text);

    private float[] Vec(string text)
    {
        var v = new float[Dims];
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+"))
        {
            var h = 17;
            foreach (var c in m.Value) h = unchecked(h * 31 + c);
            v[(h & 0x7fffffff) % Dims] += 1f;
        }
        var n = MathF.Sqrt(v.Sum(x => x * x));
        if (n > 0) for (var i = 0; i < v.Length; i++) v[i] /= n;
        return v;
    }
}

/// <summary>A temp folder with a copy of the fixture vault (tests edit it) and a temp store path.</summary>
public sealed class TestVault : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ezf_kb_" + Guid.NewGuid().ToString("N"));
    public string Vault => Path.Combine(Root, "vault");
    public string DbPath => Path.Combine(Root, "kb", "knowledge.db");

    public TestVault()
    {
        Directory.CreateDirectory(Root);
        Copy(Path.Combine(AppContext.BaseDirectory, "Knowledge", "Fixtures", "vault"), Vault);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { }
    }
}
