namespace Surveil.Core;

/// <summary>Pure selection rules that turn typed intent and encoder capabilities into an exact plan.</summary>
public static class VideoConfigurationSelector
{
    public static VideoEncoderPlan? Select(VideoEncoderInfo encoder, BulkConfigurationOptions options)
    {
        var codec = ChooseCodec(encoder, options.VideoCodec);
        if (codec is null || codec.Resolutions.Count == 0) return null;

        var switchTo = options.VideoCodec is not null && encoder.CanSwitchCodec &&
                       NormalizeCodec(codec.Codec) != NormalizeCodec(encoder.CurrentCodec)
            ? codec.Codec
            : null;

        var resolution = SelectResolution(codec, encoder.CurrentResolution, options.VideoResolution);
        float? frameRate = options.VideoFrameRate is { } requestedFps ? ClampFrameRate(codec, requestedFps) : null;
        int? bitrate = options.VideoBitrateKbps is { } requestedBitrate ? ClampBitrate(codec, requestedBitrate) : null;
        var preview = new VideoEncoderState(switchTo ?? encoder.CurrentCodec, resolution,
            frameRate ?? encoder.CurrentFrameRate, bitrate ?? encoder.CurrentBitrate);
        return new VideoEncoderPlan(encoder.ConfigurationToken, options.VideoCodec ?? preview.Codec, switchTo,
            resolution, frameRate, bitrate, preview);
    }

    public static string NormalizeCodec(string codec) => codec
        .Replace("video/", "", StringComparison.OrdinalIgnoreCase)
        .Replace(".", "", StringComparison.Ordinal)
        .ToUpperInvariant();

    private static CodecCapability? ChooseCodec(VideoEncoderInfo encoder, string? requested)
    {
        if (encoder.Codecs.Count == 0) return null;
        if (encoder.CanSwitchCodec && requested is not null)
        {
            var match = encoder.Codecs.FirstOrDefault(c => NormalizeCodec(c.Codec) == NormalizeCodec(requested));
            if (match is not null) return match;
        }
        return encoder.Codecs.FirstOrDefault(c => NormalizeCodec(c.Codec) == NormalizeCodec(encoder.CurrentCodec))
               ?? encoder.Codecs[0];
    }

    private static OnvifResolution SelectResolution(CodecCapability codec, OnvifResolution current,
        OnvifResolution? requested)
    {
        if (requested is { } wanted)
        {
            var within = codec.Resolutions.Where(r => Area(r) <= Area(wanted)).ToList();
            return within.Count > 0 ? within.MaxBy(Area)! : codec.Resolutions.MinBy(Area)!;
        }
        return codec.Resolutions.Contains(current) ? current : codec.Resolutions.MaxBy(Area)!;
    }

    private static float ClampFrameRate(CodecCapability codec, float requested)
    {
        if (codec.FrameRates.Count == 0) return requested;
        var exact = codec.FrameRates.FirstOrDefault(rate => Math.Abs(rate - requested) < 0.001f);
        if (exact > 0.001f) return exact;
        var atOrBelow = codec.FrameRates.Where(rate => rate <= requested).ToList();
        return atOrBelow.Count > 0 ? atOrBelow.Max() : codec.FrameRates.Min();
    }

    private static int ClampBitrate(CodecCapability codec, int requested) =>
        codec.Bitrate is { } range ? Math.Clamp(requested, range.Minimum, range.Maximum) : requested;

    private static long Area(OnvifResolution resolution) => (long)resolution.Width * resolution.Height;
}
