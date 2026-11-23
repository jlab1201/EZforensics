using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Analysis;

/// <summary>Turns a version 1 <c>analysis.json</c> into the version 2 model. Reads the old JSON as nodes so the removed fields can still be seen.</summary>
public static class CaseAnalysisMigration
{
    public const string FlagGroup = "flag-group";

    public static CaseAnalysis FromV1(JsonNode root, DateTimeOffset now)
    {
        var o = root.DeepClone().AsObject();
        o["version"] = 2;

        if (o["workspace"] is JsonObject ws && ws["keyEvents"] is JsonNode wk)
        {
            ws.Remove("keyEvents");
            ws["findings"] = wk;
        }

        var keyEvents = new JsonArray();
        var items = new JsonArray();
        foreach (var node in (o["items"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject it) continue;
            var kind = it["kind"]?.GetValue<string>();
            var source = it["source"] as JsonObject;
            bool flagGroup = source?["csvRelPath"] != null || source?["label"] != null;
            it.Remove("kind");
            it.Remove("spectreNoLongerHighlights");
            source?.Remove("csvRelPath");
            source?.Remove("label");

            if (flagGroup)
            {
                bool edited = Count(it["editedFields"]) > 0 || HasRowEdits(it["rowEdits"]) || it["review"] != null;
                if (!edited) continue;
                it["migratedFrom"] = FlagGroup;
                it["origin"] = "analyst";
            }

            bool notImportant = it["review"]?.GetValue<string>() == "not_important";
            if (notImportant) it["review"] = "confirmed";

            items.Add(it.DeepClone());

            bool hidden = it["duplicateOf"] != null || it["mergedInto"] != null;
            if (kind == "key_event" && !notImportant && !hidden)
                keyEvents.Add(KeyEventFor(it, now));
        }
        o["items"] = items;
        o["keyEvents"] = keyEvents;

        return JsonSerializer.Deserialize<CaseAnalysis>(o.ToJsonString(), CaseAnalysisStore.Json)!;
    }

    private static int Count(JsonNode? n) => n is JsonArray a ? a.Count : 0;

    private static bool HasRowEdits(JsonNode? n) =>
        n is JsonObject r && (Count(r["added"]) > 0 || Count(r["removed"]) > 0 || Count(r["perRow"]) > 0);

    private static JsonObject KeyEventFor(JsonObject item, DateTimeOffset now)
    {
        var k = new JsonObject
        {
            ["id"] = KeyEvent.NewId(),
            ["title"] = item["title"]?.DeepClone() ?? "",
            ["severity"] = item["severity"]?.DeepClone() ?? "Medium",
            ["mitre"] = item["mitre"]?.DeepClone() ?? new JsonArray(),
            ["notes"] = "",
            ["findingIds"] = new JsonArray(item["id"]?.DeepClone()),
            ["editedFields"] = new JsonArray(),
        };
        if (item["firstSeen"] != null) k["time"] = item["firstSeen"]!.DeepClone();
        if (item["sourceStatus"] != null) k["sourceStatus"] = item["sourceStatus"]!.DeepClone();
        if (item["keptByUser"] != null) k["keptByUser"] = item["keptByUser"]!.DeepClone();
        if (item["dismissed"] != null) k["dismissed"] = item["dismissed"]!.DeepClone();
        k["history"] = new JsonArray(new JsonObject { ["who"] = "migration", ["when"] = now.ToString("O"), ["what"] = "created from version 1 key event" });
        return k;
    }
}
