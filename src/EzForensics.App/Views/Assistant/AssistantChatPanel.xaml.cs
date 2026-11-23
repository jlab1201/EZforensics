using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views.Assistant;

/// <summary>
/// Spectre chat panel: the pop-up, the docked column or the Spectre window (each has its own instance; the conversation lives in the view model). DataContext: <see cref="AssistantChatViewModel"/>.
/// The transcript (a WebView2 page) only exists while the panel is visible, so a hidden or collapsed panel holds no browser.
/// </summary>
public partial class AssistantChatPanel : UserControl
{
    private AssistantChatViewModel? _vm;
    private bool _webActive;
    private bool _stick = true;   // WPF fallback list: follow new content until the examiner scrolls up

    public AssistantChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { Hook(); RestartTranscript(); };
        Unloaded += (_, _) => { Unhook(); StopTranscript(); };
        Loaded += (_, _) => { Hook(); RestartTranscript(); };
        IsVisibleChanged += (_, _) => RestartTranscript();
        MessageScroll.ScrollChanged += OnFallbackScrollChanged;
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModeButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ModeButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private SpectreTranscript? _transcript;

    private void RestartTranscript()
    {
        StopTranscript();
        if (IsLoaded && IsVisible) StartTranscript();
    }

    /// <summary>Shows the conversation as an HTML page; until it is ready (or when WebView2 is missing) the WPF list stays visible.</summary>
    private void StartTranscript()
    {
        if (_transcript != null || _vm == null) return;
        _transcript = new SpectreTranscript(WebHost, _vm, active =>
        {
            _webActive = active;
            WebHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            MessageScroll.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            UpdateBusyLine();
        });
        _ = _transcript.StartAsync();
    }

    private void StopTranscript()
    {
        _transcript?.Dispose();
        _transcript = null;
    }

    private void Hook()
    {
        Unhook();
        _vm = DataContext as AssistantChatViewModel;
        if (_vm != null)
        {
            _vm.Messages.CollectionChanged += OnMessagesChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        UpdateBusyLine();
    }

    private void Unhook()
    {
        if (_vm != null)
        {
            _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantChatViewModel.IsBusy)) UpdateBusyLine();
    }

    /// <summary>The "Spectre is typing…" caption is for the WPF fallback only; the transcript page shows animated dots.</summary>
    private void UpdateBusyLine() =>
        BusyLine.Visibility = _vm?.IsBusy == true && !_webActive ? Visibility.Visible : Visibility.Collapsed;

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _stick = true; // a new message always goes to the very bottom
        Dispatcher.BeginInvoke(new Action(MessageScroll.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnFallbackScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _stick = e.VerticalOffset >= MessageScroll.ScrollableHeight - 24; // the examiner scrolled
        else if (_stick)
            MessageScroll.ScrollToEnd();                                       // late layout or resize
    }

    /// <summary>Enter sends; Shift+Enter adds a line.</summary>
    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
        e.Handled = true;
    }
}
