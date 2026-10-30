using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.Services;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Small modal "enter a name" dialog (Rename, New folder) with live validation. <c>validate</c> returns an error message or null.
/// AutomationIds: <c>Results.Prompt.Window</c>, <c>Results.Prompt.Text</c>, <c>Results.Prompt.Error</c>, <c>Results.Prompt.Ok</c>, <c>Results.Prompt.Cancel</c>.
/// </summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Button _ok;
    private readonly CheckBox? _check;
    private readonly Func<string, string?> _validate;

    public TextPromptWindow(string title, string label, string initial, Func<string, string?> validate, int selectLength = -1,
        string? checkLabel = null)
    {
        _validate = validate;
        Services.WindowChrome.HideIcon(this);
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        AutomationProperties.SetAutomationId(this, "Results.Prompt.Window");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        _box = new TextBox { Text = initial };
        AutomationProperties.SetAutomationId(_box, "Results.Prompt.Text");
        AutomationProperties.SetName(_box, label);
        root.Children.Add(_box);
        _error = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
        AutomationProperties.SetAutomationId(_error, "Results.Prompt.Error");
        root.Children.Add(_error);
        if (checkLabel != null)
        {
            _check = new CheckBox { Content = checkLabel, IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetAutomationId(_check, "Results.Prompt.Check");
            root.Children.Add(_check);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        AutomationProperties.SetAutomationId(_ok, "Results.Prompt.Ok");
        _ok.Click += (_, _) => { if (Validate()) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(cancel, "Results.Prompt.Cancel");
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        _box.TextChanged += (_, _) => Validate();
        Loaded += (_, _) =>
        {
            _box.Focus();
            if (selectLength >= 0 && selectLength <= _box.Text.Length) _box.Select(0, selectLength);
            else _box.SelectAll();
            Validate();
        };
        Appearance.Attach(this);
    }

    public string Value => _box.Text.Trim();

    /// <summary>State of the optional check box (true when there is none).</summary>
    public bool Checked => _check?.IsChecked != false;

    private bool Validate()
    {
        var err = _validate(_box.Text);
        _error.Text = err ?? "";
        _error.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
        _ok.IsEnabled = err == null;
        return err == null;
    }

    /// <summary>Shows the dialog; returns the entered text or null when cancelled.</summary>
    public static string? Ask(DependencyObject owner, string title, string label, string initial, Func<string, string?> validate, int selectLength = -1)
    {
        var w = new TextPromptWindow(title, label, initial, validate, selectLength) { Owner = GetWindow(owner) };
        return w.ShowDialog() == true ? w.Value : null;
    }

    /// <summary>Like <see cref="Ask"/> with a check box (default checked); returns null when cancelled, else the text and the box state.</summary>
    public static (string Text, bool Checked)? AskWithCheck(DependencyObject owner, string title, string label, string initial,
        Func<string, string?> validate, string checkLabel)
    {
        var w = new TextPromptWindow(title, label, initial, validate, -1, checkLabel) { Owner = GetWindow(owner) };
        return w.ShowDialog() == true ? (w.Value, w.Checked) : null;
    }
}
