using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.SettingsViewModel o) o.IngestSelectionsFocusRequested -= ScrollToSelections;
            if (e.NewValue is ViewModels.SettingsViewModel n) n.IngestSelectionsFocusRequested += ScrollToSelections;
        };
    }

    private const double TwoColumnMinWidth = 1000;
    private bool? _narrow;

    // 2x2 grid of cards; one column below ~1000px.
    private void CardsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = ActualWidth < TwoColumnMinWidth;
        if (_narrow == narrow) return;
        _narrow = narrow;
        SecondColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var cards = new[] { CardInstall, CardDisplay, CardFlags, CardIngest };
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetColumn(cards[i], narrow ? 0 : i % 2);
            Grid.SetRow(cards[i], narrow ? i : i / 2);
            cards[i].Margin = new Thickness(0, 0, narrow ? 0 : 14, 14);
        }
        // Hot Keys: one column wide, in the left column below the 2x2 cards
        Grid.SetRow(CardHotKeys, narrow ? cards.Length : 2);
        Grid.SetColumn(CardHotKeys, 0);
        Grid.SetColumnSpan(CardHotKeys, 1);
        CardHotKeys.Margin = new Thickness(0, 0, narrow ? 0 : 14, 14);
    }

    // ------------------------------------------------------------ Hot Keys: capture the next key chord

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void HotKeyBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HotKeyEditItem it } b && Vm is { } vm)
        {
            vm.BeginHotKeyCapture(it);
            b.Focus();
        }
    }

    private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button { DataContext: HotKeyEditItem it } || !it.IsCapturing || Vm is not { } vm) return;
        e.Handled = true;
        if (HotKeyInput.FromEvent(e) is not { } g) return; // a lone modifier: keep waiting
        it.IsCapturing = false;
        if (g is { Key: "Esc", Ctrl: false, Alt: false, Shift: false }) return; // cancel
        vm.AssignHotKey(it, HotKeys.Format(g));
    }

    private void HotKeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: HotKeyEditItem it }) it.IsCapturing = false;
    }

    private void ScrollToSelections() =>
        Dispatcher.BeginInvoke(new Action(() => IngestSelectionsSection.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
}
