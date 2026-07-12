using System.Net;

namespace Surveil.Core;

public sealed record DiscoveryCamera(string Ip, string Building, string Area, string XAddresses);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveryCamera> Cameras, int DistinctSubnets, int DistinctBuildings);

public sealed class SurveilService
{
    private readonly JsonStore store;
    private readonly ICameraScanner scanner;
    private readonly IWsDiscovery discovery;

    public SurveilService(JsonStore? store = null, ICameraScanner? scanner = null, IWsDiscovery? discovery = null)
    {
        this.store = store ?? new JsonStore();
        this.scanner = scanner ?? new CameraScanner();
        this.discovery = discovery ?? new WsDiscovery();
    }

    public Task<SurveilConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
        store.LoadConfigAsync(cancellationToken);

    public Task SaveConfigAsync(SurveilConfig config, CancellationToken cancellationToken = default) =>
        store.SaveConfigAsync(config, cancellationToken);

    public Task<SurveilConfig> ImportConfigAsync(string path, CancellationToken cancellationToken = default) =>
        store.ImportConfigAsync(path, cancellationToken);

    public Task ExportConfigAsync(string path, CancellationToken cancellationToken = default) =>
        store.ExportConfigAsync(path, cancellationToken);

    public async Task<IReadOnlyList<CameraStatus>> ScanAsync(
        IEnumerable<string> targets, int port, int concurrency = 256, TimeSpan? timeout = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var config = await store.LoadConfigAsync(cancellationToken);
        var addresses = NetworkRanges.ExpandPrivate(targets);
        var scanned = addresses.ToHashSet();
        var foundAddresses = await scanner.ScanAsync(addresses, port, concurrency, timeout, progress, cancellationToken);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var found = foundAddresses.Select(address => {
            var location = NetworkRanges.Locate(config, address);
            return new FoundCamera { Ip = address.ToString(), Building = location?.Building ?? "", Area = location?.Area ?? "" };
        });
        var previous = await store.LoadInventoryAsync(cancellationToken);
        var (inventory, statuses) = InventoryComparer.Diff(previous, found, scanned, now);
        await store.SaveInventoryAsync(inventory, cancellationToken);
        return statuses;
    }

    public async Task<DiscoveryResult> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var config = await store.LoadConfigAsync(cancellationToken);
        var responders = await discovery.DiscoverAsync(timeout, cancellationToken);
        var cameras = responders.Select(responder => {
            var location = NetworkRanges.Locate(config, responder.Ip);
            return new DiscoveryCamera(responder.Ip.ToString(), location?.Building ?? "",
                location?.Area ?? "", responder.XAddresses);
        }).ToArray();
        var subnets = responders.Select(item => string.Join(".", item.Ip.GetAddressBytes().Take(3))).Distinct().Count();
        var buildings = cameras.Select(item => item.Building).Where(name => name.Length > 0).Distinct().Count();
        return new DiscoveryResult(cameras, subnets, buildings);
    }
}
