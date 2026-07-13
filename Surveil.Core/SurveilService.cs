using System.Net;

namespace Surveil.Core;

public sealed record DiscoveryCamera(string Ip, string Site, string Area, string XAddresses);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveryCamera> Cameras, int DistinctSubnets, int DistinctSites);

/// <summary>What a camera is and can do, read after a successful login.</summary>
public sealed record CameraFeatures(
    OnvifDeviceInformation Info, OnvifMediaGeneration MediaGeneration,
    IReadOnlyList<string> Services, IReadOnlyList<CameraEncoderSummary> Encoders);

/// <summary>One video encoder's capability summary: the codecs and resolutions it offers, its
/// top resolution/rate, the discrete frame rates it supports, and its bitrate window (kbps).</summary>
public sealed record CameraEncoderSummary(
    IReadOnlyList<string> Codecs, IReadOnlyList<OnvifResolution> Resolutions,
    OnvifResolution MaxResolution, float? MaxFrameRate,
    IReadOnlyList<float> FrameRates, OnvifRange<int>? Bitrate);

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
            return new FoundCamera { Ip = address.ToString(), Site = location?.Site ?? "", Area = location?.Area ?? "" };
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

    private static async Task<IReadOnlyList<CameraEncoderSummary>> ReadEncoderSummariesAsync(
        IOnvifVideoClient video, CancellationToken cancellationToken)
    {
        var summaries = new List<CameraEncoderSummary>();
        foreach (var config in await video.GetVideoEncoderConfigurationsAsync(cancellationToken: cancellationToken))
        {
            IReadOnlyList<OnvifVideoEncoderOptions> options;
            try { options = await video.GetVideoEncoderConfigurationOptionsAsync(config.Token, cancellationToken: cancellationToken); }
            catch { options = []; }  // some cameras reject per-config options; fall back to the current config

            var codecs = options.Select(o => o.Encoding).Prepend(config.Encoding)
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var resolutions = options.SelectMany(o => o.Resolutions).Append(config.Resolution).Distinct().ToList();
            var maxResolution = resolutions.OrderByDescending(r => (long)r.Width * r.Height).First();
            var frameRates = options.SelectMany(o => o.FrameRates).ToList();
            float? maxFrameRate = frameRates.Count > 0 ? frameRates.Max() : config.FrameRateLimit;
            var distinctRates = frameRates.Distinct().OrderByDescending(r => r).ToList();
            OnvifRange<int>? bitrate = options.Count > 0
                ? new OnvifRange<int>(options.Min(o => o.Bitrate.Minimum), options.Max(o => o.Bitrate.Maximum))
                : config.BitrateLimit is { } limit ? new OnvifRange<int>(limit, limit) : null;
            summaries.Add(new CameraEncoderSummary(codecs, resolutions, maxResolution, maxFrameRate, distinctRates, bitrate));
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
