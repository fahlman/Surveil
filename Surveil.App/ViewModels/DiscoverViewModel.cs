using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Runs WS-Discovery on the local network and lists the ONVIF responders, tagged with
/// their building/area per the map.</summary>
public sealed partial class DiscoverViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;
    private CancellationTokenSource? cts;

    [ObservableProperty] private int timeoutMs = 3000;
    [ObservableProperty] private bool isDiscovering;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string summary = "";

    public ObservableCollection<DiscoveryCamera> Results { get; } = new();

    public DiscoverViewModel() => timeoutMs = session.Settings.DiscoverTimeoutMs;

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsDiscovering) return;

        Results.Clear();
        Summary = "";
        HasError = false;
        IsDiscovering = true;
        StatusMessage = "Listening for ONVIF responders…";
        cts = new CancellationTokenSource();

        try
        {
            var result = await session.Service.DiscoverAsync(TimeSpan.FromMilliseconds(TimeoutMs), cts.Token);
            foreach (var camera in result.Cameras.OrderBy(c => c.Building).ThenBy(c => c.Ip))
                Results.Add(camera);
            Summary = $"{result.Cameras.Count} cameras · {result.DistinctSubnets} subnets · {result.DistinctBuildings} buildings";
            StatusMessage = result.Cameras.Count == 0 ? "No responders. Cameras may be on another subnet." : "Done.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Discovery cancelled.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
        finally
        {
            IsDiscovering = false;
            cts?.Dispose();
            cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => cts?.Cancel();

    /// <summary>Push the discovered cameras into the Provision drawer and open it.</summary>
    [RelayCommand]
    private void SendToProvision()
    {
        var ips = Results.Select(r => r.Ip).Distinct().ToArray();
        if (ips.Length == 0) return;
        session.RequestProvision(string.Join(" ", ips));
    }
}
