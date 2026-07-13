using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;

namespace Surveil.App.ViewModels;

/// <summary>Edits the persisted <see cref="AppSettings"/> defaults. Saving writes settings.json
/// and updates the shared session; the password is never part of settings.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSession session;

    [ObservableProperty] private string defaultUsername;
    [ObservableProperty] private int maxConfigurationConcurrency;
    [ObservableProperty] private int defaultPort;
    [ObservableProperty] private int defaultTimeoutMs;
    [ObservableProperty] private int defaultConcurrency;
    [ObservableProperty] private int discoverTimeoutMs;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;

    public string DataDirectory => session.DataDirectory;

    public SettingsViewModel(AppSession session)
    {
        this.session = session;
        var s = session.Settings;
        defaultUsername = s.DefaultUsername;
        maxConfigurationConcurrency = s.MaxConfigurationConcurrency;
        defaultPort = s.DefaultPort;
        defaultTimeoutMs = s.DefaultTimeoutMs;
        defaultConcurrency = s.DefaultConcurrency;
        discoverTimeoutMs = s.DiscoverTimeoutMs;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            session.SaveSettings(new AppSettings
            {
                DefaultUsername = DefaultUsername.Trim(),
                MaxConfigurationConcurrency = MaxConfigurationConcurrency,
                DefaultPort = DefaultPort,
                DefaultTimeoutMs = DefaultTimeoutMs,
                DefaultConcurrency = DefaultConcurrency,
                DiscoverTimeoutMs = DiscoverTimeoutMs,
            });
            HasError = false;
            StatusMessage = $"Saved to {System.IO.Path.Combine(DataDirectory, SettingsStore.FileName)}. New defaults apply to pages opened next.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo { FileName = DataDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }
}
