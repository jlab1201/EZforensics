using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EzForensics.App.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

    // Sections the person collapsed this session (all expanded by default; not persisted across runs).
    private static readonly HashSet<string> CollapsedSections = new();
    private bool _restoringSection;

    private void SectionToggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: CollectionViewGroup { Name: string name } } t) return;
        _restoringSection = true;
        try { t.IsChecked = !CollapsedSections.Contains(name); } finally { _restoringSection = false; }
    }

    private void SectionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoringSection || sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: CollectionViewGroup { Name: string name } } t) return;
        if (t.IsChecked == true) CollapsedSections.Remove(name); else CollapsedSections.Add(name);
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } lb) lb.ScrollIntoView(item);
    }

    /// <summary>Opens the tool's small menu (update / reinstall) under its button.</summary>
    private void EvtxMore_Click(object sender, RoutedEventArgs e) => MoreButton_Click(sender, e);

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } b) return;
        menu.PlacementTarget = b;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
