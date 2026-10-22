using System.Diagnostics;
using System.IO;
using System.Text;
using VeilDesk.Core.Settings;

namespace VeilDesk.App;

/// <summary>
/// Appends a small, local audit trail of lifecycle events (protection started,
/// stopped, errors) to the data root. The log never leaves the machine and a
/// failure to write must never interrupt protection.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppPaths.LogsDirectory, "app.log");

    public static void Write(string eventName, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("O"))
                    .Append(" [").Append(eventName).Append(']');
                if (!string.IsNullOrWhiteSpace(detail))
                    line.Append(' ').Append(detail);
                File.AppendAllText(Path, line.AppendLine().ToString());
            }
        }
        catch
        {
            // Logging must never be the reason protection fails.
        }
    }

    public static void WriteException(string eventName, Exception ex)
        => Write(eventName, ex.GetType().Name + ": " + ex.Message);
}
