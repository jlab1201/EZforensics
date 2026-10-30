using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace EzForensics.App.Controls;

/// <summary>
/// A small circled "?" that holds an explanation, so pages can keep one-line text. The explanation shows as a tooltip on hover,
/// when the "?" gets keyboard focus (Tab), and on click / Enter / Space (toggles it; Esc closes it). Screen readers get it as the
/// help text of a "Help" button. Collapsed when <see cref="Text"/> is empty. Template: the implicit <c>HelpTip</c> style in
/// Themes/Controls.xaml.
/// </summary>
public sealed class HelpTip : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpTip),
        new PropertyMetadata(null, (d, e) => ((HelpTip)d).OnTextChanged(e.NewValue as string)));

    private readonly ToolTip _tip;

    public HelpTip()
    {
        _tip = new ToolTip { PlacementTarget = this, Placement = PlacementMode.Bottom };
        ToolTip = _tip;
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, true);
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        Focusable = true;
        IsTabStop = true;
        AutomationProperties.SetName(this, "Help");
    }

    /// <summary>The explanation (plain text; line breaks are kept).</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnTextChanged(string? text)
    {
        _tip.Content = text;
        AutomationProperties.SetHelpText(this, text ?? "");
    }

    private void Toggle()
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        _tip.IsOpen = !_tip.IsOpen;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        Toggle();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space) { Toggle(); e.Handled = true; }
        else if (e.Key == Key.Escape && _tip.IsOpen) { _tip.IsOpen = false; e.Handled = true; }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _tip.IsOpen = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer
    {
        public Peer(HelpTip owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(HelpTip);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
