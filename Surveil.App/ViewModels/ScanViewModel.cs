using System.Collections.ObjectModel;
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
