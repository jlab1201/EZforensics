using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>
/// One input for ATT&amp;CK techniques. Type an id or part of a name ("masq" finds "T1036 · Masquerading — Defense Evasion"); a list under the box
/// offers the matches and choosing one puts its id in. Several techniques are kept as a comma separated list in <see cref="Text"/>.
/// The box shows each chosen technique spelled out ("T1036 · Masquerading — Defense Evasion"), so no separate tactic field is needed;
/// <see cref="Text"/> holds only the ids.
/// </summary>
public sealed class TechniquePicker : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(TechniquePicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((TechniquePicker)d).OnTextChanged((string?)e.NewValue ?? "")));
    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.Register(nameof(IsInvalid), typeof(bool), typeof(TechniquePicker),
        new PropertyMetadata(false, (d, _) => ((TechniquePicker)d).ApplyBorder()));

    /// <summary>The chosen technique ids, comma separated ("T1059, T1003.001").</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsInvalid { get => (bool)GetValue(IsInvalidProperty); set => SetValue(IsInvalidProperty, value); }

    private readonly TextBox _box = new();
    private readonly Popup _popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
    private readonly ListBox _list = new() { MaxHeight = 240, BorderThickness = new Thickness(0) };
    private bool _syncing;

    public TechniquePicker()
    {
        _box.TextWrapping = TextWrapping.Wrap;   // several "T1036 · Masquerading — Defense Evasion" entries wrap instead of scrolling away
        Children.Add(_box);
        _popup.PlacementTarget = _box;
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2), Child = _list };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        _popup.Child = border;
        _list.SetResourceReference(Control.BackgroundProperty, "Brush.Surface");
        AutomationProperties.SetAutomationId(_list, "TechniquePicker.Matches");
        _box.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true; Text = IdsOf(_box.Text); _syncing = false;
            ShowMatches();
        };
        _box.PreviewKeyDown += Box_PreviewKeyDown;
        _box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_list.IsKeyboardFocusWithin) return;
            _popup.IsOpen = false;
            ShowDisplay();   // ids typed by hand ("t1059") become "T1059 · Command and Scripting Interpreter — Execution"
        }));
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem) Choose();
        };
        Loaded += (_, _) =>
        {
            // the automation id / name set on the picker belong to the text box (what UI tests type into)
            if (AutomationProperties.GetAutomationId(this) is { Length: > 0 } id && AutomationProperties.GetAutomationId(_box).Length == 0)
                SetAutomation(id, AutomationProperties.GetName(this));
            ShowDisplay(); ApplyBorder();
        };
    }

    /// <summary>Sets the automation id / name of the inner text box (the part tests type into).</summary>
    public void SetAutomation(string id, string name)
    {
        AutomationProperties.SetAutomationId(_box, id);
        AutomationProperties.SetName(_box, name);
    }

    /// <summary>Focus goes to the text box.</summary>
    public new bool Focus() => _box.Focus();

    private void OnTextChanged(string value)
    {
        if (_syncing || IdsOf(_box.Text) == IdsOf(value)) return;
        ShowDisplay();
    }

    /// <summary>The ids of a box text: each comma separated entry cut to its id ("T1036 · Masquerading — …" gives "T1036").</summary>
    public static string IdsOf(string? boxText) =>
        string.Join(", ", (boxText ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(TechniqueCatalog.IdOfChoice).Where(t => t.Length > 0));

    /// <summary>What the box shows for an id list: each known id spelled out with its name and tactic; anything else as typed.</summary>
    public static string DisplayOf(string? ids) =>
        string.Join(", ", (ids ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0)
            .Select(t =>
            {
                var id = TechniqueCatalog.IdOfChoice(t);
                var name = TechniqueCatalog.IsValid(id) ? TechniqueCatalog.Name(id) : null;
                return name == null ? t : new TechniqueCatalog.Match(id.ToUpperInvariant(), name, TechniqueCatalog.TacticOf(id) ?? "").Display;
            }));

    /// <summary>Puts the spelled-out form of <see cref="Text"/> in the box (keeps the caret at the end).</summary>
    private void ShowDisplay()
    {
        var shown = DisplayOf(Text);
        if (_box.Text == shown) return;
        _syncing = true; _box.Text = shown; _box.CaretIndex = shown.Length; _syncing = false;
    }

    private void ApplyBorder()
    {
        _box.ToolTip = IsInvalid ? "Use ATT&CK ids like T1059 or T1059.001, separated by commas." : null;
        _box.SetResourceReference(Control.BorderBrushProperty, IsInvalid ? "Brush.Error" : "Brush.BorderStrong");
    }

    /// <summary>The part being typed: everything after the last separator.</summary>
    private (int Start, string Token) CurrentToken()
    {
        var t = _box.Text ?? "";
        int cut = t.LastIndexOfAny(new[] { ',', ';' }) + 1;
        return (cut, t[cut..].Trim());
    }

    private void ShowMatches()
    {
        if (!_box.IsKeyboardFocusWithin) { _popup.IsOpen = false; return; }
        var token = CurrentToken().Token;
        if (token.Length == 0) { _popup.IsOpen = false; return; }
        var matches = TechniqueCatalog.Search(token, 30);
        _list.ItemsSource = matches;
        _popup.Width = Math.Max(_box.ActualWidth, 280);
        if (matches.Count > 0) _list.SelectedIndex = 0;
        _popup.IsOpen = matches.Count > 0;
    }

    private void Choose()
    {
        if (_list.SelectedItem is not TechniqueCatalog.Match m) return;
        var (start, _) = CurrentToken();
        var head = (_box.Text ?? "")[..start].TrimEnd(',', ';', ' ');
        var text = (head.Length > 0 ? head + ", " : "") + m.Display;
        _popup.IsOpen = false;
        _box.Text = text;
        _box.CaretIndex = text.Length;
        _box.Focus();
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down: _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; break;
            case Key.Up: _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; break;
            case Key.Enter or Key.Tab: Choose(); e.Handled = true; break;
            case Key.Escape: _popup.IsOpen = false; e.Handled = true; break;
        }
    }
}
