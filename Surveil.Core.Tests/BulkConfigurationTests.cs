using System.Collections.Concurrent;
using System.Net;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class BulkConfigurationTests
{
    [Fact]
    public async Task DerivesIdentityFromSiteMapThenAppliesAndVerifies()
    {
        var config = Config("Example Hall", "Second Floor", "10.200.62.0/24");
        var devices = new ConcurrentDictionary<string, RecordingDevice>();
        var service = new BulkConfigurationService(config, endpoint => devices[endpoint.Host] = new RecordingDevice());

        var targets = service.TargetsFromAddresses([IPAddress.Parse("10.200.62.137")]);

        var plan = Assert.Single(service.Plan(targets));
        Assert.Equal("Example Hall Second Floor", plan.Name);
        Assert.Equal("example-hall-second-floor-137", plan.Hostname);

        var result = Assert.Single(await service.ConfigureAsync(targets));
        Assert.True(result.Success);
        Assert.Equal("Example Hall", result.Site);
        Assert.Equal(
            ["name=Example Hall Second Floor", "hostname=example-hall-second-floor-137", "ntp=computer-zone"],
            result.Steps);

        var device = devices["10.200.62.137"];
        Assert.Equal("Example Hall Second Floor", device.Name);
        Assert.Equal("example-hall-second-floor-137", device.Hostname);
        Assert.True(device.NtpApplied);
    }

    [Fact]
    public async Task ReportsPerCameraFailuresWithoutStoppingTheBatch()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var service = new BulkConfigurationService(config, endpoint =>
            endpoint.Host.EndsWith(".66")
                ? new RecordingDevice { FailWith = new OnvifException(HttpStatusCode.Unauthorized, "denied") }
                : new RecordingDevice());

        var targets = service.TargetsFromAddresses(
            [IPAddress.Parse("10.200.62.65"), IPAddress.Parse("10.200.62.66")]);
        var results = await service.ConfigureAsync(targets);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(item => item.Address.ToString() == "10.200.62.65").Success);
        var failed = results.Single(item => item.Address.ToString() == "10.200.62.66");
        Assert.False(failed.Success);
        Assert.Contains("authentication", failed.Error ?? "");
        Assert.Empty(failed.Steps);
    }

    [Fact]
    public async Task SkipsCamerasOutsideTheSiteMapByDefault()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var service = new BulkConfigurationService(config, _ => new RecordingDevice());

        var targets = service.TargetsFromAddresses([IPAddress.Parse("192.168.99.5")]);
        Assert.False(Assert.Single(targets).LocationKnown);

        Assert.Empty(await service.ConfigureAsync(targets));
        Assert.Single(service.Plan(targets, includeUnknownLocation: true));
    }

    [Fact]
    public async Task HonorsDisabledStepsAndReportsProgress()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var devices = new ConcurrentDictionary<string, RecordingDevice>();
        var service = new BulkConfigurationService(config, endpoint => devices[endpoint.Host] = new RecordingDevice());
        var updates = new List<BulkConfigurationProgress>();

        var targets = service.TargetsFromAddresses([IPAddress.Parse("10.200.62.10")]);
        var results = await service.ConfigureAsync(targets,
            new BulkConfigurationOptions { SetHostname = false, SetNtp = false },
            new InlineProgress<BulkConfigurationProgress>(updates.Add));

        var device = devices["10.200.62.10"];
        Assert.Equal("Hall Main", device.Name);
        Assert.Null(device.Hostname);
        Assert.False(device.NtpApplied);
        Assert.Equal(["name=Hall Main"], Assert.Single(results).Steps);
        Assert.Equal(new BulkConfigurationProgress(1, 1, 1, 0), updates[^1]);
    }

    [Fact]
    public void SlugifiesPunctuationIntoASingleDnsSafeLabel()
    {
        var config = Config("Loading Dock", "Bay #2 / North", "10.200.63.0/24");
        var service = new BulkConfigurationService(config, _ => new RecordingDevice());

        var plan = Assert.Single(service.Plan(service.TargetsFromAddresses([IPAddress.Parse("10.200.63.5")])));
        Assert.Equal("loading-dock-bay-2-north-5", plan.Hostname);
    }

    [Fact]
    public async Task SetsVideoResolutionCappedPerEncoder()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var vga = new OnvifResolution(1280, 720);
        var hd = new OnvifResolution(1920, 1080);
        var uhd = new OnvifResolution(3840, 2160);
        FakeVideo? fake = null;
        var service = new BulkConfigurationService(config,
            deviceFactory: _ => new RecordingDevice(),
            videoFactory: _ => fake = new FakeVideo(new[]
            {
                new VideoEncoderInfo("main", true, "H264", vga, 15f,
                    new[] { new CodecCapability("H264", new[] { vga, hd, uhd }, new[] { 30f, 15f }) }),
                new VideoEncoderInfo("sub", true, "H264", vga, 15f,
                    new[] { new CodecCapability("H264", new[] { new OnvifResolution(640, 480), vga }, new[] { 30f, 25f }) }),
            }));

        var result = Assert.Single(await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.5")]),
            new BulkConfigurationOptions { SetName = false, SetHostname = false, SetNtp = false, VideoResolution = uhd }));

        Assert.True(result.Success);
        // Each stream capped to the largest it supports no bigger than the request; codec left alone.
        Assert.Equal(uhd, fake!.Applied.Single(a => a.Token == "main").Resolution);
        Assert.Equal(vga, fake.Applied.Single(a => a.Token == "sub").Resolution);
        Assert.Null(fake.Applied.Single(a => a.Token == "main").Codec);   // no codec switch requested
    }

    [Fact]
    public async Task ClampsFrameRateToNearestSupportedAtOrBelow()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var hd = new OnvifResolution(1920, 1080);
        FakeVideo? fake = null;
        var service = new BulkConfigurationService(config,
            deviceFactory: _ => new RecordingDevice(),
            videoFactory: _ => fake = new FakeVideo(new[]
            {
                // "main" tops out at 25; "sub" offers exactly 30.
                new VideoEncoderInfo("main", true, "H264", hd, 25f,
                    new[] { new CodecCapability("H264", new[] { hd }, new[] { 25f, 15f }) }),
                new VideoEncoderInfo("sub", true, "H264", hd, 15f,
                    new[] { new CodecCapability("H264", new[] { hd }, new[] { 30f, 15f }) }),
            }));

        var result = Assert.Single(await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.5")]),
            new BulkConfigurationOptions { SetName = false, SetHostname = false, SetNtp = false, VideoFrameRate = 30f }));

        Assert.True(result.Success);
        Assert.Equal(25f, fake!.Applied.Single(a => a.Token == "main").FrameRate);  // clamped down to 25
        Assert.Equal(30f, fake.Applied.Single(a => a.Token == "sub").FrameRate);    // exact match kept
    }

    [Fact]
    public async Task ClampsBitrateIntoAdvertisedRange()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var hd = new OnvifResolution(1920, 1080);
        FakeVideo? fake = null;
        var service = new BulkConfigurationService(config,
            deviceFactory: _ => new RecordingDevice(),
            videoFactory: _ => fake = new FakeVideo(new[]
            {
                new VideoEncoderInfo("main", true, "H264", hd, 30f,
                    new[] { new CodecCapability("H264", new[] { hd }, new[] { 30f }, new OnvifRange<int>(512, 4096)) }),
            }));

        var result = Assert.Single(await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.5")]),
            new BulkConfigurationOptions { SetName = false, SetHostname = false, SetNtp = false, VideoBitrateKbps = 8000 }));

        Assert.True(result.Success);
        Assert.Equal(4096, fake!.Applied.Single().Bitrate);  // clamped to the top of the advertised range
    }

    [Fact]
    public async Task ReportsWhatTheCameraActuallyApplied()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var uhd = new OnvifResolution(3840, 2160);
        var hd = new OnvifResolution(1920, 1080);
        // We request 4K (advertised), but the camera actually settles on 1080p.
        var fake = new FakeVideo(
            new[] { new VideoEncoderInfo("main", true, "H264", hd, 30f,
                new[] { new CodecCapability("H264", new[] { uhd, hd }, new[] { 30f }) }) },
            camera: (codec, resolution, frameRate) => new VideoEncoderState(codec ?? "H264", hd, frameRate));
        var service = new BulkConfigurationService(config, deviceFactory: _ => new RecordingDevice(), videoFactory: _ => fake);

        var result = Assert.Single(await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.5")]),
            new BulkConfigurationOptions { SetName = false, SetHostname = false, SetNtp = false, VideoResolution = uhd }));

        var outcome = result.Video.Single();
        Assert.Equal(uhd, outcome.RequestedResolution);
        Assert.Equal(hd, outcome.AppliedResolution);    // report shows what the camera actually did
        Assert.True(outcome.ClampedByCamera);
    }

    [Fact]
    public async Task SwitchesToRequestedCodecAndReportsFallbackWhenUnsupported()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var hd = new OnvifResolution(1920, 1080);
        var uhd = new OnvifResolution(3840, 2160);
        var service = new BulkConfigurationService(config,
            deviceFactory: _ => new RecordingDevice(),
            videoFactory: endpoint =>
            {
                // .1 supports H.264 + H.265 (4K only in H.265); .2 supports H.264 only.
                var codecs = endpoint.Host.EndsWith(".1")
                    ? new[] { new CodecCapability("H264", new[] { hd }, new[] { 30f }), new CodecCapability("H265", new[] { uhd }, new[] { 30f }) }
                    : new[] { new CodecCapability("H264", new[] { hd }, new[] { 30f }) };
                return new FakeVideo(new[] { new VideoEncoderInfo("main", true, "H264", hd, 30f, codecs) },
                    camera: (codec, resolution, frameRate) => new VideoEncoderState(codec ?? "H264", resolution, frameRate));
            });

        var results = await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.1"), IPAddress.Parse("10.200.62.2")]),
            new BulkConfigurationOptions { SetName = false, SetHostname = false, SetNtp = false, VideoCodec = "H265", VideoResolution = uhd });

        var preferred = results.Single(r => r.Address.ToString() == "10.200.62.1").Video.Single();
        Assert.Equal("H265", preferred.AppliedCodec);
        Assert.Equal(uhd, preferred.AppliedResolution);
        Assert.False(preferred.CodecFallback);

        var fellBack = results.Single(r => r.Address.ToString() == "10.200.62.2").Video.Single();
        Assert.Equal("H265", fellBack.RequestedCodec);
        Assert.Equal("H264", fellBack.AppliedCodec);
        Assert.Equal(hd, fellBack.AppliedResolution);     // capped within H.264
        Assert.True(fellBack.CodecFallback);
    }

    [Fact]
    public async Task LeavesCodecUnchangedWhenCameraCannotSwitch()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var hd = new OnvifResolution(1920, 1080);
        // Legacy camera advertises H.265 but cannot switch codec (CanSwitchCodec = false).
        FakeVideo? fake = null;
        var service = new BulkConfigurationService(config,
            deviceFactory: _ => new RecordingDevice(),
            videoFactory: _ => fake = new FakeVideo(
                new[] { new VideoEncoderInfo("main", false, "H264", hd, 30f, new[]
                {
                    new CodecCapability("H264", new[] { hd }, new[] { 30f }),
                    new CodecCapability("H265", new[] { hd }, new[] { 30f }),
                }) },
                camera: (codec, resolution, frameRate) => new VideoEncoderState(codec ?? "H264", resolution, frameRate)));

        var result = Assert.Single(await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.9")]),
            new BulkConfigurationOptions { SetName = false, SetHostname = false, SetNtp = false, VideoCodec = "H265", VideoResolution = hd }));

        var outcome = result.Video.Single();
        Assert.Equal("H264", outcome.AppliedCodec);      // stayed on its current codec
        Assert.True(outcome.CodecFallback);              // reported: wanted H.265, kept H.264
        Assert.Null(fake!.Applied.Single().Codec);       // no codec switch was attempted
    }

    [Fact]
    public async Task DryRunReportsPlannedConfigurationWithoutWriting()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var hd = new OnvifResolution(1920, 1080);
        var uhd = new OnvifResolution(3840, 2160);
        FakeVideo? fake = null;
        var service = new BulkConfigurationService(config,
            deviceFactory: _ => new RecordingDevice(),
            videoFactory: _ => fake = new FakeVideo(new[]
            {
                new VideoEncoderInfo("main", true, "H264", hd, 30f, new[]
                {
                    new CodecCapability("H264", new[] { hd }, new[] { 30f }),
                    new CodecCapability("H265", new[] { uhd }, new[] { 30f }),
                }),
            }));

        var result = Assert.Single(await service.ConfigureAsync(
            service.TargetsFromAddresses([IPAddress.Parse("10.200.62.5")]),
            new BulkConfigurationOptions { SetName = true, SetHostname = false, SetNtp = false, VideoCodec = "H265", VideoResolution = uhd, DryRun = true }));

        Assert.True(result.Success);
        Assert.Empty(fake!.Applied);                                   // nothing written to the camera
        var outcome = result.Video.Single();
        Assert.Equal("H265", outcome.AppliedCodec);                    // planned pick
        Assert.Equal(uhd, outcome.AppliedResolution);                  // planned resolution within H.265
        Assert.All(result.Steps, step => Assert.StartsWith("would set ", step));
    }

    private static SurveilConfig Config(string building, string area, string cidr) =>
        new() { Sites = [new Site { Name = building, Ranges = [new NetworkRange { Name = area, Cidr = cidr }] }] };

    private sealed class FakeVideo : IConfigurableVideo
    {
        private readonly VideoEncoderInfo[] encoders;
        private readonly Func<string?, OnvifResolution, float?, VideoEncoderState>? camera;

        // camera simulates the device: given the requested codec+res+fps, returns what it actually applies.
        public FakeVideo(VideoEncoderInfo[] encoders,
            Func<string?, OnvifResolution, float?, VideoEncoderState>? camera = null)
        {
            this.encoders = encoders;
            this.camera = camera;
        }

        public List<(string Token, string? Codec, OnvifResolution Resolution, float? FrameRate, int? Bitrate)> Applied { get; } = [];

        public Task<IReadOnlyList<VideoEncoderInfo>> GetEncodersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VideoEncoderInfo>>(encoders);

        public Task<VideoEncoderState> ApplyAsync(string configurationToken, string? codec, OnvifResolution resolution,
            float? frameRate, int? bitrateKbps, CancellationToken cancellationToken)
        {
            Applied.Add((configurationToken, codec, resolution, frameRate, bitrateKbps));
            return Task.FromResult(camera?.Invoke(codec, resolution, frameRate)
                ?? new VideoEncoderState(codec ?? "H264", resolution, frameRate));
        }

        public void Dispose() { }
    }

    private sealed class RecordingDevice : IConfigurableDevice
    {
        public Exception? FailWith { get; init; }
        public string? Name { get; private set; }
        public string? Hostname { get; private set; }
        public bool NtpApplied { get; private set; }

        public Task SetNameAsync(string name, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            Name = name;
            return Task.CompletedTask;
        }

        public Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            Hostname = hostname;
            return Task.FromResult(false);
        }

        public Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            NtpApplied = true;
            return Task.CompletedTask;
        }

        public string? NtpServer { get; private set; }
        public Task SetNtpServerAsync(string server, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            NtpServer = server;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
