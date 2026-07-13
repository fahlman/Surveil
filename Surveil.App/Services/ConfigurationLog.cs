namespace Surveil.App.Services;

/// <summary>Appends a line per configuration change (success and failure) to
/// <c>logs/configuration.log</c> in the data directory. Thread-safe; failures fall back to the app log.</summary>
public static class ConfigurationLog
{
    private static readonly object Gate = new();

    public static string FilePath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Surveil", "logs", "configuration.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                System.IO.File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }
}
