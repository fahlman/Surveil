using System.Net;

namespace Surveil.Core;

/// <summary>A camera to be configured, tagged with where it lives per the site map.</summary>
public sealed record CameraConfigurationTarget(IPAddress Address, Uri DeviceEndpoint, string Site, string Area)
{
    /// <summary>True when the address fell inside a configured site range.</summary>
    public bool LocationKnown => Site.Length > 0;
}

/// <summary>The concrete identity to push to one camera, derived from its location.</summary>
public sealed record CameraConfigurationPlan(CameraConfigurationTarget Target, string Name, string? Hostname);

/// <summary>Per-camera outcome. <see cref="Steps"/> lists what was applied and verified.</summary>
public sealed record CameraConfigurationResult(
    IPAddress Address, string Site, string Area,
    bool Success, IReadOnlyList<string> Steps, string? Error,
    IReadOnlyList<VideoEncoderOutcome> Video);

public readonly record struct BulkConfigurationProgress(int Completed, int Total, int Succeeded, int Failed);

/// <summary>Which identity fields to write, plus batch behavior.</summary>
public sealed record BulkConfigurationOptions
{
    public bool SetName { get; init; } = true;
    public bool SetHostname { get; init; } = true;
    public bool SetNtp { get; init; } = true;
    /// <summary>Explicit name to write; overrides the map-derived name when set.</summary>
    public string? Name { get; init; }
    /// <summary>Explicit hostname to write; overrides the map-derived hostname when set.</summary>
    public string? Hostname { get; init; }
    /// <summary>NTP time zone as a POSIX string (e.g. "EST5EDT,M3.2.0,M11.1.0"); null leaves the
    /// camera's zone/mode unchanged.</summary>
    public string? NtpPosixTimeZone { get; init; }
    /// <summary>Manual NTP server address (IPv4 or DNS); null/blank leaves it unchanged.</summary>
    public string? NtpServer { get; init; }
    public int MaxConcurrency { get; init; } = 8;
    /// <summary>Skip cameras whose address is not inside any configured site range.</summary>
    public bool SkipUnknownLocation { get; init; } = true;
    /// <summary>Switch every encoder to this codec (e.g. "H265"); null leaves the codec unchanged.
    /// Encoders that cannot switch codec (legacy Media1) keep their current one.</summary>
    public string? VideoCodec { get; init; }
    /// <summary>Set every encoder to the largest supported resolution no bigger than this; null
    /// leaves each encoder's resolution unchanged. Requires a video factory.</summary>
    public OnvifResolution? VideoResolution { get; init; }
    /// <summary>Set every encoder's frame rate to this, clamped to the nearest rate at or below it
    /// that the encoder supports; null leaves each encoder's frame rate unchanged.</summary>
    public float? VideoFrameRate { get; init; }
    /// <summary>Set every encoder's bitrate cap to this many kbps, clamped into each encoder's
    /// advertised range; null leaves each encoder's bitrate unchanged.</summary>
    public int? VideoBitrateKbps { get; init; }
    /// <summary>Preview mode: read each camera's capabilities and report exactly what would be
    /// applied, but perform no writes. Capability reads still occur; nothing on the camera changes.</summary>
    public bool DryRun { get; init; }
}

/// <summary>A single camera's configuration surface, abstracted so the batch orchestration is
/// testable without a live camera. The default implementation drives ONVIF Device Management,
/// whose setters read the value back to confirm it took.</summary>
public interface IConfigurableDevice : IDisposable
{
    Task SetNameAsync(string name, CancellationToken cancellationToken);
    /// <returns>True when the camera reported that a reboot is required.</returns>
    Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken);
    Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken);
    Task SetNtpServerAsync(string server, CancellationToken cancellationToken);
}

/// <summary>What one codec supports on an encoder: the resolutions and frame rates available when
/// the encoder is set to this codec, plus its bitrate window (kbps) if advertised.</summary>
public sealed record CodecCapability(
    string Codec, IReadOnlyList<OnvifResolution> Resolutions, IReadOnlyList<float> FrameRates,
    OnvifRange<int>? Bitrate = null);

/// <summary>One video encoder on a camera: its token, current state, whether its codec can be
/// switched (false for legacy Media1), and its per-codec capabilities.</summary>
public sealed record VideoEncoderInfo(
    string ConfigurationToken, bool CanSwitchCodec,
    string CurrentCodec, OnvifResolution CurrentResolution, float? CurrentFrameRate,
    IReadOnlyList<CodecCapability> Codecs, int? CurrentBitrate = null);

/// <summary>An encoder's codec, resolution, frame rate, and bitrate (kbps) at a point in time.</summary>
public sealed record VideoEncoderState(string Codec, OnvifResolution Resolution, float? FrameRate, int? Bitrate = null);

/// <summary>What actually happened to one encoder, for truthful UI display: what was requested, and
/// what the camera reported after the write. The flags mark where the device could not honor the
/// request — a codec fallback (asked H.265, got H.264) or a clamped combo (asked 4K@30, got 4K@15).</summary>
public sealed record VideoEncoderOutcome(
    string ConfigurationToken,
    string RequestedCodec, OnvifResolution RequestedResolution, float? RequestedFrameRate,
    string AppliedCodec, OnvifResolution AppliedResolution, float? AppliedFrameRate,
    int? RequestedBitrate = null, int? AppliedBitrate = null)
{
    public bool CodecFallback =>
        BulkConfigurationService.NormalizeCodec(RequestedCodec) != BulkConfigurationService.NormalizeCodec(AppliedCodec);

    public bool ClampedByCamera =>
        AppliedResolution != RequestedResolution ||
        (RequestedFrameRate is { } requestedFps && AppliedFrameRate is { } appliedFps && appliedFps < requestedFps) ||
        (RequestedBitrate is { } requestedBps && AppliedBitrate is { } appliedBps && appliedBps < requestedBps);
}

/// <summary>A camera's video-encoder surface, abstracted for testing. The default implementation
/// connects over ONVIF and defers each write to the media client's capability validation.</summary>
public interface IConfigurableVideo : IDisposable
{
    Task<IReadOnlyList<VideoEncoderInfo>> GetEncodersAsync(CancellationToken cancellationToken);
    /// <summary>Apply codec (null = leave unchanged) + resolution + frame rate + bitrate (kbps, null =
    /// leave unchanged), then read the encoder back and return what the camera actually settled on.</summary>
    Task<VideoEncoderState> ApplyAsync(string configurationToken, string? codec, OnvifResolution resolution,
        float? frameRate, int? bitrateKbps, CancellationToken cancellationToken);
}

/// <summary>Bulk-configures cameras over ONVIF as an iCT replacement: it derives each camera's
/// name and hostname from the site map, applies them (plus NTP), verifies via read-back, and
/// returns a per-camera pass/fail report instead of a silent GUI you have to trust.</summary>
public sealed class BulkConfigurationService
{
    private const string DeviceServicePath = "/onvif/device_service";
    private readonly SurveilConfig config;
    private readonly Func<Uri, IConfigurableDevice> deviceFactory;
    private readonly Func<Uri, IConfigurableVideo>? videoFactory;
    private readonly Func<CameraConfigurationTarget, (string Name, string? Hostname)> naming;

    public BulkConfigurationService(SurveilConfig config, Func<Uri, IConfigurableDevice> deviceFactory,
        Func<Uri, IConfigurableVideo>? videoFactory = null,
        Func<CameraConfigurationTarget, (string Name, string? Hostname)>? naming = null)
    {
        this.config = config;
        this.deviceFactory = deviceFactory;
        this.videoFactory = videoFactory;
        this.naming = naming ?? DefaultNaming;
    }

    /// <summary>Convenience constructor that configures over real ONVIF Device Management using the
    /// given credentials (HTTP Digest).</summary>
    public BulkConfigurationService(SurveilConfig config, string username, string password,
        Func<CameraConfigurationTarget, (string Name, string? Hostname)>? naming = null)
        : this(config,
            endpoint => new OnvifDeviceConfigurator(new OnvifDeviceClient(endpoint, username, password)),
            endpoint => new OnvifVideoConfigurator(endpoint, username, password),
            naming) { }

    /// <summary>Builds targets from scanned addresses, using the standard device-service endpoint and
    /// locating each address in the site map.</summary>
    public IReadOnlyList<CameraConfigurationTarget> TargetsFromAddresses(IEnumerable<IPAddress> addresses) =>
        addresses.Select(address => Locate(address, DefaultDeviceEndpoint(address))).ToArray();

    /// <summary>Builds targets from WS-Discovery responders, preferring the advertised XAddr endpoint.</summary>
    public IReadOnlyList<CameraConfigurationTarget> TargetsFromResponders(IEnumerable<WsDiscoveryResponder> responders) =>
        responders.Select(responder => Locate(responder.Ip,
            EndpointFromXAddresses(responder.XAddresses) ?? DefaultDeviceEndpoint(responder.Ip))).ToArray();

    /// <summary>Builds targets from (address, advertised-endpoint) pairs — the endpoint each camera
    /// reported via WS-Discovery. A null endpoint falls back to the standard device-service path.</summary>
    public IReadOnlyList<CameraConfigurationTarget> TargetsFrom(IEnumerable<(IPAddress Address, Uri? Endpoint)> items) =>
        items.Select(item => Locate(item.Address, item.Endpoint ?? DefaultDeviceEndpoint(item.Address))).ToArray();

    /// <summary>Derives the planned name/hostname for each target — call this to preview a batch
    /// before applying it.</summary>
    public IReadOnlyList<CameraConfigurationPlan> Plan(IEnumerable<CameraConfigurationTarget> targets,
        bool includeUnknownLocation = false) =>
        targets.Where(target => target.LocationKnown || includeUnknownLocation).Select(PlanFor).ToArray();

    public CameraConfigurationPlan PlanFor(CameraConfigurationTarget target)
    {
        var (name, hostname) = naming(target);
        return new CameraConfigurationPlan(target, name, hostname);
    }

    public async Task<IReadOnlyList<CameraConfigurationResult>> ConfigureAsync(
        IEnumerable<CameraConfigurationTarget> targets, BulkConfigurationOptions? options = null,
        IProgress<BulkConfigurationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new BulkConfigurationOptions();
        var plans = Plan(targets, includeUnknownLocation: !options.SkipUnknownLocation);
        var results = new CameraConfigurationResult[plans.Count];
        var sync = new object();
        var completed = 0;
        var succeeded = 0;
        var failed = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));

        var work = plans.Select(async (plan, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await ConfigureOneAsync(plan, options, cancellationToken);
                lock (sync)
                {
                    results[index] = result;
                    completed++;
                    if (result.Success) succeeded++; else failed++;
                    progress?.Report(new BulkConfigurationProgress(completed, plans.Count, succeeded, failed));
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(work);
        return results;
    }

    private async Task<CameraConfigurationResult> ConfigureOneAsync(
        CameraConfigurationPlan plan, BulkConfigurationOptions options, CancellationToken cancellationToken)
    {
        var target = plan.Target;
        var steps = new List<string>();
        var videoOutcomes = new List<VideoEncoderOutcome>();
        try
        {
            var name = options.SetName ? (string.IsNullOrWhiteSpace(options.Name) ? plan.Name : options.Name!.Trim()) : null;
            var hostname = options.SetHostname ? (string.IsNullOrWhiteSpace(options.Hostname) ? plan.Hostname : options.Hostname!.Trim()) : null;
            var setNtp = options.SetNtp;  // applies NTP mode + zone (a null zone means this computer's)
            var setServer = !string.IsNullOrWhiteSpace(options.NtpServer);

            if (name is not null || hostname is not null || setNtp || setServer)
            {
                if (options.DryRun)
                {
                    if (name is not null) steps.Add($"would set name={name}");
                    if (hostname is not null) steps.Add($"would set hostname={hostname}");
                    if (setNtp) steps.Add(options.NtpPosixTimeZone is null ? "would set ntp=computer-zone" : $"would set ntp={options.NtpPosixTimeZone}");
                    if (setServer) steps.Add($"would set ntp server={options.NtpServer!.Trim()}");
                }
                else
                {
                    using var device = deviceFactory(target.DeviceEndpoint);
                    if (name is not null)
                    {
                        await device.SetNameAsync(name, cancellationToken);
                        steps.Add($"name={name}");
                    }
                    if (hostname is not null)
                    {
                        var reboot = await device.SetHostnameAsync(hostname, cancellationToken);
                        steps.Add(reboot ? $"hostname={hostname} (reboot required)" : $"hostname={hostname}");
                    }
                    if (setNtp)
                    {
                        await device.SetNtpAsync(options.NtpPosixTimeZone, cancellationToken);
                        steps.Add(options.NtpPosixTimeZone is null ? "ntp=computer-zone" : $"ntp={options.NtpPosixTimeZone}");
                    }
                    if (setServer)
                    {
                        await device.SetNtpServerAsync(options.NtpServer!.Trim(), cancellationToken);
                        steps.Add($"ntp server={options.NtpServer!.Trim()}");
                    }
                }
            }
            var changeVideo = options.VideoCodec is not null || options.VideoResolution is not null
                || options.VideoFrameRate is not null || options.VideoBitrateKbps is not null;
            if (changeVideo && videoFactory is not null)
            {
                using var video = videoFactory(target.DeviceEndpoint);
                foreach (var encoder in await video.GetEncodersAsync(cancellationToken))
                {
                    // Pick the codec to work in: the requested one if the encoder can switch to it, else current.
                    var codec = ChooseCodec(encoder, options.VideoCodec is null ? null : new[] { options.VideoCodec });
                    if (codec is null || codec.Resolutions.Count == 0) continue;
                    var switchTo = options.VideoCodec is not null && encoder.CanSwitchCodec &&
                                   NormalizeCodec(codec.Codec) != NormalizeCodec(encoder.CurrentCodec) ? codec.Codec : null;

                    // Resolution: cap to the requested size, else keep the current one (clamped into the codec).
                    OnvifResolution resolution;
                    if (options.VideoResolution is { } wanted)
                    {
                        var within = codec.Resolutions.Where(r => Area(r) <= Area(wanted)).ToList();
                        resolution = within.Count > 0 ? within.OrderByDescending(Area).First() : codec.Resolutions.OrderBy(Area).First();
                    }
                    else
                    {
                        resolution = codec.Resolutions.Contains(encoder.CurrentResolution)
                            ? encoder.CurrentResolution : codec.Resolutions.OrderByDescending(Area).First();
                    }

                    // Frame rate + bitrate: clamp the request into what this codec/encoder supports;
                    // null means "leave unchanged" (the media client won't touch that field).
                    float? frameRate = options.VideoFrameRate is { } wantFps ? ClampFrameRate(codec, wantFps) : null;
                    int? bitrate = options.VideoBitrateKbps is { } wantBps ? ClampBitrate(codec, wantBps) : null;

                    var applied = options.DryRun
                        ? new VideoEncoderState(switchTo ?? encoder.CurrentCodec, resolution,
                            frameRate ?? encoder.CurrentFrameRate, bitrate ?? encoder.CurrentBitrate)
                        : await video.ApplyAsync(encoder.ConfigurationToken, switchTo, resolution, frameRate, bitrate, cancellationToken);
                    var outcome = new VideoEncoderOutcome(encoder.ConfigurationToken,
                        options.VideoCodec ?? applied.Codec, resolution, frameRate,
                        applied.Codec, applied.Resolution, applied.FrameRate,
                        options.VideoBitrateKbps, applied.Bitrate);
                    videoOutcomes.Add(outcome);
                    steps.Add($"{(options.DryRun ? "would set " : "")}video[{encoder.ConfigurationToken}]={applied.Codec} {Format(applied.Resolution)}{FrameRateSuffix(applied.FrameRate)}{BitrateSuffix(applied.Bitrate)}"
                        + (outcome.CodecFallback ? " (codec fallback)" : "")
                        + (!options.DryRun && outcome.ClampedByCamera ? " (camera-limited)" : ""));
                }
            }
            return new CameraConfigurationResult(target.Address, target.Site, target.Area, true, steps, null, videoOutcomes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new CameraConfigurationResult(target.Address, target.Site, target.Area, false, steps, Describe(error), videoOutcomes);
        }
    }

    private CameraConfigurationTarget Locate(IPAddress address, Uri endpoint)
    {
        var location = NetworkRanges.Locate(config, address);
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

    /// <summary>Lower-cases to a single DNS-safe label: non-alphanumerics become hyphens, runs collapse,
    /// and the result is trimmed and capped at the 63-character label limit.</summary>
    internal static string Slug(string value)
    {
        var slug = new string(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length > 63 ? slug[..63].Trim('-') : slug;
    }

    /// <summary>Pick the codec to configure: the first preferred codec the camera supports (only when
    /// it can switch), otherwise its current codec. Null when the encoder reports no codecs at all.</summary>
    private static CodecCapability? ChooseCodec(VideoEncoderInfo encoder, IReadOnlyList<string>? preferred)
    {
        if (encoder.Codecs.Count == 0) return null;
        if (encoder.CanSwitchCodec && preferred is { Count: > 0 })
            foreach (var want in preferred)
            {
                var match = encoder.Codecs.FirstOrDefault(c => NormalizeCodec(c.Codec) == NormalizeCodec(want));
                if (match is not null) return match;
            }
        return encoder.Codecs.FirstOrDefault(c => NormalizeCodec(c.Codec) == NormalizeCodec(encoder.CurrentCodec))
            ?? encoder.Codecs[0];
    }

    /// <summary>Compare codec names loosely: "video/H265", "H.265", "h265" all match.</summary>
    internal static string NormalizeCodec(string codec) => codec
        .Replace("video/", "", StringComparison.OrdinalIgnoreCase)
        .Replace(".", "", StringComparison.Ordinal)
        .ToUpperInvariant();

    /// <summary>Pick the frame rate to write: an exact supported match, else the fastest supported
    /// rate no quicker than requested, else the slowest the encoder offers. Passes the request
    /// through unchanged when the encoder advertises no discrete rates.</summary>
    private static float ClampFrameRate(CodecCapability codec, float requested)
    {
        if (codec.FrameRates.Count == 0) return requested;
        var exact = codec.FrameRates.FirstOrDefault(rate => Math.Abs(rate - requested) < 0.001f);
        if (exact > 0.001f) return exact;
        var atOrBelow = codec.FrameRates.Where(rate => rate <= requested).ToList();
        return atOrBelow.Count > 0 ? atOrBelow.Max() : codec.FrameRates.Min();
    }

    /// <summary>Clamp the requested bitrate (kbps) into the codec's advertised range so the media
    /// client's validation won't reject it; passes it through when no range is advertised.</summary>
    private static int ClampBitrate(CodecCapability codec, int requested) =>
        codec.Bitrate is { } range ? Math.Clamp(requested, range.Minimum, range.Maximum) : requested;

    private static long Area(OnvifResolution resolution) => (long)resolution.Width * resolution.Height;
    private static string Format(OnvifResolution resolution) => $"{resolution.Width}x{resolution.Height}";
    private static string FrameRateSuffix(float? frameRate) => frameRate is { } fps ? $"@{fps:0.##}fps" : "";
    private static string BitrateSuffix(int? bitrate) => bitrate is { } kbps ? $" {kbps}kbps" : "";

    private static Uri DefaultDeviceEndpoint(IPAddress address) =>
        new UriBuilder("http", address.ToString()) { Path = DeviceServicePath }.Uri;

    private static Uri? EndpointFromXAddresses(string xAddresses)
    {
        var first = xAddresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Uri.TryCreate(first, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string Describe(Exception error) => error switch
    {
        OnvifException { IsAuthenticationFailure: true } => "authentication failed (check ONVIF credentials)",
        OnvifException onvif => onvif.Message,
        _ => error.Message,
    };
}

/// <summary>Default <see cref="IConfigurableDevice"/> backed by ONVIF Device Management. Owns the
/// underlying client and disposes it after the camera is configured.</summary>
internal sealed class OnvifDeviceConfigurator(OnvifDeviceClient client) : IConfigurableDevice
{
    public Task SetNameAsync(string name, CancellationToken cancellationToken) =>
        client.SetCameraNameAsync(name, cancellationToken);

    public async Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken) =>
        (await client.SetAndVerifyHostnameAsync(hostname, cancellationToken)).RebootRequired;

    public Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken) =>
        posixTimeZone is null
            ? client.SetNtpFromComputerTimeZoneAsync(cancellationToken)
            : client.SetNtpTimeAsync(posixTimeZone, true, cancellationToken);

    public Task SetNtpServerAsync(string server, CancellationToken cancellationToken) =>
        client.SetNtpServerAsync(server, cancellationToken);

    public void Dispose() => client.Dispose();
}

/// <summary>Default <see cref="IConfigurableVideo"/>. Connects over ONVIF, discovers each encoder's
/// supported resolutions, and applies changes through the media client's capability validation —
/// which rejects any resolution the camera did not advertise.</summary>
internal sealed class OnvifVideoConfigurator : IConfigurableVideo
{
    private readonly Uri deviceEndpoint;
    private readonly OnvifCameraConnector connector;
    private OnvifCameraConnection? connection;

    public OnvifVideoConfigurator(Uri deviceEndpoint, string username, string password)
    {
        this.deviceEndpoint = deviceEndpoint;
        connector = new OnvifCameraConnector(username, password);
    }

    public async Task<IReadOnlyList<VideoEncoderInfo>> GetEncodersAsync(CancellationToken cancellationToken)
    {
        connection ??= await connector.ConnectAsync(deviceEndpoint, null, cancellationToken);
        var canSwitchCodec = connection.Video.Generation == OnvifMediaGeneration.Media2;
        var encoders = new List<VideoEncoderInfo>();
        foreach (var config in await connection.Video.GetVideoEncoderConfigurationsAsync(cancellationToken: cancellationToken))
        {
            var options = await connection.Video.GetVideoEncoderConfigurationOptionsAsync(
                config.Token, cancellationToken: cancellationToken);
            var codecs = options
                .Select(option => new CodecCapability(option.Encoding,
                    option.Resolutions.Distinct().ToArray(), option.FrameRates.Distinct().ToArray(), option.Bitrate))
                .ToArray();
            encoders.Add(new VideoEncoderInfo(
                config.Token, canSwitchCodec, config.Encoding, config.Resolution, config.FrameRateLimit,
                codecs, config.BitrateLimit));
        }
        return encoders;
    }

    public async Task<VideoEncoderState> ApplyAsync(string configurationToken, string? codec,
        OnvifResolution resolution, float? frameRate, int? bitrateKbps, CancellationToken cancellationToken)
    {
        await connection!.Video.UpdateVideoEncoderAsync(configurationToken, resolution.Width, resolution.Height,
            framesPerSecond: frameRate, bitrateKbps: bitrateKbps, encoding: codec, cancellationToken: cancellationToken);
        var applied = (await connection.Video.GetVideoEncoderConfigurationsAsync(
            configurationToken, cancellationToken: cancellationToken)).Single();
        return new VideoEncoderState(applied.Encoding, applied.Resolution, applied.FrameRateLimit, applied.BitrateLimit);
    }

    public void Dispose() => connection?.Dispose();
}
