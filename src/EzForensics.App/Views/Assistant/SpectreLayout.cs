using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views.Assistant;

/// <summary>
/// Lays out the app-wide Spectre in the main window's content area from the chat's mode and open flag.
/// Pop-up: a panel overlaid at the bottom right above the launcher, clamped to the room and resized from its top-left grip.
/// Docked: a column on the right with a splitter, whose width is remembered; it hides (the setting stays) when the page would get
/// narrower than <see cref="MinPage"/> and comes back when there is room. Window: <see cref="SpectreWindowHost"/>.
/// The launcher shows whenever Spectre is collapsed, and in pop-up mode also while it is open. Each mode has its own panel instance
/// (hidden panels hold no WebView2); the conversation lives in the view model, so a mode switch re-renders the same chat.
/// </summary>
internal sealed class SpectreLayout
{
    private const double MinPage = 528, SplitterWidth = 12, MinDock = 300;
    private const double TitleGap = 8;
    private const double PopupRight = 24, PopupBottom = 74, MinPopupW = 300, MinPopupH = 300;

    private readonly FrameworkElement _area;
    private readonly Func<AssistantChatViewModel?> _chat;
    private readonly Func<PageViewModel?> _page;
    private readonly ColumnDefinition _splitCol, _dockCol;
    private readonly GridSplitter _splitter;
    private readonly AssistantChatPanel _dockPanel;
    private readonly FrameworkElement _popup;
    private readonly Thumb _grip;
    private readonly UIElement _launcher;
    private readonly ContentControl? _pageHost;
    private static readonly Thickness PageMargin = new(24, 20, 24, 12), PageMarginDocked = new(24, 20, 0, 12);
    private static readonly string[] BodyNames = { "PageBody", "Body" };
    private AssistantChatViewModel? _hooked;
    private AppSettings? _settings;

    public SpectreLayout(FrameworkElement area, Func<AssistantChatViewModel?> chat, Func<PageViewModel?> page, ColumnDefinition splitCol, ColumnDefinition dockCol, GridSplitter splitter,
        AssistantChatPanel dockPanel, FrameworkElement popup, Thumb grip, UIElement launcher)
    {
        _area = area; _chat = chat; _page = page; _splitCol = splitCol; _dockCol = dockCol; _splitter = splitter; _dockPanel = dockPanel; _popup = popup; _grip = grip; _launcher = launcher;
        _pageHost = (area as Panel)?.Children.OfType<ContentControl>().FirstOrDefault(c => c.Name == "PageHost");
        if (_pageHost != null) _pageHost.LayoutUpdated += (_, _) => AlignDock();
        SetDock(false, 0, 0);
        _popup.Visibility = Visibility.Collapsed;
        area.Loaded += (_, _) => Attach();
        area.Unloaded += (_, _) => Detach();
        area.SizeChanged += (_, _) => Refresh();
        splitter.DragCompleted += (_, _) => SaveDockWidth();
        grip.DragDelta += OnGripDelta;
        grip.DragCompleted += (_, _) => SavePopupSize();
    }

    /// <summary>Re-reads the chat (the data context arrives after construction) and lays everything out.</summary>
    public void Attach()
    {
        Detach();
        _settings = AppServices.Current?.Settings;
        _hooked = _chat();
        if (_hooked != null) _hooked.PropertyChanged += OnChatChanged;
        Refresh();
    }

    private void Detach()
    {
        if (_hooked != null) _hooked.PropertyChanged -= OnChatChanged;
        _hooked = null;
        _settings = null;
        SpectreWindowHost.Hide();
    }

    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantChatViewModel.IsExpanded) or nameof(AssistantChatViewModel.Mode)) Refresh();
    }

    /// <summary>Brings launcher, pop-up, dock and window in line with the open flag, the mode and the room.</summary>
    public void Refresh()
    {
        var vm = _hooked;
        var s = _settings;
        if (vm == null || s == null || !_area.IsLoaded || _page() is not { ShowsAssistant: true })   // other pages: hidden, the open flag and mode stay
        {
            _launcher.Visibility = Visibility.Collapsed;
            _popup.Visibility = Visibility.Collapsed;
            SetDock(false, 0, 0);
            SpectreWindowHost.Hide();
            return;
        }
        var open = vm.IsExpanded;
        // docked but the window is too narrow: Spectre shows as the pop-up until there is room again (the page keeps its width)
        var docked = open && vm.IsDockedMode && PlaceDock(s);
        if (!docked) SetDock(false, 0, 0);
        var popup = open && (vm.IsPopupMode || (vm.IsDockedMode && !docked));
        _launcher.Visibility = !open || popup ? Visibility.Visible : Visibility.Collapsed;

        if (popup) { PlacePopup(s); _popup.Visibility = Visibility.Visible; }
        else _popup.Visibility = Visibility.Collapsed;

        if (open && vm.IsWindowMode) SpectreWindowHost.Show(vm, s); else SpectreWindowHost.Hide();
    }

    // ------------------------------------------------------------------ pop-up

    private void PlacePopup(AppSettings s)
    {
        var (w, h) = ClampPopup(s.AssistantPopupWidth, s.AssistantPopupHeight);
        _popup.Width = w; _popup.Height = h;
        _popup.Margin = new Thickness(0, 0, PopupRight, PopupBottom);
    }

    /// <summary>The pop-up never leaves the content area: at most its width minus the right gap, its height minus the launcher row.</summary>
    private (double W, double H) ClampPopup(double w, double h)
    {
        var maxW = Math.Max(MinPopupW, _area.ActualWidth - PopupRight - 8);
        var maxH = Math.Max(MinPopupH, _area.ActualHeight - PopupBottom - 8);
        if (_area.ActualWidth > 0) maxW = Math.Min(maxW, Math.Max(200, _area.ActualWidth - PopupRight - 8));
        if (_area.ActualHeight > 0) maxH = Math.Min(maxH, Math.Max(200, _area.ActualHeight - PopupBottom - 8));
        return (Math.Min(Math.Max(w, Math.Min(MinPopupW, maxW)), maxW), Math.Min(Math.Max(h, Math.Min(MinPopupH, maxH)), maxH));
    }

    private void OnGripDelta(object sender, DragDeltaEventArgs e)
    {
        // anchored bottom right: dragging up / left grows it
        var (w, h) = ClampPopup(_popup.ActualWidth - e.HorizontalChange, _popup.ActualHeight - e.VerticalChange);
        _popup.Width = w; _popup.Height = h;
    }

    private void SavePopupSize()
    {
        if (_settings == null || _popup.Visibility != Visibility.Visible) return;
        _settings.AssistantPopupWidth = _popup.ActualWidth;
        _settings.AssistantPopupHeight = _popup.ActualHeight;
        Save("size");
    }

    // ------------------------------------------------------------------ dock

    /// <summary>Docks the panel; false when there is no room for it beside the page's minimum width.</summary>
    private bool PlaceDock(AppSettings s)
    {
        var total = _area.ActualWidth;
        if (total <= 0) return false;
        var room = total - SplitterWidth - MinPage;   // what the dock may take and still leave the page its minimum
        if (room < MinDock) return false;
        SetDock(true, Math.Min(Math.Max(s.AssistantDockWidth, MinDock), room), room);
        return true;
    }

    private void SetDock(bool visible, double width, double max)
    {
        _dockPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _splitCol.Width = new GridLength(visible ? SplitterWidth : 0);
        _dockCol.MinWidth = visible ? MinDock : 0;
        _dockCol.MaxWidth = visible ? Math.Max(MinDock, max) : double.PositiveInfinity;
        _dockCol.Width = new GridLength(visible ? width : 0);
        if (_pageHost != null) _pageHost.Margin = visible ? PageMarginDocked : PageMargin;
        if (!visible) _dockPanel.Margin = DockMargin(20, 12);
        else AlignDock();
    }

    private static Thickness DockMargin(double top, double bottom) => new(0, top, 12, bottom);

    /// <summary>Lines the docked panel's top and bottom up with the page's content panes (not its header).</summary>
    private void AlignDock()
    {
        if (_dockPanel.Visibility != Visibility.Visible || _pageHost == null) return;
        double top = 20, bottom = 12;
        if (PageView() is { } page && _area.ActualHeight > 0)
        {
            // top: right under the page title on every page, the same gap as Analyze's body (its 8 px top margin)
            if (FindHeader(page) is { } title && title.IsDescendantOf(_area))
            {
                var t = title.TransformToAncestor(_area).Transform(new Point(0, title.ActualHeight)).Y + TitleGap;
                if (FindScroller(title) is { } sv) t += sv.VerticalOffset;   // as laid out unscrolled: the panel does not move while the page scrolls
                if (t > top && t < _area.ActualHeight / 2) top = t;
            }
            // bottom: level with a named body (Analyze, Explore), else the page's own bottom margin
            foreach (var name in BodyNames)
            {
                if (page.FindName(name) is not FrameworkElement body || !body.IsVisible || body.ActualHeight <= 0 || !body.IsDescendantOf(_area)) continue;
                var b = _area.ActualHeight - (body.TransformToAncestor(_area).Transform(new Point(0, 0)).Y + body.ActualHeight);
                if (b >= 0) bottom = b;
                break;
            }
        }
        var m = DockMargin(Math.Round(top), Math.Round(bottom));
        if (_dockPanel.Margin != m) _dockPanel.Margin = m;
    }

    /// <summary>
    /// The page's view: Analyze sets the view itself as the host's content, the other pages set their view model, which the host
    /// renders from a data template (the view is then the content presenter's child).
    /// </summary>
    private FrameworkElement? PageView()
    {
        if (_pageHost == null) return null;
        if (_pageHost.Content is FrameworkElement view) return view;
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(_pageHost) == 0) return null;
        var presenter = System.Windows.Media.VisualTreeHelper.GetChild(_pageHost, 0);
        if (presenter is FrameworkElement direct and not ContentPresenter) return direct;
        return System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter) > 0 ? System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement : null;
    }

    /// <summary>The page's title control (first <see cref="EzForensics.App.Controls.PageHeader"/> in its visual tree), or null.</summary>
    private static FrameworkElement? FindHeader(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is EzForensics.App.Controls.PageHeader h && h.IsVisible) return h;
            for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i < n; i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    private static ScrollViewer? FindScroller(DependencyObject d)
    {
        for (var p = System.Windows.Media.VisualTreeHelper.GetParent(d); p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }

    private void SaveDockWidth()
    {
        if (_settings == null || _dockPanel.Visibility != Visibility.Visible) return;
        var w = _dockCol.ActualWidth;
        if (w < MinDock) return;
        _settings.AssistantDockWidth = w;
        Save("width");
    }

    private static void Save(string what)
    {
        try { AppServices.Current?.SaveSettings(); }
        catch (Exception ex) { AppLog.Warn($"Could not save the Spectre {what}: " + ex.Message); }
    }
}
