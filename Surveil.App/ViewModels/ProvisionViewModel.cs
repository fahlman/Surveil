using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Bulk-provisions cameras: derives name/hostname from the site map, optionally
/// maximizes video (codec preference, resolution-first), and reports a truthful per-camera
/// outcome. Supports a dry-run that reads capabilities but writes nothing.</summary>
public sealed partial class ProvisionViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;
    private CancellationTokenSource? cts;

    [ObservableProperty] private string targets = "";

    // Identity — single-camera only (a name/hostname is per-camera; nonsense to type one for many).
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string hostname = "";

    // Time — an optional NTP server plus a time-zone choice, applied to the whole selection.
    [ObservableProperty] private string ntpServer = "";
    [ObservableProperty] private TimeZoneChoice? selectedTimeZone;

    // Video — offered only when every selected camera has encoders. Codec + resolution are the shared
    // intersection; "Leave unchanged" keeps each camera's current setting.
    [ObservableProperty] private string selectedCodec = LeaveCodec;
    [ObservableProperty] private ResolutionChoice? selectedResolution;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMaximum = 1;
    [ObservableProperty] private bool progressIndeterminate;

    private const string LeaveCodec = "Leave unchanged";
    private static readonly ResolutionChoice LeaveResolution = new(null, "Leave unchanged");

    /// <summary>Number of cameras ticked in the Sites tree — the provisioning targets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCameraSummary), nameof(AnyCamerasSelected), nameof(SingleCameraSelected))]
    private int selectedCameraCount;

    /// <summary>"12 cameras · 2 models" — shown at the top of the panel.</summary>
    [ObservableProperty] private string selectionSummary = "";

    /// <summary>Time + Video are offered whenever at least one camera is selected.</summary>
    public bool AnyCamerasSelected => SelectedCameraCount > 0;

    /// <summary>Name + hostname are offered only for a single camera.</summary>
    public bool SingleCameraSelected => SelectedCameraCount == 1;

    /// <summary>True when every selected camera has encoders — the Video section is then offered.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(VideoHiddenNote))] private bool showVideoSection;

    private bool someHaveVideo;

    /// <summary>Explains a Video section that dropped out because the selection mixes cameras that
    /// have encoders with ones that don't.</summary>
    public string VideoHiddenNote =>
        !ShowVideoSection && AnyCamerasSelected && someHaveVideo
            ? "Video hidden — not every selected camera exposes it."
            : "";

    /// <summary>Codec choices shared by the whole selection (plus "Leave unchanged").</summary>
    public ObservableCollection<string> AvailableCodecs { get; } = new();

    /// <summary>Resolutions shared by the whole selection (plus "Leave unchanged").</summary>
    public ObservableCollection<ResolutionChoice> AvailableResolutions { get; } = new();

    /// <summary>Friendly time-zone options; the camera syncs via NTP using the chosen zone.</summary>
    public IReadOnlyList<TimeZoneChoice> AvailableTimeZones { get; } = BuildTimeZones();

    public string SelectedCameraSummary => SelectedCameraCount switch
    {
        0 => "No cameras selected — tick cameras in the tree",
        1 => "1 camera selected",
        _ => $"{SelectedCameraCount} cameras selected",
    };

    private IReadOnlyList<ProvisionCandidate> selection = Array.Empty<ProvisionCandidate>();

    public ObservableCollection<ProvisionPlanRow> Plans { get; } = new();
    public ObservableCollection<ProvisionResultRow> Results { get; } = new();

    /// <summary>Called by the Sites tree whenever the camera selection changes: recomputes the shared
    /// (intersection) codec/resolution options and whether the Video section applies at all.</summary>
    public void SetSelectedTargets(IReadOnlyList<ProvisionCandidate> newSelection)
    {
        selection = newSelection;
        SelectedCameraCount = newSelection.Count;
        var models = newSelection.Select(camera => camera.Model).Distinct().Count();
        SelectionSummary = newSelection.Count == 0 ? ""
            : $"{newSelection.Count} camera{(newSelection.Count == 1 ? "" : "s")} · {models} model{(models == 1 ? "" : "s")}";
        RecomputeVideoIntersection();
    }

    private void RecomputeVideoIntersection()
    {
        someHaveVideo = selection.Any(camera => camera.HasVideo);

        var codecs = Intersection(selection.Select(camera => camera.Codecs))
            .OrderBy(codec => codec, StringComparer.OrdinalIgnoreCase).ToList();
        AvailableCodecs.Clear();
        AvailableCodecs.Add(LeaveCodec);
        foreach (var codec in codecs) AvailableCodecs.Add(codec);
        if (!AvailableCodecs.Contains(SelectedCodec)) SelectedCodec = LeaveCodec;

        var previousResolution = SelectedResolution?.Resolution;
        var resolutions = Intersection(selection.Select(camera => camera.Resolutions))
            .OrderByDescending(r => (long)r.Width * r.Height).ToList();
        AvailableResolutions.Clear();
        AvailableResolutions.Add(LeaveResolution);
        foreach (var r in resolutions) AvailableResolutions.Add(new ResolutionChoice(r, $"{r.Width} × {r.Height}"));
        SelectedResolution = AvailableResolutions.FirstOrDefault(choice => choice.Resolution == previousResolution) ?? LeaveResolution;

        ShowVideoSection = selection.Count > 0 && selection.All(camera => camera.HasVideo);
        OnPropertyChanged(nameof(VideoHiddenNote));
    }

    private static IReadOnlyList<T> Intersection<T>(IEnumerable<IEnumerable<T>> sets)
    {
        HashSet<T>? accumulator = null;
        foreach (var set in sets)
        {
            if (accumulator is null) accumulator = new HashSet<T>(set);
            else accumulator.IntersectWith(set);
        }
        return accumulator?.ToList() ?? new List<T>();
    }

    private static IReadOnlyList<TimeZoneChoice> BuildTimeZones()
    {
        var zones = new List<TimeZoneChoice> { new("Leave unchanged", null, LeaveUnchanged: true) };
        try
        {
            var local = OnvifDeviceClient.ResolveTimeZone(TimeZoneInfo.Local);
            zones.Add(new($"This computer ({local.PosixValue})", null));  // Posix null = computer zone at write time
        }
        catch { /* unmapped local zone: offer the explicit ones only */ }
        zones.Add(new("Eastern (EST5EDT)", "EST5EDT,M3.2.0,M11.1.0"));
        zones.Add(new("Central (CST6CDT)", "CST6CDT,M3.2.0,M11.1.0"));
        zones.Add(new("Mountain (MST7MDT)", "MST7MDT,M3.2.0,M11.1.0"));
        zones.Add(new("Arizona (MST7)", "MST7"));
        zones.Add(new("Pacific (PST8PDT)", "PST8PDT,M3.2.0,M11.1.0"));
        zones.Add(new("UTC", "UTC0"));
        return zones;
    }

    /// <summary>The selected camera IPs, for the pre-write confirmation dialog.</summary>
    public IReadOnlyList<string> SelectedIps => selection.Select(camera => camera.Address.ToString()).ToList();

    public ProvisionViewModel()
    {
        selectedResolution = LeaveResolution;
        selectedTimeZone = AvailableTimeZones[0];  // "Leave unchanged"
    }

    /// <summary>Preview the derived name/hostname for each in-range camera without touching it.</summary>
    [RelayCommand]
    private void Preview()
    {
        Plans.Clear();
        HasError = false;
        try
        {
            var service = BuildService();
            var provisionTargets = service.TargetsFrom(selection.Select(camera => (camera.Address, camera.Endpoint)));
            var plans = service.Plan(provisionTargets, includeUnknownLocation: true);
            foreach (var plan in plans) Plans.Add(new ProvisionPlanRow(plan));
            StatusMessage = plans.Count == 0
                ? "No targets. Check the addresses and the site map."
                : $"{plans.Count} cameras planned.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Apply the selected identity/NTP/video settings to the ticked cameras. Called from the
    /// panel, which gates a real (non-dry-run) write behind a confirmation.</summary>
    public async Task ProvisionAsync()
    {
        if (IsBusy) return;
        Results.Clear();
        HasError = false;

        IReadOnlyList<CameraProvisionTarget> provisionTargets;
        BulkProvisioningService service;
        try
        {
            service = BuildService();
            provisionTargets = service.TargetsFrom(selection.Select(camera => (camera.Address, camera.Endpoint)));
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
            return;
        }

        if (provisionTargets.Count == 0)
        {
            HasError = true;
            StatusMessage = "No targets. Enter addresses and confirm the site map.";
            return;
        }

        // Contacting cameras (any real write, or a dry run that reads video capabilities) needs a
        // username. A pure identity dry run never touches a camera, so credentials aren't required.
        if (string.IsNullOrWhiteSpace(session.Username))
        {
            HasError = true;
            StatusMessage = "Enter the ONVIF username in Settings (needed to contact cameras).";
            return;
        }

        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        StatusMessage = "Provisioning…";
        cts = new CancellationTokenSource();

        var options = new BulkProvisionOptions
        {
            SetName = SingleCameraSelected && !string.IsNullOrWhiteSpace(Name),
            Name = Name.Trim(),
            SetHostname = SingleCameraSelected && !string.IsNullOrWhiteSpace(Hostname),
            Hostname = Hostname.Trim(),
            SetNtp = SelectedTimeZone is { LeaveUnchanged: false },
            NtpPosixTimeZone = SelectedTimeZone?.Posix,
            NtpServer = string.IsNullOrWhiteSpace(NtpServer) ? null : NtpServer.Trim(),
            VideoCodec = ShowVideoSection && SelectedCodec != LeaveCodec ? SelectedCodec : null,
            VideoResolution = ShowVideoSection ? SelectedResolution?.Resolution : null,
            SkipUnknownLocation = false,  // selection is explicit — provision exactly what's ticked
            MaxConcurrency = Math.Max(1, session.Settings.MaxProvisionConcurrency),
            DryRun = false,
        };

        var progress = new Progress<BulkProvisionProgress>(p =>
        {
            ProgressIndeterminate = false;
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Completed;
            StatusMessage = $"{p.Completed}/{p.Total} — {p.Succeeded} ok, {p.Failed} failed";
        });

        try
        {
            var results = await service.ProvisionAsync(provisionTargets, options, progress, cts.Token);
            foreach (var result in results.OrderBy(r => r.Success).ThenBy(r => r.Address.ToString()))
                Results.Add(new ProvisionResultRow(result));
            var ok = results.Count(r => r.Success);
            StatusMessage = $"Done: {ok} ok, {results.Count - ok} failed of {results.Count}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            cts?.Dispose();
            cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => cts?.Cancel();

    private BulkProvisioningService BuildService() =>
        new(session.Config, session.Username, session.Password);
}

/// <summary>A resolution option in the Video picker; a null <see cref="Resolution"/> means "Leave
/// unchanged" (keep the camera's current resolution).</summary>
public sealed record ResolutionChoice(OnvifResolution? Resolution, string Label);

/// <summary>A time-zone option: a friendly label plus the ONVIF POSIX rule to write. Posix null means
/// "this computer's zone" (resolved at write time); LeaveUnchanged skips the NTP zone/mode entirely.</summary>
public sealed record TimeZoneChoice(string Label, string? Posix, bool LeaveUnchanged = false);

/// <summary>A selected camera handed from the tree to the Provision panel, with just enough of its
/// discovered features to compute the settable-capability intersection.</summary>
public sealed record ProvisionCandidate(
    IPAddress Address, Uri? Endpoint, bool HasVideo, string Model,
    IReadOnlyList<string> Codecs, IReadOnlyList<OnvifResolution> Resolutions);

/// <summary>Preview row: the identity that would be pushed to one camera.</summary>
public sealed class ProvisionPlanRow
{
    public string Address { get; }
    public string Location { get; }
    public string Name { get; }
    public string Hostname { get; }
    public bool LocationKnown { get; }

    public ProvisionPlanRow(CameraProvisionPlan plan)
    {
        Address = plan.Target.Address.ToString();
        Location = Join(plan.Target.Site, plan.Target.Area);
        Name = plan.Name;
        Hostname = plan.Hostname ?? "—";
        LocationKnown = plan.Target.LocationKnown;
    }

    private static string Join(string site, string area)
    {
        var parts = new[] { site, area }.Where(p => p.Length > 0);
        var joined = string.Join(" · ", parts);
        return joined.Length == 0 ? "(unknown)" : joined;
    }
}

/// <summary>Result row: what actually happened to one camera, including video outcomes.</summary>
public sealed class ProvisionResultRow
{
    public string Address { get; }
    public string Location { get; }
    public bool Success { get; }
    public string Glyph => Success ? "" : ""; // CheckMark / Cancel
    public string Steps { get; }
    public string Error { get; }
    public bool HasError => Error.Length > 0;
    public string VideoSummary { get; }
    public bool HasVideo => VideoSummary.Length > 0;

    public ProvisionResultRow(CameraProvisionResult result)
    {
        Address = result.Address.ToString();
        Location = ProvisionPlanRowLocation(result.Site, result.Area);
        Success = result.Success;
        Steps = string.Join("\n", result.Steps);
        Error = result.Error ?? "";
        VideoSummary = string.Join("\n", result.Video.Select(FormatVideo));
    }

    private static string ProvisionPlanRowLocation(string site, string area)
    {
        var joined = string.Join(" · ", new[] { site, area }.Where(p => p.Length > 0));
        return joined.Length == 0 ? "(unknown)" : joined;
    }

    private static string FormatVideo(VideoEncoderOutcome v)
    {
        var fps = v.AppliedFrameRate is { } f ? $"@{f:0.##}fps" : "";
        var flags = new List<string>();
        if (v.CodecFallback) flags.Add("codec fallback");
        if (v.ClampedByCamera) flags.Add("camera-limited");
        var suffix = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";
        return $"{v.ConfigurationToken}: {v.AppliedCodec} {v.AppliedResolution.Width}×{v.AppliedResolution.Height}{fps}{suffix}";
    }
}
