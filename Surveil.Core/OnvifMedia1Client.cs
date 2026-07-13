using System.Globalization;
using System.Xml.Linq;

namespace Surveil.Core;

/// <summary>Legacy ONVIF Media (ver10) adapter for older Profile S cameras.</summary>
public sealed class OnvifMedia1Client : IOnvifVideoClient
{
    public const string MediaNamespace = "http://www.onvif.org/ver10/media/wsdl";
    public const string SchemaNamespace = "http://www.onvif.org/ver10/schema";
    private readonly OnvifSoapTransport transport;
    public OnvifMediaGeneration Generation => OnvifMediaGeneration.Media1;
    public Uri Endpoint => transport.Endpoint;

    public OnvifMedia1Client(Uri endpoint, string username, string password)
    {
        transport = new OnvifSoapTransport(endpoint, username, password);
    }

    public OnvifMedia1Client(Uri endpoint, HttpClient httpClient) =>
        transport = new OnvifSoapTransport(endpoint, httpClient);

    public async Task<IReadOnlyList<OnvifMediaProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetProfiles", new XElement(M("GetProfiles")), cancellationToken);
        return response.Descendants(M("Profiles")).Select(profile =>
        {
            var encoder = profile.Element(S("VideoEncoderConfiguration"));
            var source = profile.Element(S("VideoSourceConfiguration"));
            return new OnvifMediaProfile(Attribute(profile, "token"), Value(profile, "Name") ?? "",
                encoder?.Attribute("token")?.Value, Value(source, "SourceToken"));
        }).ToArray();
    }

    public async Task<IReadOnlyList<OnvifVideoEncoderConfiguration>> GetVideoEncoderConfigurationsAsync(
        string? configurationToken = null, string? profileToken = null, CancellationToken cancellationToken = default)
    {
        XElement request;
        string operation;
        if (configurationToken is not null)
        {
            operation = "GetVideoEncoderConfiguration";
            request = new XElement(M(operation), new XElement(M("ConfigurationToken"), configurationToken));
        }
        else if (profileToken is not null)
        {
            operation = "GetCompatibleVideoEncoderConfigurations";
            request = new XElement(M(operation), new XElement(M("ProfileToken"), profileToken));
        }
        else
        {
            operation = "GetVideoEncoderConfigurations";
            request = new XElement(M(operation));
        }
        var response = await SendAsync(operation, request, cancellationToken);
        var mediaNs = (XNamespace)MediaNamespace;
        return response.Descendants().Where(x => x.Name.Namespace == mediaNs &&
            (x.Name.LocalName == "Configurations" || x.Name.LocalName == "Configuration"))
            .Select(ParseConfiguration).ToArray();
    }

    public async Task<IReadOnlyList<OnvifVideoEncoderOptions>> GetVideoEncoderConfigurationOptionsAsync(
        string? configurationToken = null, string? profileToken = null, CancellationToken cancellationToken = default)
    {
        var request = new XElement(M("GetVideoEncoderConfigurationOptions"),
            configurationToken is null ? null : new XElement(M("ConfigurationToken"), configurationToken),
            profileToken is null ? null : new XElement(M("ProfileToken"), profileToken));
        var response = await SendAsync("GetVideoEncoderConfigurationOptions", request, cancellationToken);
        var root = response.Descendants(M("Options")).Single();
        var quality = Range(root.Element(S("QualityRange"))!);
        var options = new List<OnvifVideoEncoderOptions>();
        AddCodec(root, "JPEG", "JPEG", quality, options);
        AddCodec(root, "MPEG4", "MPEG4", quality, options);
        AddCodec(root, "H264", "H264", quality, options);
        AddCodec(root, "H265", "H265", quality, options);
        return options;
    }

    public Task SetVideoEncoderConfigurationAsync(OnvifVideoEncoderConfiguration configuration,
        CancellationToken cancellationToken = default) => SendAndDiscardAsync("SetVideoEncoderConfiguration",
            new XElement(M("SetVideoEncoderConfiguration"),
                new XElement(M("Configuration"), configuration.WireValue.Attributes(), configuration.WireValue.Nodes()),
                new XElement(M("ForcePersistence"), true)), cancellationToken);

    public async Task UpdateVideoEncoderAsync(string configurationToken, int? width = null, int? height = null,
        float? framesPerSecond = null, int? bitrateKbps = null, float? quality = null, string? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var current = (await GetVideoEncoderConfigurationsAsync(configurationToken, null, cancellationToken)).Single();
        if (encoding is not null && !encoding.Equals(current.Encoding, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Codec switching is not safely portable through legacy ONVIF Media1.");
        var options = await GetVideoEncoderConfigurationOptionsAsync(configurationToken, null, cancellationToken);
        Validate(current, options, width, height, framesPerSecond, bitrateKbps, quality);
        var wire = new XElement(current.WireValue);
        var resolution = wire.Element(S("Resolution"))!;
        if (width.HasValue) resolution.SetElementValue(S("Width"), width.Value);
        if (height.HasValue) resolution.SetElementValue(S("Height"), height.Value);
        var rate = wire.Element(S("RateControl"));
        if ((framesPerSecond.HasValue || bitrateKbps.HasValue) && rate is null)
            throw new InvalidOperationException("Camera did not return a Media1 RateControl configuration.");
        if (framesPerSecond.HasValue) rate!.SetElementValue(S("FrameRateLimit"), (int)framesPerSecond.Value);
        if (bitrateKbps.HasValue) rate!.SetElementValue(S("BitrateLimit"), bitrateKbps.Value);
        if (quality.HasValue) wire.SetElementValue(S("Quality"), quality.Value.ToString(CultureInfo.InvariantCulture));
        await SetVideoEncoderConfigurationAsync(current with { WireValue = wire }, cancellationToken);
    }

    internal static OnvifVideoEncoderConfiguration ParseConfiguration(XElement value)
    {
        var resolution = value.Element(S("Resolution")) ?? throw new FormatException("Missing Resolution.");
        var rate = value.Element(S("RateControl"));
        var encoding = Value(value, "Encoding") ?? throw new FormatException("Missing Encoding.");
        var codec = value.Element(S(encoding));
        return new OnvifVideoEncoderConfiguration(Attribute(value, "token"), Value(value, "Name") ?? "", encoding,
            new OnvifResolution(Int(resolution, "Width"), Int(resolution, "Height")),
            IntOrNull(rate, "FrameRateLimit"), IntOrNull(rate, "BitrateLimit"), Float(value, "Quality"),
            IntOrNull(codec, "GovLength"), Value(codec, "H264Profile") ?? Value(codec, "MPEG4Profile"), new XElement(value));
    }

    private static void AddCodec(XElement root, string elementName, string encoding,
        OnvifRange<int> quality, List<OnvifVideoEncoderOptions> result)
    {
        foreach (var codec in root.Elements(S(elementName)))
        {
            var frame = Range(codec.Element(S("FrameRateRange"))!);
            var bitrate = Range(codec.Element(S("BitrateRange"))!);
            var govElement = codec.Element(S("GovLengthRange"));
            result.Add(new OnvifVideoEncoderOptions(encoding,
                codec.Elements(S("ResolutionsAvailable")).Select(x => new OnvifResolution(Int(x, "Width"), Int(x, "Height"))).ToArray(),
                Enumerable.Range(frame.Minimum, frame.Maximum - frame.Minimum + 1).Select(x => (float)x).Reverse().ToArray(),
                bitrate, new OnvifRange<float>(quality.Minimum, quality.Maximum),
                govElement is null ? null : Range(govElement)));
        }
    }

    private async Task<XElement> SendAsync(string operation, XElement body, CancellationToken cancellationToken) =>
        await transport.SendAsync(MediaNamespace, operation, body, cancellationToken);
    private async Task SendAndDiscardAsync(string operation, XElement body, CancellationToken cancellationToken) =>
        _ = await SendAsync(operation, body, cancellationToken);

    private static void Validate(OnvifVideoEncoderConfiguration current, IReadOnlyList<OnvifVideoEncoderOptions> all,
        int? width, int? height, float? fps, int? bitrate, float? quality)
    {
        var option = all.FirstOrDefault(x => x.Encoding.Equals(current.Encoding, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Camera returned no options for {current.Encoding}.");
        var resolution = new OnvifResolution(width ?? current.Resolution.Width, height ?? current.Resolution.Height);
        if ((width.HasValue || height.HasValue) && !option.Resolutions.Contains(resolution))
            throw new ArgumentOutOfRangeException(nameof(width), $"Resolution {resolution.Width}x{resolution.Height} is not supported.");
        if (fps.HasValue && !option.FrameRates.Contains(fps.Value)) throw new ArgumentOutOfRangeException(nameof(fps));
        if (bitrate.HasValue && (bitrate.Value < option.Bitrate.Minimum || bitrate.Value > option.Bitrate.Maximum))
            throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (quality.HasValue && (quality.Value < option.Quality.Minimum || quality.Value > option.Quality.Maximum))
            throw new ArgumentOutOfRangeException(nameof(quality));
    }

    private static XName M(string name) => XName.Get(name, MediaNamespace);
    private static XName S(string name) => XName.Get(name, SchemaNamespace);
    private static string Attribute(XElement e, string name) => e.Attribute(name)?.Value ?? throw new FormatException($"Missing {name}.");
    private static string? Value(XElement? e, string name) => e?.Element(S(name))?.Value;
    private static int Int(XElement e, string name) => int.Parse(Value(e, name)!, CultureInfo.InvariantCulture);
    private static int? IntOrNull(XElement? e, string name) => int.TryParse(Value(e, name), out var x) ? x : null;
    private static float Float(XElement e, string name) => float.Parse(Value(e, name)!, CultureInfo.InvariantCulture);
    private static OnvifRange<int> Range(XElement e) => new(Int(e, "Min"), Int(e, "Max"));
    public void Dispose() => transport.Dispose();
}
