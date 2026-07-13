using System.Net;

namespace Surveil.Core;

/// <summary>Resolves camera locations/endpoints and derives deterministic identity values.</summary>
public sealed class CameraConfigurationPlanner
{
    private const string DeviceServicePath = "/onvif/device_service";
    private readonly NetworkMapIndex networkMap;
    private readonly Func<CameraConfigurationTarget, (string Name, string? Hostname)> naming;

    public CameraConfigurationPlanner(SurveilConfig config,
        Func<CameraConfigurationTarget, (string Name, string? Hostname)>? naming = null)
    {
        networkMap = new NetworkMapIndex(config);
        this.naming = naming ?? DefaultNaming;
    }

    public IReadOnlyList<CameraConfigurationTarget> TargetsFromAddresses(IEnumerable<IPAddress> addresses) =>
        addresses.Select(address => Locate(address, DefaultDeviceEndpoint(address))).ToArray();

    public IReadOnlyList<CameraConfigurationTarget> TargetsFromResponders(IEnumerable<WsDiscoveryResponder> responders) =>
        responders.Select(responder => Locate(responder.Ip,
            OnvifEndpoint.FirstAdvertised(responder.XAddresses) ?? DefaultDeviceEndpoint(responder.Ip))).ToArray();

    public IReadOnlyList<CameraConfigurationTarget> TargetsFrom(IEnumerable<(IPAddress Address, Uri? Endpoint)> items) =>
        items.Select(item => Locate(item.Address, item.Endpoint ?? DefaultDeviceEndpoint(item.Address))).ToArray();

    public IReadOnlyList<CameraConfigurationPlan> Plan(IEnumerable<CameraConfigurationTarget> targets,
        bool includeUnknownLocation = false) =>
        targets.Where(target => target.LocationKnown || includeUnknownLocation).Select(PlanFor).ToArray();

    public CameraConfigurationPlan PlanFor(CameraConfigurationTarget target)
    {
        var (name, hostname) = naming(target);
        return new CameraConfigurationPlan(target, name, hostname);
    }

    private CameraConfigurationTarget Locate(IPAddress address, Uri endpoint)
    {
        var location = networkMap.Locate(address);
        return new CameraConfigurationTarget(address, endpoint, location?.Site ?? "", location?.Area ?? "");
    }

    private static (string Name, string? Hostname) DefaultNaming(CameraConfigurationTarget target)
    {
        var name = string.Join(" ", new[] { target.Site, target.Area }.Where(part => part.Length > 0));
        if (name.Length == 0) name = target.Address.ToString();
        var lastOctet = target.Address.GetAddressBytes()[^1];
        var hostname = Slug($"{target.Site}-{target.Area}-{lastOctet}");
        return (name, hostname.Length == 0 ? null : hostname);
    }

    internal static string Slug(string value)
    {
        var slug = new string(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length > 63 ? slug[..63].Trim('-') : slug;
    }

    private static Uri DefaultDeviceEndpoint(IPAddress address) =>
        new UriBuilder("http", address.ToString()) { Path = DeviceServicePath }.Uri;
}

public static class OnvifEndpoint
{
    /// <summary>Returns the first absolute URI in a WS-Discovery XAddrs value.</summary>
    public static Uri? FirstAdvertised(string xAddresses)
    {
        var first = xAddresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Uri.TryCreate(first, UriKind.Absolute, out var uri) ? uri : null;
    }
}
