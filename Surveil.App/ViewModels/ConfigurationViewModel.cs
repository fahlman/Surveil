using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Backs the Configuration drawer: collects the settings to apply (identity, NTP, video)
/// as the shared intersection of the selected cameras' capabilities, then applies them to every
/// ticked camera and appends a truthful per-camera outcome to the configuration log.</summary>
public sealed partial class ConfigurationViewModel : ObservableObject
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

    // Video — offered only when every selected camera has encoders. Codec + resolution + frame rate
    // are the shared intersection; "Leave unchanged" keeps each camera's current setting. Bitrate is
    // a value inside the shared window (NaN = leave unchanged).
    [ObservableProperty] private string selectedCodec = LeaveCodec;
    [ObservableProperty] private ResolutionChoice? selectedResolution;
    [ObservableProperty] private FrameRateChoice? selectedFrameRate;
    [ObservableProperty] private double bitrateKbps = double.NaN;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BitrateRangeLabel))] private double bitrateMinKbps;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BitrateRangeLabel))] private double bitrateMaxKbps;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BitrateRangeLabel))] private bool canSetBitrate;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMaximum = 1;
    [ObservableProperty] private bool progressIndeterminate;

    private const string LeaveCodec = "Leave unchanged";
    private static readonly ResolutionChoice LeaveResolution = new(null, "Leave unchanged");
    private static readonly FrameRateChoice LeaveFrameRate = new(null, "Leave unchanged");

    /// <summary>Number of cameras ticked in the Sites tree — the cameras settings apply to.</summary>
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

    /// <summary>Frame rates shared by the whole selection (plus "Leave unchanged").</summary>
    public ObservableCollection<FrameRateChoice> AvailableFrameRates { get; } = new();

    /// <summary>Caption under the bitrate box showing the window every selected camera can honor.</summary>
    public string BitrateRangeLabel => CanSetBitrate ? $"Allowed {(int)BitrateMinKbps:N0}–{(int)BitrateMaxKbps:N0} kbps" : "";

    /// <summary>Friendly time-zone options; the camera syncs via NTP using the chosen zone.</summary>
    public IReadOnlyList<TimeZoneChoice> AvailableTimeZones { get; } = BuildTimeZones();

    public string SelectedCameraSummary => SelectedCameraCount switch
    {
        0 => "No cameras selected — tick cameras in the tree",
        1 => "1 camera selected",
        _ => $"{SelectedCameraCount} cameras selected",
    };

    private IReadOnlyList<ConfigurationCandidate> selection = Array.Empty<ConfigurationCandidate>();

    /// <summary>Called by the Sites tree whenever the camera selection changes: recomputes the shared
    /// (intersection) codec/resolution options and whether the Video section applies at all.</summary>
    public void SetSelectedTargets(IReadOnlyList<ConfigurationCandidate> newSelection)
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

        var previousFps = SelectedFrameRate?.Fps;
        var frameRates = Intersection(selection.Select(camera => camera.FrameRates))
            .OrderByDescending(r => r).ToList();
        AvailableFrameRates.Clear();
        AvailableFrameRates.Add(LeaveFrameRate);
        foreach (var r in frameRates) AvailableFrameRates.Add(new FrameRateChoice(r, $"{Math.Round(r)} fps"));
        SelectedFrameRate = AvailableFrameRates.FirstOrDefault(choice => choice.Fps == previousFps) ?? LeaveFrameRate;

        // Bitrate window a single value can satisfy across the whole selection: only when every camera
        // advertises a range and those ranges overlap (max of the mins ≤ min of the maxes).
        var ranges = selection.Select(camera => camera.Bitrate).OfType<OnvifRange<int>>().ToList();
        if (selection.Count > 0 && ranges.Count == selection.Count &&
            ranges.Max(r => r.Minimum) is var lo && ranges.Min(r => r.Maximum) is var hi && lo <= hi)
        {
            BitrateMinKbps = lo;
            BitrateMaxKbps = hi;
            CanSetBitrate = true;
        }
        else
        {
            CanSetBitrate = false;
            BitrateKbps = double.NaN;
        }

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

    public ConfigurationViewModel()
    {
        selectedResolution = LeaveResolution;
        selectedFrameRate = LeaveFrameRate;
        selectedTimeZone = AvailableTimeZones[0];  // "Leave unchanged"
    }

    /// <summary>Apply the selected identity/NTP/video settings to the ticked cameras. Called from the
    /// panel, which gates the write behind a confirmation; every outcome is appended to the log.</summary>
    public async Task ApplyAsync()
    {
        if (IsBusy) return;
        HasError = false;

        IReadOnlyList<CameraConfigurationTarget> configTargets;
        BulkConfigurationService service;
        try
        {
            service = BuildService();
            configTargets = service.TargetsFrom(selection.Select(camera => (camera.Address, camera.Endpoint)));
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
            return;
        }

        if (configTargets.Count == 0)
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
        StatusMessage = "Applying…";
        cts = new CancellationTokenSource();

        var options = new BulkConfigurationOptions
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
            VideoFrameRate = ShowVideoSection ? SelectedFrameRate?.Fps : null,
            VideoBitrateKbps = ShowVideoSection && CanSetBitrate && !double.IsNaN(BitrateKbps) && BitrateKbps > 0
                ? (int)Math.Round(BitrateKbps) : null,
            SkipUnknownLocation = false,  // selection is explicit — configure exactly what's ticked
            MaxConcurrency = Math.Max(1, session.Settings.MaxConfigurationConcurrency),
            DryRun = false,
        };

        var progress = new Progress<BulkConfigurationProgress>(p =>
        {
            ProgressIndeterminate = false;
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Completed;
            StatusMessage = $"{p.Completed}/{p.Total} — {p.Succeeded} ok, {p.Failed} failed";
        });

        try
        {
            var results = await service.ConfigureAsync(configTargets, options, progress, cts.Token);
            LogResults(results);
            var ok = results.Count(r => r.Success);
            StatusMessage = $"Done: {ok} ok, {results.Count - ok} failed of {results.Count}. Written to the configuration log.";
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

    private BulkConfigurationService BuildService() =>
        new(session.Config, session.Username, session.Password);

    /// <summary>Append every camera's outcome — what changed, or the error — to the configuration log.</summary>
    private static void LogResults(IReadOnlyList<CameraConfigurationResult> results)
    {
        var when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ConfigurationLog.Write($"=== {when}  configuration: {results.Count(r => r.Success)} ok, {results.Count(r => !r.Success)} failed ===");
        foreach (var result in results.OrderBy(r => r.Success).ThenBy(r => r.Address.ToString()))
        {
            var location = string.Join(" ", new[] { result.Site, result.Area }.Where(part => part.Length > 0));
            var detail = result.Success
                ? (result.Steps.Count > 0 ? string.Join("; ", result.Steps) : "(nothing to change)")
                : $"ERROR {result.Error}";
            ConfigurationLog.Write($"{when}  {(result.Success ? "OK  " : "FAIL")}  {result.Address}  [{location}]  {detail}");
        }
    }
}

/// <summary>A resolution option in the Video picker; a null <see cref="Resolution"/> means "Leave
/// unchanged" (keep the camera's current resolution).</summary>
public sealed record ResolutionChoice(OnvifResolution? Resolution, string Label);

/// <summary>A frame-rate option in the Video picker; a null <see cref="Fps"/> means "Leave
/// unchanged" (keep the camera's current frame rate).</summary>
public sealed record FrameRateChoice(float? Fps, string Label);

/// <summary>A time-zone option: a friendly label plus the ONVIF POSIX rule to write. Posix null means
/// "this computer's zone" (resolved at write time); LeaveUnchanged skips the NTP zone/mode entirely.</summary>
public sealed record TimeZoneChoice(string Label, string? Posix, bool LeaveUnchanged = false);

/// <summary>A selected camera handed from the tree to the Configuration panel, with just enough of
/// its discovered features to compute the settable-capability intersection.</summary>
public sealed record ConfigurationCandidate(
    IPAddress Address, Uri? Endpoint, bool HasVideo, string Model,
    IReadOnlyList<string> Codecs, IReadOnlyList<OnvifResolution> Resolutions,
    IReadOnlyList<float> FrameRates, OnvifRange<int>? Bitrate);
