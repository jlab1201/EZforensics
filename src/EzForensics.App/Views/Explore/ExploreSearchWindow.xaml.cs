using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Explore;
using EzForensics.App.Views.Analyze;

namespace EzForensics.App.Views.Explore;

/// <summary>
/// The Advanced search popup (modeless, one instance). Closing it hides it and stops a running search; results stay for the next time.
/// Results are only revealed in the Explore page (never opened or run).
/// </summary>
public partial class ExploreSearchWindow : Window
{
    private static ExploreSearchWindow? s_instance;

    public ExploreSearchWindow(ExploreSearchViewModel vm)
    {
        InitializeComponent();
        EzForensics.App.Controls.WindowSizing.FitToWorkArea(this);
        DataContext = vm;
        WindowChrome.HideIcon(this);
        Appearance.Attach(this);
        // Enter in any text box or combo of the criteria starts the search.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private ExploreSearchViewModel? Vm => DataContext as ExploreSearchViewModel;

    /// <summary>Set when the window is closed for good (app exit, another case); otherwise closing only hides it.</summary>
    public bool ClosingByHost { get; set; }

    /// <summary>Shows the window, or brings the existing one forward. One window per search view model.</summary>
    public static ExploreSearchWindow Open(ExploreSearchViewModel vm)
    {
        if (s_instance != null && !ReferenceEquals(s_instance.DataContext, vm)) Shutdown();
        var w = s_instance;
        if (w == null)
        {
            w = s_instance = new ExploreSearchWindow(vm);
            if (Application.Current?.MainWindow is { IsVisible: true } mw) w.Owner = mw;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        return w;
    }

    /// <summary>App exit: closes the kept-alive window.</summary>
    public static void Shutdown()
    {
        var w = s_instance;
        s_instance = null;
        if (w == null) return;
        w.ClosingByHost = true;
        try { w.Vm?.Cancel(); w.Close(); } catch (InvalidOperationException) { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        Vm?.Cancel();
        if (ClosingByHost || Application.Current is not { } app || app.Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
    }

    // ------------------------------------------------------------------------------ criteria

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm == null) return;
        if (e.OriginalSource is not (TextBox or ComboBox or CheckBox)) return;
        if (e.OriginalSource is TextBox { AcceptsReturn: true }) return;
        if (Vm.StartCommand.CanExecute(null)) Vm.StartCommand.Execute(null);
        e.Handled = true;
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, DataContext = vm };

        var saved = new MenuItem { Header = "Saved searches" };
        if (vm.SavedNames.Count == 0) saved.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
        foreach (var name in vm.SavedNames.ToList())
        {
            var item = new MenuItem { Header = name, IsChecked = string.Equals(name, vm.SelectedSaved, StringComparison.OrdinalIgnoreCase) };
            var n = name;
            item.Click += (_, _) => { vm.SelectedSaved = null; vm.SelectedSaved = n; };
            saved.Items.Add(item);
        }
        menu.Items.Add(saved);
        var save = new MenuItem { Header = "Save as..." };
        save.Click += (_, _) => SaveAs();
        menu.Items.Add(save);
        menu.Items.Add(new MenuItem { Header = vm.SelectedSaved is { } sel ? $"Delete \"{sel}\"" : "Delete saved search", Command = vm.DeleteSavedCommand, IsEnabled = vm.SelectedSaved != null });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Export results to CSV...", Command = vm.ExportCsvCommand });
        menu.Items.Add(new MenuItem { Header = "Clear", Command = vm.ClearCommand });
        menu.Items.Add(new Separator());
        var help = new MenuItem { Header = "Search syntax" };
        help.Click += (_, _) => HelpPopup.IsOpen = true;
        menu.Items.Add(help);
        menu.IsOpen = true;
    }

    private void SaveAs()
    {
        if (Vm is not { } vm) return;
        var name = TextPromptWindow.Ask(this, "Save search", "Name for this search", vm.SuggestedSaveName,
            t => string.IsNullOrWhiteSpace(t) ? "Enter a name." : null);
        if (name == null) return;
        name = name.Trim();
        if (vm.SavedNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) &&
            !Dialogs.Confirm("Replace saved search", $"A saved search named '{name}' already exists. Replace it?", yes: "Replace", no: "Cancel", defaultNo: true, owner: this))
            return;
        vm.SaveCurrentAs(name);
    }

    // ------------------------------------------------------------------------------ results

    private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null || !IsOverRow(e) || ResultGrid.SelectedItem is not SearchRow row) return;
        _ = Vm.OpenInExploreCommand.ExecuteAsync(row);
    }

    private void ResultGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || e.Key != Key.Enter || ResultGrid.SelectedItem is not SearchRow row) return;
        _ = Vm.OpenInExploreCommand.ExecuteAsync(row);
        e.Handled = true;
    }

    // A result with content hits switches the side pane to Hits; a name-only result leaves it where it was.
    private void ResultGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultGrid.SelectedItem is SearchRow { HitCount: > 0 }) SideTabs.SelectedIndex = 1;
    }

    private void ResultGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row) { if (!row.IsSelected) row.IsSelected = true; return; }
    }

    private static bool IsOverRow(MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow) return true;
        return false;
    }
}
