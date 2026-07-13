using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>The site map as a hierarchical editor: sites (parents) each hold their CIDR
/// ranges (children). Per-node edit/delete/add live on the nodes themselves; this view model owns
/// the root list plus save / import / export.</summary>
public sealed partial class SitesViewModel : ObservableObject, IDisposable
{
    private readonly AppSession session;
    private readonly ConfigurationViewModel configuration;
    private readonly CameraIdentificationCoordinator identifier;
    private readonly bool demoMode;
    private CancellationTokenSource? identificationCts;
    private Task identificationTask = Task.CompletedTask;
    private bool initialized;

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

    /// <summary>The faceted filters shown above the tree, rebuilt from the identified cameras.</summary>
    public ObservableCollection<FacetGroup> Facets { get; } = new();

    [ObservableProperty] private bool filterActive;
    [ObservableProperty] private string filterSummary = "";
    [ObservableProperty] private bool unmappedGroupVisible;
    private bool suppressFilter;

    private IEnumerable<CameraItem> AllCameras() =>
        CameraTreeProjector.All(Sites, UnmappedCameras);

    public string DataDirectory => session.DataDirectory;

    public SitesViewModel(AppSession session, ConfigurationViewModel configuration, bool demoMode = false)
    {
        this.session = session;
        this.configuration = configuration;
        this.demoMode = demoMode;
        identifier = new CameraIdentificationCoordinator(session.Service);
    }

    /// <summary>Explicit page lifecycle initialization; constructors perform no file or network work.</summary>
    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        Load();
        if (demoMode) SeedDemoCameras();
        else await LoadInventoryAsync();
    }

    private void SeedDemoCameras()
    {
        var ranges = Sites.SelectMany(site => site.Children
            .Where(range => !string.IsNullOrWhiteSpace(range.Cidr))
            .Select(range => (site.Name, range.Name, range.Cidr))).ToList();
        var index = CameraTreeProjector.BuildRangeIndex(Sites);
        foreach (var seed in DemoCameras.CreateSeeds(ranges))
        {
            var bucket = IPAddress.TryParse(seed.Status.Ip, out var address)
                ? index.Find(address)?.Cameras ?? UnmappedCameras
                : UnmappedCameras;
            CameraTreeProjector.Add(bucket, seed.Status, null, OnConfigurationSelectionChanged)?.ApplyFeatures(seed.Features);
        }
        RebuildFacets();
        OnConfigurationSelectionChanged();
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
            CameraTreeProjector.PopulateInventory(Sites, UnmappedCameras, inventory, OnConfigurationSelectionChanged);
            if (UnmappedCameras.Count > 0) UnmappedExpanded = true;
            OnConfigurationSelectionChanged();
            RebuildFacets();
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

        var valid = selected.Where(range => NetworkRanges.IsValid(range.Cidr)).ToList();
        if (valid.Count == 0)
        {
            HasError = true;
            StatusMessage = "None of the checked CIDRs are valid private ranges.";
            return;
        }

        foreach (var range in valid) range.Cameras.Clear();
        var rangeIndex = new IpRangeMap<NetworkRangeItem>(valid.Select(range => (range.Cidr, range)));
        var targets = NetworkRanges.ExpandPrivate(valid.Select(range => range.Cidr))
            .Select(address => address.ToString()).ToArray();

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
            foreach (var camera in statuses.Where(status => status.Presence != CameraPresenceStatus.Offline))
            {
                var range = IPAddress.TryParse(camera.Ip, out var address) ? rangeIndex.Find(address) : null;
                if (range is null) continue;
                CameraTreeProjector.Add(range.Cameras, camera, null, OnConfigurationSelectionChanged);
                found++;
            }
            OnConfigurationSelectionChanged();  // scanned ranges were cleared — drop any stale selections
            RebuildFacets();
            StartIdentification();
            StatusMessage = $"Found {found} camera(s) across {valid.Count} CIDR(s).";
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

            var ranges = CameraTreeProjector.BuildRangeIndex(Sites);
            UnmappedCameras.Clear();
            var unmapped = 0;
            foreach (var camera in result.Cameras)
            {
                var status = new CameraStatus
                {
                    Ip = camera.Ip,
                    Site = camera.Site,
                    Area = camera.Area,
                    Presence = CameraPresenceStatus.Discovered,
                };
                var endpoint = OnvifEndpoint.FirstAdvertised(camera.XAddresses);
                var range = IPAddress.TryParse(camera.Ip, out var address) ? ranges.Find(address) : null;
                if (range is not null)
                {
                    CameraTreeProjector.Add(range.Cameras, status, endpoint, OnConfigurationSelectionChanged);
                }
                else
                {
                    CameraTreeProjector.Add(UnmappedCameras, status, endpoint, OnConfigurationSelectionChanged);
                    unmapped++;
                }
            }
            if (UnmappedCameras.Count > 0) UnmappedExpanded = true;
            OnConfigurationSelectionChanged();  // Unmapped was rebuilt — drop any stale selections
            RebuildFacets();
            StartIdentification();
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

    /// <summary>Rebuild the configuration target set from every ticked camera in the tree, carrying
    /// each camera's advertised endpoint so configuration connects exactly where it announced.</summary>
    private void OnConfigurationSelectionChanged()
    {
        var candidates = new List<ConfigurationCandidate>();
        var seen = new HashSet<string>();
        foreach (var cam in AllCameras())
        {
            if (!cam.IsSelected || !IPAddress.TryParse(cam.Ip, out var address) || !seen.Add(cam.Ip)) continue;
            candidates.Add(new ConfigurationCandidate(address, cam.Endpoint, cam.HasVideo,
                cam.ModelName ?? "Unknown", cam.Encoders));
        }
        configuration.SetSelectedTargets(candidates);
    }

    // --- Faceted filtering ---

    /// <summary>Rebuild the facet groups from the current cameras, preserving any selected values.
    /// A facet appears only when at least one camera contributes a value to it.</summary>
    public void RebuildFacets()
    {
        suppressFilter = true;
        CameraFacetService.Rebuild(Facets, AllCameras().ToList(), OnFacetChanged);
        suppressFilter = false;
        ApplyFilters();
    }

    private void OnFacetChanged()
    {
        if (suppressFilter) return;
        ApplyFilters();
    }

    /// <summary>Apply the active facet selections: hide non-matching cameras (and now-empty ranges /
    /// sites), and recompute each facet option's count against the other active facets.</summary>
    public void ApplyFilters()
    {
        var state = CameraFacetService.Apply(AllCameras().ToList(), Sites, UnmappedCameras, Facets);
        FilterActive = state.Active;
        FilterSummary = state.Summary;
        UnmappedGroupVisible = state.UnmappedVisible;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        suppressFilter = true;
        foreach (var option in Facets.SelectMany(g => g.Options)) option.IsSelected = false;
        suppressFilter = false;
        ApplyFilters();
    }

    private void StartIdentification()
    {
        var cameras = AllCameras().Where(camera =>
            camera.LoginState is not CameraLoginState.Success and not CameraLoginState.InProgress).ToList();
        if (cameras.Count == 0) return;
        identificationCts?.Cancel();
        identificationCts?.Dispose();
        identificationCts = new CancellationTokenSource();
        identificationTask = ObserveIdentificationAsync(cameras, identificationCts.Token);
    }

    private async Task ObserveIdentificationAsync(IReadOnlyList<CameraItem> cameras, CancellationToken cancellationToken)
    {
        try
        {
            await identifier.IdentifyAsync(cameras, session.Username, session.Password, cancellationToken);
            if (!cancellationToken.IsCancellationRequested) RebuildFacets();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Write(error);
        }
    }

    /// <summary>Ranges that are ticked and carry a CIDR — the scan targets offered after discovery.</summary>
    public int CheckedRangeCount => Sites.SelectMany(s => s.Children)
        .Count(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Cidr));

    private SurveilConfig ToConfig() =>
        new() { Sites = Sites.Select(site => site.ToSite()).ToList() };

    public void Dispose()
    {
        busyCts?.Cancel();
        identificationCts?.Cancel();
        busyCts?.Dispose();
        identificationCts?.Dispose();
    }
}

/// <summary>What a discovery run found, for the prompt that offers a follow-up scan.</summary>
public readonly record struct DiscoverySummary(int Found, int Unmapped);
