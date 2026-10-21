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
    /// <summary>Extra probes for a known camera that didn't answer the sweep, before it's reported missing.</summary>
    public const int MissingRetryAttempts = 2;

    /// <summary>Minimum timeout for those extra probes: long enough to outlast Windows' ~1 s wait before
    /// it resends a dropped connection request, which a 400 ms sweep probe never reaches.</summary>
    public static readonly TimeSpan MissingRetryTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>How many devices a scan signs into at once to read their identity.</summary>
    public const int IdentifyConcurrency = 16;

    private readonly IConfigurationRepository configurations;
    private readonly IInventoryRepository inventory;
    private readonly ICameraScanner scanner;
    private readonly IWsDiscovery discovery;
    private readonly ICameraIdentifier identifier;

    public SurveilService(JsonStore? store = null, ICameraScanner? scanner = null, IWsDiscovery? discovery = null,
        ICameraIdentifier? identifier = null)
    {
        var json = store ?? new JsonStore();
        configurations = json;
        inventory = json;
        this.scanner = scanner ?? new CameraScanner();
        this.discovery = discovery ?? new WsDiscovery();
        this.identifier = identifier ?? new OnvifCameraIdentifier();
    }

    public SurveilService(IConfigurationRepository configurations, IInventoryRepository inventory,
        ICameraScanner? scanner = null, IWsDiscovery? discovery = null, ICameraIdentifier? identifier = null)
    {
        this.configurations = configurations;
        this.inventory = inventory;
        this.scanner = scanner ?? new CameraScanner();
        this.discovery = discovery ?? new WsDiscovery();
        this.identifier = identifier ?? new OnvifCameraIdentifier();
    }

    public Task<SurveilConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
        configurations.LoadConfigAsync(cancellationToken);

    public Task SaveConfigAsync(SurveilConfig config, CancellationToken cancellationToken = default) =>
        configurations.SaveConfigAsync(config, cancellationToken);

    public Task<SurveilConfig> ImportConfigAsync(string path, CancellationToken cancellationToken = default) =>
        configurations.ImportConfigAsync(path, cancellationToken);

    public Task ExportConfigAsync(string path, CancellationToken cancellationToken = default) =>
        configurations.ExportConfigAsync(path, cancellationToken);

    /// <summary>Probe the targets, sign into every device that answers to read its identity, and
    /// compare with the inventory by identity — so a camera that changed address is reported moved,
    /// not missing + new.</summary>
    public async Task<IReadOnlyList<CameraStatus>> ScanAsync(
        IEnumerable<string> targets, int port, OnvifCredentials credentials, int concurrency = 256,
        TimeSpan? timeout = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var config = await configurations.LoadConfigAsync(cancellationToken);
        var addresses = NetworkRanges.ExpandPrivate(targets);
        var scanned = addresses.ToHashSet();
        var foundAddresses = await scanner.ScanAsync(addresses, port, concurrency, timeout, progress, cancellationToken);
        var previous = await this.inventory.LoadInventoryAsync(cancellationToken);
        foundAddresses = await ConfirmMissingAsync(previous, scanned, foundAddresses, port, concurrency, timeout,
            cancellationToken);
        var found = await IdentifyAsync(foundAddresses, credentials, config, progress, cancellationToken);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (inventory, statuses) = InventoryComparer.Diff(previous, found, scanned, now);
        await this.inventory.SaveInventoryAsync(inventory, cancellationToken);
        return statuses;
    }

    /// <summary>Read each responding device's identity. A device that can't be identified is still
    /// returned, with the reason, so the scan can report it without tracking it.</summary>
    private async Task<IReadOnlyList<FoundCamera>> IdentifyAsync(IReadOnlyList<IPAddress> addresses,
        OnvifCredentials credentials, SurveilConfig config, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ScanProgress(0, addresses.Count, 0, ScanPhase.Identifying));
        using var gate = new SemaphoreSlim(IdentifyConcurrency);
        var sync = new object();
        int done = 0, identified = 0;
        return await Task.WhenAll(addresses.Select(async address =>
        {
            var location = NetworkRanges.Locate(config, address);
            var camera = new FoundCamera
            {
                Ip = address.ToString(), Site = location?.Site ?? "", Area = location?.Area ?? "",
            };
            await gate.WaitAsync(cancellationToken);
            try
            {
                camera.Identity = await identifier.IdentifyAsync(address, credentials, cancellationToken);
                if (camera.Identity is null) camera.Problem = "reports no serial number";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                camera.Problem = DescribeIdentificationFailure(error);
            }
            finally
            {
                gate.Release();
            }
            ScanProgress update;
            lock (sync) update = new(++done, addresses.Count, camera.Identity is null ? identified : ++identified,
                ScanPhase.Identifying);
            progress?.Report(update);
            return camera;
        }));
    }

    private static string DescribeIdentificationFailure(Exception error) => error switch
    {
        OnvifException { IsAuthenticationFailure: true } => "sign-in failed (check the ONVIF credentials)",
        OnvifException { StatusCode: HttpStatusCode.NotFound } => "no ONVIF service (not a camera?)",
        OnvifException onvif => $"ONVIF error (HTTP {(int)onvif.StatusCode})",
        _ => "no ONVIF response",
    };

    /// <summary>Re-probe the known cameras in scope that didn't answer the sweep, with a longer timeout,
    /// so one dropped connection attempt doesn't report an online camera missing. Only those few
    /// addresses are retried — never the empty ones — so a full sweep takes no longer.</summary>
    private async Task<IReadOnlyList<IPAddress>> ConfirmMissingAsync(Inventory previous, ISet<IPAddress> scanned,
        IReadOnlyList<IPAddress> found, int port, int concurrency, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var responding = found.ToHashSet();
        var unconfirmed = previous.Cameras
            .Select(camera => IPAddress.TryParse(camera.Ip, out var address) ? address : null)
            .OfType<IPAddress>()
            .Where(address => scanned.Contains(address) && !responding.Contains(address))
            .Distinct()
            .ToList();
        if (unconfirmed.Count == 0) return found;

        // Never retry with less patience than the sweep itself was given.
        var retryTimeout = timeout is { } configured && configured > MissingRetryTimeout ? configured : MissingRetryTimeout;
        for (var attempt = 0; attempt < MissingRetryAttempts && unconfirmed.Count > 0; attempt++)
        {
            var answered = (await scanner.ScanAsync(unconfirmed, port, concurrency, retryTimeout, null,
                cancellationToken)).ToHashSet();
            responding.UnionWith(answered);
            unconfirmed.RemoveAll(answered.Contains);
        }
        return responding.OrderBy(IpAddress.ToUInt32).ToArray();
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
