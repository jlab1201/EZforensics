using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Assistant;

/// <summary>
/// The assistant's instructions live in Markdown files (Resources/prompts). A file in <c>&lt;data&gt;\prompts\</c> wins over the
/// embedded copy. Leading HTML comments are not sent. Texts are cached per process.
/// </summary>
public static class PromptStore
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly ConcurrentDictionary<string, bool> Logged = new();
    private static readonly Regex LeadingComment = new(@"\A(\s*<!--.*?-->)+\s*", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Set by the app at startup (AppLog.Info). Called once per file with its source and a short hash.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Folder whose files override the built-in ones.</summary>
    public static string OverrideDir => Path.Combine(AppPaths.DataRoot, "prompts");

    public static void Reload() { Cache.Clear(); Logged.Clear(); }

    /// <summary>The text of <c>name.md</c> without its leading comment.</summary>
    public static string Get(string name) => Cache.GetOrAdd(name, Load);

    /// <summary>The trimmed text under <c>## key</c> in <c>name.md</c>; throws when the key is missing.</summary>
    public static string Section(string name, string key)
    {
        foreach (var (k, text) in Sections(Get(name)))
            if (k == key) return text;
        throw new InvalidOperationException($"Prompt file '{name}.md' has no '## {key}' section.");
    }

    /// <summary>Section text with <c>{name}</c> placeholders replaced.</summary>
    public static string Section(string name, string key, params (string Name, string Value)[] values) => Fill(Section(name, key), values);

    /// <summary>Replaces <c>{name}</c> placeholders (other braces are left alone).</summary>
    public static string Fill(string text, params (string Name, string Value)[] values)
    {
        foreach (var (n, v) in values) text = text.Replace("{" + n + "}", v, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Keys of the <c>## key</c> sections of a file.</summary>
    public static IReadOnlyList<string> Keys(string name) => Sections(Get(name)).Select(s => s.Key).ToList();

    private static List<(string Key, string Text)> Sections(string text)
    {
        var list = new List<(string, string)>();
        string? key = null; var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (key != null) list.Add((key, sb.ToString().Trim()));
                key = line[3..].Trim(); sb.Clear();
            }
            else if (key != null) sb.Append(line).Append('\n');
        }
        if (key != null) list.Add((key, sb.ToString().Trim()));
        return list;
    }

    private static string Load(string name)
    {
        string raw, source;
        var path = Path.Combine(OverrideDir, name + ".md");
        string? over = null;
        try { if (File.Exists(path)) over = File.ReadAllText(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (over != null) { raw = over; source = path; }
        else
        {
            var asm = typeof(PromptStore).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name + ".md", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Built-in prompt '{name}.md' is missing.");
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            raw = r.ReadToEnd(); source = "built-in";
        }
        raw = raw.Replace("\r\n", "\n").TrimStart('﻿');
        var text = LeadingComment.Replace(raw, "").Trim();
        if (Logged.TryAdd(name, true))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
            try { Log?.Invoke($"Spectre prompt {name}.md loaded from {source} (sha256 {hash})."); } catch { }
        }
        return text;
    }
}
