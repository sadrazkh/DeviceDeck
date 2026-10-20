using System.Diagnostics;
using System.Globalization;

namespace DeviceDeck.Shell.Hosting;

/// <summary>A small append-only log in %LocalAppData%\DeviceDeck\logs, one file per day.</summary>
internal static class ShellLog
{
    private static readonly Lock Gate = new();
    private static string? _folder;

    public static void Initialize(string folder)
    {
        Directory.CreateDirectory(folder);
        _folder = folder;
    }

    public static void Info(string message) => Write("info", message, null);

    public static void Warn(string message) => Write("warn", message, null);

    public static void Error(string message, Exception? error = null) => Write("error", message, error);

    private static void Write(string level, string message, Exception? error)
    {
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level}] {message}" +
            (error is null ? "" : Environment.NewLine + error);
        Debug.WriteLine(line);
        if (_folder is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(_folder, $"devicedeck-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
