using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Surveil.Core;

public sealed record OnvifMediaProfile(string Token, string Name, string? VideoEncoderToken,
    string? VideoSourceToken = null);
public sealed record OnvifResolution(int Width, int Height);
public sealed record OnvifRange<T>(T Minimum, T Maximum);

public sealed record OnvifVideoEncoderConfiguration(
    string Token, string Name, string Encoding, OnvifResolution Resolution,
    float? FrameRateLimit, int? BitrateLimit, float Quality, int? GovLength,
    string? Profile, XElement WireValue);

public sealed record OnvifVideoEncoderOptions(
    string Encoding, IReadOnlyList<OnvifResolution> Resolutions,
    IReadOnlyList<float> FrameRates, OnvifRange<int> Bitrate,
    OnvifRange<float> Quality, OnvifRange<int>? GovLength);

/// <summary>ONVIF Media2 (ver20) SOAP client based on the current published WSDL.</summary>
public sealed class OnvifMedia2Client : IOnvifVideoClient
{
    public const string MediaNamespace = "http://www.onvif.org/ver20/media/wsdl";
    public const string SchemaNamespace = "http://www.onvif.org/ver10/schema";
    private const string SoapNamespace = "http://www.w3.org/2003/05/soap-envelope";

    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;

    public OnvifMedia2Client(Uri endpoint, string username, string password)
    {
        this.endpoint = endpoint;
        var credentials = new NetworkCredential(username, password);
        var handler = new HttpClientHandler { Credentials = credentials, PreAuthenticate = true };
        http = new HttpClient(handler);
        ownsHttpClient = true;
    }

    public OnvifMedia2Client(Uri endpoint, HttpClient httpClient)
    {
        this.endpoint = endpoint;
        http = httpClient;
    }

    public OnvifMediaGeneration Generation => OnvifMediaGeneration.Media2;
    public Uri Endpoint => endpoint;

    public async Task<IReadOnlyList<OnvifMediaProfile>> GetProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetProfiles",
            new XElement(M("GetProfiles"), new XElement(M("Type"), "VideoEncoder")), cancellationToken);
        return response.Descendants(M("Profiles")).Select(profile => {
            var source = Descendant(profile, "VideoSource");
            return new OnvifMediaProfile(
                RequiredAttribute(profile, "token"),
                Child(profile, "Name")?.Value ?? "",
                Descendant(profile, "VideoEncoder")?.Attribute("token")?.Value,
                source is null ? null : Child(source, "SourceToken")?.Value);
        }).ToArray();
    }

    public async Task<IReadOnlyList<OnvifVideoEncoderConfiguration>> GetVideoEncoderConfigurationsAsync(
        string? configurationToken = null, string? profileToken = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetVideoEncoderConfigurations",
            ConfigurationRequest("GetVideoEncoderConfigurations", configurationToken, profileToken), cancellationToken);
        return response.Descendants(M("Configurations")).Select(ParseConfiguration).ToArray();
    }

    public async Task<IReadOnlyList<OnvifVideoEncoderOptions>> GetVideoEncoderConfigurationOptionsAsync(
        string? configurationToken = null, string? profileToken = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetVideoEncoderConfigurationOptions",
            ConfigurationRequest("GetVideoEncoderConfigurationOptions", configurationToken, profileToken), cancellationToken);
        return response.Descendants(M("Options")).Select(ParseOptions).ToArray();
    }

    public async Task SetVideoEncoderConfigurationAsync(
        OnvifVideoEncoderConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await SendAsync("SetVideoEncoderConfiguration",
            new XElement(M("SetVideoEncoderConfiguration"),
                new XElement(M("Configuration"), configuration.WireValue.Attributes(),
                    configuration.WireValue.Nodes())), cancellationToken);
    }

    public async Task UpdateVideoEncoderAsync(string configurationToken, int? width = null, int? height = null,
        float? framesPerSecond = null, int? bitrateKbps = null, float? quality = null, string? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = (await GetVideoEncoderConfigurationsAsync(configurationToken, null, cancellationToken)).Single();
        var options = await GetVideoEncoderConfigurationOptionsAsync(configurationToken, null, cancellationToken);
        ValidateUpdate(configuration, options, width, height, framesPerSecond, bitrateKbps, quality, encoding);
        if (encoding is not null)
            encoding = options.First(x => NormalizeEncoding(x.Encoding) == NormalizeEncoding(encoding)).Encoding;

        var wire = new XElement(configuration.WireValue);
        var resolution = wire.Element(S("Resolution"))!;
        if (encoding is not null) wire.SetElementValue(S("Encoding"), encoding);
        if (width.HasValue) resolution.SetElementValue(S("Width"), width.Value);
        if (height.HasValue) resolution.SetElementValue(S("Height"), height.Value);
        var rate = wire.Element(S("RateControl"));
        if ((framesPerSecond.HasValue || bitrateKbps.HasValue) && rate is null)
            throw new InvalidOperationException("Camera did not return a Media2 RateControl configuration.");
        if (framesPerSecond.HasValue) rate!.SetElementValue(S("FrameRateLimit"), F(framesPerSecond.Value));
        if (bitrateKbps.HasValue) rate!.SetElementValue(S("BitrateLimit"), bitrateKbps.Value);
        if (quality.HasValue) wire.SetElementValue(S("Quality"), F(quality.Value));
        await SetVideoEncoderConfigurationAsync(configuration with { WireValue = wire }, cancellationToken);
    }

    internal static OnvifVideoEncoderConfiguration ParseConfiguration(XElement value)
    {
        var resolution = value.Element(S("Resolution")) ?? throw new FormatException("Missing Resolution.");
        var rate = value.Element(S("RateControl"));
        return new OnvifVideoEncoderConfiguration(RequiredAttribute(value, "token"), Value(value, S("Name")) ?? "",
            RequiredValue(value, S("Encoding")),
            new OnvifResolution(Int(resolution, "Width"), Int(resolution, "Height")),
            FloatOrNull(rate, "FrameRateLimit"), IntOrNull(rate, "BitrateLimit"),
            Float(value, "Quality"), AttributeInt(value, "GovLength"), value.Attribute("Profile")?.Value,
            new XElement(value));
    }

    internal static OnvifVideoEncoderOptions ParseOptions(XElement value)
    {
        var bitrate = value.Element(S("BitrateRange")) ?? throw new FormatException("Missing BitrateRange.");
        var quality = value.Element(S("QualityRange")) ?? throw new FormatException("Missing QualityRange.");
        var gov = value.Attribute("GovLengthRange")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new OnvifVideoEncoderOptions(EncodingOf(value),
            value.Elements(S("ResolutionsAvailable")).Select(x => new OnvifResolution(Int(x, "Width"), Int(x, "Height"))).ToArray(),
            (value.Attribute("FrameRatesSupported")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
            new OnvifRange<int>(Int(bitrate, "Min"), Int(bitrate, "Max")),
            new OnvifRange<float>(Float(quality, "Min"), Float(quality, "Max")),
            gov is { Length: 2 } ? new OnvifRange<int>(gov[0], gov[1]) : null);
    }

    private async Task<XElement> SendAsync(string operation, XElement body, CancellationToken cancellationToken)
    {
        var envelope = new XDocument(new XElement(XName.Get("Envelope", SoapNamespace),
            new XAttribute(XNamespace.Xmlns + "s", SoapNamespace),
            new XAttribute(XNamespace.Xmlns + "tr2", MediaNamespace),
            new XAttribute(XNamespace.Xmlns + "tt", SchemaNamespace),
            new XElement(XName.Get("Body", SoapNamespace), body)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            $"application/soap+xml; charset=utf-8; action=\"{MediaNamespace}/{operation}\"");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new OnvifException(response.StatusCode, SoapFault(xml));
        try { return XDocument.Parse(xml).Root ?? throw new FormatException("Empty SOAP response."); }
        catch (Exception error) when (error is not OnvifException) { throw new OnvifException(response.StatusCode, "Invalid SOAP response.", error); }
    }

    private static XElement ConfigurationRequest(string operation, string? configurationToken, string? profileToken) =>
        new(M(operation), configurationToken is null ? null : new XElement(M("ConfigurationToken"), configurationToken),
            profileToken is null ? null : new XElement(M("ProfileToken"), profileToken));

    private static void ValidateUpdate(OnvifVideoEncoderConfiguration current, IReadOnlyList<OnvifVideoEncoderOptions> all,
        int? width, int? height, float? fps, int? bitrate, float? quality, string? encoding)
    {
        var desiredEncoding = encoding ?? current.Encoding;
        var option = all.FirstOrDefault(x => NormalizeEncoding(x.Encoding) == NormalizeEncoding(desiredEncoding))
            ?? throw new NotSupportedException($"Camera returned no options for {desiredEncoding}.");
        var desired = new OnvifResolution(width ?? current.Resolution.Width, height ?? current.Resolution.Height);
        if ((width.HasValue || height.HasValue || encoding is not null) && !option.Resolutions.Contains(desired))
            throw new ArgumentOutOfRangeException(nameof(width), $"Resolution {desired.Width}x{desired.Height} is not supported.");
        if (fps.HasValue && option.FrameRates.Count > 0 && !option.FrameRates.Any(x => Math.Abs(x - fps.Value) < .001f))
            throw new ArgumentOutOfRangeException(nameof(fps), $"Frame rate {fps} is not supported.");
        if (bitrate.HasValue && (bitrate < option.Bitrate.Minimum || bitrate > option.Bitrate.Maximum))
            throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (quality.HasValue && (quality < option.Quality.Minimum || quality > option.Quality.Maximum))
            throw new ArgumentOutOfRangeException(nameof(quality));
    }

    private static string NormalizeEncoding(string value) => value.Replace("video/", "", StringComparison.OrdinalIgnoreCase)
        .Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string SoapFault(string xml) { try { return XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == "Text")?.Value ?? xml; } catch { return xml; } }
    private static XName M(string name) => XName.Get(name, MediaNamespace);
    private static XName S(string name) => XName.Get(name, SchemaNamespace);
    private static string RequiredAttribute(XElement e, string name) => e.Attribute(name)?.Value ?? throw new FormatException($"Missing {name} attribute.");
    private static string? Value(XElement e, XName name) => e.Element(name)?.Value;
    private static string RequiredValue(XElement e, XName name) => Value(e, name) ?? throw new FormatException($"Missing {name.LocalName}.");
    // Media2 qualifies MediaProfile/ConfigurationSet members (Name, VideoSource, VideoEncoder) in the
    // ver20 media namespace (tr2); tolerate the ver10 schema namespace (tt) for devices that differ.
    private static XElement? Child(XElement e, string localName) => e.Element(M(localName)) ?? e.Element(S(localName));
    private static XElement? Descendant(XElement e, string localName) =>
        e.Descendants(M(localName)).FirstOrDefault() ?? e.Descendants(S(localName)).FirstOrDefault();
    // VideoEncoder2ConfigurationOptions carries Encoding as an attribute; accept a tt element as a fallback.
    private static string EncodingOf(XElement options) => options.Attribute("Encoding")?.Value
        ?? Value(options, S("Encoding")) ?? throw new FormatException("Missing Encoding.");
    private static int Int(XElement e, string name) => int.Parse(RequiredValue(e, S(name)), CultureInfo.InvariantCulture);
    private static int? IntOrNull(XElement? e, string name) => e?.Element(S(name)) is { } x ? int.Parse(x.Value, CultureInfo.InvariantCulture) : null;
    private static int? AttributeInt(XElement e, string name) => e.Attribute(name) is { } x ? int.Parse(x.Value, CultureInfo.InvariantCulture) : null;
    private static float Float(XElement e, string name) => float.Parse(RequiredValue(e, S(name)), CultureInfo.InvariantCulture);
    private static float? FloatOrNull(XElement? e, string name) => e?.Element(S(name)) is { } x ? float.Parse(x.Value, CultureInfo.InvariantCulture) : null;
    private static string F(float value) => value.ToString(CultureInfo.InvariantCulture);

    public void Dispose() { if (ownsHttpClient) http.Dispose(); }
}

public sealed class OnvifException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    public OnvifException(HttpStatusCode statusCode, string message, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;
}
