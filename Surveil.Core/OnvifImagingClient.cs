using System.Globalization;
using System.Xml.Linq;

namespace Surveil.Core;

public enum OnvifWhiteBalanceMode { Auto, Manual }
public sealed record OnvifFloatRange(float Minimum, float Maximum);
public sealed record OnvifWhiteBalanceSettings(OnvifWhiteBalanceMode Mode, float? CrGain, float? CbGain);
public sealed record OnvifImagingSettings(float? Brightness, OnvifWhiteBalanceSettings? WhiteBalance, XElement WireValue);
public sealed record OnvifWhiteBalanceOptions(IReadOnlyList<OnvifWhiteBalanceMode> Modes,
    OnvifFloatRange? CrGain, OnvifFloatRange? CbGain);
public sealed record OnvifImagingOptions(OnvifFloatRange? Brightness, OnvifWhiteBalanceOptions? WhiteBalance);

public sealed class OnvifImagingClient : IDisposable
{
    public const string ImagingNamespace = "http://www.onvif.org/ver20/imaging/wsdl";
    private const string SchemaNamespace = "http://www.onvif.org/ver10/schema";
    private readonly OnvifSoapTransport transport;
    public Uri Endpoint => transport.Endpoint;

    public OnvifImagingClient(Uri endpoint, string username, string password)
    {
        transport = new OnvifSoapTransport(endpoint, username, password);
    }
    public OnvifImagingClient(Uri endpoint, HttpClient httpClient) =>
        transport = new OnvifSoapTransport(endpoint, httpClient);

    public async Task<OnvifImagingSettings> GetSettingsAsync(string videoSourceToken,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetImagingSettings", new XElement(I("GetImagingSettings"),
            new XElement(I("VideoSourceToken"), videoSourceToken)), cancellationToken);
        var settings = response.Descendants(I("ImagingSettings")).Single();
        var white = settings.Element(S("WhiteBalance"));
        return new OnvifImagingSettings(FloatOrNull(settings, "Brightness"), white is null ? null :
            new OnvifWhiteBalanceSettings(ParseMode(Value(white, "Mode")), FloatOrNull(white, "CrGain"),
                FloatOrNull(white, "CbGain")), new XElement(settings));
    }

    public async Task<OnvifImagingOptions> GetOptionsAsync(string videoSourceToken,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetOptions", new XElement(I("GetOptions"),
            new XElement(I("VideoSourceToken"), videoSourceToken)), cancellationToken);
        var options = response.Descendants(I("ImagingOptions")).Single();
        var white = options.Element(S("WhiteBalance"));
        return new OnvifImagingOptions(ParseRange(options.Element(S("Brightness"))), white is null ? null :
            new OnvifWhiteBalanceOptions(white.Elements(S("Mode")).Select(x => ParseMode(x.Value)).Distinct().ToArray(),
                ParseRange(white.Element(S("YrGain")) ?? white.Element(S("CrGain"))),
                ParseRange(white.Element(S("YbGain")) ?? white.Element(S("CbGain")))));
    }

    public async Task UpdateAsync(string videoSourceToken, float? brightness = null,
        OnvifWhiteBalanceMode? whiteBalanceMode = null, float? crGain = null, float? cbGain = null,
        CancellationToken cancellationToken = default)
    {
        var current = await GetSettingsAsync(videoSourceToken, cancellationToken);
        var options = await GetOptionsAsync(videoSourceToken, cancellationToken);
        Validate(brightness, whiteBalanceMode, crGain, cbGain, options);
        var wire = new XElement(current.WireValue);
        if (brightness.HasValue) wire.SetElementValue(S("Brightness"), F(brightness.Value));
        if (whiteBalanceMode.HasValue || crGain.HasValue || cbGain.HasValue)
        {
            var white = wire.Element(S("WhiteBalance")) ?? new XElement(S("WhiteBalance"));
            if (white.Parent is null) wire.Add(white);
            if (whiteBalanceMode.HasValue) white.SetElementValue(S("Mode"),
                whiteBalanceMode == OnvifWhiteBalanceMode.Auto ? "AUTO" : "MANUAL");
            if (crGain.HasValue) white.SetElementValue(S("CrGain"), F(crGain.Value));
            if (cbGain.HasValue) white.SetElementValue(S("CbGain"), F(cbGain.Value));
        }
        await SendAsync("SetImagingSettings", new XElement(I("SetImagingSettings"),
            new XElement(I("VideoSourceToken"), videoSourceToken),
            new XElement(I("ImagingSettings"), wire.Attributes(), wire.Nodes()),
            new XElement(I("ForcePersistence"), true)), cancellationToken);
    }

    private async Task<XElement> SendAsync(string operation, XElement body, CancellationToken cancellationToken) =>
        await transport.SendAsync(ImagingNamespace, operation, body, cancellationToken);

    private static void Validate(float? brightness, OnvifWhiteBalanceMode? mode, float? cr, float? cb,
        OnvifImagingOptions options)
    {
        Check(brightness, options.Brightness, nameof(brightness));
        if (mode.HasValue && (options.WhiteBalance is null || !options.WhiteBalance.Modes.Contains(mode.Value)))
            throw new NotSupportedException($"Camera does not support {mode} white balance.");
        if ((cr.HasValue || cb.HasValue) && mode == OnvifWhiteBalanceMode.Auto)
            throw new ArgumentException("White-balance gains require manual mode.");
        Check(cr, options.WhiteBalance?.CrGain, nameof(cr));
        Check(cb, options.WhiteBalance?.CbGain, nameof(cb));
    }
    private static void Check(float? value, OnvifFloatRange? range, string name)
    {
        if (!value.HasValue) return;
        if (range is null) throw new NotSupportedException($"Camera does not expose {name} configuration.");
        if (value < range.Minimum || value > range.Maximum) throw new ArgumentOutOfRangeException(name);
    }
    private static OnvifWhiteBalanceMode ParseMode(string? value) =>
        value?.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) == true
            ? OnvifWhiteBalanceMode.Manual : OnvifWhiteBalanceMode.Auto;
    private static OnvifFloatRange? ParseRange(XElement? value) => value is null ? null :
        new(float.Parse(Value(value, "Min")!, CultureInfo.InvariantCulture),
            float.Parse(Value(value, "Max")!, CultureInfo.InvariantCulture));
    private static float? FloatOrNull(XElement e, string name) => float.TryParse(Value(e, name),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string? Value(XElement e, string name) => e.Element(S(name))?.Value;
    private static string F(float value) => value.ToString(CultureInfo.InvariantCulture);
    private static XName I(string name) => XName.Get(name, ImagingNamespace);
    private static XName S(string name) => XName.Get(name, SchemaNamespace);
    public void Dispose() => transport.Dispose();
}
