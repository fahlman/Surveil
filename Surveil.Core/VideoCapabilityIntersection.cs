namespace Surveil.Core;

public sealed record SharedVideoCapabilities(
    bool EveryCameraHasVideo,
    IReadOnlyList<string> Codecs,
    IReadOnlyList<OnvifResolution> Resolutions,
    IReadOnlyList<float> FrameRates,
    OnvifRange<int>? Bitrate);

/// <summary>Computes choices every selected encoder can honor without losing codec relationships.</summary>
public static class VideoCapabilityIntersection
{
    public static SharedVideoCapabilities ForSelection(
        IEnumerable<IReadOnlyList<VideoEncoderInfo>> cameraEncoders, string? requestedCodec = null)
    {
        var cameras = cameraEncoders.Select(encoders => encoders.ToList()).ToList();
        var everyCameraHasVideo = cameras.Count > 0 && cameras.All(camera => camera.Count > 0);
        if (!everyCameraHasVideo)
            return new SharedVideoCapabilities(false, [], [], [], null);

        var encoders = cameras.SelectMany(camera => camera).ToList();
        var codecs = Intersect(encoders.Select(SupportedCodecNames), StringComparer.OrdinalIgnoreCase)
            .OrderBy(codec => codec, StringComparer.OrdinalIgnoreCase).ToList();

        var effective = encoders.Select(encoder => CapabilityFor(encoder, requestedCodec)).ToList();
        if (effective.Any(capability => capability is null))
            return new SharedVideoCapabilities(true, codecs, [], [], null);

        var capabilities = effective.OfType<CodecCapability>().ToList();
        var resolutions = Intersect(capabilities.Select(capability => capability.Resolutions))
            .OrderByDescending(resolution => (long)resolution.Width * resolution.Height).ToList();
        var frameRates = Intersect(capabilities.Select(capability => capability.FrameRates))
            .OrderByDescending(rate => rate).ToList();
        var bitrate = IntersectBitrate(capabilities);
        return new SharedVideoCapabilities(true, codecs, resolutions, frameRates, bitrate);
    }

    private static IEnumerable<string> SupportedCodecNames(VideoEncoderInfo encoder) =>
        encoder.CanSwitchCodec
            ? encoder.Codecs.Select(capability => VideoConfigurationSelector.NormalizeCodec(capability.Codec))
            : [VideoConfigurationSelector.NormalizeCodec(encoder.CurrentCodec)];

    private static CodecCapability? CapabilityFor(VideoEncoderInfo encoder, string? requestedCodec)
    {
        var codec = requestedCodec ?? encoder.CurrentCodec;
        if (!encoder.CanSwitchCodec &&
            VideoConfigurationSelector.NormalizeCodec(codec) != VideoConfigurationSelector.NormalizeCodec(encoder.CurrentCodec))
            return null;
        return encoder.Codecs.FirstOrDefault(capability =>
                   VideoConfigurationSelector.NormalizeCodec(capability.Codec) ==
                   VideoConfigurationSelector.NormalizeCodec(codec))
               ?? (requestedCodec is null ? encoder.Codecs.FirstOrDefault() : null);
    }

    private static OnvifRange<int>? IntersectBitrate(IReadOnlyList<CodecCapability> capabilities)
    {
        var ranges = capabilities.Select(capability => capability.Bitrate).ToList();
        if (ranges.Any(range => range is null)) return null;
        var minimum = ranges.OfType<OnvifRange<int>>().Max(range => range.Minimum);
        var maximum = ranges.OfType<OnvifRange<int>>().Min(range => range.Maximum);
        return minimum <= maximum ? new OnvifRange<int>(minimum, maximum) : null;
    }

    private static IReadOnlyList<T> Intersect<T>(IEnumerable<IEnumerable<T>> sets,
        IEqualityComparer<T>? comparer = null)
    {
        HashSet<T>? result = null;
        foreach (var set in sets)
        {
            if (result is null) result = new HashSet<T>(set, comparer);
            else result.IntersectWith(set);
        }
        return result?.ToList() ?? [];
    }
}
