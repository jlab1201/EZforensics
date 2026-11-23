using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace EzForensics.App.Views;

public enum EzDialogSeverity { Info, Question, Warning, Error, Success }

/// <summary>The one themed message dialog (replaces MessageBox). Use <see cref="EzForensics.App.Services.Dialogs"/> rather than this directly.</summary>
public partial class EzDialog : Window
{
    private readonly List<Button> _buttons = new();
    private readonly int _cancelIndex;

    /// <summary>Shows a one-line text box under the message (see <see cref="EzForensics.App.Services.Dialogs.Prompt"/>); read the text in <see cref="InputText"/>.</summary>
    public void EnableInput(string? initial)
    {
        InputBox.Text = initial ?? "";
        InputBox.Visibility = Visibility.Visible;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    public string InputText => InputBox.Text;

    /// <summary>Index of the clicked button; the cancel index (-1 when none) when closed with Esc.</summary>
    public int Result { get; private set; } = -1;

    public EzDialog(string title, string message, EzDialogSeverity severity, string? details, IReadOnlyList<string> buttons, int primary, int cancelIndex, bool plain = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        _cancelIndex = cancelIndex;
        var (glyph, key) = severity switch
        {
            EzDialogSeverity.Error => ("", "Brush.Error"),
            EzDialogSeverity.Warning => ("", "Brush.Warning"),
            EzDialogSeverity.Success => ("", "Brush.Success"),
            EzDialogSeverity.Question => ("", "Brush.Accent"),
            _ => ("", "Brush.Info"),
        };
        IconText.Text = glyph;
        IconText.SetResourceReference(TextBlock.ForegroundProperty, key);
        Frame.SetResourceReference(Border.BorderBrushProperty, key);
        AutomationProperties.SetHelpText(this, severity.ToString());
        // Always plain: no severity glyph and no EZ Forensics icon (window icon and header logo); the severity shows in the frame colour only.
        IconText.Visibility = Visibility.Collapsed;
        Logo.Visibility = Visibility.Collapsed;
        Icon = null;
        if (!string.IsNullOrWhiteSpace(details)) { DetailsBox.Text = details; DetailsExpander.Visibility = Visibility.Visible; }

        for (var i = 0; i < buttons.Count; i++)
        {
            var idx = i;
            var b = new Button { Content = buttons[i], MinWidth = 84, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = i == primary };
            if (i == primary) b.SetResourceReference(StyleProperty, "AccentButton");
            AutomationProperties.SetAutomationId(b, "EzDialog.Button." + (i + 1));
            AutomationProperties.SetName(b, buttons[i]);
            b.Click += (_, _) => { Result = idx; DialogResult = true; };
            _buttons.Add(b);
            ButtonRow.Children.Add(b);
        }
        Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Result = _cancelIndex; DialogResult = false; e.Handled = true;
        };
        Loaded += (_, _) => { if (primary >= 0 && primary < _buttons.Count) _buttons[primary].Focus(); };
    }
}
