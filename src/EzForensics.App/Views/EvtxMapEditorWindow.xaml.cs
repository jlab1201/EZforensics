using System.Windows;
using System.Windows.Input;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views;

/// <summary>Map editor for one event (opened from Customize event selection › Edit fields…).</summary>
public partial class EvtxMapEditorWindow : Window
{
    private bool _saving;

    public EvtxMapEditorWindow()
    {
        InitializeComponent();
        EzForensics.App.Controls.WindowSizing.FitToWorkArea(this);
        Services.WindowChrome.HideIcon(this);
        Services.Appearance.Attach(this);
    }

    private EvtxMapEditorViewModel? Vm => DataContext as EvtxMapEditorViewModel;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.StartAsync();
    }

    private void Window_Closed(object? sender, EventArgs e) => Vm?.Cancel();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || Vm is not { } vm) return;
        _saving = true;
        try { if (await vm.SaveAsync(this)) DialogResult = true; }
        finally { _saving = false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Fields_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.AssignCommand.CanExecute(null) == true) Vm.AssignCommand.Execute(null);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
