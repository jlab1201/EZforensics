using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Assistant;

/// <summary>
/// Hands the open EZ Forensics case to the Spectre engine: builds the <c>spectre_open_ezf_case</c> arguments (case folder as a WSL path,
/// every file with its SHA-256 and case-relative path) so the model never types paths or hashes. Spectre re-hashes each file itself.
/// </summary>
public static class SpectreCaseHandoff
{
    public const string ToolName = "spectre_open_current_case";
    public const string EngineTool = "spectre_open_ezf_case";
    public const int MaxFiles = 5000;

    /// <summary>Arguments for <see cref="EngineTool"/>, or an error text for the model (no case, folder not visible from WSL, no files).</summary>
    public static (JsonObject? Args, string? Error) Build(string? caseDir, CancellationToken ct = default)
    {
        var (args, error, _) = BuildWithFiles(caseDir, ct);
        return (args, error);
    }

    /// <summary>Like <see cref="Build"/>, plus what was handed over (path, hash, size, time) so the app can tell later whether the evidence changed.</summary>
    public static (JsonObject? Args, string? Error, IReadOnlyList<Spectre.SpectreHandoffFile> Files) BuildWithFiles(string? caseDir, CancellationToken ct = default)
    {
        var none = Array.Empty<Spectre.SpectreHandoffFile>();
        if (string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) return (null, "No case is open.", none);
        var wsl = WslRuntime.ToWslPath(caseDir);
        if (wsl == null) return (null, "The case folder is not on a local drive, so the Spectre engine cannot see it.", none);
        var root = Path.GetFullPath(caseDir);
        var files = new JsonArray();
        var handed = new List<Spectre.SpectreHandoffFile>();
        foreach (var f in Enumerate(root))
        {
            ct.ThrowIfCancellationRequested();
            if (files.Count >= MaxFiles) break;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            long size = 0, mtime = 0;
            try { var info = new FileInfo(f); size = info.Length; mtime = info.LastWriteTimeUtc.Ticks; } catch (IOException) { }
            var sha = Sha256(f);
            files.Add(new JsonObject { ["path"] = rel, ["sha256"] = sha });
            handed.Add(new Spectre.SpectreHandoffFile(rel, sha, size, mtime));
        }
        if (files.Count == 0) return (null, "The case has no files to hand over yet.", none);
        return (new JsonObject { ["ezf_case_path"] = wsl, ["case_name"] = Path.GetFileName(root.TrimEnd('\\', '/')), ["files"] = files }, null, handed);
    }

    /// <summary>The case's files except the app's own hidden folders (.ezassistant, .git, ...), in a stable order.</summary>
    internal static IEnumerable<string> Enumerate(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var result = new List<string>();
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(d))
                    if (!Path.GetFileName(sub).StartsWith('.')) stack.Push(sub);
                foreach (var f in Directory.EnumerateFiles(d))
                    if (!Path.GetFileName(f).StartsWith('.')) result.Add(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    internal static string Sha256(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>The app tool <c>spectre_open_current_case</c>; null when the engine's tools lack <see cref="EngineTool"/>.</summary>
    public static IAssistantTool? CreateTool(IReadOnlyList<IAssistantTool> engineTools, Func<string?> caseDir)
    {
        var inner = engineTools.FirstOrDefault(t => t.Spec.Name == EngineTool);
        return inner == null ? null : new HandoffTool(inner, caseDir);
    }

    private sealed class HandoffTool : IAssistantTool
    {
        private readonly IAssistantTool _inner;
        private readonly Func<string?> _caseDir;
        public HandoffTool(IAssistantTool inner, Func<string?> caseDir) { _inner = inner; _caseDir = caseDir; }

        public ToolSpec Spec { get; } = new(ToolName,
            "Open the examiner's current EZ Forensics case in the Spectre engine (no arguments: the app lists the case files and their hashes itself). Returns the Spectre case_id for spectre_run and the other spectre_* tools.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, ToolKind.Write);

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var dir = _caseDir();
            var (args, error) = await Task.Run(() => Build(dir, ct), ct).ConfigureAwait(false);
            if (args == null) return new ToolOutcome(error ?? "No case.", true);
            return await _inner.RunAsync(call with { Name = EngineTool, Input = args }, ct).ConfigureAwait(false);
        }
    }
}
