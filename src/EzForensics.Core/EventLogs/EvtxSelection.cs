using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.EventLogs;

/// <summary>Identity of an event: channel + provider + id (case-insensitive on the names).</summary>
public readonly record struct EvtxEventKey(string Channel, string Provider, int EventId)
{
    public bool Equals(EvtxEventKey o) => EventId == o.EventId
        && string.Equals(Channel, o.Channel, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Provider, o.Provider, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => HashCode.Combine(Channel.ToUpperInvariant(), Provider.ToUpperInvariant(), EventId);
}

public sealed class EvtxEventEntry
{
    public string Channel { get; set; } = "";
    public string Provider { get; set; } = "";
    public int EventId { get; set; }
    public string Title { get; set; } = "";
    public List<string> Fields { get; set; } = new();
    public List<string> MapProperties { get; set; } = new();
    public string MapStatus { get; set; } = "";
    /// <summary>Why it is on or off by default (volume, audit policy needed); shown as a tooltip. Empty when there is nothing to say.</summary>
    public string Note { get; set; } = "";
    /// <summary>On in the recommended set.</summary>
    public bool Default { get; set; }
    /// <summary>On in the user's selection.</summary>
    public bool Selected { get; set; }
    /// <summary>Events found in scanned evidence (null = not scanned / not found).</summary>
    public int? EvidenceCount { get; set; }

    public EvtxEventKey Key => new(Channel, Provider, EventId);
    public string FieldsText => string.Join(", ", Fields);
}

public sealed class EvtxEventGroup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<EvtxEventEntry> Events { get; set; } = new();
}

/// <summary>Event found in evidence by a scan.</summary>
public sealed record EvtxFoundEvent(string Channel, string Provider, int EventId, int Count);

/// <summary>
/// The event selection EvtxECmd runs with: the recommended catalogue (<c>Resources/evtx/recommended-events.json</c>) plus the user's
/// ticks. EvtxECmd filters by event ID only, so the selection is turned into one <c>--inc</c> ID list (<see cref="EvtxArgs"/>).
/// </summary>
public sealed class EvtxSelection
{
    public const string OtherGroupId = "other-evidence";
    public const string OtherGroupTitle = "Other (found in evidence)";
    public const string ImportedGroupId = "other-imported";
    public const string ImportedGroupTitle = "Other (imported)";
    private const string ResourceName = "EzForensics.Core.Resources.evtx.recommended-events.json";

    public List<EvtxEventGroup> Groups { get; set; } = new();

    public IEnumerable<EvtxEventEntry> AllEvents => Groups.SelectMany(g => g.Events);
    public int TotalCount => AllEvents.Count();
    public int SelectedCount => AllEvents.Count(e => e.Selected);
    public int GroupCount => Groups.Count(g => g.Events.Count > 0 && g.Id != OtherGroupId);

    /// <summary>Catalogue with only the recommended events ticked.</summary>
    public static EvtxSelection Recommended()
    {
        var asm = typeof(EvtxSelection).Assembly;
        using var s = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded resource missing: " + ResourceName);
        using var r = new StreamReader(s);
        var sel = Parse(r.ReadToEnd());
        foreach (var e in sel.AllEvents) e.Selected = e.Default;
        return sel;
    }

    /// <summary>Parses the schema of recommended-events.json. An event's <c>selected</c> value (when present) sets <see cref="EvtxEventEntry.Selected"/>.</summary>
    public static EvtxSelection Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new JsonException("Not a JSON object.");
        var sel = new EvtxSelection();
        foreach (var gn in root["groups"] as JsonArray ?? new JsonArray())
        {
            if (gn is not JsonObject g) continue;
            var group = new EvtxEventGroup { Id = Str(g["id"]), Title = Str(g["title"]) };
            foreach (var en in g["events"] as JsonArray ?? new JsonArray())
            {
                if (en is not JsonObject e || e["eventId"] is not JsonValue idv || !idv.TryGetValue<int>(out var id)) continue;
                var entry = new EvtxEventEntry
                {
                    Channel = Str(e["channel"]), Provider = Str(e["provider"]), EventId = id, Title = Str(e["title"]),
                    Fields = Strs(e["fields"]), MapProperties = Strs(e["mapProperties"]), MapStatus = Str(e["mapStatus"]), Note = Str(e["note"]),
                    Default = Bool(e["default"]),
                };
                entry.Selected = e["selected"] != null ? Bool(e["selected"]) : entry.Default;
                group.Events.Add(entry);
            }
            if (group.Id.Length == 0) group.Id = group.Title.ToLowerInvariant().Replace(' ', '-');
            sel.Groups.Add(group);
        }
        return sel;
    }

    /// <summary>
    /// Serialises the selection in the recommended-events.json schema. <paramref name="selectedOnly"/> (export, user file) writes only the
    /// ticked events and drops empty groups; every event carries <c>"selected"</c>.
    /// </summary>
    public string ToJson(bool selectedOnly = true)
    {
        var groups = new JsonArray();
        foreach (var g in Groups)
        {
            var events = new JsonArray();
            foreach (var e in g.Events.Where(e => !selectedOnly || e.Selected))
            {
                var o = new JsonObject
                {
                    ["channel"] = e.Channel, ["provider"] = e.Provider, ["eventId"] = e.EventId, ["title"] = e.Title,
                    ["fields"] = new JsonArray(e.Fields.Select(f => (JsonNode?)f).ToArray()),
                };
                if (e.MapProperties.Count > 0) o["mapProperties"] = new JsonArray(e.MapProperties.Select(f => (JsonNode?)f).ToArray());
                if (e.MapStatus.Length > 0) o["mapStatus"] = e.MapStatus;
                if (e.Note.Length > 0) o["note"] = e.Note;
                o["default"] = e.Default;
                o["selected"] = e.Selected;
                events.Add(o);
            }
            if (events.Count == 0 && selectedOnly) continue;
            groups.Add(new JsonObject { ["id"] = g.Id, ["title"] = g.Title, ["events"] = events });
        }
        var root = new JsonObject { ["schemaVersion"] = 1, ["tool"] = "EvtxECmd", ["groups"] = groups };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Applies a saved/imported selection onto this catalogue: events in <paramref name="json"/> become ticked, every other event is
    /// unticked. Events the catalogue does not know are added (to their file group when it exists, else to <see cref="ImportedGroupTitle"/>).
    /// </summary>
    public void MergeFrom(string json)
    {
        var other = Parse(json);
        var chosen = new HashSet<EvtxEventKey>(other.AllEvents.Where(e => e.Selected).Select(e => e.Key));
        var known = new Dictionary<EvtxEventKey, EvtxEventEntry>();
        foreach (var e in AllEvents) known[e.Key] = e;
        foreach (var e in known.Values) e.Selected = chosen.Contains(e.Key);
        foreach (var g in other.Groups)
            foreach (var e in g.Events.Where(e => e.Selected && !known.ContainsKey(e.Key)))
            {
                var target = Groups.FirstOrDefault(x => string.Equals(x.Id, g.Id, StringComparison.OrdinalIgnoreCase))
                    ?? GetOrAddGroup(ImportedGroupId, ImportedGroupTitle);
                e.Default = false;
                target.Events.Add(e);
                known[e.Key] = e;
            }
    }

    /// <summary>Recommended catalogue with a saved selection applied.</summary>
    public static EvtxSelection FromSaved(string? savedJson)
    {
        var sel = Recommended();
        if (!string.IsNullOrWhiteSpace(savedJson))
        {
            try { sel.MergeFrom(savedJson); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { sel = Recommended(); }
        }
        return sel;
    }

    /// <summary>Back to the recommended ticks; events added from evidence / imports stay listed but unticked.</summary>
    public void Reset()
    {
        foreach (var e in AllEvents) e.Selected = e.Default;
    }

    /// <summary>
    /// Records a scan: counts on known events, and every other (channel, provider, id) as an unticked entry of
    /// <see cref="OtherGroupTitle"/>. Returns the number of events added.
    /// </summary>
    public int AddEvidence(IEnumerable<EvtxFoundEvent> found)
    {
        var known = new Dictionary<EvtxEventKey, EvtxEventEntry>();
        foreach (var e in AllEvents) known[e.Key] = e;
        foreach (var e in known.Values) e.EvidenceCount = null;   // a previous scan's counts are replaced
        int added = 0;
        foreach (var f in found.OrderBy(f => f.Channel, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.EventId))
        {
            var key = new EvtxEventKey(f.Channel, f.Provider, f.EventId);
            if (known.TryGetValue(key, out var e)) { e.EvidenceCount = (e.EvidenceCount ?? 0) + f.Count; continue; }
            var group = GetOrAddGroup(OtherGroupId, OtherGroupTitle);
            e = new EvtxEventEntry { Channel = f.Channel, Provider = f.Provider, EventId = f.EventId, Title = "(not in the recommended list)", EvidenceCount = f.Count };
            group.Events.Add(e);
            known[key] = e;
            added++;
        }
        return added;
    }

    private EvtxEventGroup GetOrAddGroup(string id, string title)
    {
        var g = Groups.FirstOrDefault(x => x.Id == id);
        if (g == null) { g = new EvtxEventGroup { Id = id, Title = title }; Groups.Add(g); }
        return g;
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static List<string> Strs(JsonNode? n) => (n as JsonArray)?.Select(Str).Where(s => s.Length > 0).ToList() ?? new List<string>();
}
