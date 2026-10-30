using System.Windows;
using System.Windows.Input;

namespace EzForensics.App.Views;

public partial class EvtxCustomizeWindow : Window
{
    public EvtxCustomizeWindow()
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Services.Appearance.Attach(this);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
