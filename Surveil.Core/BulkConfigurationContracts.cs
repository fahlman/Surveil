using System.Net;

namespace Surveil.Core;

/// <summary>A camera to be configured, tagged with where it lives per the site map.</summary>
public sealed record CameraConfigurationTarget(IPAddress Address, Uri DeviceEndpoint, string Site, string Area)
{
    public bool LocationKnown => Site.Length > 0;
}

/// <summary>The identity derived for a camera before any device capabilities need to be read.</summary>
public sealed record CameraConfigurationPlan(CameraConfigurationTarget Target, string Name, string? Hostname);

public sealed record CameraConfigurationResult(
    IPAddress Address, string Site, string Area,
    bool Success, IReadOnlyList<string> Steps, string? Error,
    IReadOnlyList<VideoEncoderOutcome> Video);

public readonly record struct BulkConfigurationProgress(int Completed, int Total, int Succeeded, int Failed);

/// <summary>Typed configuration intent shared by preview, confirmation, and execution.</summary>
public sealed record BulkConfigurationOptions
{
    public bool SetName { get; init; } = true;
    public bool SetHostname { get; init; } = true;
    public bool SetNtp { get; init; } = true;
    public string? Name { get; init; }
    public string? Hostname { get; init; }
    public string? NtpPosixTimeZone { get; init; }
    public string? NtpServer { get; init; }
    public int MaxConcurrency { get; init; } = 8;
    public bool SkipUnknownLocation { get; init; } = true;
    public string? VideoCodec { get; init; }
    public OnvifResolution? VideoResolution { get; init; }
    public float? VideoFrameRate { get; init; }
    public int? VideoBitrateKbps { get; init; }
    public bool DryRun { get; init; }

    public bool ChangesDevice => SetName || SetHostname || SetNtp || !string.IsNullOrWhiteSpace(NtpServer);
    public bool ChangesVideo => VideoCodec is not null || VideoResolution is not null ||
                                VideoFrameRate is not null || VideoBitrateKbps is not null;
    public bool HasChanges => ChangesDevice || ChangesVideo;
}

public interface IConfigurableDevice : IDisposable
{
    Task SetNameAsync(string name, CancellationToken cancellationToken);
    Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken);
    Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken);
    Task SetNtpServerAsync(string server, CancellationToken cancellationToken);
}

/// <summary>The valid combinations for one codec on one encoder.</summary>
public sealed record CodecCapability(
    string Codec, IReadOnlyList<OnvifResolution> Resolutions, IReadOnlyList<float> FrameRates,
    OnvifRange<int>? Bitrate = null);

/// <summary>A capability-preserving encoder description. Codec-specific choices are never flattened.</summary>
public sealed record VideoEncoderInfo(
    string ConfigurationToken, bool CanSwitchCodec,
    string CurrentCodec, OnvifResolution CurrentResolution, float? CurrentFrameRate,
    IReadOnlyList<CodecCapability> Codecs, int? CurrentBitrate = null);

public sealed record VideoEncoderState(
    string Codec, OnvifResolution Resolution, float? FrameRate, int? Bitrate = null);

/// <summary>The exact typed operation selected for one encoder before it is written.</summary>
public sealed record VideoEncoderPlan(
    string ConfigurationToken, string RequestedCodec, string? CodecToWrite,
    OnvifResolution Resolution, float? FrameRate, int? BitrateKbps,
    VideoEncoderState PreviewState);

public sealed record VideoEncoderOutcome(
    string ConfigurationToken,
    string RequestedCodec, OnvifResolution RequestedResolution, float? RequestedFrameRate,
    string AppliedCodec, OnvifResolution AppliedResolution, float? AppliedFrameRate,
    int? RequestedBitrate = null, int? AppliedBitrate = null)
{
    public bool CodecFallback =>
        VideoConfigurationSelector.NormalizeCodec(RequestedCodec) !=
        VideoConfigurationSelector.NormalizeCodec(AppliedCodec);

    public bool ClampedByCamera =>
        AppliedResolution != RequestedResolution ||
        (RequestedFrameRate is { } requestedFps && AppliedFrameRate is { } appliedFps && appliedFps < requestedFps) ||
        (RequestedBitrate is { } requestedBps && AppliedBitrate is { } appliedBps && appliedBps < requestedBps);
}

public interface IConfigurableVideo : IDisposable
{
    Task<IReadOnlyList<VideoEncoderInfo>> GetEncodersAsync(CancellationToken cancellationToken);
    Task<VideoEncoderState> ApplyAsync(string configurationToken, string? codec, OnvifResolution resolution,
        float? frameRate, int? bitrateKbps, CancellationToken cancellationToken);
}
