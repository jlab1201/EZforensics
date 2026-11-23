using EzForensics.Core.Knowledge;

// kbbuilder <vault-folder> <out.ezkb> [--name N] [--model-dir DIR]
// Builds a knowledge pack with a prebuilt embedding index. Needs the model (build/get-embedding-model.ps1).

string? vault = null, output = null, name = null, modelDir = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name" when i + 1 < args.Length: name = args[++i]; break;
        case "--model-dir" when i + 1 < args.Length: modelDir = args[++i]; break;
        case "-h" or "--help": Usage(); return 0;
        default:
            if (vault == null) vault = args[i];
            else if (output == null) output = args[i];
            else { Console.Error.WriteLine("Unexpected argument: " + args[i]); Usage(); return 2; }
            break;
    }
}
if (vault == null || output == null) { Usage(); return 2; }
if (!Directory.Exists(vault)) { Console.Error.WriteLine("Vault folder not found: " + vault); return 1; }

modelDir ??= FindModelDir();
var embedder = modelDir == null ? null : OnnxEmbedder.TryCreate(modelDir);
if (embedder == null)
{
    Console.Error.WriteLine("Embedding model not found" + (modelDir != null ? " in " + modelDir : "") + ".");
    Console.Error.WriteLine("Run build\\get-embedding-model.ps1, or pass --model-dir <folder with model_quantized.onnx and vocab.txt>.");
    return 1;
}

name ??= new DirectoryInfo(vault).Name;
using (embedder)
{
    var progress = new Progress<(int done, int total, string item)>(p => Console.Write($"\r{p.item}: {p.done}/{p.total}   "));
    var m = EzkbPack.Write(vault, output, name, embedder, progress);
    Console.WriteLine();
    Console.WriteLine($"Wrote {output}: {m.Notes} notes, {m.Chunks} chunks, model {m.EmbeddingModel} ({m.Dims} dims).");
}
return 0;

static string? FindModelDir()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "models", "bge-small-en-v1.5");
            if (File.Exists(Path.Combine(p, "model_quantized.onnx"))) return p;
        }
    return null;
}

static void Usage() => Console.Error.WriteLine("Usage: kbbuilder <vault-folder> <out.ezkb> [--name NAME] [--model-dir DIR]");
