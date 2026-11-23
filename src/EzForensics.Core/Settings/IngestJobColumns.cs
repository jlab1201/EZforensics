namespace EzForensics.Core.Settings;

/// <summary>Ingest jobs list: which optional columns are shown (Tool / Artifact / Category / Progress / Status are always shown).</summary>
public sealed class IngestJobColumns
{
    public bool ShowMatchedPath { get; set; } = false;
    public bool ShowDuration { get; set; } = true;
    public bool ShowResult { get; set; } = true;
    public bool ShowOutput { get; set; } = false;
}
