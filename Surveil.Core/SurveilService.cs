using System.Net;

namespace Surveil.Core;

public sealed record DiscoveryCamera(string Ip, string Site, string Area, string XAddresses);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveryCamera> Cameras, int DistinctSubnets, int DistinctSites);

/// <summary>What a camera is and can do, read after a successful login.</summary>
public sealed record CameraFeatures(
    OnvifDeviceInformation Info, OnvifMediaGeneration MediaGeneration,
    IReadOnlyList<string> Services, IReadOnlyList<VideoEncoderInfo> Encoders);

public sealed class SurveilService
{
    private readonly IConfigurationRepository configurations;
    private readonly IInventoryRepository inventory;
    private readonly ICameraScanner scanner;
    private readonly IWsDiscovery discovery;

    public SurveilService(JsonStore? store = null, ICameraScanner? scanner = null, IWsDiscovery? discovery = null)
    {
        var json = store ?? new JsonStore();
        configurations = json;
        inventory = json;
        this.scanner = scanner ?? new CameraScanner();
        this.discovery = discovery ?? new WsDiscovery();
    }

    public SurveilService(IConfigurationRepository configurations, IInventoryRepository inventory,
        ICameraScanner? scanner = null, IWsDiscovery? discovery = null)
    {
        this.configurations = configurations;
        this.inventory = inventory;
        this.scanner = scanner ?? new CameraScanner();
        this.discovery = discovery ?? new WsDiscovery();
    }

    public Task<SurveilConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
        configurations.LoadConfigAsync(cancellationToken);

    public Task SaveConfigAsync(SurveilConfig config, CancellationToken cancellationToken = default) =>
        configurations.SaveConfigAsync(config, cancellationToken);

    public Task<SurveilConfig> ImportConfigAsync(string path, CancellationToken cancellationToken = default) =>
        configurations.ImportConfigAsync(path, cancellationToken);

    public Task ExportConfigAsync(string path, CancellationToken cancellationToken = default) =>
        configurations.ExportConfigAsync(path, cancellationToken);

    public async Task<IReadOnlyList<CameraStatus>> ScanAsync(
        IEnumerable<string> targets, int port, int concurrency = 256, TimeSpan? timeout = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var config = await configurations.LoadConfigAsync(cancellationToken);
        var addresses = NetworkRanges.ExpandPrivate(targets);
        var scanned = addresses.ToHashSet();
        var foundAddresses = await scanner.ScanAsync(addresses, port, concurrency, timeout, progress, cancellationToken);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var found = foundAddresses.Select(address =>
        {
            var location = NetworkRanges.Locate(config, address);
            return new FoundCamera { Ip = address.ToString(), Site = location?.Site ?? "", Area = location?.Area ?? "" };
        });
        var previous = await this.inventory.LoadInventoryAsync(cancellationToken);
        var (inventory, statuses) = InventoryComparer.Diff(previous, found, scanned, now);
        await this.inventory.SaveInventoryAsync(inventory, cancellationToken);
        return statuses;
    }

    public async Task<DiscoveryResult> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var config = await configurations.LoadConfigAsync(cancellationToken);
        var responders = await discovery.DiscoverAsync(timeout, cancellationToken);
        var cameras = responders.Select(responder =>
        {
            var location = NetworkRanges.Locate(config, responder.Ip);
            return new DiscoveryCamera(responder.Ip.ToString(), location?.Site ?? "",
                location?.Area ?? "", responder.XAddresses);
        }).ToArray();
        var subnets = responders.Select(item => string.Join(".", item.Ip.GetAddressBytes().Take(3))).Distinct().Count();
        var sites = cameras.Select(item => item.Site).Where(name => name.Length > 0).Distinct().Count();
        return new DiscoveryResult(cameras, subnets, sites);
    }

    public Task<OnvifCameraConnection> ConnectCameraAsync(Uri deviceEndpoint, string username, string password,
        IEnumerable<string>? discoveryScopes = null, CancellationToken cancellationToken = default) =>
        new OnvifCameraConnector(username, password)
            .ConnectAsync(deviceEndpoint, discoveryScopes, cancellationToken);

    public OnvifImagingClient CreateImagingClient(Uri imagingEndpoint, string username, string password) =>
        new(imagingEndpoint, username, password);

    public OnvifDeviceClient CreateDeviceClient(Uri deviceEndpoint, string username, string password) =>
        new(deviceEndpoint, username, password);

    /// <summary>Log into a camera and read what it is and can do: identity, supported services, and
    /// each video encoder's codecs and top resolution. Throws on bad credentials or an unreachable
    /// camera — the caller distinguishes the two via <see cref="OnvifException.IsAuthenticationFailure"/>.</summary>
    public async Task<CameraFeatures> IdentifyAsync(Uri deviceEndpoint, string username, string password,
        CancellationToken cancellationToken = default)
    {
        using var device = new OnvifDeviceClient(deviceEndpoint, username, password);
        var info = await device.GetDeviceInformationAsync(cancellationToken);

        using var connection = await new OnvifCameraConnector(username, password)
            .ConnectAsync(deviceEndpoint, null, cancellationToken);
        var services = FriendlyServiceNames(connection.Capabilities.Services);
        var encoders = await ReadEncoderSummariesAsync(connection.Video, cancellationToken);
        return new CameraFeatures(info, connection.Capabilities.MediaGeneration, services, encoders);
    }

    private static async Task<IReadOnlyList<VideoEncoderInfo>> ReadEncoderSummariesAsync(
        IOnvifVideoClient video, CancellationToken cancellationToken)
    {
        var summaries = new List<VideoEncoderInfo>();
        foreach (var config in await video.GetVideoEncoderConfigurationsAsync(cancellationToken: cancellationToken))
        {
            IReadOnlyList<OnvifVideoEncoderOptions> options;
            try { options = await video.GetVideoEncoderConfigurationOptionsAsync(config.Token, cancellationToken: cancellationToken); }
            catch { options = []; }  // some cameras reject per-config options; fall back to the current config

            var codecs = options.Select(option => new CodecCapability(option.Encoding,
                    option.Resolutions.Distinct().ToArray(), option.FrameRates.Distinct().ToArray(), option.Bitrate))
                .ToList();
            if (codecs.Count == 0)
                codecs.Add(new CodecCapability(config.Encoding, [config.Resolution],
                    config.FrameRateLimit is { } fps ? [fps] : [],
                    config.BitrateLimit is { } bitrate ? new OnvifRange<int>(bitrate, bitrate) : null));
            summaries.Add(new VideoEncoderInfo(config.Token, video.Generation == OnvifMediaGeneration.Media2,
                config.Encoding, config.Resolution, config.FrameRateLimit, codecs, config.BitrateLimit));
        }
        return summaries;
    }

    private static IReadOnlyList<string> FriendlyServiceNames(IReadOnlyList<OnvifService> services)
    {
        var names = new List<string>();
        foreach (var name in services.Select(service => ServiceName(service.Namespace)))
            if (name is not null && !names.Contains(name)) names.Add(name);
        return names;
    }

    private static string? ServiceName(string ns) => ns.ToLowerInvariant() switch
    {
        var n when n.Contains("/media/") => "media",
        var n when n.Contains("/ptz/") => "PTZ",
        var n when n.Contains("/imaging/") => "imaging",
        var n when n.Contains("/analytics") => "analytics",
        var n when n.Contains("/event") => "events",
        var n when n.Contains("/deviceio/") => "device I/O",
        var n when n.Contains("/recording/") => "recording",
        var n when n.Contains("/replay/") => "replay",
        var n when n.Contains("/receiver/") => "receiver",
        var n when n.Contains("/device/") => "device",
        _ => null,
    };
}
