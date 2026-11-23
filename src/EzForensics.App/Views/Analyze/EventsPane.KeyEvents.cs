using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views.Analyze;

/// <summary>Key events on the grid: "New finding…" and "Add to finding ▸" for the selected rows (row menu).</summary>
public partial class EventsPane
{
    // Segoe Fluent Icons: Tag (findings; the star is for key events only) / Add
    private const string KeyEventGlyph = "", AddGlyph = "";

    private const string EditGlyph = "";   // Edit

    private static MenuItem KeyEventItem(string id, string header, Action action, bool enabled, string glyph)
    {
        var mi = new MenuItem { Header = new TextBlock { Text = header }, IsEnabled = enabled, Icon = MenuUi.Icon(glyph, "Warn") };
        AutomationProperties.SetAutomationId(mi, "Results.Menu." + id);
        AutomationProperties.SetName(mi, header);
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Adds the two finding entries for <paramref name="sel"/> to <paramref name="items"/>.</summary>
    private void AddKeyEventItems(ItemCollection items, IReadOnlyList<RowItem> sel)
    {
        if (_vm == null) return;
        bool can = _vm.CanBundleRows && sel.Count > 0;
        items.Add(KeyEventItem("NewKeyEvent", $"New finding… ({sel.Count:N0})", () => NewKeyEvent(sel), can, KeyEventGlyph));
        var add = new MenuItem { Header = new TextBlock { Text = "Add to finding" }, Icon = MenuUi.Icon(AddGlyph, "Warn"), IsEnabled = can && _vm.Findings.Count > 0 };
        AutomationProperties.SetAutomationId(add, "Results.Menu.AddToFinding");
        AutomationProperties.SetName(add, "Add to finding");
        foreach (var k in _vm.Findings)
        {
            var id = k.Id;
            var mi = new MenuItem { Header = new TextBlock { Text = $"{k.Title}  ({k.Severity}, {k.CountText})" } };
            AutomationProperties.SetAutomationId(mi, "Results.Menu.AddToKeyEvent.Item");
            AutomationProperties.SetName(mi, k.Title);
            mi.Click += (_, _) => _vm?.AddToFinding(id, sel);
            add.Items.Add(mi);
        }
        items.Add(add);

        // rows that belong to a finding: edit it
        var owners = _vm.FindingsOfRows(sel.Select(r => r.Source));
        if (owners.Count == 1)
            items.Add(KeyEventItem("EditFinding", "Edit finding…", () => EditFinding(owners[0].Id), true, EditGlyph));
        else if (owners.Count > 1)
        {
            var edit = new MenuItem { Header = new TextBlock { Text = "Edit finding…" }, Icon = MenuUi.Icon(EditGlyph, "Warn") };
            AutomationProperties.SetAutomationId(edit, "Results.Menu.EditFinding");
            AutomationProperties.SetName(edit, "Edit finding");
            foreach (var (fid, title) in owners)
            {
                var mi = new MenuItem { Header = new TextBlock { Text = title } };
                AutomationProperties.SetAutomationId(mi, "Results.Menu.EditFinding.Item");
                mi.Click += (_, _) => EditFinding(fid);
                edit.Items.Add(mi);
            }
            items.Add(edit);
        }
    }

    private void EditFinding(string id)
    {
        if (_vm == null || _vm.DraftOf(id) is not { } draft) return;
        if (KeyEventEditorWindow.Edit("Edit finding", draft, Window.GetWindow(this)) is { } result) _vm.UpdateFinding(id, result);
    }

    private void NewKeyEvent(IReadOnlyList<RowItem> sel)
    {
        if (_vm == null || sel.Count == 0) return;
        var draft = KeyEventEditorWindow.Edit("New finding", new KeyEventDraft("", "Medium", Array.Empty<string>(), Rows: _vm.NewFindingRows(sel)),
            Window.GetWindow(this), _vm.MixedFlagChoiceOf(sel));
        if (draft != null) _vm.CreateFinding(sel, draft);
    }
}
