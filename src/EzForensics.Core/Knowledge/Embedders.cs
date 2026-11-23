using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace EzForensics.Core.Knowledge;

/// <summary>Turns text into unit-length vectors. Documents and queries may be embedded differently.</summary>
public interface IEmbedder
{
    string ModelId { get; }
    int Dims { get; }
    float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct);
    float[] EmbedQuery(string text, CancellationToken ct);
}

/// <summary>
/// bge-small-en-v1.5 (quantized ONNX): BERT word-piece tokens, CLS pooling, L2-normalized, at most 512 tokens.
/// Model files come from <c>&lt;app folder&gt;\models\bge-small-en-v1.5\</c> (see build/get-embedding-model.ps1).
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    public const string Id = "bge-small-en-v1.5-q";
    public const string QueryPrefix = "Represent this sentence for searching relevant passages: ";
    public const int MaxTokens = 512;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _hasTypeIds;

    public string ModelId => Id;
    public int Dims => 384;

    public static string DefaultModelDir => Path.Combine(AppContext.BaseDirectory, "models", "bge-small-en-v1.5");

    private OnnxEmbedder(InferenceSession session, BertTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;
        _hasTypeIds = session.InputMetadata.ContainsKey("token_type_ids");
    }

    /// <summary>The embedder, or null when the model files are missing or cannot be loaded.</summary>
    public static OnnxEmbedder? TryCreate(string? modelDir = null)
    {
        modelDir ??= DefaultModelDir;
        var onnx = Path.Combine(modelDir, "model_quantized.onnx");
        var vocab = Path.Combine(modelDir, "vocab.txt");
        if (!File.Exists(onnx) || !File.Exists(vocab)) return null;
        try
        {
            var tok = BertTokenizer.Create(vocab, new BertOptions { LowerCaseBeforeTokenization = true });
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            return new OnnxEmbedder(new InferenceSession(onnx, opts), tok);
        }
        catch (Exception ex)
        {
            KnowledgeLog.Info("Embedding model could not be loaded: " + ex.Message);
            return null;
        }
    }

    public float[] EmbedQuery(string text, CancellationToken ct) => Embed(new[] { QueryPrefix + text }, ct)[0];

    public float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        const int batch = 16;
        for (var start = 0; start < texts.Count; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(batch, texts.Count - start);
            var ids = new List<IReadOnlyList<int>>(n);
            for (var i = 0; i < n; i++) ids.Add(_tokenizer.EncodeToIds(texts[start + i], MaxTokens, out _, out _));
            var len = ids.Max(x => x.Count);
            var inputIds = new DenseTensor<long>(new[] { n, len });
            var mask = new DenseTensor<long>(new[] { n, len });
            var types = new DenseTensor<long>(new[] { n, len });
            for (var i = 0; i < n; i++)
                for (var j = 0; j < ids[i].Count; j++) { inputIds[i, j] = ids[i][j]; mask[i, j] = 1; }
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            };
            if (_hasTypeIds) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
            using var output = _session.Run(inputs);
            var hidden = output.First().AsTensor<float>();
            for (var i = 0; i < n; i++)
            {
                var v = new float[Dims];
                for (var d = 0; d < Dims; d++) v[d] = hidden[i, 0, d];
                result[start + i] = Normalize(v);
            }
        }
        return result;
    }

    internal static float[] Normalize(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += x * x;
        var norm = (float)Math.Sqrt(s);
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    public void Dispose() => _session.Dispose();
}
