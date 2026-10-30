using System.Windows;
using System.Windows.Controls;

namespace EzForensics.App.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

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
