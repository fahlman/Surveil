using Surveil.Core;

namespace Surveil.App.Services;

/// <summary>Process-wide state shared by every page: the core service, the loaded building
/// map, and the ONVIF credentials used for provisioning. The password is held in memory only
/// and is never written to disk.</summary>
public sealed class AppSession
{
    public static AppSession Current { get; } = new();

    public JsonStore Store { get; }
    public SurveilService Service { get; }

    /// <summary>The building map, kept in memory so every page sees the same edits. Reloaded
    /// from disk on startup; persisted explicitly from the Buildings page.</summary>
    public SurveilConfig Config { get; set; } = new();

    /// <summary>ONVIF credentials for provisioning and camera connections (in-memory only).</summary>
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";

    private AppSession()
    {
        Store = new JsonStore();
        Service = new SurveilService(Store);
    }

    public string DataDirectory => Store.DataDirectory;

    public async Task LoadConfigAsync(CancellationToken cancellationToken = default) =>
        Config = await Service.GetConfigAsync(cancellationToken);
}
