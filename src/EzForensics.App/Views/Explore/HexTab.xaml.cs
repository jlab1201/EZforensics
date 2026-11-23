using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EzForensics.App.ViewModels.Explore;
using EzForensics.Core.Explore;

namespace EzForensics.App.Views.Explore;

/// <summary>Hex tab: read-only hex dump of the selected file (own read-only stream), go-to offset, selection inspector, send to CyberChef.</summary>
public partial class HexTab : UserControl
{
    private ExploreViewModel? _vm;
    private string? _openPath;

    public HexTab()
    {
        InitializeComponent();
        Hex.SelectionChanged += OnSelectionChanged;
        Hex.ViewChanged += SyncScroll;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { Detach(); Hex.Close(); _openPath = null; };
    }

    private void Attach()
    {
        Detach();
        _vm = DataContext as ExploreViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        OpenCurrent();
    }

    private void Detach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = null;
    }

    private void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExploreViewModel.HexPath)) OpenCurrent();
    }

    private void OpenCurrent()
    {
        var path = _vm?.HexPath;
        if (path == _openPath) return;
        _openPath = path;
        OffsetBox.Clear();   // the go-to offset belongs to the previous file
        Hex.Close();
        EmptyText.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(path)) { InfoText.Text = ""; return; }
        try
        {
            Hex.Open(FileFactsReader.OpenEvidence(path));   // read-only, shared, never through a reparse point
            EmptyText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InfoText.Text = "Cannot read the file: " + ex.Message;
            return;
        }
        SyncScroll();
        OnSelectionChanged();
    }

    private void SyncScroll()
    {
        long total = Hex.TotalRows, vis = Hex.VisibleRows;
        VScroll.Maximum = Math.Max(0, total - vis);
        VScroll.ViewportSize = vis;
        VScroll.Value = Hex.FirstRow;
        VScroll.IsEnabled = total > vis;
    }

    private void VScroll_Scroll(object sender, ScrollEventArgs e) => Hex.FirstRow = (long)e.NewValue;

    private void OnSelectionChanged()
    {
        SendBtn.IsEnabled = Hex.HasSelection;
        InspectorBorder.Visibility = Hex.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!Hex.HasSelection)
        {
            InspectorList.ItemsSource = null;
            InfoText.Text = Hex.Length > 0 ? $"{Hex.Length:N0} bytes" : "";
            return;
        }
        InspectorList.ItemsSource = SelectionInspector.Describe(Hex.GetSelectedBytes(8));
        InfoText.Text = $"Offset 0x{Hex.SelectionStart:X} ({Hex.SelectionStart:N0})   {Hex.SelectionLength:N0} byte{(Hex.SelectionLength == 1 ? "" : "s")} selected";
    }

    private void OffsetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GoTo_Click(sender, e); e.Handled = true; }
    }

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        var t = OffsetBox.Text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 0) return;
        if (long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var off)) Hex.GoTo(off);
        else InfoText.Text = "Not a valid hex offset.";
    }

    private async void SendSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || !Hex.HasSelection) return;
        await _vm.SendToCyberChefAsync(Hex.GetSelectedBytes());
    }
}
