using EzForensics.Core.Settings;

namespace EzForensics.Core.EventLogs;

/// <summary>User file holding the EvtxECmd event selection (<c>%LocalAppData%\...\evtx-selection.json</c>, recommended-events.json schema, ticked events only).</summary>
public sealed class EvtxSelectionStore
{
    public EvtxSelectionStore() : this(AppPaths.EvtxSelectionPath) { }
    public EvtxSelectionStore(string path) { Path = path; }

    public string Path { get; }

    /// <summary>The recommended catalogue with the saved selection applied (recommended when there is no usable file).</summary>
    public EvtxSelection Load()
    {
        try { return EvtxSelection.FromSaved(File.Exists(Path) ? File.ReadAllText(Path) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return EvtxSelection.Recommended(); }
    }

    public void Save(EvtxSelection selection)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, selection.ToJson(selectedOnly: true));
        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
