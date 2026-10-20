using System.IO;

namespace DocsViewer.Infrastructure;

/// <summary>Appends exceptions to %LocalAppData%\DocsViewer\errors.log. Never throws.</summary>
public static class ErrorLog
{
    public static string LogPath => Path.Combine(AppSettings.DataDirectory, "errors.log");

    public static void Write(Exception? ex, string? context = null)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context ?? "Unhandled"}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(LogPath, text);
        }
        catch
        {
            // Logging must never fail the app.
        }
    }
}
