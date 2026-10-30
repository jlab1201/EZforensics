using System.Windows;
using System.Windows.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views.Analyze;

namespace EzForensics.App.Views;

public partial class FilterBuilderWindow : Window
{
    public FilterBuilderWindow()
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Services.Appearance.Attach(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private FilterBuilderViewModel? Vm => DataContext as FilterBuilderViewModel;

    private void Apply_Click(object sender, RoutedEventArgs e) => Vm?.Apply();

    /// <summary>Builder tab: apply, then close (stays open when the apply failed). Saved tab: Close.</summary>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { SelectedTabIndex: 0 } vm && !vm.Apply()) return;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    // ---- saving

    private void Save_Click(object sender, RoutedEventArgs e) => Vm?.Save();

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm == null || !vm.CanSave) return;
        var name = Analyze.SavedFilterUi.PromptSaveName(this, vm.CategorySignature, vm.CategoryName, SavedFilters.Instance.NextNumberedName(vm.CategorySignature));
        if (name == null) return;
        vm.Save(name);
    }

    // ---- saved filters tab

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterUi.Import(this) is { } msg && Vm != null) Vm.Notice = msg;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterUi.Export(this) is { } msg && Vm != null) Vm.Notice = msg;
    }

    private static SavedFilterItemViewModel? Item(object sender) => (sender as FrameworkElement)?.DataContext as SavedFilterItemViewModel;
    private static SavedCategoryViewModel? Category(object sender) => (sender as FrameworkElement)?.DataContext as SavedCategoryViewModel;

    private void SavedRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && Item(sender) is { CanLoad: true } item) { Vm?.LoadSaved(item); e.Handled = true; }
    }

    private void EditSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is { CanLoad: true } item) Vm?.LoadSaved(item);
    }

    // ---- syntax box

    private readonly System.Windows.Threading.DispatcherTimer _syntaxTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _syntaxTimerWired;

    private void SyntaxBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Only keyboard / paste edits count; the binding pushing builder text into the box happens while the box is not focused.
        if (Vm is not { } vm || !SyntaxBox.IsKeyboardFocusWithin) return;
        vm.SyntaxEditing = true;
        if (!_syntaxTimerWired)
        {
            _syntaxTimerWired = true;
            _syntaxTimer.Tick += (_, _) =>
            {
                _syntaxTimer.Stop();
                Vm?.ApplySyntax(SyntaxBox.Text);
            };
        }
        _syntaxTimer.Stop();
        _syntaxTimer.Start();
    }

    private void SyntaxBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (_syntaxTimer.IsEnabled) { _syntaxTimer.Stop(); vm.ApplySyntax(SyntaxBox.Text); }
        vm.EndSyntaxEditing();
    }

    private void CopySyntax_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetDataObject(SyntaxBox.Text, true); if (Vm != null) Vm.Notice = "Syntax copied"; }
        catch { /* clipboard busy */ }
    }

    private void RenameSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Item(sender) is not { } item) return;
        var name = TextPromptWindow.Ask(this, "Rename saved filter", "Filter name:", item.Name, t =>
        {
            if (string.IsNullOrWhiteSpace(t)) return "Enter a name.";
            var same = string.Equals(t.Trim(), item.Name, StringComparison.OrdinalIgnoreCase);
            return !same && SavedFilters.Instance.Find(item.CategorySignature, t.Trim()) != null ? "A filter with this name already exists in the category." : null;
        });
        if (name != null) vm.Rename(item, name);
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Item(sender) is not { } item) return;
        if (Dialogs.Confirm("Delete saved filter", $"Delete the saved filter '{item.Name}'?", yes: "Delete", no: "Cancel", defaultNo: true, owner: this))
            vm.Delete(item);
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Category(sender) is not { } cat) return;
        var name = TextPromptWindow.Ask(this, "Rename category", "Category name:", cat.Name, t => string.IsNullOrWhiteSpace(t) ? "Enter a name." : null);
        if (name != null) vm.RenameCategory(cat, name.Trim());
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Category(sender) is not { } cat) return;
        if (Dialogs.Confirm("Delete category", $"Delete the category '{cat.Name}' and its {cat.Count} saved filter(s)?", yes: "Delete", no: "Cancel", defaultNo: true, owner: this))
            vm.DeleteCategory(cat);
    }
}
