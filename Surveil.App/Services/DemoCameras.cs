using Surveil.Core;

namespace Surveil.App.Services;

/// <summary>Opt-in development fixtures used only when SURVEIL_DEMO=1 is set.</summary>
public static class DemoCameras
{
    public sealed record Seed(CameraStatus Status, CameraFeatures Features);

    /// <summary>One i-PRO model and the capabilities it advertises over ONVIF.</summary>
    public sealed record Model(
        string Name, string Firmware, OnvifMediaGeneration Generation,
        string[] Services, string[] Codecs, OnvifResolution[] Resolutions,
        float[] FrameRates, OnvifRange<int> Bitrate);

    // Resolutions (i-PRO reports 16:9 by default; 2MP = 1080p, 5MP = 2992x1680).
    private static readonly OnvifResolution R1080 = new(1920, 1080);
    private static readonly OnvifResolution R960 = new(1280, 960);
    private static readonly OnvifResolution R720 = new(1280, 720);
    private static readonly OnvifResolution R360 = new(640, 360);
    private static readonly OnvifResolution R5MP = new(2992, 1680);
    private static readonly OnvifResolution R1440 = new(2560, 1440);

    // Modern i-PRO codec set (H.265 "Smart Coding", H.264, JPEG snapshot).
    private static readonly string[] Modern = { "H265", "H264", "JPEG" };
    // i-PRO frame-rate ladder, max 30 fps.
    private static readonly float[] To30 = { 30f, 15f, 10f, 5f, 3f, 1f };
    // Service surface: value line is basic; S/X lines add analytics (X = AI).
    private static readonly string[] Basic = { "media", "imaging", "events" };
    private static readonly string[] Analytics = { "media", "imaging", "analytics", "events" };

    public static IReadOnlyList<Model> Models { get; } = new[]
    {
        // U-series (value) 2MP — H.265, 1080p@30, up to 8 Mbps, motion detection only.
        new Model("WV-U1130",  "3.10", OnvifMediaGeneration.Media2, Basic, Modern,
            new[] { R1080, R720, R360 }, To30, new OnvifRange<int>(64, 8192)),
        new Model("WV-U2130L", "3.10", OnvifMediaGeneration.Media2, Basic, Modern,
            new[] { R1080, R720, R360 }, To30, new OnvifRange<int>(64, 8192)),

        // S-series 2MP (box + IR dome) — H.265, 1080p@30, up to 12 Mbps, i-VMD analytics.
        new Model("WV-S1136",  "2.40", OnvifMediaGeneration.Media2, Analytics, Modern,
            new[] { R1080, R960, R720, R360 }, To30, new OnvifRange<int>(64, 12288)),
        new Model("WV-S2136L", "2.40", OnvifMediaGeneration.Media2, Analytics, Modern,
            new[] { R1080, R960, R720, R360 }, To30, new OnvifRange<int>(64, 12288)),

        // X-series AI 5MP (box + IR dome) — H.265, 2992x1680@30, up to 24 Mbps, AI analytics.
        new Model("WV-X1571LN", "2.30", OnvifMediaGeneration.Media2, Analytics, Modern,
            new[] { R5MP, R1440, R1080, R720 }, To30, new OnvifRange<int>(64, 24576)),
        new Model("WV-X2571LN", "2.30", OnvifMediaGeneration.Media2, Analytics, Modern,
            new[] { R5MP, R1440, R1080, R720 }, To30, new OnvifRange<int>(64, 24576)),
    };

    /// <summary>Build the identified-camera features for one copy of a model.</summary>
    public static CameraFeatures Features(Model m, int index)
    {
        var codecs = m.Codecs.Select(codec => new CodecCapability(codec, m.Resolutions, m.FrameRates, m.Bitrate)).ToArray();
        var encoder = new VideoEncoderInfo("main", m.Generation == OnvifMediaGeneration.Media2,
            m.Codecs[0], m.Resolutions[0], m.FrameRates.Max(), codecs, m.Bitrate.Maximum);
        var info = new OnvifDeviceInformation("i-PRO", m.Name, m.Firmware, $"{m.Name}-{index:0000}", m.Name);
        return new CameraFeatures(info, m.Generation, m.Services, new[] { encoder });
    }

    public static IReadOnlyList<Seed> CreateSeeds(IReadOnlyList<(string Site, string Area, string Cidr)> ranges,
        int count = 36, int? randomSeed = null)
    {
        var rng = randomSeed is { } seed ? new Random(seed) : new Random();
        var counts = Enumerable.Repeat(Math.Max(1, count / Models.Count), Models.Count).ToArray();
        for (var extra = counts.Sum(); extra < count; extra++) counts[rng.Next(counts.Length)]++;
        var order = Models.SelectMany((model, index) => Enumerable.Repeat(model, counts[index])).Take(count).ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order.Select((model, index) =>
        {
            var location = ranges.Count > 0 ? ranges[index % ranges.Count] : default;
            var ip = ranges.Count > 0 ? IpIn(location.Cidr, 11 + index / ranges.Count) : $"192.168.0.{11 + index}";
            var status = new CameraStatus
            {
                Ip = ip,
                Site = location.Site ?? "",
                Area = location.Area ?? "",
                Presence = CameraPresenceStatus.Discovered,
            };
            return new Seed(status, Features(model, index));
        }).ToArray();
    }

    private static string IpIn(string cidr, int host)
    {
        var parts = cidr.Split('/')[0].Split('.');
        if (parts.Length == 4)
        {
            parts[3] = Math.Clamp(host, 2, 254).ToString();
            return string.Join('.', parts);
        }
        return $"192.168.0.{Math.Clamp(host, 2, 254)}";
    }
}
