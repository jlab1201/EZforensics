using System.Windows;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views.Analyze;

public partial class KeyEventsPanel : UserControl
{
    public KeyEventsPanel() => InitializeComponent();

    private ResultsViewModel? Vm => DataContext as ResultsViewModel;
    private void Spectre_Click(object sender, RoutedEventArgs e) => Vm?.ToggleSpectreFilter();

    private static FindingListItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as FindingListItem;

    private async void Item_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || ItemOf(sender) is not { } item) return;
        await Vm.ToggleFindingFilterAsync(item.Id);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || ItemOf(sender) is not { } item || Vm.DraftOf(item.Id) is not { } draft) return;
        if (KeyEventEditorWindow.Edit("Edit finding", draft, Window.GetWindow(this)) is { } result)
            Vm.UpdateFinding(item.Id, result);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || ItemOf(sender) is not { } item) return;
        if (Dialogs.Confirm("Remove finding", $"Remove finding \"{item.Title}\"? Its rows stay flagged.", "Remove", "Cancel", defaultNo: true, owner: Window.GetWindow(this)))
            Vm.RemoveFinding(item.Id);
    }
}
