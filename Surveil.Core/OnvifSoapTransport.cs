using System.Net;
using System.Xml.Linq;

namespace Surveil.Core;

/// <summary>Shared HTTP lifetime and SOAP dispatch for all ONVIF service clients.</summary>
internal sealed class OnvifSoapTransport : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public Uri Endpoint { get; }

    public OnvifSoapTransport(Uri endpoint, string username, string password)
    {
        Endpoint = endpoint;
        http = new HttpClient(new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
        });
        ownsHttp = true;
    }

    public OnvifSoapTransport(Uri endpoint, HttpClient httpClient)
    {
        Endpoint = endpoint;
        http = httpClient;
    }

    public Task<XElement> SendAsync(string serviceNamespace, string operation, XElement body,
        CancellationToken cancellationToken) =>
        OnvifSoap.SendAsync(http, Endpoint, serviceNamespace, operation, body, cancellationToken);

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }
}
