using System.Text.Json.Nodes;

namespace EzForensics.Core.Spectre;

/// <summary>
/// The <c>changes</c> of one <c>spectre_finding_edit</c> call. Only fields that were set are serialized; setting a field to
/// null reverts the examiner's override of it (an explicit JSON null).
/// </summary>
public sealed class SpectreFindingChanges
{
    private readonly JsonObject _json = new();

    public bool IsEmpty => _json.Count == 0;
    public IReadOnlyList<string> Fields => _json.Select(kv => kv.Key).ToList();
    public bool Reverts(string field) => _json.TryGetPropertyValue(field, out var v) && v == null;

    public SpectreFindingChanges Title(string? value) => Set("title", value is null ? null : JsonValue.Create(value));
    public SpectreFindingChanges Description(string? value) => Set("description", value is null ? null : JsonValue.Create(value));
    public SpectreFindingChanges Severity(string? value) => Set("severity", value is null ? null : JsonValue.Create(value));
    public SpectreFindingChanges Notes(string? value) => Set("notes", value is null ? null : JsonValue.Create(value));
    public SpectreFindingChanges IncludeInReport(bool? value) => Set("include_in_report", value is null ? null : JsonValue.Create(value.Value));
    /// <summary>"pin", "unpin" or "auto" (null reverts to auto).</summary>
    public SpectreFindingChanges KeyMoment(string? value) => Set("key_moment", value is null ? null : JsonValue.Create(value));
    public SpectreFindingChanges MomentCaption(string? value) => Set("moment_caption", value is null ? null : JsonValue.Create(value));
    public SpectreFindingChanges MitreAttack(IEnumerable<string>? value) =>
        Set("mitre_attack", value is null ? null : new JsonArray(value.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));

    /// <summary>Reverts one field by its engine name (title, description, severity, mitre_attack, notes, include_in_report, key_moment, moment_caption).</summary>
    public SpectreFindingChanges Revert(string field) => Set(field, null);

    private SpectreFindingChanges Set(string field, JsonNode? value) { _json[field] = value; return this; }

    public JsonObject ToJson() => (JsonObject)_json.DeepClone();
}
