using System.Windows;
using System.Windows.Controls;
using EzForensics.App.Services;

namespace EzForensics.App.Controls;

/// <summary>Read-only monospace log view fed by a <see cref="LogBuffer"/> (AppendText in 100 ms batches).</summary>
public sealed class LogConsole : TextBox
{
    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer), typeof(LogBuffer), typeof(LogConsole), new PropertyMetadata(null, OnBufferChanged));

    public LogConsole()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        SetResourceReference(StyleProperty, "LogBox");
        Unloaded += (_, _) => Hook(Buffer, null);
        Loaded += (_, _) => { if (Buffer != null) { Hook(null, Buffer); Text = Buffer.Text; ScrollToEnd(); } };
    }

    public LogBuffer? Buffer
    {
        get => (LogBuffer?)GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    private static void OnBufferChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (LogConsole)d;
        c.Hook(e.OldValue as LogBuffer, e.NewValue as LogBuffer);
        c.Text = (e.NewValue as LogBuffer)?.Text ?? "";
        c.ScrollToEnd();
    }

    private void Hook(LogBuffer? oldB, LogBuffer? newB)
    {
        if (oldB != null) { oldB.Appended -= OnAppended; oldB.Reset -= OnReset; }
        if (newB != null)
        {
            newB.Appended -= OnAppended; newB.Reset -= OnReset;
            newB.Appended += OnAppended; newB.Reset += OnReset;
        }
    }

    private bool IsAtBottom => VerticalOffset + ViewportHeight >= ExtentHeight - 20;

    private void OnAppended(string chunk)
    {
        bool stick = IsAtBottom;
        AppendText(chunk);
        if (stick) ScrollToEnd();
    }

    private void OnReset()
    {
        Text = Buffer?.Text ?? "";
        ScrollToEnd();
    }
}
