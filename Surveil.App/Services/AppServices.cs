using Surveil.App.ViewModels;

namespace Surveil.App.Services;

/// <summary>The application composition root. It creates process-lifetime state explicitly and
/// hands dependencies to windows/pages rather than exposing a global service locator.</summary>
public sealed record AppServices(AppSession Session, ConfigurationViewModel Configuration, bool DemoMode)
{
    public static AppServices Create()
    {
        var session = new AppSession();
        var demoMode = string.Equals(Environment.GetEnvironmentVariable("SURVEIL_DEMO"), "1",
            StringComparison.OrdinalIgnoreCase);
        return new AppServices(session, new ConfigurationViewModel(session), demoMode);
    }
}
