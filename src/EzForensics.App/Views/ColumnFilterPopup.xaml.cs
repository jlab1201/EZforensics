using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views;

public partial class ColumnFilterPopup : UserControl
{
    public ColumnFilterPopup() => InitializeComponent();

    private void Popup_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ColumnValuesViewModel vm) return;
        if (e.Key == Key.Escape) { vm.CancelCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter && e.OriginalSource is not ComboBox) { vm.OkCommand.Execute(null); e.Handled = true; }
    }
}
