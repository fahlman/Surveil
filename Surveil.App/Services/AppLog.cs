using System.Text;

namespace Surveil.App.Services;

/// <summary>Best-effort file logger. Writing to the log must never throw or interrupt the app,
/// so every failure here is swallowed. Logs live next to the app data under <c>logs\</c>.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Surveil", "logs", "surveil.log");

    public static void Write(string message)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate)
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging is best-effort; never let it surface.
        }
    }

    public static void Write(Exception error) =>
        Write($"ERROR {error.GetType().Name}: {error.Message}{Environment.NewLine}{error}");
}
