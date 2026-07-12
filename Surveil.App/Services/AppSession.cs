using Surveil.App.ViewModels;
using Surveil.Core;

namespace Surveil.App.Services;

/// <summary>Process-wide state shared by every page: the core service, the loaded site
/// map, and the ONVIF credentials used for provisioning. The password is held in memory only
/// and is never written to disk.</summary>
public sealed class AppSession
{
    public static AppSession Current { get; } = new();

    public JsonStore Store { get; }
    public SurveilService Service { get; }
    public SettingsStore SettingsStore { get; }

    /// <summary>Persisted user defaults (port, timeouts, default username, …). Never the password.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>The site map, kept in memory so every page sees the same edits. Reloaded
    /// from disk on startup; persisted explicitly from the Sites page.</summary>
    public SurveilConfig Config { get; set; } = new();

    /// <summary>ONVIF credentials for provisioning and camera connections (in-memory only).</summary>
    public string Username { get; set; }
    public string Password { get; set; } = "";

    private AppSession()
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

    // --- Provision drawer (single shared instance so Scan/Discover can push targets to it) ---

    private ProvisionViewModel? provision;

    /// <summary>The one Provision view model behind the right-side drawer. Created lazily so it
    /// isn't built during this singleton's own construction.</summary>
    public ProvisionViewModel Provision => provision ??= new ProvisionViewModel();

    /// <summary>Raised when a page asks to open the Provision drawer (e.g. "Send to Provision").</summary>
    public event Action? ProvisionDrawerRequested;

    /// <summary>Load addresses into the Provision panel and open it.</summary>
    public void RequestProvision(string targets)
    {
        Provision.Targets = targets;
        Provision.IsPaneOpen = true;
        ProvisionDrawerRequested?.Invoke();
    }
}
