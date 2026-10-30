using System.Windows;
using EzForensics.App.Services;
using EzForensics.Core.Archives;

namespace EzForensics.App.Views;

/// <summary>
/// Asks the user for an encrypted archive's password (Ingest page). Shows the archive name, format and encryption method, and
/// "Incorrect password — try again" after a failed attempt. The PasswordBox is cleared as soon as the value is read and when the
/// window closes; the password is never logged, persisted or shown. Cancel returns null (the scan stops cleanly).
/// </summary>
public partial class ArchivePasswordWindow : Window
{
    private string? _result;

    public ArchivePasswordWindow(ArchivePasswordRequest request)
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Appearance.Attach(this);
        NameText.Text = Path.GetFileName(request.ArchivePath);
        PathText.Text = request.ArchivePath;
        DetailText.Text = $"Format: {FormatName(request.Format)}  ·  Encryption: {request.EncryptionMethod ?? "unknown method"}";
        ErrorPanel.Visibility = request.WrongPassword ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Box.Focus();
        Closed += (_, _) => Box.Clear();
    }

    public static string FormatName(ArchiveFormat f) => f switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7-Zip",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.GZip => "GZip",
        ArchiveFormat.TarGz => "TAR.GZ",
        ArchiveFormat.Vhd => "VHD",
        ArchiveFormat.Vhdx => "VHDX",
        _ => "Unknown",
    };

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _result = Box.Password;
        Box.Clear();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Box.Clear();
        DialogResult = false;
    }

    /// <summary>
    /// The scan was cancelled while the dialog is open: clear the box and close (ShowDialog returns false, so Ask returns null). Before the
    /// window is loaded the close is deferred to Loaded (DialogResult cannot be set earlier).
    /// </summary>
    private void CloseForCancellation()
    {
        _result = null;
        Box.Clear();
        if (_closed) return;
        if (!IsLoaded) { _cancelRequested = true; return; }
        try { DialogResult = false; }
        catch (InvalidOperationException) { Close(); }
    }

    private bool _cancelRequested, _closed;

    /// <summary>
    /// Shows the dialog (UI thread). Null = the user cancelled, or <paramref name="ct"/> was cancelled (e.g. Cancel on the Ingest page):
    /// the dialog then closes itself. The typed password is returned only for OK and is not kept by the window.
    /// </summary>
    public static string? Ask(Window? owner, ArchivePasswordRequest request, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return null;
        var w = new ArchivePasswordWindow(request);
        if (owner != null && owner.IsVisible) w.Owner = owner;
        w.Closed += (_, _) => w._closed = true;
        w.Loaded += (_, _) =>
        {
            if (w._cancelRequested || ct.IsCancellationRequested) w.Dispatcher.InvokeAsync(w.CloseForCancellation);
        };
        bool? shown;
        using (ct.Register(() => w.Dispatcher.InvokeAsync(w.CloseForCancellation)))
            shown = w.ShowDialog();
        var value = shown == true && !ct.IsCancellationRequested ? w._result : null;
        w._result = null;
        return value;
    }
}
