using System.Windows;
using System.Windows.Controls;
using EzForensics.App.ViewModels.Learn;

namespace EzForensics.App.Views.Learn;

/// <summary>The Library tab: sources with counts; expanding a source lists its accepted documents (select some to distill or label).</summary>
public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private async void Docs_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Expander { DataContext: LibrarySourceRow row } && DataContext is LibraryViewModel vm) await vm.SetExpandedAsync(row, true);
    }

    private async void Docs_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Expander { DataContext: LibrarySourceRow row } && DataContext is LibraryViewModel vm) await vm.SetExpandedAsync(row, false);
    }

    private void Docs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { DataContext: LibrarySourceRow row } list) row.SetSelectedDocs(list.SelectedItems.OfType<LibraryDocRow>());
    }
}
