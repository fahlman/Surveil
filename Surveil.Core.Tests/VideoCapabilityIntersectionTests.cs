using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class VideoCapabilityIntersectionTests
{
    private static readonly OnvifResolution Hd = new(1920, 1080);
    private static readonly OnvifResolution Uhd = new(3840, 2160);

    [Fact]
    public void PreservesCodecResolutionRelationships()
    {
        var camera = new[]
        {
            new VideoEncoderInfo("main", true, "H264", Uhd, 30, new[]
            {
                new CodecCapability("H264", new[] { Uhd, Hd }, new[] { 30f }),
                new CodecCapability("H265", new[] { Hd }, new[] { 30f }),
            }),
        };

        var shared = VideoCapabilityIntersection.ForSelection(new[] { camera }, "H265");

        Assert.Contains("H265", shared.Codecs);
        Assert.Equal(new[] { Hd }, shared.Resolutions);
        Assert.DoesNotContain(Uhd, shared.Resolutions);
    }

    [Fact]
    public void RequiresEveryEncoderToHonorAChoice()
    {
        var main = new VideoEncoderInfo("main", true, "H264", Uhd, 30, new[]
        {
            new CodecCapability("H264", new[] { Uhd, Hd }, new[] { 30f }),
            new CodecCapability("H265", new[] { Hd }, new[] { 30f }),
        });
        var legacy = new VideoEncoderInfo("sub", false, "H264", Hd, 15, new[]
        {
            new CodecCapability("H264", new[] { Hd }, new[] { 15f }),
            new CodecCapability("H265", new[] { Hd }, new[] { 15f }),
        });

        var shared = VideoCapabilityIntersection.ForSelection(new[] { new[] { main, legacy } });

        Assert.Equal(new[] { "H264" }, shared.Codecs);
        Assert.Equal(new[] { Hd }, shared.Resolutions);
    }

    [Fact]
    public void IntersectsBitrateForTheEffectiveCodec()
    {
        var first = Encoder("one", new OnvifRange<int>(128, 8192));
        var second = Encoder("two", new OnvifRange<int>(512, 4096));

        var shared = VideoCapabilityIntersection.ForSelection(new[] { new[] { first }, new[] { second } });

        Assert.Equal(new OnvifRange<int>(512, 4096), shared.Bitrate);
    }

    private static VideoEncoderInfo Encoder(string token, OnvifRange<int> bitrate) =>
        new(token, true, "H264", Hd, 30,
            new[] { new CodecCapability("H264", new[] { Hd }, new[] { 30f }, bitrate) });
}
