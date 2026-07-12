using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Drives a TCP port sweep over the given targets and shows each camera's status
/// (new / online / offline) against the saved inventory.</summary>
public sealed partial class ScanViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;
    private CancellationTokenSource? cts;

    [ObservableProperty] private string targets = "";
    [ObservableProperty] private int port = 80;
    [ObservableProperty] private int timeoutMs = 400;
    [ObservableProperty] private int concurrency = 256;

    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMaximum = 1;
    [ObservableProperty] private bool progressIndeterminate;

    public ObservableCollection<CameraStatus> Results { get; } = new();

    public ScanViewModel()
    {
        var settings = session.Settings;
        port = settings.DefaultPort;
        timeoutMs = settings.DefaultTimeoutMs;
        concurrency = settings.DefaultConcurrency;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        var specs = ParseTargets(Targets);
        if (specs.Count == 0)
        {
            HasError = true;
            StatusMessage = "Enter at least one IP or CIDR range to scan.";
            return;
        }

        // Pre-flight: expand once so a bad token is reported clearly instead of mid-scan.
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = NetworkRanges.ExpandPrivate(specs);
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Could not read targets: {ex.Message}";
            AppLog.Write(ex);
            return;
        }
        if (addresses.Count == 0)
        {
            HasError = true;
            StatusMessage = "No private IPv4 addresses in those targets. Use ranges like 10.x, 172.16–31.x or 192.168.x.";
            return;
        }

        Results.Clear();
        HasError = false;
        IsScanning = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        StatusMessage = "Scanning…";
        cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressIndeterminate = false;
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Scanned;
            StatusMessage = $"Scanned {p.Scanned}/{p.Total} — {p.Found} responding";
        });

        try
        {
            var statuses = await session.Service.ScanAsync(
                specs, Port, Concurrency, TimeSpan.FromMilliseconds(TimeoutMs), progress, cts.Token);
            foreach (var status in statuses.OrderBy(s => s.Status).ThenBy(s => s.Ip))
                Results.Add(status);
            StatusMessage = $"Done. {statuses.Count(s => s.Status != "offline")} online of {statuses.Count} tracked.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
        finally
        {
            IsScanning = false;
            ProgressIndeterminate = false;
            cts?.Dispose();
            cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => cts?.Cancel();

    private static List<string> ParseTargets(string text) => text
        .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}
