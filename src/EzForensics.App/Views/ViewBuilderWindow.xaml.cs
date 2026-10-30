using System.Windows;
using System.Windows.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views.Analyze;

namespace EzForensics.App.Views;

public partial class ViewBuilderWindow : Window
{
    public ViewBuilderWindow()
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Services.Appearance.Attach(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private ViewBuilderViewModel? Vm => DataContext as ViewBuilderViewModel;

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

    private void Save_Click(object sender, RoutedEventArgs e) => Vm?.Save();

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm == null || !vm.CanSave) return;
        var r = SavedViewUi.PromptSaveName(this, vm.CategorySignature, vm.CategoryName, SavedViews.Instance.NextNumberedName(vm.CategorySignature), offerGrouping: false);
        if (r is { } x) vm.Save(x.Name);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (SavedViewUi.Import(this) is { } msg && Vm != null) Vm.Notice = msg;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SavedViewUi.Export(this) is { } msg && Vm != null) Vm.Notice = msg;
    }

    private static SavedViewItemViewModel? Item(object sender) => (sender as FrameworkElement)?.DataContext as SavedViewItemViewModel;
    private static SavedViewCategoryViewModel? Category(object sender) => (sender as FrameworkElement)?.DataContext as SavedViewCategoryViewModel;

    private void SavedRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && Item(sender) is { CanLoad: true } item) { Vm?.LoadSaved(item); e.Handled = true; }
    }

    private void EditSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is { CanLoad: true } item) Vm?.LoadSaved(item);
    }

    private void RenameSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Item(sender) is not { } item) return;
        var name = TextPromptWindow.Ask(this, "Rename saved view", "View name:", item.Name, t =>
        {
            if (string.IsNullOrWhiteSpace(t)) return "Enter a name.";
            var same = string.Equals(t.Trim(), item.Name, StringComparison.OrdinalIgnoreCase);
            return !same && SavedViews.Instance.Find(item.CategorySignature, t.Trim()) != null ? "A view with this name already exists in the category." : null;
        });
        if (name != null) vm.Rename(item, name);
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Item(sender) is not { } item) return;
        if (Dialogs.Confirm("Delete saved view", $"Delete the saved view '{item.Name}'?", yes: "Delete", no: "Cancel", defaultNo: true, owner: this))
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
        if (Dialogs.Confirm("Delete category", $"Delete the category '{cat.Name}' and its {cat.Count} saved view(s)?", yes: "Delete", no: "Cancel", defaultNo: true, owner: this))
            vm.DeleteCategory(cat);
    }
}
