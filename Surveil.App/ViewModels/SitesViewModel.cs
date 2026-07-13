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

    /// <summary>The faceted filters shown above the tree, rebuilt from the identified cameras.</summary>
    public ObservableCollection<FacetGroup> Facets { get; } = new();

    [ObservableProperty] private bool filterActive;
    [ObservableProperty] private string filterSummary = "";
    [ObservableProperty] private bool unmappedGroupVisible;
    private bool suppressFilter;

    /// <summary>Each facet: a display name and the value(s) a camera contributes to it.</summary>
    private static readonly (string Name, Func<CameraItem, IEnumerable<string>> Values)[] FacetDefs =
    {
        ("Manufacturer", c => Single(c.Manufacturer)),
        ("Model", c => Single(c.ModelName)),
        ("Codec", c => c.Codecs),
        ("Resolution", c => Single(c.ResolutionBucket)),
        ("Capability", c => c.Capabilities),
        ("ONVIF", c => Single(c.MediaGen)),
        ("Sign-in", c => Single(c.SignInLabel)),
        ("Location", c => Single(c.LocationLabel)),
    };

    private static IEnumerable<string> Single(string? value) =>
        value is null ? Array.Empty<string>() : new[] { value };

    private IEnumerable<CameraItem> AllCameras() =>
        Sites.SelectMany(s => s.Children).SelectMany(r => r.Cameras).Concat(UnmappedCameras);

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
            RebuildFacets();
            _ = IdentifyFoundCamerasAsync();  // background: log in and read features
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
                var endpoint = EndpointFromXAddresses(camera.XAddresses);
                var range = sets.FirstOrDefault(kv => kv.Value.Contains(camera.Ip)).Key;
                if (range is not null)
                {
                    AddCamera(range.Cameras, status, endpoint);
                }
                else
                {
                    AddCamera(UnmappedCameras, status, endpoint);
                    unmapped++;
                }
            }
            if (UnmappedCameras.Count > 0) UnmappedExpanded = true;
            OnProvisionSelectionChanged();  // Unmapped was rebuilt — drop any stale selections
            RebuildFacets();
            _ = IdentifyFoundCamerasAsync();  // background: log in and read features
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

    private void AddCamera(ObservableCollection<CameraItem> cameras, CameraStatus camera, Uri? endpoint = null)
    {
        if (cameras.Any(c => c.Ip == camera.Ip)) return;  // dedupe by IP
        cameras.Add(new CameraItem(camera, endpoint) { SelectionChanged = OnProvisionSelectionChanged });
    }

    /// <summary>Rebuild the provisioning target set from every ticked camera in the tree, carrying
    /// each camera's advertised endpoint so provisioning connects exactly where it announced.</summary>
    private void OnProvisionSelectionChanged()
    {
        var targets = new List<(IPAddress Address, Uri? Endpoint)>();
        var seen = new HashSet<string>();
        foreach (var cam in Sites.SelectMany(s => s.Children).SelectMany(r => r.Cameras).Concat(UnmappedCameras))
        {
            if (!cam.IsSelected || !IPAddress.TryParse(cam.Ip, out var address) || !seen.Add(cam.Ip)) continue;
            targets.Add((address, cam.Endpoint));
        }
        session.Provision.SetSelectedTargets(targets);
    }

    /// <summary>First absolute URL from a space-separated WS-Discovery XAddrs list, or null.</summary>
    private static Uri? EndpointFromXAddresses(string xAddresses)
    {
        var first = xAddresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Uri.TryCreate(first, UriKind.Absolute, out var uri) ? uri : null;
    }

    // --- Faceted filtering ---

    /// <summary>Rebuild the facet groups from the current cameras, preserving any selected values.
    /// A facet appears only when at least one camera contributes a value to it.</summary>
    public void RebuildFacets()
    {
        var cameras = AllCameras().ToList();
        var previouslySelected = Facets.ToDictionary(g => g.Name, g => g.SelectedValues.ToHashSet());

        suppressFilter = true;
        Facets.Clear();
        foreach (var (name, values) in FacetDefs)
        {
            var distinct = cameras.SelectMany(values).Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0) continue;

            var group = new FacetGroup(name);
            var keep = previouslySelected.TryGetValue(name, out var set) ? set : new HashSet<string>();
            foreach (var value in distinct)
                group.Options.Add(new FacetOption(value) { IsSelected = keep.Contains(value), SelectionChanged = OnFacetChanged });
            Facets.Add(group);
        }
        suppressFilter = false;
        ApplyFilters();
        foreach (var group in Facets) group.RefreshHeader();
    }

    private void OnFacetChanged()
    {
        if (suppressFilter) return;
        ApplyFilters();
        foreach (var group in Facets) group.RefreshHeader();
    }

    /// <summary>Apply the active facet selections: hide non-matching cameras (and now-empty ranges /
    /// sites), and recompute each facet option's count against the other active facets.</summary>
    public void ApplyFilters()
    {
        var cameras = AllCameras().ToList();
        var active = Facets.Where(g => g.HasSelection)
            .Select(g => (Values: FacetDefs.First(d => d.Name == g.Name).Values, Selected: g.SelectedValues.ToHashSet(), g.Name))
            .ToList();
        var filtering = active.Count > 0;

        var visible = 0;
        foreach (var cam in cameras)
        {
            cam.IsVisible = !filtering || active.All(a => a.Values(cam).Any(a.Selected.Contains));
            if (cam.IsVisible) visible++;
        }

        foreach (var site in Sites)
        {
            var siteHasMatch = false;
            foreach (var range in site.Children)
            {
                var rangeHasMatch = range.Cameras.Any(c => c.IsVisible);
                range.IsVisible = !filtering || rangeHasMatch;
                siteHasMatch |= rangeHasMatch;
            }
            site.IsVisible = !filtering || siteHasMatch;
        }
        UnmappedGroupVisible = filtering ? UnmappedCameras.Any(c => c.IsVisible) : UnmappedCameras.Count > 0;

        // Faceted counts: for each option, how many cameras match the OTHER active facets and this value.
        foreach (var group in Facets)
        {
            var others = active.Where(a => a.Name != group.Name).ToList();
            var def = FacetDefs.First(d => d.Name == group.Name);
            foreach (var option in group.Options)
                option.Count = cameras.Count(cam =>
                    others.All(a => a.Values(cam).Any(a.Selected.Contains)) && def.Values(cam).Contains(option.Value));
        }

        FilterActive = filtering;
        FilterSummary = filtering
            ? $"{visible} of {cameras.Count} cameras"
            : $"{cameras.Count} camera{(cameras.Count == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private void ClearFilters()
    {
        suppressFilter = true;
        foreach (var option in Facets.SelectMany(g => g.Options)) option.IsSelected = false;
        suppressFilter = false;
        ApplyFilters();
        foreach (var group in Facets) group.RefreshHeader();
    }

    /// <summary>Log into every not-yet-identified camera and read its features, using the Provision
    /// credentials. Background enrichment: it doesn't set the busy state; each row shows its own
    /// per-camera login state instead. Bounded concurrency keeps it gentle on the network.</summary>
    private async Task IdentifyFoundCamerasAsync()
    {
        var cameras = Sites.SelectMany(s => s.Children).SelectMany(r => r.Cameras)
            .Concat(UnmappedCameras)
            .Where(c => c.LoginState is not CameraLoginState.Success and not CameraLoginState.InProgress)
            .ToList();
        if (cameras.Count == 0) return;

        var username = session.Username;
        var password = session.Password;
        if (string.IsNullOrWhiteSpace(username))
        {
            foreach (var cam in cameras) cam.LoginState = CameraLoginState.NoCredentials;
            return;
        }

        using var gate = new SemaphoreSlim(6);
        var work = cameras.Select(async cam =>
        {
            await gate.WaitAsync();
            cam.LoginState = CameraLoginState.InProgress;
            try
            {
                var endpoint = cam.Endpoint ?? new UriBuilder("http", cam.Ip) { Path = "/onvif/device_service" }.Uri;
                cam.ApplyFeatures(await session.Service.IdentifyAsync(endpoint, username, password));
            }
            catch (OnvifException ex) when (ex.IsAuthenticationFailure)
            {
                cam.LoginState = CameraLoginState.AuthFailed;
                cam.ErrorText = ex.Message;
            }
            catch (Exception ex)
            {
                cam.LoginState = CameraLoginState.Unreachable;
                cam.ErrorText = ex.Message;
                AppLog.Write(ex);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(work);
        RebuildFacets();  // identities are in — the manufacturer/model/codec/… facets can populate
    }

    /// <summary>Ranges that are ticked and carry a CIDR — the scan targets offered after discovery.</summary>
    public int CheckedRangeCount => Sites.SelectMany(s => s.Children)
        .Count(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Cidr));

    private SurveilConfig ToConfig() =>
        new() { Sites = Sites.Select(site => site.ToSite()).ToList() };
}

/// <summary>What a discovery run found, for the prompt that offers a follow-up scan.</summary>
public readonly record struct DiscoverySummary(int Found, int Unmapped);
