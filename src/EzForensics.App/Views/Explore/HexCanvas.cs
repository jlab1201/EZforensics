using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views.Explore;

/// <summary>
/// Read-only hex dump of a seekable stream (offset | 16 bytes | ASCII). Only the visible rows are read and drawn, so any file size works.
/// The stream is owned by the caller's <see cref="Open"/> call and closed by <see cref="Close"/>; the control never writes to it.
/// </summary>
public sealed class HexCanvas : FrameworkElement
{
    public const int BytesPerRow = 16;

    private Stream? _stream;
    private long _length;
    private long _firstRow;
    private long _anchor = -1, _caret = -1;
    private double _charW = 7.2, _rowH = 16;
    private bool _dragging;
    private Typeface _face = new("Cascadia Mono, Consolas, Courier New");
    private const double FontSize = 12;

    public HexCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        MeasureFont();
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    public event Action? SelectionChanged;
    public event Action? ViewChanged;

    public long Length => _length;
    public long TotalRows => (_length + BytesPerRow - 1) / BytesPerRow;
    public int VisibleRows => Math.Max(1, (int)(ActualHeight / _rowH));

    public long FirstRow
    {
        get => _firstRow;
        set
        {
            var max = Math.Max(0, TotalRows - VisibleRows);
            var v = Math.Clamp(value, 0, max);
            if (v == _firstRow) return;
            _firstRow = v;
            InvalidateVisual();
            ViewChanged?.Invoke();
        }
    }

    public bool HasSelection => _anchor >= 0 && _caret >= 0;
    public long SelectionStart => HasSelection ? Math.Min(_anchor, _caret) : -1;
    public long SelectionLength => HasSelection ? Math.Abs(_caret - _anchor) + 1 : 0;

    public void Open(Stream s)
    {
        Close();
        _stream = s;
        _length = s.Length;
        _firstRow = 0;
        _anchor = _caret = -1;
        InvalidateVisual();
        ViewChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void Close()
    {
        var s = _stream;
        _stream = null;
        _length = 0;
        _anchor = _caret = -1;
        try { s?.Dispose(); } catch (IOException) { }
        InvalidateVisual();
        ViewChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects the byte at <paramref name="offset"/> and scrolls it into view.</summary>
    public void GoTo(long offset)
    {
        if (_length == 0) return;
        offset = Math.Clamp(offset, 0, _length - 1);
        _anchor = _caret = offset;
        long row = offset / BytesPerRow;
        if (row < _firstRow || row >= _firstRow + VisibleRows) FirstRow = Math.Max(0, row - VisibleRows / 3);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    /// <summary>The selected bytes (at most <paramref name="max"/>).</summary>
    public byte[] GetSelectedBytes(int max = 10 * 1024 * 1024)
    {
        if (!HasSelection) return Array.Empty<byte>();
        return ReadRange(SelectionStart, (int)Math.Min(SelectionLength, max));
    }

    private byte[] ReadRange(long offset, int count)
    {
        var s = _stream;
        if (s == null || count <= 0 || offset >= _length) return Array.Empty<byte>();
        count = (int)Math.Min(count, _length - offset);
        var buf = new byte[count];
        try
        {
            s.Position = offset;
            int read = 0, r;
            while (read < count && (r = s.Read(buf, read, count - read)) > 0) read += r;
            return read == count ? buf : buf[..read];
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return Array.Empty<byte>(); }
    }

    private void MeasureFont()
    {
        var ft = new FormattedText("0000000000", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize, Brushes.Black, 1.0);
        _charW = ft.Width / 10.0;
        _rowH = Math.Ceiling(ft.Height) + 2;
    }

    // layout: [8 hex digits][2 spaces][16 x "XX "][1 extra gap after 8][ ASCII ]
    private double HexX(int col) => _charW * 10 + col * _charW * 3 + (col >= 8 ? _charW : 0);
    private double AsciiX => _charW * 10 + 16 * _charW * 3 + _charW + _charW * 2;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? AsciiX + 16 * _charW + 8 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ViewChanged?.Invoke();
    }

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var bg = Res("Brush.Code", Brushes.Black);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_stream == null || _length == 0) return;
        var text = Res("Brush.Text", Brushes.White);
        var dim = Res("Brush.TextSecondary", Brushes.Gray);
        var sel = Res("Brush.Selection", Brushes.SteelBlue);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        int rows = (int)Math.Min(VisibleRows + 1, TotalRows - _firstRow);
        if (rows <= 0) return;
        var data = ReadRange(_firstRow * BytesPerRow, rows * BytesPerRow);
        long selS = SelectionStart, selE = HasSelection ? selS + SelectionLength - 1 : -1;

        FormattedText Ft(string s, Brush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize, b, dpi);

        for (int r = 0; r < rows; r++)
        {
            long rowOff = (_firstRow + r) * BytesPerRow;
            double y = r * _rowH + 1;
            dc.DrawText(Ft(rowOff.ToString("X8"), dim), new Point(2, y));
            int n = Math.Min(BytesPerRow, data.Length - r * BytesPerRow);
            if (n <= 0) continue;
            var ascii = new char[n];
            for (int c = 0; c < n; c++)
            {
                byte b = data[r * BytesPerRow + c];
                ascii[c] = b is >= 0x20 and < 0x7F ? (char)b : '.';
                long off = rowOff + c;
                bool isSel = off >= selS && off <= selE;
                if (isSel)
                {
                    dc.DrawRectangle(sel, null, new Rect(HexX(c) - 1, y - 1, _charW * 2 + 2, _rowH));
                    dc.DrawRectangle(sel, null, new Rect(AsciiX + c * _charW, y - 1, _charW, _rowH));
                }
                dc.DrawText(Ft(b.ToString("X2"), b == 0 ? dim : text), new Point(HexX(c), y));
            }
            dc.DrawText(Ft(new string(ascii), text), new Point(AsciiX, y));
        }
    }

    private long OffsetAt(Point p)
    {
        long row = _firstRow + (long)Math.Max(0, p.Y / _rowH);
        int col;
        if (p.X >= AsciiX - _charW) col = (int)Math.Clamp((p.X - AsciiX) / _charW, 0, BytesPerRow - 1);
        else
        {
            col = 0;
            for (int c = 0; c < BytesPerRow; c++) if (p.X >= HexX(c) - _charW / 2) col = c;
        }
        return Math.Clamp(row * BytesPerRow + col, 0, Math.Max(0, _length - 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_stream == null) return;
        Focus();
        _dragging = true;
        CaptureMouse();
        var off = OffsetAt(e.GetPosition(this));
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _anchor >= 0) _caret = off; else _anchor = _caret = off;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        if (p.Y < 0) FirstRow--; else if (p.Y > ActualHeight) FirstRow++;
        _caret = OffsetAt(p);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        FirstRow -= Math.Sign(e.Delta) * 3;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_stream == null) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        long cur = _caret < 0 ? 0 : _caret;
        long next = e.Key switch
        {
            Key.Left => cur - 1,
            Key.Right => cur + 1,
            Key.Up => cur - BytesPerRow,
            Key.Down => cur + BytesPerRow,
            Key.PageUp => cur - BytesPerRow * VisibleRows,
            Key.PageDown => cur + BytesPerRow * VisibleRows,
            Key.Home => 0,
            Key.End => _length - 1,
            _ => -1
        };
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            next = Math.Clamp(next, 0, Math.Max(0, _length - 1));
            _caret = next;
            if (!shift || _anchor < 0) _anchor = next;
            long row = next / BytesPerRow;
            if (row < _firstRow) FirstRow = row; else if (row >= _firstRow + VisibleRows) FirstRow = row - VisibleRows + 1;
            InvalidateVisual();
            SelectionChanged?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && HasSelection)
        {
            try { Clipboard.SetText(string.Join(" ", GetSelectedBytes(64 * 1024).Select(b => b.ToString("X2")))); }
            catch (System.Runtime.InteropServices.ExternalException) { }
            e.Handled = true;
        }
    }
}
