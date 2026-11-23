using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EzForensics.App.Controls;
using EzForensics.App.ViewModels;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Modal editor for a finding: Title, Severity (Info..Critical), Mapping (type-ahead ATT&amp;CK techniques, shown in the box as
/// "T1007 · System Service Discovery — Discovery"), Rows (per CSV, written like a spreadsheet range: "12-15" or "1, 15, 16, 21-24"; rows can be added or removed here),
/// Notes and, when the rows have mixed flag colours, an optional "Recolour all".
/// The mapping logic is the one the Findings editor uses (<see cref="FindingDraft.ParseMitre"/>, <see cref="TechniqueCatalog"/>); rows use <see cref="RowRanges"/>.
/// AutomationIds: <c>Results.KeyEvent.Editor</c>, <c>.Title</c>, <c>.Severity</c>, <c>.Mapping</c>, <c>.Rows</c> (one box per CSV),
/// <c>.RowsError</c>, <c>.Notes</c>, <c>.Recolour</c>, <c>.RecolourFlag</c>, <c>.Ok</c>, <c>.Cancel</c>.
/// </summary>
public sealed class KeyEventEditorWindow : Window
{
    private readonly TextBox _title;
    private readonly ComboBox _severity;
    private readonly TechniquePicker _mapping;
    private readonly Button _ok;
    private readonly KeyEventDraft _initial;
    private readonly List<(FindingFileRows File, TextBox Box)> _rowBoxes = new();
    private readonly TextBlock _rowsError;
    private readonly TextBox _notes;
    private readonly CheckBox? _recolour;
    private readonly ComboBox? _flagPick;

    /// <summary>The tactic of the first technique in <paramref name="mappingText"/> the list knows; "" when none.</summary>
    public static string TacticOf(string? mappingText) =>
        FindingDraft.ParseMitre(mappingText).Select(TechniqueCatalog.TacticOf).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";

    /// <summary>The mapping box holds something that is not an ATT&amp;CK id (T1234 or T1234.567).</summary>
    public static bool MappingInvalid(string? mappingText) => TechniqueCatalog.Invalid(FindingDraft.ParseMitre(mappingText)).Count > 0;

    public KeyEventEditorWindow(string windowTitle, KeyEventDraft initial, FindingFlagChoice? mixedFlags = null)
    {
        _initial = initial;
        Services.WindowChrome.HideIcon(this);
        Title = windowTitle;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        AutomationProperties.SetAutomationId(this, "Results.KeyEvent.Editor");

        var root = new StackPanel { Margin = new Thickness(16) };
        TextBlock Label(string text, double top = 0)
        {
            var l = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 4) };
            l.SetResourceReference(TextBlock.StyleProperty, "Caption");
            return l;
        }

        root.Children.Add(Label("Title"));
        _title = new TextBox { Text = initial.Title };
        AutomationProperties.SetAutomationId(_title, "Results.KeyEvent.Editor.Title");
        AutomationProperties.SetName(_title, "Title");
        root.Children.Add(_title);

        root.Children.Add(Label("Severity", 10));
        _severity = new ComboBox { ItemsSource = SpectreFinding.Severities.Reverse().ToList() };
        _severity.SelectedItem = SpectreFinding.Severities.FirstOrDefault(s => string.Equals(s, initial.Severity, StringComparison.OrdinalIgnoreCase)) ?? "Medium";
        AutomationProperties.SetAutomationId(_severity, "Results.KeyEvent.Editor.Severity");
        AutomationProperties.SetName(_severity, "Severity");
        root.Children.Add(_severity);

        root.Children.Add(Label("Mapping (ATT&CK technique)", 10));
        _mapping = new TechniquePicker { Text = string.Join(", ", initial.Mitre) };
        _mapping.SetAutomation("Results.KeyEvent.Editor.Mapping", "Technique");
        root.Children.Add(_mapping);

        // rows: one range box per CSV ("12-15" or "1, 15, 16, 21-24"), numbers as the grid shows them
        var files = initial.Rows ?? Array.Empty<FindingFileRows>();
        if (files.Count > 0)
        {
            root.Children.Add(Label(files.Count == 1 ? "Rows in " + files[0].FileName : "Rows", 10));
            foreach (var f in files)
            {
                var box = new TextBox { Text = RowRanges.Format(f.Rows.Select(r => r + 1)), ToolTip = "Row numbers as the grid shows them, for example 12-15 or 1, 15, 16, 21-24" };
                AutomationProperties.SetAutomationId(box, "Results.KeyEvent.Editor.Rows");
                AutomationProperties.SetName(box, "Rows in " + f.FileName);
                box.TextChanged += (_, _) => Refresh();
                _rowBoxes.Add((f, box));
                if (files.Count == 1) { root.Children.Add(box); continue; }
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var name = new TextBlock { Text = f.FileName, Width = 150, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.CsvRelPath };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                DockPanel.SetDock(name, Dock.Left);
                line.Children.Add(name);
                line.Children.Add(box);
                root.Children.Add(line);
            }
        }
        _rowsError = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0), Visibility = Visibility.Collapsed };
        _rowsError.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
        _rowsError.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Small");
        AutomationProperties.SetAutomationId(_rowsError, "Results.KeyEvent.Editor.RowsError");
        root.Children.Add(_rowsError);

        root.Children.Add(Label("Notes", 10));
        _notes = new TextBox { Text = initial.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(_notes, "Results.KeyEvent.Editor.Notes");
        AutomationProperties.SetName(_notes, "Notes");
        root.Children.Add(_notes);

        if (mixedFlags != null)
        {
            var mixed = new TextBlock { Text = "Rows have mixed flags", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
            AutomationProperties.SetAutomationId(mixed, "Results.KeyEvent.Editor.MixedFlags");
            root.Children.Add(mixed);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            _recolour = new CheckBox { Content = "Recolour all to", VerticalAlignment = VerticalAlignment.Center, IsChecked = false };
            AutomationProperties.SetAutomationId(_recolour, "Results.KeyEvent.Editor.Recolour");
            _flagPick = new ComboBox { ItemsSource = mixedFlags.Flags, DisplayMemberPath = "Label", SelectedValuePath = "Id", MinWidth = 140, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
            _flagPick.SelectedValue = mixedFlags.DefaultId;
            if (_flagPick.SelectedIndex < 0 && mixedFlags.Flags.Count > 0) _flagPick.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_flagPick, "Results.KeyEvent.Editor.RecolourFlag");
            AutomationProperties.SetName(_flagPick, "Flag colour");
            _recolour.Checked += (_, _) => _flagPick.IsEnabled = true;
            _recolour.Unchecked += (_, _) => _flagPick.IsEnabled = false;
            line.Children.Add(_recolour);
            line.Children.Add(_flagPick);
            root.Children.Add(line);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
        AutomationProperties.SetAutomationId(_ok, "Results.KeyEvent.Editor.Ok");
        _ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(cancel, "Results.KeyEvent.Editor.Cancel");
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        _title.TextChanged += (_, _) => Refresh();
        _mapping.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Refresh()));
        Refresh();
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    private void Refresh()
    {
        var text = _mapping.Text;
        bool bad = MappingInvalid(text);
        _mapping.IsInvalid = bad;
        var rowsError = RowsError();
        _rowsError.Text = rowsError ?? "";
        _rowsError.Visibility = rowsError != null ? Visibility.Visible : Visibility.Collapsed;
        _ok.IsEnabled = !bad && rowsError == null && _title.Text.Trim().Length > 0;
    }

    /// <summary>Why the row boxes can't be saved (bad range, past the end, no rows left at all); null when they are fine.</summary>
    private string? RowsError()
    {
        if (_rowBoxes.Count == 0) return null;
        long total = 0;
        foreach (var (f, box) in _rowBoxes)
        {
            var rows = RowRanges.Parse(box.Text, out var error, f.RowCount);
            box.SetResourceReference(Control.BorderBrushProperty, rows == null ? "Brush.Error" : "Brush.BorderStrong");
            if (rows == null) return (_rowBoxes.Count > 1 ? f.FileName + ": " : "") + error;
            total += rows.Count;
        }
        return total == 0 ? "A finding needs at least one row (Remove finding deletes it)." : null;
    }

    /// <summary>The rows in the boxes, 0-based, per CSV; null when the editor had no rows to show.</summary>
    private IReadOnlyList<FindingFileRows>? EditedRows() => _rowBoxes.Count == 0 ? null
        : _rowBoxes.Select(b => b.File with { Rows = (RowRanges.Parse(b.Box.Text, out _, b.File.RowCount) ?? new List<long>()).Select(r => r - 1).ToList() }).ToList();

    public KeyEventDraft Result => new(_title.Text.Trim(), _severity.SelectedItem as string ?? "Medium",
        FindingDraft.ParseMitre(_mapping.Text).Select(s => s.ToUpperInvariant()).ToList(), _notes.Text.Trim(), _initial.Evidence,
        _recolour?.IsChecked == true ? _flagPick?.SelectedValue as string : null, EditedRows());

    /// <summary>Shows the editor; null when cancelled. <paramref name="mixedFlags"/> turns on the "Recolour all" choice for rows with mixed flags.</summary>
    public static KeyEventDraft? Edit(string windowTitle, KeyEventDraft initial, Window? owner, FindingFlagChoice? mixedFlags = null)
    {
        var w = new KeyEventEditorWindow(windowTitle, initial, mixedFlags);
        if (owner != null) w.Owner = owner;
        return w.ShowDialog() == true ? w.Result : null;
    }
}
