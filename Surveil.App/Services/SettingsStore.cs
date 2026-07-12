using System.Text.Json;

namespace Surveil.App.Services;

/// <summary>User-tunable defaults, persisted to <c>settings.json</c> in the data directory.
/// Never holds a password — credentials stay in memory only.</summary>
public sealed class AppSettings
{
    public string DefaultUsername { get; set; } = "admin";
    public int DefaultPort { get; set; } = 80;
    public int DefaultTimeoutMs { get; set; } = 400;
    public int DefaultConcurrency { get; set; } = 256;
    public int DiscoverTimeoutMs { get; set; } = 3000;
    public string PreferredCodecs { get; set; } = "H265, H264";
    public bool DryRunByDefault { get; set; } = true;
}

/// <summary>Loads and saves <see cref="AppSettings"/>. Loading never throws (falls back to
/// defaults and logs); saving surfaces failures so the UI can report them.</summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string dataDirectory;
    private string Path => System.IO.Path.Combine(dataDirectory, FileName);

    public SettingsStore(string dataDirectory) => this.dataDirectory = dataDirectory;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            throw;
        }
    }
}
