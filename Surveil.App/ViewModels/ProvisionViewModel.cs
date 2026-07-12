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
    [ObservableProperty] private string username;
    [ObservableProperty] private string password;

    [ObservableProperty] private bool setName = true;
    [ObservableProperty] private bool setHostname = true;
    [ObservableProperty] private bool setNtp = true;
    [ObservableProperty] private bool maximizeVideo;
    [ObservableProperty] private string preferredCodecs = "H265, H264";
    [ObservableProperty] private string ntpPosixTimeZone = "";
    [ObservableProperty] private bool skipUnknownLocation = true;
    [ObservableProperty] private int maxConcurrency = 8;
    [ObservableProperty] private bool dryRun = true;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMaximum = 1;
    [ObservableProperty] private bool progressIndeterminate;

    /// <summary>Number of cameras ticked in the Sites tree — the provisioning targets.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SelectedCameraSummary))] private int selectedCameraCount;

    /// <summary>Human-readable line shown in place of the old free-text targets box.</summary>
    public string SelectedCameraSummary => SelectedCameraCount switch
    {
        0 => "No cameras selected — tick cameras in the tree",
        1 => "1 camera selected",
        _ => $"{SelectedCameraCount} cameras selected",
    };

    /// <summary>The IPs of the ticked cameras; provisioning targets exactly these.</summary>
    private IReadOnlyList<string> selectedCameraIps = Array.Empty<string>();

    public ObservableCollection<ProvisionPlanRow> Plans { get; } = new();
    public ObservableCollection<ProvisionResultRow> Results { get; } = new();

    /// <summary>Called by the Sites tree whenever the camera selection changes.</summary>
    public void SetSelectedTargets(IReadOnlyList<string> ips)
    {
        selectedCameraIps = ips;
        SelectedCameraCount = ips.Count;
    }

    public ProvisionViewModel()
    {
        username = session.Username;
        password = session.Password;
        preferredCodecs = session.Settings.PreferredCodecs;
        dryRun = session.Settings.DryRunByDefault;
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
            var provisionTargets = service.TargetsFromAddresses(ExpandTargets());
            var plans = service.Plan(provisionTargets, includeUnknownLocation: !SkipUnknownLocation);
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

    [RelayCommand]
    private async Task ProvisionAsync()
    {
        if (IsBusy) return;
        Results.Clear();
        HasError = false;

        IReadOnlyList<CameraProvisionTarget> provisionTargets;
        BulkProvisioningService service;
        try
        {
            service = BuildService();
            provisionTargets = service.TargetsFromAddresses(ExpandTargets());
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
        if ((!DryRun || MaximizeVideo) && string.IsNullOrWhiteSpace(Username))
        {
            HasError = true;
            StatusMessage = "Enter the ONVIF username (needed to contact cameras).";
            return;
        }

        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        StatusMessage = DryRun ? "Dry run — reading capabilities…" : "Provisioning…";
        cts = new CancellationTokenSource();

        var options = new BulkProvisionOptions
        {
            SetName = SetName,
            SetHostname = SetHostname,
            SetNtp = SetNtp,
            NtpPosixTimeZone = string.IsNullOrWhiteSpace(NtpPosixTimeZone) ? null : NtpPosixTimeZone.Trim(),
            MaximizeVideo = MaximizeVideo,
            PreferredCodecs = ParseCodecs(PreferredCodecs),
            SkipUnknownLocation = SkipUnknownLocation,
            MaxConcurrency = Math.Max(1, MaxConcurrency),
            DryRun = DryRun,
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
            StatusMessage = $"{(DryRun ? "Dry run complete" : "Done")}: {ok} ok, {results.Count - ok} failed of {results.Count}.";
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

    private BulkProvisioningService BuildService()
    {
        session.Username = Username;
        session.Password = Password;
        return new BulkProvisioningService(session.Config, Username, Password);
    }

    private IReadOnlyList<IPAddress> ExpandTargets()
    {
        var addresses = new List<IPAddress>();
        foreach (var ip in selectedCameraIps)
            if (IPAddress.TryParse(ip, out var address)) addresses.Add(address);
        return addresses;
    }

    private static IReadOnlyList<string>? ParseCodecs(string text)
    {
        var codecs = text.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return codecs.Length == 0 ? null : codecs;
    }
}

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
