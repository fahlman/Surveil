using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Surveil.Core;

public sealed record WsDiscoveryResponder(IPAddress Ip, string XAddresses);

public interface IWsDiscovery
{
    Task<IReadOnlyList<WsDiscoveryResponder>> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

public sealed class WsDiscovery : IWsDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    public async Task<IReadOnlyList<WsDiscoveryResponder>> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var message = Encoding.UTF8.GetBytes(BuildProbe());
        var responders = new ConcurrentDictionary<IPAddress, WsDiscoveryResponder>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));

        // Probe from every multicast-capable interface, not just the OS default. A work machine is
        // usually multi-homed (camera VLAN + WiFi + VPN + Hyper-V/WSL/VMware virtual adapters), and
        // the default interface is often a virtual one with no route to the cameras — which is why a
        // single-socket probe throws WSAEHOSTUNREACH or silently reaches nothing. Send out each NIC
        // and ignore the ones that can't route; collect replies from the ones that can.
        var clients = new List<UdpClient>();
        var listeners = new List<Task>();
        foreach (var local in MulticastInterfaces())
        {
            UdpClient client;
            try { client = new UdpClient(new IPEndPoint(local, 0)) { Ttl = 8 }; }
            catch (SocketException) { continue; }  // can't bind this interface — skip it
            clients.Add(client);
            try
            {
                await client.SendAsync(message, MulticastEndpoint, deadline.Token);
                await client.SendAsync(message, MulticastEndpoint, deadline.Token);
            }
            catch (SocketException) { continue; }              // no multicast route out this NIC
            catch (OperationCanceledException) { break; }      // caller cancelled or deadline hit
            listeners.Add(ReceiveUntilDeadlineAsync(client, responders, deadline.Token));
        }

        if (listeners.Count == 0)
        {
            foreach (var client in clients) client.Dispose();
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "Discovery couldn't reach the network. Check that this PC is connected to the camera network.");
        }

        try { await Task.WhenAll(listeners); }
        catch (OperationCanceledException) { }
        finally { foreach (var client in clients) client.Dispose(); }

        return responders.Values.OrderBy(item => IpAddress.ToUInt32(item.Ip)).ToArray();
    }

    private static async Task ReceiveUntilDeadlineAsync(UdpClient client,
        ConcurrentDictionary<IPAddress, WsDiscoveryResponder> responders, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var response = await client.ReceiveAsync(token);
                var body = Encoding.UTF8.GetString(response.Buffer);
                // WS-Discovery shares its multicast group (239.255.255.250:3702) with Microsoft WSD,
                // so NAS boxes, printers and Windows PCs answer our probe even though it asks for the
                // ONVIF NetworkVideoTransmitter type. Keep only responders that advertise that type.
                if (!IsOnvifCamera(body)) continue;
                responders.TryAdd(response.RemoteEndPoint.Address,
                    new WsDiscoveryResponder(response.RemoteEndPoint.Address, ExtractXAddresses(body)));
            }
        }
        catch (OperationCanceledException) { }  // deadline reached — stop listening on this socket
        catch (SocketException) { }             // socket torn down at the deadline
    }

    /// <summary>Local IPv4 addresses of every up, non-loopback, multicast-capable interface, so the
    /// probe reaches the camera LAN regardless of which interface Windows would pick by default.
    /// Falls back to <see cref="IPAddress.Any"/> if none are enumerable.</summary>
    private static IReadOnlyList<IPAddress> MulticastInterfaces()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;  // APIPA / link-local: no real route
                addresses.Add(ip);
            }
        }
        return addresses.Count > 0 ? addresses : new List<IPAddress> { IPAddress.Any };
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

    /// <summary>True only when a ProbeMatch advertises the ONVIF <c>NetworkVideoTransmitter</c> type,
    /// which is what distinguishes a camera from the other WS-Discovery / WSD devices (NAS, printers,
    /// PCs) that also answer the probe.</summary>
    internal static bool IsOnvifCamera(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants()
                .Where(element => element.Name.LocalName.Equals("Types", StringComparison.OrdinalIgnoreCase))
                .Any(element => element.Value.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException)
        {
            return xml.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase);
        }
    }

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
