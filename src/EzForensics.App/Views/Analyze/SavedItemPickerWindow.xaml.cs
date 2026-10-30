using System.Windows;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Modal selection list for exporting / importing saved filters and views: items grouped by category with check boxes, a tri-state "Select all",
/// the count, and (import) the "If a name already exists" choice. AutomationIds: <c>SavedPicker.List</c>, <c>.SelectAll</c>, <c>.Conflict</c>, <c>.Ok</c>, <c>.Cancel</c>.
/// </summary>
public partial class SavedItemPickerWindow : Window
{
    private SavedItemPickerWindow(SavedItemPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Services.WindowChrome.HideIcon(this);
        Appearance.Attach(this);
    }

    private SavedItemPickerViewModel Vm => (SavedItemPickerViewModel)DataContext;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.CanOk) DialogResult = true;
    }

    /// <summary>Shows the picker (all items selected); null when cancelled.</summary>
    public static (IReadOnlyList<PickerEntry> Selected, ConflictMode Conflict)? Pick(Window? owner, string title, string caption,
        IEnumerable<PickerEntry> entries, bool askConflict, string okText = "OK")
    {
        var vm = new SavedItemPickerViewModel(title, caption, entries, askConflict, okText);
        var dlg = new SavedItemPickerWindow(vm);
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        if (dlg.ShowDialog() != true) return null;
        return (vm.Selected.ToList(), vm.Conflict);
    }
}
