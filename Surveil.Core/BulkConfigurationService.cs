using System.Net;

namespace Surveil.Core;

/// <summary>Executes typed configuration plans concurrently and reports truthful per-camera results.</summary>
public sealed class BulkConfigurationService
{
    private readonly CameraConfigurationPlanner planner;
    private readonly Func<Uri, IConfigurableDevice> deviceFactory;
    private readonly Func<Uri, IConfigurableVideo>? videoFactory;

    public BulkConfigurationService(SurveilConfig config, Func<Uri, IConfigurableDevice> deviceFactory,
        Func<Uri, IConfigurableVideo>? videoFactory = null,
        Func<CameraConfigurationTarget, (string Name, string? Hostname)>? naming = null)
    {
        planner = new CameraConfigurationPlanner(config, naming);
        this.deviceFactory = deviceFactory;
        this.videoFactory = videoFactory;
    }

    public BulkConfigurationService(SurveilConfig config, string username, string password,
        Func<CameraConfigurationTarget, (string Name, string? Hostname)>? naming = null)
        : this(config,
            endpoint => new OnvifDeviceConfigurator(new OnvifDeviceClient(endpoint, username, password)),
            endpoint => new OnvifVideoConfigurator(endpoint, username, password), naming)
    { }

    public IReadOnlyList<CameraConfigurationTarget> TargetsFromAddresses(IEnumerable<IPAddress> addresses) =>
        planner.TargetsFromAddresses(addresses);

    public IReadOnlyList<CameraConfigurationTarget> TargetsFromResponders(IEnumerable<WsDiscoveryResponder> responders) =>
        planner.TargetsFromResponders(responders);

    public IReadOnlyList<CameraConfigurationTarget> TargetsFrom(IEnumerable<(IPAddress Address, Uri? Endpoint)> items) =>
        planner.TargetsFrom(items);

    public IReadOnlyList<CameraConfigurationPlan> Plan(IEnumerable<CameraConfigurationTarget> targets,
        bool includeUnknownLocation = false) => planner.Plan(targets, includeUnknownLocation);

    public CameraConfigurationPlan PlanFor(CameraConfigurationTarget target) => planner.PlanFor(target);

    internal static string Slug(string value) => CameraConfigurationPlanner.Slug(value);
    internal static string NormalizeCodec(string codec) => VideoConfigurationSelector.NormalizeCodec(codec);

    public async Task<IReadOnlyList<CameraConfigurationResult>> ConfigureAsync(
        IEnumerable<CameraConfigurationTarget> targets, BulkConfigurationOptions? options = null,
        IProgress<BulkConfigurationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new BulkConfigurationOptions();
        var plans = Plan(targets, includeUnknownLocation: !options.SkipUnknownLocation);
        var results = new CameraConfigurationResult[plans.Count];
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
                results[index] = result;
                var done = Interlocked.Increment(ref completed);
                if (result.Success) Interlocked.Increment(ref succeeded); else Interlocked.Increment(ref failed);
                progress?.Report(new BulkConfigurationProgress(done, plans.Count,
                    Volatile.Read(ref succeeded), Volatile.Read(ref failed)));
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
        CameraConfigurationPlan identity, BulkConfigurationOptions options, CancellationToken cancellationToken)
    {
        var target = identity.Target;
        var steps = new List<string>();
        var videoOutcomes = new List<VideoEncoderOutcome>();
        try
        {
            await ConfigureDeviceAsync(identity, options, steps, cancellationToken);
            if (options.ChangesVideo && videoFactory is not null)
                await ConfigureVideoAsync(target, options, steps, videoOutcomes, cancellationToken);
            return new CameraConfigurationResult(target.Address, target.Site, target.Area, true, steps, null, videoOutcomes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new CameraConfigurationResult(target.Address, target.Site, target.Area, false, steps,
                Describe(error), videoOutcomes);
        }
    }

    private async Task ConfigureDeviceAsync(CameraConfigurationPlan identity, BulkConfigurationOptions options,
        List<string> steps, CancellationToken cancellationToken)
    {
        var name = options.SetName
            ? (string.IsNullOrWhiteSpace(options.Name) ? identity.Name : options.Name.Trim()) : null;
        var hostname = options.SetHostname
            ? (string.IsNullOrWhiteSpace(options.Hostname) ? identity.Hostname : options.Hostname.Trim()) : null;
        var setServer = !string.IsNullOrWhiteSpace(options.NtpServer);
        if (name is null && hostname is null && !options.SetNtp && !setServer) return;

        if (options.DryRun)
        {
            if (name is not null) steps.Add($"would set name={name}");
            if (hostname is not null) steps.Add($"would set hostname={hostname}");
            if (options.SetNtp) steps.Add(options.NtpPosixTimeZone is null
                ? "would set ntp=computer-zone" : $"would set ntp={options.NtpPosixTimeZone}");
            if (setServer) steps.Add($"would set ntp server={options.NtpServer!.Trim()}");
            return;
        }

        using var device = deviceFactory(identity.Target.DeviceEndpoint);
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
        if (options.SetNtp)
        {
            await device.SetNtpAsync(options.NtpPosixTimeZone, cancellationToken);
            steps.Add(options.NtpPosixTimeZone is null ? "ntp=computer-zone" : $"ntp={options.NtpPosixTimeZone}");
        }
        if (setServer)
        {
            await device.SetNtpServerAsync(options.NtpServer!.Trim(), cancellationToken);
            steps.Add($"ntp server={options.NtpServer.Trim()}");
        }
    }

    private async Task ConfigureVideoAsync(CameraConfigurationTarget target, BulkConfigurationOptions options,
        List<string> steps, List<VideoEncoderOutcome> outcomes, CancellationToken cancellationToken)
    {
        using var video = videoFactory!(target.DeviceEndpoint);
        foreach (var encoder in await video.GetEncodersAsync(cancellationToken))
        {
            var plan = VideoConfigurationSelector.Select(encoder, options);
            if (plan is null) continue;
            var applied = options.DryRun
                ? plan.PreviewState
                : await video.ApplyAsync(plan.ConfigurationToken, plan.CodecToWrite, plan.Resolution,
                    plan.FrameRate, plan.BitrateKbps, cancellationToken);
            var outcome = new VideoEncoderOutcome(plan.ConfigurationToken, plan.RequestedCodec,
                plan.Resolution, plan.FrameRate, applied.Codec, applied.Resolution, applied.FrameRate,
                options.VideoBitrateKbps, applied.Bitrate);
            outcomes.Add(outcome);
            steps.Add(FormatVideoStep(options.DryRun, applied, outcome));
        }
    }

    private static string FormatVideoStep(bool dryRun, VideoEncoderState applied, VideoEncoderOutcome outcome) =>
        $"{(dryRun ? "would set " : "")}video[{outcome.ConfigurationToken}]={applied.Codec} " +
        $"{applied.Resolution.Width}x{applied.Resolution.Height}" +
        (applied.FrameRate is { } fps ? $"@{fps:0.##}fps" : "") +
        (applied.Bitrate is { } kbps ? $" {kbps}kbps" : "") +
        (outcome.CodecFallback ? " (codec fallback)" : "") +
        (!dryRun && outcome.ClampedByCamera ? " (camera-limited)" : "");

    private static string Describe(Exception error) => error switch
    {
        OnvifException { IsAuthenticationFailure: true } => "authentication failed (check ONVIF credentials)",
        OnvifException onvif => onvif.Message,
        _ => error.Message,
    };
}
