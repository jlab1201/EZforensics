using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views;

/// <summary>Non-modal "Cell value" window: full value of one cell, prev/next row in the same column, wrap and JSON/XML pretty print.</summary>
public partial class CellViewerWindow : Window
{
    private VirtualRowList? _rows;
    private int _position;
    private int _column;
    private string _columnName = "";
    private string _raw = "";
    private string? _pretty;

    public CellViewerWindow()
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Appearance.Attach(this);
        var s = AppServices.Current.Settings;
        Width = Math.Max(MinWidth, s.CellViewerWidth);
        Height = Math.Max(MinHeight, s.CellViewerHeight);
        Closed += (_, _) =>
        {
            s.CellViewerWidth = Math.Round(ActualWidth);
            s.CellViewerHeight = Math.Round(ActualHeight);
            try { AppServices.Current.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        };
    }

    /// <summary>Shows the cell at view <paramref name="position"/>, CSV column <paramref name="column"/>.</summary>
    public void ShowCell(VirtualRowList rows, IReadOnlyList<string> headers, int position, int column)
    {
        _rows = rows;
        _column = column;
        _columnName = (uint)column < (uint)headers.Count ? headers[column] : $"Column {column + 1}";
        Load(position);
    }

    private void Load(int position)
    {
        if (_rows == null || _rows.Count == 0) return;
        int step = position < _position ? -1 : 1;
        _position = Math.Clamp(position, 0, _rows.Count - 1);
        // grouped (pivot) view: skip group header lines, in the direction of travel first
        if (_rows[_position] is GroupRowItem)
        {
            int p = _position;
            while ((uint)p < (uint)_rows.Count && _rows[p] is GroupRowItem) p += step;
            if ((uint)p >= (uint)_rows.Count) { p = _position; while ((uint)p < (uint)_rows.Count && _rows[p] is GroupRowItem) p -= step; }
            if ((uint)p < (uint)_rows.Count) _position = p;
        }
        var item = _rows[_position] as RowItem;
        _raw = item?[_column] ?? "";
        _pretty = TryPretty(_raw);
        PrettyCheck.IsEnabled = _pretty != null;
        PrettyCheck.Visibility = _pretty != null ? Visibility.Visible : Visibility.Collapsed;
        var row = item?.RowNumber ?? _position + 1;
        Title = $"Cell value — {_columnName} — row {row:N0}";
        HeaderText.Text = $"{_columnName}  ·  row {row:N0}";
        InfoText.Text = $"{_raw.Length:N0} characters  ·  {_position + 1:N0} of {_rows.Count:N0} in view" + (_pretty != null ? "  ·  " + (_raw.TrimStart().StartsWith('<') ? "XML" : "JSON") : "");
        PrevButton.IsEnabled = _position > 0;
        NextButton.IsEnabled = _position < _rows.Count - 1;
        Render();
    }

    private void Render()
    {
        ValueBox.TextWrapping = WrapCheck.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ValueBox.Text = PrettyCheck.IsChecked == true && _pretty != null ? _pretty : _raw;
    }

    /// <summary>Indented JSON or XML, or null when the value is neither.</summary>
    public static string? TryPretty(string s)
    {
        var t = s.Trim();
        if (t.Length < 2) return null;
        try
        {
            if ((t[0] == '{' && t[^1] == '}') || (t[0] == '[' && t[^1] == ']'))
            {
                using var doc = JsonDocument.Parse(t);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            if (t[0] == '<' && t[^1] == '>')
                return XDocument.Parse(t).ToString();
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException) { }
        return null;
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => Render();
    private void Prev_Click(object sender, RoutedEventArgs e) => Load(_position - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Load(_position + 1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = ValueBox.SelectionLength > 0 ? ValueBox.SelectedText : ValueBox.Text;
        try { Clipboard.SetDataObject(text, true); }
        catch (Exception ex) { AppLog.Error("Could not copy", ex); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control) { Load(_position - 1); e.Handled = true; }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control) { Load(_position + 1); e.Handled = true; }
    }
}
