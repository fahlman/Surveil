using System.Net;
using System.Xml.Linq;

namespace Surveil.Core;

public sealed record OnvifScope(string Definition, string Item);
public sealed record OnvifHostnameUpdate(string Hostname, bool RebootRequired);
public sealed record OnvifDeviceInformation(
    string Manufacturer, string Model, string FirmwareVersion, string SerialNumber, string HardwareId);
public sealed record OnvifTimeZone(string SystemIdentifier, string PosixValue, bool DaylightSavings);

public sealed class OnvifDeviceClient : IDisposable
{
    public const string DeviceNamespace = "http://www.onvif.org/ver10/device/wsdl";
    private const string SchemaNamespace = "http://www.onvif.org/ver10/schema";
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    public Uri Endpoint { get; }

    public OnvifDeviceClient(Uri endpoint, string username, string password)
    {
        Endpoint = endpoint;
        http = new HttpClient(new HttpClientHandler {
            Credentials = new NetworkCredential(username, password), PreAuthenticate = true
        });
        ownsHttp = true;
    }
    public OnvifDeviceClient(Uri endpoint, HttpClient httpClient) => (Endpoint, http) = (endpoint, httpClient);

    public async Task<string> GetHostnameAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetHostname", new XElement(D("GetHostname")), cancellationToken);
        return response.Descendants(S("Name")).Single().Value;
    }

    public async Task SetHostnameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 253 ||
            name.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Hostname must contain valid DNS labels.", nameof(name));
        await SendAsync("SetHostname", new XElement(D("SetHostname"), new XElement(D("Name"), name)), cancellationToken);
    }

    public async Task<OnvifHostnameUpdate> SetAndVerifyHostnameAsync(string name,
        CancellationToken cancellationToken = default)
    {
        ValidateHostname(name);
        var response = await SendAsync("SetHostnameFromDHCP", new XElement(D("SetHostnameFromDHCP"),
            new XElement(D("FromDHCP"), false)), cancellationToken);
        var reboot = bool.TryParse(response.Descendants(D("RebootNeeded")).FirstOrDefault()?.Value, out var needed) && needed;
        await SetHostnameAsync(name, cancellationToken);
        var confirmed = await GetHostnameAsync(cancellationToken);
        if (!confirmed.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Camera reported hostname '{confirmed}' after setting '{name}'.");
        return new OnvifHostnameUpdate(confirmed, reboot);
    }

    public async Task<IReadOnlyList<OnvifScope>> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetScopes", new XElement(D("GetScopes")), cancellationToken);
        return response.Descendants(D("Scopes")).Select(scope => new OnvifScope(
            scope.Element(S("ScopeDef"))?.Value ?? "", scope.Element(S("ScopeItem"))?.Value ?? "")).ToArray();
    }

    public async Task<string?> GetCameraNameAsync(CancellationToken cancellationToken = default)
    {
        var item = (await GetScopesAsync(cancellationToken)).Select(x => x.Item)
            .FirstOrDefault(x => x.StartsWith("onvif://www.onvif.org/name/", StringComparison.OrdinalIgnoreCase));
        return item is null ? null : Uri.UnescapeDataString(item["onvif://www.onvif.org/name/".Length..]);
    }

    /// <summary>The camera's make/model/firmware/serial. Also serves as a lightweight authenticated
    /// probe: it fails if the credentials are wrong. Fields are matched by local name to tolerate
    /// namespace quirks across vendors.</summary>
    public async Task<OnvifDeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("GetDeviceInformation", new XElement(D("GetDeviceInformation")), cancellationToken);
        string Value(string local) => response.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value?.Trim() ?? "";
        return new OnvifDeviceInformation(Value("Manufacturer"), Value("Model"),
            Value("FirmwareVersion"), Value("SerialNumber"), Value("HardwareId"));
    }

    public async Task SetCameraNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Camera name is required.", nameof(name));
        var scopes = await GetScopesAsync(cancellationToken);
        var configurable = scopes.Where(x => x.Definition.Equals("Configurable", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Item).Where(x => !x.StartsWith("onvif://www.onvif.org/name/", StringComparison.OrdinalIgnoreCase))
            .Append("onvif://www.onvif.org/name/" + Uri.EscapeDataString(name.Trim())).ToArray();
        await SendAsync("SetScopes", new XElement(D("SetScopes"),
            configurable.Select(x => new XElement(D("Scopes"), x))), cancellationToken);
        var confirmed = await GetCameraNameAsync(cancellationToken);
        if (!string.Equals(confirmed, name.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Camera did not retain the requested ONVIF name scope.");
    }

    public Task SetNtpTimeAsync(string posixTimeZone, bool daylightSavings = true,
        CancellationToken cancellationToken = default) => SetTimeAsync("NTP", daylightSavings, posixTimeZone, null, cancellationToken);

    public Task SetNtpTimeAsync(CancellationToken cancellationToken = default) =>
        SetNtpFromComputerTimeZoneAsync(cancellationToken);

    public Task SetNtpFromComputerTimeZoneAsync(CancellationToken cancellationToken = default)
    {
        var zone = ResolveTimeZone(TimeZoneInfo.Local);
        return SetNtpTimeAsync(zone.PosixValue, zone.DaylightSavings, cancellationToken);
    }

    public static OnvifTimeZone ResolveTimeZone(TimeZoneInfo zone)
    {
        var id = zone.Id;
        var mapping = id switch {
            "Eastern Standard Time" => ("EST5EDT,M3.2.0,M11.1.0", true),
            "Central Standard Time" => ("CST6CDT,M3.2.0,M11.1.0", true),
            "Mountain Standard Time" => ("MST7MDT,M3.2.0,M11.1.0", true),
            "US Mountain Standard Time" => ("MST7", false),
            "Pacific Standard Time" => ("PST8PDT,M3.2.0,M11.1.0", true),
            "UTC" => ("UTC0", false),
            _ => throw new NotSupportedException(
                $"Surveil does not yet have a verified ONVIF POSIX mapping for computer time zone '{id}'.")
        };
        return new OnvifTimeZone(id, mapping.Item1, mapping.Item2);
    }

    public Task SetManualTimeAsync(DateTimeOffset utcTime, string posixTimeZone, bool daylightSavings = true,
        CancellationToken cancellationToken = default) =>
        SetTimeAsync("Manual", daylightSavings, posixTimeZone, utcTime.ToUniversalTime(), cancellationToken);

    private async Task SetTimeAsync(string type, bool daylight, string zone, DateTimeOffset? utc,
        CancellationToken cancellationToken)
    {
        var body = new XElement(D("SetSystemDateAndTime"), new XElement(D("DateTimeType"), type),
            new XElement(D("DaylightSavings"), daylight),
            new XElement(D("TimeZone"), new XElement(S("TZ"), zone)));
        if (utc.HasValue) body.Add(new XElement(D("UTCDateTime"),
            new XElement(S("Time"), new XElement(S("Hour"), utc.Value.Hour),
                new XElement(S("Minute"), utc.Value.Minute), new XElement(S("Second"), utc.Value.Second)),
            new XElement(S("Date"), new XElement(S("Year"), utc.Value.Year),
                new XElement(S("Month"), utc.Value.Month), new XElement(S("Day"), utc.Value.Day))));
        await SendAsync("SetSystemDateAndTime", body, cancellationToken);
    }

    private async Task<XElement> SendAsync(string operation, XElement body, CancellationToken cancellationToken) =>
        await OnvifSoap.SendAsync(http, Endpoint, DeviceNamespace, operation, body, cancellationToken);
    private static XName D(string name) => XName.Get(name, DeviceNamespace);
    private static XName S(string name) => XName.Get(name, SchemaNamespace);
    public void Dispose() { if (ownsHttp) http.Dispose(); }

    private static void ValidateHostname(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 253 ||
            name.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Hostname must contain valid DNS labels.", nameof(name));
    }
}
