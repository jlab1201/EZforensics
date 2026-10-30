using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views;

/// <summary>The setup wizard window (thin: everything is in <see cref="SetupWizardViewModel"/> and Core/Setup).</summary>
public partial class SetupWizardWindow : Window
{
    private bool _closingFromVm;

    public SetupWizardWindow()
    {
        InitializeComponent();
        Appearance.Attach(this);
    }

    public void CloseFromViewModel()
    {
        _closingFromVm = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingFromVm || DataContext is not SetupWizardViewModel vm) return;
        if (vm.FirstSetup && vm.Page != SetupPage.Done)
        {
            // A first setup asks, reverts and then closes itself: never inside this Closing event, where Close() is refused
            // ("while a Window is closing") and the wizard stayed open after the user confirmed.
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => vm.OnWindowClosing()));
            return;
        }
        bool running = vm.IsRunning && !vm.FirstSetup;
        if (vm.OnWindowClosing()) return;
        e.Cancel = true; // a first setup asks (Cancel) and closes itself; a later one must stop its install first
        if (running)
            EzForensics.App.Services.Dialogs.Info("Setup is still installing", "Click Cancel first (finished steps are kept), then close.", owner: this);
    }

    private void Log_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox t) t.ScrollToEnd();
    }
}