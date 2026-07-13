using Surveil.Core;

namespace Surveil.App.Services;

/// <summary>Process-wide state shared by every page: the core service, the loaded site
/// map, and the ONVIF credentials used to configure cameras. The password is held in memory only
/// and is never written to disk.</summary>
public sealed class AppSession
{
    public JsonStore Store { get; }
    public SurveilService Service { get; }
    public SettingsStore SettingsStore { get; }

    /// <summary>Persisted user defaults (port, timeouts, default username, …). Never the password.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>The site map, kept in memory so every page sees the same edits. Reloaded
    /// from disk on startup; persisted explicitly from the Sites page.</summary>
    public SurveilConfig Config { get; set; } = new();

    /// <summary>ONVIF credentials for configuring and connecting to cameras (in-memory only).</summary>
    public string Username { get; set; }
    public string Password { get; set; } = "";

    public AppSession()
    {
        Store = new JsonStore();
        Service = new SurveilService(Store);
        SettingsStore = new SettingsStore(Store.DataDirectory);
        Settings = SettingsStore.Load();
        Username = Settings.DefaultUsername;
    }

    /// <summary>Persist new settings and apply the ones that seed live state (the default username).</summary>
    public void SaveSettings(AppSettings settings)
    {
        SettingsStore.Save(settings);
        Settings = settings;
        Username = settings.DefaultUsername;
    }

    public string DataDirectory => Store.DataDirectory;

    public async Task LoadConfigAsync(CancellationToken cancellationToken = default) =>
        Config = await Service.GetConfigAsync(cancellationToken);

}
