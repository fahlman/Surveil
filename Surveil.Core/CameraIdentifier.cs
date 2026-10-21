using System.Net;

namespace Surveil.Core;

public sealed record OnvifCredentials(string Username, string Password);

/// <summary>Reads a device's identity over ONVIF, so a scan can tell cameras apart regardless of
/// which address each one has today.</summary>
public interface ICameraIdentifier
{
    /// <summary>Returns null when the device answers ONVIF but reports no serial number. Throws
    /// when it can't be read at all (wrong credentials, no ONVIF service, no answer).</summary>
    Task<CameraIdentity?> IdentifyAsync(IPAddress address, OnvifCredentials credentials,
        CancellationToken cancellationToken);
}

/// <summary>Default <see cref="ICameraIdentifier"/>: one ONVIF GetDeviceInformation call at the
/// standard device-service path.</summary>
public sealed class OnvifCameraIdentifier : ICameraIdentifier
{
    /// <summary>A device that accepts the connection but never answers HTTP mustn't stall the scan
    /// for HttpClient's 100-second default.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<CameraIdentity?> IdentifyAsync(IPAddress address, OnvifCredentials credentials,
        CancellationToken cancellationToken)
    {
        var endpoint = new UriBuilder("http", address.ToString()) { Path = "/onvif/device_service" }.Uri;
        using var http = new HttpClient(new HttpClientHandler
        {
            Credentials = new NetworkCredential(credentials.Username, credentials.Password),
            PreAuthenticate = true,
        }) { Timeout = Timeout };
        using var device = new OnvifDeviceClient(endpoint, http);
        var info = await device.GetDeviceInformationAsync(cancellationToken);
        return info.SerialNumber.Length == 0 ? null
            : new CameraIdentity(info.Manufacturer, info.Model, info.SerialNumber);
    }
}
