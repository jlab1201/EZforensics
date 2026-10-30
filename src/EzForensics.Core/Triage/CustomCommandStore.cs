using System.Text;
using System.Text.Json;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Triage;

/// <summary>User file holding custom Ingest commands per rule (<c>ingest-commands.json</c>: <c>{ "commands": { "&lt;ruleId&gt;": "&lt;text as typed&gt;" } }</c>).</summary>
public sealed class CustomCommandStore
{
    private sealed class FileModel { public Dictionary<string, string>? Commands { get; set; } }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);

    public CustomCommandStore() : this(AppPaths.IngestCommandsPath) { }
    public CustomCommandStore(string path) { Path = path; }

    public string Path { get; }

    public IReadOnlyDictionary<string, string> All => _commands;

    /// <summary>Reads the file; a missing, corrupt or unreadable file gives an empty store.</summary>
    public void Load()
    {
        _commands.Clear();
        try
        {
            if (!File.Exists(Path)) return;
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(Path), Json);
            if (model?.Commands == null) return;
            foreach (var (k, v) in model.Commands)
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) _commands[k] = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _commands.Clear();
        }
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new FileModel { Commands = new Dictionary<string, string>(_commands) }, Json);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, Path, overwrite: true);
    }

    public string? Get(string ruleId) => _commands.TryGetValue(ruleId, out var t) ? t : null;

    /// <summary>Null / whitespace removes the entry.</summary>
    public void Set(string ruleId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) _commands.Remove(ruleId);
        else _commands[ruleId] = text;
    }
}
