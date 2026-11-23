using System.Text;
using EzForensics.Core.Assistant;

namespace EzForensics.App.ViewModels;

/// <summary>What Spectre sees and may propose on the Tools page: each tool's status, and installing or updating one.</summary>
public sealed partial class ToolsViewModel
{
    private const int AssistantMaxTools = 40;

    public override SpectrePage AssistantPage => SpectrePage.Tools;

    public override string? AssistantState() => OnUi(() =>
    {
        var rows = Manager.ToolRows;
        var sb = new StringBuilder();
        if (SelectedPage != null) sb.Append("Open section: ").Append(SelectedPage.Title).Append('\n');
        if (rows.Count == 0) return sb.Append("Tools: the list is not loaded yet.").ToString();
        sb.Append($"Tools ({rows.Count}; name - status - version):");
        foreach (var r in rows.Take(AssistantMaxTools))
            sb.Append("\n- ").Append(r.Name).Append(" - ").Append(r.Status).Append(r.Version.Length > 0 ? " - " + r.Version : "");
        if (rows.Count > AssistantMaxTools) sb.Append($"\n+{rows.Count - AssistantMaxTools} more");
        if (Manager.IsInstalling) sb.Append("\nAn install is running now.");
        return sb.ToString();
    });

    public override IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host)
    {
        foreach (var t in base.AssistantTools(host)) yield return t;
        yield return SpectrePageTools.Create(SpectrePageTools.ToolsInstallSpec, host, (id, input) => OnUi(() =>
        {
            if (FindTool(input["tool"]?.ToString()) is not { } row) return null;
            return new PageActionProposal(id, $"Install or update {row.Name}", $"Now: {row.Status}", async () =>
            {
                if (Manager.IsInstalling) return "Another install is running; try again when it is done.";
                // tick only this row and press the button under the table, as the examiner would
                foreach (var r in Manager.ToolRows) r.IsSelected = ReferenceEquals(r, row);
                await Manager.PrimaryActionCommand.ExecuteAsync(null);
                return null;
            }, AutoAllowed: false);
        }), input => OnUi(() => FindTool(input["tool"]?.ToString()) == null
            ? "Unknown tool. Tools: " + string.Join(", ", Manager.ToolRows.Select(r => r.Name).Take(AssistantMaxTools)) + "." : null));
    }

    /// <summary>The tool row with this name (exact, then the only one containing it).</summary>
    private ToolRow? FindTool(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        var rows = Manager.ToolRows;
        var exact = rows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var part = rows.Where(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return part.Count == 1 ? part[0] : null;
    }
}
