using System.Net;

namespace Surveil.Core;

/// <summary>A camera to be provisioned, tagged with where it lives per the building map.</summary>
public sealed record CameraProvisionTarget(IPAddress Address, Uri DeviceEndpoint, string Building, string Area)
{
    /// <summary>True when the address fell inside a configured building range.</summary>
    public bool LocationKnown => Building.Length > 0;
}

/// <summary>The concrete identity to push to one camera, derived from its location.</summary>
public sealed record CameraProvisionPlan(CameraProvisionTarget Target, string Name, string? Hostname);

/// <summary>Per-camera outcome. <see cref="Steps"/> lists what was applied and verified.</summary>
public sealed record CameraProvisionResult(
    IPAddress Address, string Building, string Area,
    bool Success, IReadOnlyList<string> Steps, string? Error);

public readonly record struct BulkProvisionProgress(int Completed, int Total, int Succeeded, int Failed);

/// <summary>Which identity fields to write, plus batch behavior.</summary>
public sealed record BulkProvisionOptions
{
    public bool SetName { get; init; } = true;
    public bool SetHostname { get; init; } = true;
    public bool SetNtp { get; init; } = true;
    /// <summary>Null derives the POSIX zone from this computer's time zone.</summary>
    public string? NtpPosixTimeZone { get; init; }
    public int MaxConcurrency { get; init; } = 8;
    /// <summary>Skip cameras whose address is not inside any configured building range.</summary>
    public bool SkipUnknownLocation { get; init; } = true;
}

/// <summary>A single camera's configuration surface, abstracted so the batch orchestration is
/// testable without a live camera. The default implementation drives ONVIF Device Management,
/// whose setters read the value back to confirm it took.</summary>
public interface IProvisionableDevice : IDisposable
{
    Task SetNameAsync(string name, CancellationToken cancellationToken);
    /// <returns>True when the camera reported that a reboot is required.</returns>
    Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken);
    Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken);
}

/// <summary>Bulk-provisions cameras over ONVIF as an iCT replacement: it derives each camera's
/// name and hostname from the building map, applies them (plus NTP), verifies via read-back, and
/// returns a per-camera pass/fail report instead of a silent GUI you have to trust.</summary>
public sealed class BulkProvisioningService
{
    private const string DeviceServicePath = "/onvif/device_service";
    private readonly SurveilConfig config;
    private readonly Func<Uri, IProvisionableDevice> deviceFactory;
    private readonly Func<CameraProvisionTarget, (string Name, string? Hostname)> naming;

    public BulkProvisioningService(SurveilConfig config, Func<Uri, IProvisionableDevice> deviceFactory,
        Func<CameraProvisionTarget, (string Name, string? Hostname)>? naming = null)
    {
        this.config = config;
        this.deviceFactory = deviceFactory;
        this.naming = naming ?? DefaultNaming;
    }

    /// <summary>Convenience constructor that provisions over real ONVIF Device Management using the
    /// given credentials (HTTP Digest).</summary>
    public BulkProvisioningService(SurveilConfig config, string username, string password,
        Func<CameraProvisionTarget, (string Name, string? Hostname)>? naming = null)
        : this(config, endpoint => new OnvifDeviceProvisioner(new OnvifDeviceClient(endpoint, username, password)),
            naming) { }

    /// <summary>Builds targets from scanned addresses, using the standard device-service endpoint and
    /// locating each address in the building map.</summary>
    public IReadOnlyList<CameraProvisionTarget> TargetsFromAddresses(IEnumerable<IPAddress> addresses) =>
        addresses.Select(address => Locate(address, DefaultDeviceEndpoint(address))).ToArray();

    /// <summary>Builds targets from WS-Discovery responders, preferring the advertised XAddr endpoint.</summary>
    public IReadOnlyList<CameraProvisionTarget> TargetsFromResponders(IEnumerable<WsDiscoveryResponder> responders) =>
        responders.Select(responder => Locate(responder.Ip,
            EndpointFromXAddresses(responder.XAddresses) ?? DefaultDeviceEndpoint(responder.Ip))).ToArray();

    /// <summary>Derives the planned name/hostname for each target — call this to preview a batch
    /// before applying it.</summary>
    public IReadOnlyList<CameraProvisionPlan> Plan(IEnumerable<CameraProvisionTarget> targets,
        bool includeUnknownLocation = false) =>
        targets.Where(target => target.LocationKnown || includeUnknownLocation).Select(PlanFor).ToArray();

    public CameraProvisionPlan PlanFor(CameraProvisionTarget target)
    {
        var (name, hostname) = naming(target);
        return new CameraProvisionPlan(target, name, hostname);
    }

    public async Task<IReadOnlyList<CameraProvisionResult>> ProvisionAsync(
        IEnumerable<CameraProvisionTarget> targets, BulkProvisionOptions? options = null,
        IProgress<BulkProvisionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new BulkProvisionOptions();
        var plans = Plan(targets, includeUnknownLocation: !options.SkipUnknownLocation);
        var results = new CameraProvisionResult[plans.Count];
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
                var result = await ProvisionOneAsync(plan, options, cancellationToken);
                lock (sync)
                {
                    results[index] = result;
                    completed++;
                    if (result.Success) succeeded++; else failed++;
                    progress?.Report(new BulkProvisionProgress(completed, plans.Count, succeeded, failed));
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

    private async Task<CameraProvisionResult> ProvisionOneAsync(
        CameraProvisionPlan plan, BulkProvisionOptions options, CancellationToken cancellationToken)
    {
        var target = plan.Target;
        var steps = new List<string>();
        try
        {
            using var device = deviceFactory(target.DeviceEndpoint);
            if (options.SetName)
            {
                await device.SetNameAsync(plan.Name, cancellationToken);
                steps.Add($"name={plan.Name}");
            }
            if (options.SetHostname && plan.Hostname is not null)
            {
                var reboot = await device.SetHostnameAsync(plan.Hostname, cancellationToken);
                steps.Add(reboot ? $"hostname={plan.Hostname} (reboot required)" : $"hostname={plan.Hostname}");
            }
            if (options.SetNtp)
            {
                await device.SetNtpAsync(options.NtpPosixTimeZone, cancellationToken);
                steps.Add(options.NtpPosixTimeZone is null ? "ntp=computer-zone" : $"ntp={options.NtpPosixTimeZone}");
            }
            return new CameraProvisionResult(target.Address, target.Building, target.Area, true, steps, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new CameraProvisionResult(target.Address, target.Building, target.Area, false, steps, Describe(error));
        }
    }

    private CameraProvisionTarget Locate(IPAddress address, Uri endpoint)
    {
        var location = NetworkRanges.Locate(config, address);
        return new CameraProvisionTarget(address, endpoint, location?.Building ?? "", location?.Area ?? "");
    }

    private static (string Name, string? Hostname) DefaultNaming(CameraProvisionTarget target)
    {
        var name = string.Join(" ", new[] { target.Building, target.Area }.Where(part => part.Length > 0));
        if (name.Length == 0) name = target.Address.ToString();
        var lastOctet = target.Address.GetAddressBytes()[^1];
        var hostname = Slug($"{target.Building}-{target.Area}-{lastOctet}");
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

/// <summary>Default <see cref="IProvisionableDevice"/> backed by ONVIF Device Management. Owns the
/// underlying client and disposes it after the camera is provisioned.</summary>
internal sealed class OnvifDeviceProvisioner(OnvifDeviceClient client) : IProvisionableDevice
{
    public Task SetNameAsync(string name, CancellationToken cancellationToken) =>
        client.SetCameraNameAsync(name, cancellationToken);

    public async Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken) =>
        (await client.SetAndVerifyHostnameAsync(hostname, cancellationToken)).RebootRequired;

    public Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken) =>
        posixTimeZone is null
            ? client.SetNtpFromComputerTimeZoneAsync(cancellationToken)
            : client.SetNtpTimeAsync(posixTimeZone, true, cancellationToken);

    public void Dispose() => client.Dispose();
}
