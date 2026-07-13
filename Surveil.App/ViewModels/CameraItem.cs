using CommunityToolkit.Mvvm.ComponentModel;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Result of trying to log into a found camera.</summary>
public enum CameraLoginState { NotTried, NoCredentials, InProgress, Success, AuthFailed, Unreachable }

/// <summary>A camera row in the tree. Wraps the core <see cref="CameraStatus"/> and adds a
/// provision-selection checkbox plus, once identified, what the camera is and can do. The
/// provision checkbox is a separate axis from the range checkboxes.</summary>
public sealed partial class CameraItem : ObservableObject
{
    public CameraStatus Camera { get; }

    /// <summary>The device-service URL this camera advertised via WS-Discovery, if known. Identify
    /// and provisioning connect here rather than assuming the standard path. Null for scanned cameras.</summary>
    public Uri? Endpoint { get; }

    [ObservableProperty] private bool isSelected;

    /// <summary>Raised when the provision checkbox toggles, so the owner can refresh the target set.</summary>
    public Action? SelectionChanged { get; set; }

    // --- Login / feature discovery ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine), nameof(StateGlyph), nameof(HasStateGlyph))]
    private CameraLoginState loginState;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusLine))] private string identity = "";
    [ObservableProperty] private string servicesText = "";
    [ObservableProperty] private string videoText = "";
    [ObservableProperty] private string errorText = "";

    public CameraItem(CameraStatus camera, Uri? endpoint = null)
    {
        Camera = camera;
        Endpoint = endpoint;
    }

    public string Ip => Camera.Ip;
    public string StatusText => Camera.Status;

    /// <summary>The line shown after the IP: the camera's identity once logged in, otherwise a short
    /// word describing the login state (or the raw scan/discovery status before we've tried).</summary>
    public string StatusLine => LoginState switch
    {
        CameraLoginState.Success => Identity,
        CameraLoginState.InProgress => "signing in…",
        CameraLoginState.AuthFailed => "sign-in failed",
        CameraLoginState.Unreachable => "unreachable",
        CameraLoginState.NoCredentials => "enter ONVIF credentials to identify",
        _ => StatusText,
    };

    public string StateGlyph => LoginState switch
    {
        CameraLoginState.Success => "",      // CheckMark
        CameraLoginState.InProgress => "",   // Sync
        CameraLoginState.AuthFailed => "",   // Lock
        CameraLoginState.Unreachable => "",  // Error
        _ => "",
    };

    public bool HasStateGlyph => StateGlyph.Length > 0;

    /// <summary>Fold a successful identification into the display fields.</summary>
    public void ApplyFeatures(CameraFeatures features)
    {
        var makeModel = string.Join(" ", new[] { features.Info.Manufacturer, features.Info.Model }
            .Where(part => part.Length > 0));
        if (makeModel.Length == 0) makeModel = "ONVIF camera";
        Identity = features.Info.FirmwareVersion.Length > 0 ? $"{makeModel} · fw {features.Info.FirmwareVersion}" : makeModel;
        ServicesText = features.Services.Count > 0 ? "services: " + string.Join(", ", features.Services) : "";
        VideoText = features.Encoders.Count > 0 ? "video: " + string.Join("    ", features.Encoders.Select(FormatEncoder)) : "";
        ErrorText = "";
        LoginState = CameraLoginState.Success;
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
