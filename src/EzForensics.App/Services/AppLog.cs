using System.Text;

namespace EzForensics.App.Services;

/// <summary>Minimal append-only file logger at %LOCALAPPDATA%\EzForensicsData\logs\app.log.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = EzForensics.Core.Settings.AppPaths.LogsDir;

    public static string LogPath { get; } = Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never throw
        }
    }
}
