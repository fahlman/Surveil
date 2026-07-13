using CommunityToolkit.Mvvm.ComponentModel;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Result of trying to log into a found camera.</summary>
public enum CameraLoginState { NotTried, NoCredentials, InProgress, Success, AuthFailed, Unreachable }

/// <summary>A camera row in the tree. Wraps the core <see cref="CameraStatus"/>, carries a
/// provision-selection checkbox, and once identified exposes what the camera is and can do — both
/// for a compact row summary and to feed the faceted filters. The provision checkbox is a separate
/// axis from the range checkboxes.</summary>
public sealed partial class CameraItem : ObservableObject
{
    public CameraStatus Camera { get; }

    /// <summary>The device-service URL this camera advertised via WS-Discovery, if known. Identify
    /// and provisioning connect here rather than assuming the standard path. Null for scanned cameras.</summary>
    public Uri? Endpoint { get; }

    [ObservableProperty] private bool isSelected;

    /// <summary>Raised when the provision checkbox toggles, so the owner can refresh the target set.</summary>
    public Action? SelectionChanged { get; set; }

    /// <summary>False when a facet filter is active and this camera doesn't match — the row hides.</summary>
    [ObservableProperty] private bool isVisible = true;

    // --- Login / feature discovery ---

    /// <summary>The raw features read after a successful login; null until then. Facets read this.</summary>
    public CameraFeatures? Features { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine), nameof(StateGlyph), nameof(HasStateGlyph), nameof(SignInLabel))]
    private CameraLoginState loginState;

    [ObservableProperty] private string identity = "";
    [ObservableProperty] private string servicesText = "";
    [ObservableProperty] private string videoText = "";
    [ObservableProperty] private string moreText = "";
    [ObservableProperty] private string errorText = "";

    public CameraItem(CameraStatus camera, Uri? endpoint = null)
    {
        Camera = camera;
        Endpoint = endpoint;
    }

    public string Ip => Camera.Ip;
    public string StatusText => Camera.Status;

    // --- Facet accessors (null / empty until identified) ---

    public string? Manufacturer => Features?.Info.Manufacturer is { Length: > 0 } value ? value : null;
    public string? ModelName => Features?.Info.Model is { Length: > 0 } value ? value : null;
    public IReadOnlyList<string> Codecs =>
        Features?.Encoders.SelectMany(e => e.Codecs).Select(PrettyCodec).Distinct().ToList() ?? [];
    public string? ResolutionBucket => Features is { Encoders.Count: > 0 } f
        ? Bucket(f.Encoders.Select(e => e.MaxResolution).OrderByDescending(r => (long)r.Width * r.Height).First())
        : null;
    public IReadOnlyList<string> Capabilities => Features?.Services.Where(IsCapability).ToList() ?? [];
    public string? MediaGen => Features is { } f ? (f.MediaGeneration == OnvifMediaGeneration.Media2 ? "Media2" : "Media1") : null;
    public string LocationLabel => Camera.Site.Length > 0 ? Camera.Site : "Unmapped";
    public string SignInLabel => LoginState switch
    {
        CameraLoginState.Success => "Identified",
        CameraLoginState.AuthFailed => "Sign-in failed",
        CameraLoginState.Unreachable => "Unreachable",
        _ => "Not identified",
    };

    /// <summary>Compact one-line summary shown on the row once identified.</summary>
    public string Summary
    {
        get
        {
            if (Features is null) return "";
            var parts = new List<string>();
            if (Manufacturer is { } make) parts.Add(make);
            if (Codecs.Count > 0) parts.Add(Codecs[0]);
            if (ResolutionBucket is { } bucket) parts.Add(bucket);
            if (Capabilities.Count > 0) parts.Add(string.Join(", ", Capabilities));
            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>The line shown after the IP: the compact summary once identified, otherwise a short
    /// word describing the login state (or the raw scan/discovery status before we've tried).</summary>
    public string StatusLine => LoginState switch
    {
        CameraLoginState.Success => Summary,
        CameraLoginState.InProgress => "signing in…",
        CameraLoginState.AuthFailed => "sign-in failed",
        CameraLoginState.Unreachable => "unreachable",
        CameraLoginState.NoCredentials => "enter ONVIF credentials to identify",
        _ => StatusText,
    };

    public bool CanShowDetails => Features is not null;

    public string StateGlyph => LoginState switch
    {
        CameraLoginState.Success => "",      // CheckMark
        CameraLoginState.InProgress => "",   // Sync
        CameraLoginState.AuthFailed => "",   // Lock
        CameraLoginState.Unreachable => "",  // Status
        _ => "",
    };

    public bool HasStateGlyph => StateGlyph.Length > 0;

    /// <summary>Fold a successful identification into the display + facet fields.</summary>
    public void ApplyFeatures(CameraFeatures features)
    {
        Features = features;
        var makeModel = string.Join(" ", new[] { features.Info.Manufacturer, features.Info.Model }.Where(p => p.Length > 0));
        if (makeModel.Length == 0) makeModel = "ONVIF camera";
        Identity = features.Info.FirmwareVersion.Length > 0 ? $"{makeModel} · fw {features.Info.FirmwareVersion}" : makeModel;
        ServicesText = features.Services.Count > 0 ? "Services: " + string.Join(", ", features.Services) : "";
        VideoText = features.Encoders.Count > 0 ? "Video: " + string.Join("    ", features.Encoders.Select(FormatEncoder)) : "";
        var more = new List<string> { $"ONVIF {(features.MediaGeneration == OnvifMediaGeneration.Media2 ? "Media2" : "Media1")}" };
        if (features.Info.SerialNumber.Length > 0) more.Add("S/N " + features.Info.SerialNumber);
        MoreText = string.Join("    ", more);
        ErrorText = "";
        LoginState = CameraLoginState.Success;
        OnPropertyChanged(nameof(Summary));
    }

    private static bool IsCapability(string service) =>
        service is "PTZ" or "imaging" or "analytics" or "events" or "recording" or "replay";

    private static string Bucket(OnvifResolution resolution)
    {
        var pixels = (long)resolution.Width * resolution.Height;
        return pixels switch
        {
            >= 7_000_000 => "4K (8MP+)",
            >= 4_500_000 => "5MP",
            >= 3_500_000 => "4MP",
            >= 2_500_000 => "3MP",
            >= 1_900_000 => "1080p (2MP)",
            >= 900_000 => "720p",
            _ => "SD",
        };
    }

    private static string FormatEncoder(CameraEncoderSummary encoder)
    {
        var codecs = encoder.Codecs.Select(PrettyCodec).ToList();
        var primary = codecs.Count > 0 ? codecs[0] : "?";
        var others = codecs.Skip(1).ToList();
        var fps = encoder.MaxFrameRate is { } rate and > 0 ? $" @{rate:0.#}fps" : "";
        var main = $"{primary} {encoder.MaxResolution.Width}×{encoder.MaxResolution.Height}{fps}";
        return others.Count > 0 ? $"{main} (+ {string.Join(", ", others)})" : main;
    }

    private static string PrettyCodec(string codec)
    {
        var normalized = codec.Replace("video/", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized switch { "H265" => "H.265", "H264" => "H.264", "JPEG" or "MJPEG" => "MJPEG", _ => codec };
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
