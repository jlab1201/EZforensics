using System.Windows;
using System.Windows.Controls;
using EzForensics.App.ViewModels.Learn;

namespace EzForensics.App.Views.Learn;

/// <summary>The Learn page: Graph, Intake, Inbox and Library tabs. Files or folders dropped anywhere on the page go to Intake.</summary>
public partial class LearnView : UserControl
{
    public LearnView() => InitializeComponent();

    private static string[]? Dropped(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } p ? p : null;

    private void Page_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (Dropped(e) == null) return;
        e.Effects = DataContext is LearnViewModel { Intake.IsBusy: false } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Page_PreviewDrop(object sender, DragEventArgs e)
    {
        if (Dropped(e) is not { } paths || DataContext is not LearnViewModel vm) return;
        e.Handled = true;
        await vm.AddAndShowIntakeAsync(paths);
    }
}
