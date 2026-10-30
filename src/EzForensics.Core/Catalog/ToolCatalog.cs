using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Catalog;

/// <summary>
/// Loads tool definitions from embedded JSON (Resources/tools/*.json).
/// Parameters may reference shared entries from Resources/tools/_shared.json:
/// <c>{"use":"dedupe"}</c> pulls in the shared definition, and any other property in the
/// entry overrides it (e.g. <c>{"use":"dt","default":"yyyy-MM-dd HH:mm:ss.fffffff"}</c>).
/// The shared key becomes the parameter id unless "id" is given.
/// </summary>
public static class ToolCatalog
{
    public const string SharedFileName = "_shared.json";
    private const string ResourceMarker = ".Resources.tools.";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<IReadOnlyList<ToolDefinition>> Embedded = new(LoadEmbeddedCore);

    public static IReadOnlyList<ToolDefinition> LoadEmbedded() => Embedded.Value;

    public static ToolDefinition? Find(string id) =>
        LoadEmbedded().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses one tool JSON, resolving "use" references against the shared map.</summary>
    public static ToolDefinition Parse(string json, IReadOnlyDictionary<string, JsonObject>? shared = null)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })?.AsObject() ?? throw new InvalidDataException("Tool definition is not a JSON object.");

        if (root["parameters"] is JsonArray parameters)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i] is not JsonObject p || !TryGetString(p, "use", out var use)) continue;
                if (shared is null || !TryGetShared(shared, use, out var baseDef))
                    throw new InvalidDataException($"Unknown shared parameter '{use}'.");

                var merged = (JsonObject)baseDef.DeepClone();
                foreach (var (key, value) in p)
                {
                    if (key == "use") continue;
                    merged[key] = value?.DeepClone();
                }
                if (!merged.ContainsKey("id")) merged["id"] = use;
                parameters[i] = merged;
            }
        }

        var def = root.Deserialize<ToolDefinition>(JsonOptions)
                  ?? throw new InvalidDataException("Tool definition deserialized to null.");
        foreach (var p in def.Parameters)
            if (string.IsNullOrEmpty(p.Label)) p.Label = p.Id;
        return def;
    }

    /// <summary>Parses _shared.json: an object whose properties are parameter definitions keyed by shared name.</summary>
    public static IReadOnlyDictionary<string, JsonObject> ParseShared(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })?.AsObject() ?? throw new InvalidDataException("_shared.json is not a JSON object.");
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in root)
            if (value is JsonObject o) map[key] = o;
        return map;
    }

    private static IReadOnlyList<ToolDefinition> LoadEmbeddedCore()
    {
        var asm = typeof(ToolCatalog).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.Contains(ResourceMarker, StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sharedName = names.FirstOrDefault(n => n.EndsWith("." + SharedFileName, StringComparison.OrdinalIgnoreCase));
        var shared = sharedName is null
            ? new Dictionary<string, JsonObject>()
            : ParseShared(ReadResource(asm, sharedName));

        var list = new List<ToolDefinition>();
        foreach (var name in names.Where(n => n != sharedName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(Parse(ReadResource(asm, name), shared)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"Failed to load tool definition '{name}': {ex.Message}", ex);
            }
        }
        return list.OrderBy(t => t.Kind).ThenBy(t => t.Category).ThenBy(t => t.DisplayName).ToList();
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static bool TryGetString(JsonObject o, string key, out string value)
    {
        value = "";
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) { value = s; return true; }
        return false;
    }

    private static bool TryGetShared(IReadOnlyDictionary<string, JsonObject> shared, string key, out JsonObject def)
    {
        if (shared.TryGetValue(key, out def!)) return true;
        foreach (var kv in shared)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { def = kv.Value; return true; }
        return false;
    }
}
