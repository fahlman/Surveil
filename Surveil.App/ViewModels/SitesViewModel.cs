using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>The site map as a hierarchical editor: sites (parents) each hold their CIDR
/// ranges (children). Per-node edit/delete/add live on the nodes themselves; this view model owns
/// the root list plus save / import / export.</summary>
public sealed partial class SitesViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsBusy))] private bool isScanning;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsBusy))] private bool isDiscovering;
    [ObservableProperty] private double scanProgressValue;
    [ObservableProperty] private double scanProgressMaximum = 1;
    [ObservableProperty] private bool unmappedExpanded = true;

    /// <summary>True while a scan or discovery is running.</summary>
    public bool IsBusy => IsScanning || IsDiscovering;

    private CancellationTokenSource? busyCts;

    public ObservableCollection<SiteItem> Sites { get; } = new();

    /// <summary>Cameras found (by discovery) that fall outside every mapped CIDR.</summary>
    public ObservableCollection<CameraItem> UnmappedCameras { get; } = new();

    public string DataDirectory => session.DataDirectory;

    public SitesViewModel()
    {
        Load();
        _ = LoadInventoryAsync();
    }

    public void Load()
    {
        Sites.Clear();
        foreach (var site in session.Config.Sites)
            Sites.Add(new SiteItem(site, Sites));
        // There is always at least one site — a fresh/empty map starts with an empty one.
        if (Sites.Count == 0) Sites.Add(new SiteItem("Site 1", Sites));
    }

    /// <summary>Populate the tree from the saved inventory (cameras.json): each known camera under
    /// its mapped CIDR, or in the Unmapped group. This makes the map double as the inventory view.</summary>
    public async Task LoadInventoryAsync()
    {
        try
        {
            var inventory = await session.Store.LoadInventoryAsync();
            foreach (var range in Sites.SelectMany(b => b.Children)) range.Cameras.Clear();
            UnmappedCameras.Clear();

            var sets = RangeAddressSets();
            foreach (var record in inventory.Cameras)
            {
                var camera = new CameraStatus
                {
                    Ip = record.Ip, Site = record.Site, Area = record.Area,
                    FirstSeen = record.FirstSeen, LastSeen = record.LastSeen,
                };
                var range = sets.FirstOrDefault(kv => kv.Value.Contains(record.Ip)).Key;
                if (range is not null) AddCamera(range.Cameras, camera);
                else AddCamera(UnmappedCameras, camera);
            }
            if (UnmappedCameras.Count > 0) UnmappedExpanded = true;
            OnProvisionSelectionChanged();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var config = ToConfig();
            await session.Service.SaveConfigAsync(config);
            session.Config = config;
            HasError = false;
            StatusMessage = $"Saved {config.Sites.Count} sites to {session.DataDirectory}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Import a config file chosen by the page; replaces the current in-memory map.</summary>
    public async Task ImportAsync(string path)
    {
        try
        {
            var config = await session.Service.ImportConfigAsync(path);
            session.Config = config;
            Load();
            await LoadInventoryAsync();
            HasError = false;
            StatusMessage = $"Imported {config.Sites.Count} sites from {path}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Export the current map to a path chosen by the page.</summary>
    public async Task ExportAsync(string path)
    {
        try
        {
            session.Config = ToConfig();
            await session.Service.SaveConfigAsync(session.Config);
            await session.Service.ExportConfigAsync(path);
            HasError = false;
            StatusMessage = $"Exported to {path}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Scan the checked CIDRs and populate the cameras found in each one under it.
    /// Offered from the dialog that follows a discovery; not a standalone toolbar command.</summary>
    public async Task ScanSelectedAsync()
    {
        if (IsBusy) return;

        var selected = Sites.SelectMany(b => b.Children)
            .Where(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Cidr))
            .ToList();
        if (selected.Count == 0)
        {
            HasError = true;
            StatusMessage = "Check one or more CIDRs to scan.";
            return;
        }

        // Expand each selected CIDR to the set of addresses it covers (skip invalid ranges).
        var addressesByRange = new Dictionary<NetworkRangeItem, HashSet<string>>();
        foreach (var range in selected)
        {
            try
            {
                addressesByRange[range] = NetworkRanges.ExpandPrivate(new[] { range.Cidr })
                    .Select(ip => ip.ToString()).ToHashSet();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        }
        if (addressesByRange.Count == 0)
        {
            HasError = true;
            StatusMessage = "None of the checked CIDRs are valid private ranges.";
            return;
        }

        foreach (var range in addressesByRange.Keys) range.Cameras.Clear();
        var targets = addressesByRange.Values.SelectMany(set => set).Distinct().ToArray();

        IsScanning = true;
        HasError = false;
        ScanProgressValue = 0;
        ScanProgressMaximum = Math.Max(1, targets.Length);
        StatusMessage = "Scanning…";
        busyCts = new CancellationTokenSource();
        var settings = session.Settings;

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressMaximum = Math.Max(1, p.Total);
            ScanProgressValue = p.Scanned;
            StatusMessage = $"Scanned {p.Scanned}/{p.Total} — {p.Found} responding";
        });

        try
        {
            var statuses = await session.Service.ScanAsync(targets, settings.DefaultPort,
                settings.DefaultConcurrency, TimeSpan.FromMilliseconds(settings.DefaultTimeoutMs),
                progress, busyCts.Token);

            var found = 0;
            foreach (var camera in statuses.Where(s => s.Status != "offline"))
            {
                var range = addressesByRange.FirstOrDefault(kv => kv.Value.Contains(camera.Ip)).Key;
                if (range is null) continue;
                AddCamera(range.Cameras, camera);
                found++;
            }
            OnProvisionSelectionChanged();  // scanned ranges were cleared — drop any stale selections
            StatusMessage = $"Found {found} camera(s) across {addressesByRange.Count} CIDR(s).";
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
            busyCts?.Dispose();
            busyCts = null;
        }
    }

    /// <summary>Discover cameras on the local network (WS-Discovery); each lands under its CIDR, or
    /// in the Unmapped group if it falls outside every mapped range. Returns a summary of what was
    /// found, or null if the run was cancelled or failed.</summary>
    public async Task<DiscoverySummary?> DiscoverAsync()
    {
        if (IsBusy) return null;

        IsDiscovering = true;
        HasError = false;
        StatusMessage = "Discovering…";
        busyCts = new CancellationTokenSource();
        try
        {
            var result = await session.Service.DiscoverAsync(
                TimeSpan.FromMilliseconds(session.Settings.DiscoverTimeoutMs), busyCts.Token);

            var sets = RangeAddressSets();
            UnmappedCameras.Clear();
            var unmapped = 0;
            foreach (var camera in result.Cameras)
            {
                var status = new CameraStatus
                {
                    Ip = camera.Ip, Site = camera.Site, Area = camera.Area, Status = "discovered",
                };
                var range = sets.FirstOrDefault(kv => kv.Value.Contains(camera.Ip)).Key;
                if (range is not null)
                {
                    AddCamera(range.Cameras, status);
                }
                else
                {
                    AddCamera(UnmappedCameras, status);
                    unmapped++;
                }
            }
            if (UnmappedCameras.Count > 0) UnmappedExpanded = true;
            OnProvisionSelectionChanged();  // Unmapped was rebuilt — drop any stale selections
            StatusMessage = $"Discovered {result.Cameras.Count} camera(s) — {unmapped} unmapped.";
            return new DiscoverySummary(result.Cameras.Count, unmapped);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Discovery cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
            return null;
        }
        finally
        {
            IsDiscovering = false;
            busyCts?.Dispose();
            busyCts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => busyCts?.Cancel();

    [RelayCommand]
    private void ToggleUnmapped() => UnmappedExpanded = !UnmappedExpanded;

    /// <summary>Expand every valid mapped CIDR to the set of addresses it covers.</summary>
    private Dictionary<NetworkRangeItem, HashSet<string>> RangeAddressSets()
    {
        var sets = new Dictionary<NetworkRangeItem, HashSet<string>>();
        foreach (var range in Sites.SelectMany(b => b.Children)
                     .Where(r => !string.IsNullOrWhiteSpace(r.Cidr)))
        {
            try
            {
                sets[range] = NetworkRanges.ExpandPrivate(new[] { range.Cidr }).Select(ip => ip.ToString()).ToHashSet();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        }
        return sets;
    }

    private void AddCamera(ObservableCollection<CameraItem> cameras, CameraStatus camera)
    {
        if (cameras.Any(c => c.Ip == camera.Ip)) return;  // dedupe by IP
        cameras.Add(new CameraItem(camera) { SelectionChanged = OnProvisionSelectionChanged });
    }

    /// <summary>Rebuild the provisioning target set from every ticked camera in the tree.</summary>
    private void OnProvisionSelectionChanged()
    {
        var ips = Sites.SelectMany(s => s.Children).SelectMany(r => r.Cameras)
            .Concat(UnmappedCameras)
            .Where(c => c.IsSelected)
            .Select(c => c.Ip)
            .Distinct()
            .ToList();
        session.Provision.SetSelectedTargets(ips);
    }

    /// <summary>Ranges that are ticked and carry a CIDR — the scan targets offered after discovery.</summary>
    public int CheckedRangeCount => Sites.SelectMany(s => s.Children)
        .Count(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Cidr));

    private SurveilConfig ToConfig() =>
        new() { Sites = Sites.Select(site => site.ToSite()).ToList() };
}

/// <summary>What a discovery run found, for the prompt that offers a follow-up scan.</summary>
public readonly record struct DiscoverySummary(int Found, int Unmapped);
