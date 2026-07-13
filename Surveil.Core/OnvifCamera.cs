using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Surveil.Core;

public enum OnvifMediaGeneration { Media1, Media2 }

public sealed record OnvifService(string Namespace, Uri Endpoint, int? MajorVersion, int? MinorVersion);
public sealed record OnvifCameraCapabilities(
    Uri DeviceEndpoint, IReadOnlyList<OnvifService> Services, OnvifMediaGeneration MediaGeneration,
    Uri MediaEndpoint, bool AdvertisesProfileT, bool AdvertisesProfileS);

public interface IOnvifVideoClient : IDisposable
{
    OnvifMediaGeneration Generation { get; }
    Uri Endpoint { get; }
    Task<IReadOnlyList<OnvifMediaProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnvifVideoEncoderConfiguration>> GetVideoEncoderConfigurationsAsync(
        string? configurationToken = null, string? profileToken = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnvifVideoEncoderOptions>> GetVideoEncoderConfigurationOptionsAsync(
        string? configurationToken = null, string? profileToken = null, CancellationToken cancellationToken = default);
    Task SetVideoEncoderConfigurationAsync(OnvifVideoEncoderConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task UpdateVideoEncoderAsync(string configurationToken, int? width = null, int? height = null,
        float? framesPerSecond = null, int? bitrateKbps = null, float? quality = null, string? encoding = null,
        CancellationToken cancellationToken = default);
}

public sealed class OnvifCameraConnection : IDisposable
{
    private readonly HttpClient transport;
    public OnvifCameraCapabilities Capabilities { get; }
    public IOnvifVideoClient Video { get; }
    internal OnvifCameraConnection(OnvifCameraCapabilities capabilities, IOnvifVideoClient video, HttpClient transport) =>
        (Capabilities, Video, this.transport) = (capabilities, video, transport);
    public void Dispose() { Video.Dispose(); transport.Dispose(); }
}

/// <summary>Discovers ONVIF services and selects Media2 when available, otherwise Media1.</summary>
public sealed class OnvifCameraConnector
{
    public const string DeviceNamespace = "http://www.onvif.org/ver10/device/wsdl";
    public const string Media1Namespace = "http://www.onvif.org/ver10/media/wsdl";
    public const string Media2Namespace = "http://www.onvif.org/ver20/media/wsdl";
    private readonly Func<Uri, HttpClient> httpClientFactory;

    public OnvifCameraConnector(string username, string password) : this(_ =>
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };
        return new HttpClient(handler);
    })
    { }

    public OnvifCameraConnector(Func<Uri, HttpClient> httpClientFactory) => this.httpClientFactory = httpClientFactory;

    public async Task<OnvifCameraConnection> ConnectAsync(Uri deviceEndpoint,
        IEnumerable<string>? discoveryScopes = null, CancellationToken cancellationToken = default)
    {
        using var deviceHttp = httpClientFactory(deviceEndpoint);
        var services = await GetServicesAsync(deviceEndpoint, deviceHttp, cancellationToken);
        var service = services.FirstOrDefault(x => x.Namespace == Media2Namespace) ??
            services.FirstOrDefault(x => x.Namespace == Media1Namespace) ??
            throw new NotSupportedException("Camera advertises neither ONVIF Media2 nor Media1.");
        var generation = service.Namespace == Media2Namespace ? OnvifMediaGeneration.Media2 : OnvifMediaGeneration.Media1;
        var scopes = discoveryScopes?.ToArray() ?? [];
        var capabilities = new OnvifCameraCapabilities(deviceEndpoint, services, generation, service.Endpoint,
            scopes.Any(x => x.Contains("/Profile/T", StringComparison.OrdinalIgnoreCase)),
            scopes.Any(x => x.Contains("/Profile/Streaming", StringComparison.OrdinalIgnoreCase) ||
                            x.Contains("/Profile/S", StringComparison.OrdinalIgnoreCase)));
        var mediaHttp = httpClientFactory(service.Endpoint);
        IOnvifVideoClient client = generation == OnvifMediaGeneration.Media2
            ? new OnvifMedia2Client(service.Endpoint, mediaHttp)
            : new OnvifMedia1Client(service.Endpoint, mediaHttp);
        return new OnvifCameraConnection(capabilities, client, mediaHttp);
    }

    internal static async Task<IReadOnlyList<OnvifService>> GetServicesAsync(Uri endpoint, HttpClient http,
        CancellationToken cancellationToken = default)
    {
        XNamespace td = DeviceNamespace;
        var response = await OnvifSoap.SendAsync(http, endpoint, DeviceNamespace, "GetServices",
            new XElement(td + "GetServices", new XElement(td + "IncludeCapability", false)), cancellationToken);
        return response.Descendants(td + "Service").Select(service =>
        {
            var ns = service.Element(td + "Namespace")?.Value ?? throw new FormatException("Service has no namespace.");
            var address = service.Element(td + "XAddr")?.Value ?? throw new FormatException("Service has no XAddr.");
            var version = service.Element(td + "Version");
            return new OnvifService(ns, new Uri(address),
                ParseInt(version?.Element(td + "Major")?.Value), ParseInt(version?.Element(td + "Minor")?.Value));
        }).ToArray();
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;
}

internal static class OnvifSoap
{
    private const string Soap = "http://www.w3.org/2003/05/soap-envelope";
    public static async Task<XElement> SendAsync(HttpClient http, Uri endpoint, string serviceNamespace,
        string operation, XElement body, CancellationToken cancellationToken)
    {
        var prefix = serviceNamespace switch
        {
            OnvifCameraConnector.DeviceNamespace => "td",
            OnvifCameraConnector.Media1Namespace => "trt",
            OnvifCameraConnector.Media2Namespace => "tr2",
            _ => "tns"
        };
        var document = new XDocument(new XElement(XName.Get("Envelope", Soap),
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(XNamespace.Xmlns + prefix, serviceNamespace),
            new XAttribute(XNamespace.Xmlns + "tt", OnvifMedia2Client.SchemaNamespace),
            new XElement(XName.Get("Body", Soap), body)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            $"application/soap+xml; charset=utf-8; action=\"{serviceNamespace}/{operation}\"");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new OnvifException(response.StatusCode, Fault(xml));
        try { return XDocument.Parse(xml).Root ?? throw new FormatException("Empty SOAP response."); }
        catch (Exception error) { throw new OnvifException(response.StatusCode, "Invalid SOAP response.", error); }
    }

    private static string Fault(string xml) { try { return XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == "Text")?.Value ?? xml; } catch { return xml; } }
}
