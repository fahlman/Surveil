using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Surveil.Core;

public sealed record WsDiscoveryResponder(IPAddress Ip, string XAddresses);

public sealed class WsDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    public async Task<IReadOnlyList<WsDiscoveryResponder>> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        client.Ttl = 8;
        var message = Encoding.UTF8.GetBytes(BuildProbe());
        await client.SendAsync(message, MulticastEndpoint, cancellationToken);
        await client.SendAsync(message, MulticastEndpoint, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
        var responders = new Dictionary<IPAddress, WsDiscoveryResponder>();
        try
        {
            while (true)
            {
                var response = await client.ReceiveAsync(deadline.Token);
                var body = Encoding.UTF8.GetString(response.Buffer);
                if (!body.Contains("ProbeMatch", StringComparison.OrdinalIgnoreCase) &&
                    !body.Contains("XAddrs", StringComparison.OrdinalIgnoreCase)) continue;
                responders.TryAdd(response.RemoteEndPoint.Address,
                    new WsDiscoveryResponder(response.RemoteEndPoint.Address, ExtractXAddresses(body)));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return responders.Values.OrderBy(item => IpAddress.ToUInt32(item.Ip)).ToArray();
        }
    }

    internal static string BuildProbe() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
          xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
          xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
          xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <e:Header><w:MessageID>urn:uuid:{Guid.NewGuid()}</w:MessageID>
          <w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
          <w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
          </e:Header><e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>
        </e:Envelope>
        """;

    internal static string ExtractXAddresses(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("XAddrs", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        }
        catch (System.Xml.XmlException)
        {
            return "";
        }
    }
}
