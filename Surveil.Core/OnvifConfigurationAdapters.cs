namespace Surveil.Core;

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
            var codecs = options.Select(option => new CodecCapability(option.Encoding,
                option.Resolutions.Distinct().ToArray(), option.FrameRates.Distinct().ToArray(), option.Bitrate)).ToArray();
            encoders.Add(new VideoEncoderInfo(config.Token, canSwitchCodec, config.Encoding, config.Resolution,
                config.FrameRateLimit, codecs, config.BitrateLimit));
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
