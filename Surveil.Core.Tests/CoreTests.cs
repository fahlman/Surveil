using System.Net;
using System.Net.Sockets;
using System.Text;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void MapsWindowsEasternTimeToOnvifPosixRules()
    {
        var windows = TimeZoneInfo.CreateCustomTimeZone("Eastern Standard Time", TimeSpan.FromHours(-5),
            "Eastern", "Eastern");
        var result = OnvifDeviceClient.ResolveTimeZone(windows);
        Assert.Equal("EST5EDT,M3.2.0,M11.1.0", result.PosixValue);
        Assert.True(result.DaylightSavings);
    }

    [Fact]
    public void TargetsFromHonorsAdvertisedEndpointAndFallsBackToStandardPath()
    {
        var config = new SurveilConfig
        {
            Sites = [new Site { Name = "Kettler Hall", Ranges = [new NetworkRange { Name = "Ground", Cidr = "10.25.69.0/24" }] }],
        };
        var service = new BulkProvisioningService(config, "admin", "pw");

        var advertised = new Uri("http://10.25.69.14:8000/onvif/device_service");
        var targets = service.TargetsFrom(new (IPAddress, Uri?)[]
        {
            (IPAddress.Parse("10.25.69.14"), advertised),  // advertised endpoint is used verbatim
            (IPAddress.Parse("10.25.69.20"), null),         // null falls back to the standard path
        });

        Assert.Equal(2, targets.Count);
        Assert.Equal(advertised, targets[0].DeviceEndpoint);
        Assert.Equal("Kettler Hall", targets[0].Site);      // still located in the site map
        Assert.Equal("Ground", targets[0].Area);
        Assert.Equal(new Uri("http://10.25.69.20/onvif/device_service"), targets[1].DeviceEndpoint);
    }

    [Fact]
    public async Task DeviceManagementReadsDeviceInformation()
    {
        var handler = new SoapHandler(request =>
            request.Contains("GetDeviceInformation") ? DeviceInformationResponse : DeviceEmptyResponse);
        using var client = new OnvifDeviceClient(new Uri("http://camera/onvif/device_service"), new HttpClient(handler));
        var info = await client.GetDeviceInformationAsync();
        Assert.Equal("Hikvision", info.Manufacturer);
        Assert.Equal("DS-2CD2143G0-I", info.Model);
        Assert.Equal("V5.6.3", info.FirmwareVersion);
        Assert.Equal("DS-2CD2143G012345", info.SerialNumber);
    }

    [Fact]
    public async Task DeviceManagementSetsAndVerifiesHostnameWithoutDhcpOverride()
    {
        var handler = new SoapHandler(request => request.Contains("SetHostnameFromDHCP") ? HostnameDhcpResponse :
            request.Contains("GetHostname") ? UpdatedHostnameResponse : DeviceEmptyResponse);
        using var client = new OnvifDeviceClient(new Uri("http://camera/onvif/device_service"), new HttpClient(handler));
        var result = await client.SetAndVerifyHostnameAsync("loading-dock-01");
        Assert.Equal("loading-dock-01", result.Hostname);
        Assert.True(result.RebootRequired);
        Assert.Contains(handler.Requests, x => x.Contains("<td:FromDHCP>false</td:FromDHCP>"));
    }

    [Fact]
    public async Task DeviceManagementReplacesOnlyConfigurableCameraNameScope()
    {
        var handler = new SoapHandler(request => request.Contains("GetScopes") ? NamedScopesResponse : DeviceEmptyResponse);
        using var client = new OnvifDeviceClient(new Uri("http://camera/onvif/device_service"), new HttpClient(handler));
        Assert.Equal("Loading Dock", await client.GetCameraNameAsync());
        await client.SetCameraNameAsync("Loading Dock");
        var set = handler.Requests.Single(x => x.Contains("SetScopes"));
        Assert.Contains("onvif://www.onvif.org/location/office", set);
        Assert.Contains("onvif://www.onvif.org/name/Loading%20Dock", set);
        Assert.DoesNotContain("Profile/T", set);
    }

    [Fact]
    public async Task Media2UsesCanonicalSupportedCodecName()
    {
        var handler = new SoapHandler(request => request.Contains("GetVideoEncoderConfigurationOptions")
            ? OptionsResponse.Replace("video/H264", "video/H265")
            : request.Contains("GetVideoEncoderConfigurations") ? ConfigurationResponse : EmptyResponse);
        using var client = new OnvifMedia2Client(new Uri("http://camera/onvif/media2"), new HttpClient(handler));
        await client.UpdateVideoEncoderAsync("encoder-1", encoding: "H265");
        Assert.Contains("<tt:Encoding>video/H265</tt:Encoding>",
            handler.Requests.Single(x => x.Contains("SetVideoEncoderConfiguration")));
    }

    [Fact]
    public async Task ImagingValidatesAndUpdatesBrightnessAndWhiteBalance()
    {
        var handler = new SoapHandler(request => request.Contains("GetImagingSettings") ? ImagingSettingsResponse :
            request.Contains("GetOptions") ? ImagingOptionsResponse : ImagingEmptyResponse);
        using var client = new OnvifImagingClient(new Uri("http://camera/onvif/imaging"), new HttpClient(handler));
        await client.UpdateAsync("source-1", 60, OnvifWhiteBalanceMode.Manual, 1.5f, 1.25f);
        var set = handler.Requests.Single(x => x.Contains("SetImagingSettings"));
        Assert.Contains("<tt:Brightness>60</tt:Brightness>", set);
        Assert.Contains("<tt:Mode>MANUAL</tt:Mode>", set);
        Assert.Contains("<tt:CrGain>1.5</tt:CrGain>", set);
        Assert.Contains("ForcePersistence>true", set);
    }

    [Fact]
    public async Task DeviceManagementSetsHostnameAndNtpTime()
    {
        var handler = new SoapHandler(request => request.Contains("GetHostname") ? HostnameResponse : DeviceEmptyResponse);
        using var client = new OnvifDeviceClient(new Uri("http://camera/onvif/device_service"), new HttpClient(handler));
        Assert.Equal("camera-01", await client.GetHostnameAsync());
        await client.SetHostnameAsync("loading-dock-01");
        await client.SetNtpTimeAsync("EST5EDT,M3.2.0,M11.1.0");
        Assert.Contains(handler.Requests, x => x.Contains("<td:Name>loading-dock-01</td:Name>"));
        Assert.Contains(handler.Requests, x => x.Contains("<td:DateTimeType>NTP</td:DateTimeType>") &&
            x.Contains("EST5EDT,M3.2.0,M11.1.0"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetHostnameAsync("bad name"));
    }

    [Fact]
    public async Task ConnectorPrefersMedia2AndReportsAdvertisedProfile()
    {
        var handler = new SoapHandler(_ => ServicesResponse);
        var connector = new OnvifCameraConnector(_ => new HttpClient(handler, false));
        using var camera = await connector.ConnectAsync(new Uri("http://camera/onvif/device_service"),
            ["onvif://www.onvif.org/Profile/T"]);
        Assert.Equal(OnvifMediaGeneration.Media2, camera.Video.Generation);
        Assert.Equal("http://camera/onvif/media2", camera.Video.Endpoint.AbsoluteUri);
        Assert.True(camera.Capabilities.AdvertisesProfileT);
    }

    [Fact]
    public async Task ConnectorFallsBackToMedia1ForLegacyCamera()
    {
        var handler = new SoapHandler(_ => LegacyServicesResponse);
        var connector = new OnvifCameraConnector(_ => new HttpClient(handler, false));
        using var camera = await connector.ConnectAsync(new Uri("http://old-camera/onvif/device_service"));
        Assert.Equal(OnvifMediaGeneration.Media1, camera.Video.Generation);
        Assert.IsType<OnvifMedia1Client>(camera.Video);
    }

    [Fact]
    public async Task Media1ReadsAndPersistentlyUpdatesLegacyEncoder()
    {
        var handler = new SoapHandler(request => request.Contains("GetVideoEncoderConfigurationOptions")
            ? Media1OptionsResponse : request.Contains("GetVideoEncoderConfiguration")
                ? Media1ConfigurationResponse : Media1EmptyResponse);
        using var client = new OnvifMedia1Client(new Uri("http://old-camera/onvif/media"), new HttpClient(handler));
        await client.UpdateVideoEncoderAsync("legacy-encoder", 1280, 720, 15, 2048, 4);
        var set = handler.Requests.Single(x => x.Contains("SetVideoEncoderConfiguration"));
        Assert.Contains("<tt:Width>1280</tt:Width>", set);
        Assert.Contains("<tt:FrameRateLimit>15</tt:FrameRateLimit>", set);
        Assert.Contains("<trt:ForcePersistence>true</trt:ForcePersistence>", set);
    }

    [Fact]
    public async Task Media2ReadsProfilesConfigurationsAndOptions()
    {
        var handler = new SoapHandler(request => request.Contains("GetProfiles") ? ProfilesResponse :
            request.Contains("GetVideoEncoderConfigurationOptions") ? OptionsResponse : ConfigurationResponse);
        using var client = new OnvifMedia2Client(new Uri("http://camera/onvif/media2"), new HttpClient(handler));

        var profile = Assert.Single(await client.GetProfilesAsync());
        Assert.Equal("profile-1", profile.Token);
        Assert.Equal("Main Stream", profile.Name);
        Assert.Equal("encoder-1", profile.VideoEncoderToken);
        Assert.Equal("source-1", profile.VideoSourceToken);
        var configuration = Assert.Single(await client.GetVideoEncoderConfigurationsAsync("encoder-1"));
        Assert.Equal(new OnvifResolution(1920, 1080), configuration.Resolution);
        Assert.Equal(15f, configuration.FrameRateLimit);
        var options = Assert.Single(await client.GetVideoEncoderConfigurationOptionsAsync("encoder-1"));
        Assert.Contains(new OnvifResolution(1280, 720), options.Resolutions);
        Assert.Equal([30f, 15f, 10f], options.FrameRates);
        Assert.Equal(64, options.Bitrate.Minimum);
    }

    [Fact]
    public async Task Media2ValidatesAndWritesVideoEncoderUpdate()
    {
        var handler = new SoapHandler(request => request.Contains("GetVideoEncoderConfigurationOptions")
            ? OptionsResponse : request.Contains("GetVideoEncoderConfigurations") ? ConfigurationResponse : EmptyResponse);
        using var client = new OnvifMedia2Client(new Uri("http://camera/onvif/media2"), new HttpClient(handler));

        await client.UpdateVideoEncoderAsync("encoder-1", 1280, 720, 30, 2048, 4);
        var set = handler.Requests.Single(x => x.Contains("SetVideoEncoderConfiguration"));
        Assert.Contains("<tt:Width>1280</tt:Width>", set);
        Assert.Contains("<tt:Height>720</tt:Height>", set);
        Assert.Contains("<tt:FrameRateLimit>30</tt:FrameRateLimit>", set);
        Assert.Contains("<tt:BitrateLimit>2048</tt:BitrateLimit>", set);
        Assert.Contains("<tt:Quality>4</tt:Quality>", set);
        Assert.Contains("action=\"http://www.onvif.org/ver20/media/wsdl/SetVideoEncoderConfiguration\"",
            handler.ContentTypes[^1]);
    }

    [Fact]
    public async Task Media2RejectsUnsupportedResolutionBeforeWriting()
    {
        var handler = new SoapHandler(request => request.Contains("GetVideoEncoderConfigurationOptions")
            ? OptionsResponse : ConfigurationResponse);
        using var client = new OnvifMedia2Client(new Uri("http://camera/onvif/media2"), new HttpClient(handler));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.UpdateVideoEncoderAsync("encoder-1", 999, 999));
        Assert.DoesNotContain(handler.Requests, x => x.Contains("SetVideoEncoderConfiguration"));
    }

    [Fact]
    public async Task ScannerFindsListenerAndReportsProgress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var updates = new List<ScanProgress>();
            var progress = new InlineProgress<ScanProgress>(updates.Add);
            var found = await new CameraScanner().ScanAsync(
                [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")], port, 2,
                TimeSpan.FromMilliseconds(300), progress);
            Assert.Contains(IPAddress.Loopback, found);
            Assert.Equal(2, updates[^1].Scanned);
            Assert.Equal(1, updates[^1].Found);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task JsonStoreRoundTripsConfigurationAndInventory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"surveil-tests-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonStore(directory);
            var config = new SurveilConfig { Sites = [new Site {
                Name = "Example", Ranges = [new NetworkRange { Name = "Main", Cidr = "192.168.10.0/24" }]
            }]};
            await store.SaveConfigAsync(config);
            Assert.Equal("Example", (await store.LoadConfigAsync()).Sites[0].Name);
            await store.SaveInventoryAsync(new Inventory { LastScan = 123, Cameras = [Record("192.168.10.5")] });
            Assert.Equal((ulong)123, (await store.LoadInventoryAsync()).LastScan);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory).Select(Path.GetFileName),
                name => name?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void BuildsAndParsesWsDiscoveryMessages()
    {
        Assert.Contains("NetworkVideoTransmitter", WsDiscovery.BuildProbe());
        const string xml = "<d:ProbeMatch xmlns:d='urn:test'><d:XAddrs>http://192.168.10.7/onvif/device_service</d:XAddrs></d:ProbeMatch>";
        Assert.Equal("http://192.168.10.7/onvif/device_service", WsDiscovery.ExtractXAddresses(xml));
    }

    [Fact]
    public void DiscoveryKeepsOnlyOnvifCameras()
    {
        const string camera = "<d:ProbeMatch xmlns:d='urn:disc' xmlns:dn='http://www.onvif.org/ver10/network/wsdl'>" +
            "<d:Types>dn:NetworkVideoTransmitter</d:Types><d:XAddrs>http://10.0.0.5/onvif/device_service</d:XAddrs></d:ProbeMatch>";
        // A Synology NAS / WSD device answers the same probe but advertises a non-camera type.
        const string nas = "<d:ProbeMatch xmlns:d='urn:disc' xmlns:wsdp='urn:wsdp'>" +
            "<d:Types>wsdp:Device</d:Types><d:XAddrs>http://10.0.0.9/</d:XAddrs></d:ProbeMatch>";

        Assert.True(WsDiscovery.IsOnvifCamera(camera));
        Assert.False(WsDiscovery.IsOnvifCamera(nas));
        Assert.False(WsDiscovery.IsOnvifCamera("not xml at all"));
    }

    [Fact]
    public async Task MigratesEveryLegacyConfigurationShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"surveil-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonStore(directory);
            var path = Path.Combine(directory, JsonStore.ConfigFileName);
            await File.WriteAllTextAsync(path,
                "{\"buildings\":[{\"name\":\"Hall\",\"ranges\":[\"10.20.68.0/24\"],\"notes\":\"\"}]}");
            Assert.Equal("Basement", (await store.LoadConfigAsync()).Sites[0].Ranges[0].Name);

            await File.WriteAllTextAsync(path,
                "[{\"octet\":20,\"name\":\"Hall\",\"basement\":false,\"ground\":true,\"floors\":2}]");
            Assert.Equal(3, (await store.LoadConfigAsync()).Sites[0].Ranges.Count);

            await File.WriteAllTextAsync(path,
                "{\"network\":{\"octets\":[\"10\",\"building\",\"level\",\"host\"]}," +
                "\"levels\":[{\"label\":\"Second Floor\",\"code\":62}]," +
                "\"buildings\":[{\"octet\":20,\"name\":\"Hall\",\"levels\":[\"Second Floor\"]}],\"subnets\":[]}");
            Assert.Equal("10.20.62.0/24", (await store.LoadConfigAsync()).Sites[0].Ranges[0].Cidr);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadsLegacyBuildingsFileAsSites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"surveil-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Current object-ranges shape saved under the old filename — must load + migrate to sites.
            await File.WriteAllTextAsync(Path.Combine(directory, JsonStore.LegacyConfigFileName),
                "{\"buildings\":[{\"name\":\"Hall\",\"ranges\":[{\"name\":\"Main\",\"cidr\":\"10.20.0.0/24\"}],\"notes\":\"\"}]}");
            var config = await new JsonStore(directory).LoadConfigAsync();
            Assert.Equal("Hall", config.Sites.Single().Name);
            Assert.Equal("10.20.0.0/24", config.Sites.Single().Ranges.Single().Cidr);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ApplicationServiceOrchestratesScanAndDiscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"surveil-service-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonStore(directory);
            await store.SaveConfigAsync(new SurveilConfig { Sites = [new Site {
                Name = "Hall", Ranges = [new NetworkRange { Name = "Main", Cidr = "192.168.10.0/30" }]
            }]});
            var service = new SurveilService(store, new FakeScanner([IPAddress.Parse("192.168.10.1")]),
                new FakeDiscovery([new WsDiscoveryResponder(IPAddress.Parse("192.168.10.1"), "http://camera/onvif")]));
            var statuses = await service.ScanAsync(["192.168.10.0/30"], 80);
            Assert.Equal("new", statuses.Single().Status);
            Assert.Equal("Hall", statuses.Single().Site);
            var discovered = await service.DiscoverAsync();
            Assert.Equal("Hall", discovered.Cameras.Single().Site);
            Assert.Equal(1, discovered.DistinctSubnets);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    [Fact]
    public void ExpandsPrivateRangesAndRejectsHugeScans()
    {
        var addresses = NetworkRanges.ExpandPrivate(["192.168.10.0/30", "192.168.11.0/30"]);
        Assert.Equal(4, addresses.Count);
        Assert.Throws<InvalidOperationException>(() => NetworkRanges.ExpandPrivate(["10.0.0.0/8"]));
    }

    [Fact]
    public void ValidatesOverlappingRanges()
    {
        var config = new SurveilConfig { Sites = [new Site {
            Name = "Example", Ranges = [
                new NetworkRange { Name = "One", Cidr = "192.168.10.0/24" },
                new NetworkRange { Name = "Two", Cidr = "192.168.10.0/25" }
            ]
        }]};
        Assert.Contains("overlaps", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config)).Message);
    }

    [Fact]
    public void LocatesSiteAndArea()
    {
        var config = new SurveilConfig { Sites = [new Site {
            Name = "Example Hall", Ranges = [new NetworkRange { Name = "Second Floor", Cidr = "10.200.62.0/24" }]
        }]};
        Assert.Equal(("Example Hall", "Second Floor"), NetworkRanges.Locate(config, IPAddress.Parse("10.200.62.137")));
    }

    [Fact]
    public void DiffsNewPresentAndMissingWithinScope()
    {
        var previous = new Inventory { LastScan = 100, Cameras = [
            Record("192.168.50.5"), Record("192.168.50.6"), Record("192.168.99.9")
        ]};
        var scanned = new HashSet<IPAddress> {
            IPAddress.Parse("192.168.50.5"), IPAddress.Parse("192.168.50.6"), IPAddress.Parse("192.168.50.7")
        };
        var (_, statuses) = InventoryComparer.Diff(previous,
            [Found("192.168.50.5"), Found("192.168.50.7")], scanned, 200);
        Assert.Equal("present", statuses.Single(item => item.Ip == "192.168.50.5").Status);
        Assert.Equal("absent", statuses.Single(item => item.Ip == "192.168.50.6").Status);
        Assert.Equal("new", statuses.Single(item => item.Ip == "192.168.50.7").Status);
        Assert.DoesNotContain(statuses, item => item.Ip == "192.168.99.9");
    }

    private static CameraRecord Record(string ip) => new() {
        Ip = ip, Site = "Hall", Area = "First Floor", FirstSeen = 100, LastSeen = 100
    };
    private static FoundCamera Found(string ip) => new() { Ip = ip, Site = "Hall", Area = "First Floor" };

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeScanner(IReadOnlyList<IPAddress> result) : ICameraScanner
    {
        public Task<IReadOnlyList<IPAddress>> ScanAsync(IReadOnlyCollection<IPAddress> addresses, int port,
            int concurrency = 256, TimeSpan? timeout = null, IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FakeDiscovery(IReadOnlyList<WsDiscoveryResponder> result) : IWsDiscovery
    {
        public Task<IReadOnlyList<WsDiscoveryResponder>> DiscoverAsync(TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class SoapHandler(Func<string, string> response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> ContentTypes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(body);
            ContentTypes.Add(request.Content.Headers.ContentType!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(response(body), Encoding.UTF8, "application/soap+xml")
            };
        }
    }

    private const string SoapStart = "<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' " +
        "xmlns:tr2='http://www.onvif.org/ver20/media/wsdl' xmlns:tt='http://www.onvif.org/ver10/schema'><s:Body>";
    private const string SoapEnd = "</s:Body></s:Envelope>";
    private const string ProfilesResponse = SoapStart + "<tr2:GetProfilesResponse><tr2:Profiles token='profile-1'>" +
        "<tr2:Name>Main Stream</tr2:Name><tr2:Configurations>" +
        "<tr2:VideoSource token='vsc-1'><tt:SourceToken>source-1</tt:SourceToken></tr2:VideoSource>" +
        "<tr2:VideoEncoder token='encoder-1'><tt:Encoding>video/H264</tt:Encoding></tr2:VideoEncoder>" +
        "</tr2:Configurations></tr2:Profiles></tr2:GetProfilesResponse>" + SoapEnd;
    private const string ConfigurationResponse = SoapStart +
        "<tr2:GetVideoEncoderConfigurationsResponse><tr2:Configurations token='encoder-1'>" +
        "<tt:Name>Main</tt:Name><tt:Encoding>video/H264</tt:Encoding>" +
        "<tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>" +
        "<tt:RateControl><tt:FrameRateLimit>15</tt:FrameRateLimit><tt:BitrateLimit>4096</tt:BitrateLimit></tt:RateControl>" +
        "<tt:Quality>3</tt:Quality></tr2:Configurations></tr2:GetVideoEncoderConfigurationsResponse>" + SoapEnd;
    private const string OptionsResponse = SoapStart +
        "<tr2:GetVideoEncoderConfigurationOptionsResponse>" +
        "<tr2:Options Encoding='video/H264' FrameRatesSupported='30 15 10' GovLengthRange='1 300'>" +
        "<tt:QualityRange><tt:Min>1</tt:Min><tt:Max>5</tt:Max></tt:QualityRange>" +
        "<tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>" +
        "<tt:ResolutionsAvailable><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:ResolutionsAvailable>" +
        "<tt:BitrateRange><tt:Min>64</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange>" +
        "</tr2:Options></tr2:GetVideoEncoderConfigurationOptionsResponse>" + SoapEnd;
    private const string EmptyResponse = SoapStart + "<tr2:SetVideoEncoderConfigurationResponse/>" + SoapEnd;
    private const string DeviceSoapStart = "<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' " +
        "xmlns:td='http://www.onvif.org/ver10/device/wsdl'><s:Body>";
    private const string ServicesResponse = DeviceSoapStart + "<td:GetServicesResponse>" +
        "<td:Service><td:Namespace>http://www.onvif.org/ver10/media/wsdl</td:Namespace><td:XAddr>http://camera/onvif/media</td:XAddr></td:Service>" +
        "<td:Service><td:Namespace>http://www.onvif.org/ver20/media/wsdl</td:Namespace><td:XAddr>http://camera/onvif/media2</td:XAddr>" +
        "<td:Version><td:Major>2</td:Major><td:Minor>6</td:Minor></td:Version></td:Service>" +
        "</td:GetServicesResponse>" + SoapEnd;
    private const string LegacyServicesResponse = DeviceSoapStart + "<td:GetServicesResponse>" +
        "<td:Service><td:Namespace>http://www.onvif.org/ver10/media/wsdl</td:Namespace>" +
        "<td:XAddr>http://old-camera/onvif/media</td:XAddr></td:Service></td:GetServicesResponse>" + SoapEnd;
    private const string Media1SoapStart = "<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' " +
        "xmlns:trt='http://www.onvif.org/ver10/media/wsdl' xmlns:tt='http://www.onvif.org/ver10/schema'><s:Body>";
    private const string Media1ConfigurationResponse = Media1SoapStart +
        "<trt:GetVideoEncoderConfigurationResponse><trt:Configuration token='legacy-encoder'>" +
        "<tt:Name>Main</tt:Name><tt:Encoding>H264</tt:Encoding>" +
        "<tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution><tt:Quality>3</tt:Quality>" +
        "<tt:RateControl><tt:FrameRateLimit>30</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval>" +
        "<tt:BitrateLimit>4096</tt:BitrateLimit></tt:RateControl><tt:H264><tt:GovLength>30</tt:GovLength>" +
        "<tt:H264Profile>Main</tt:H264Profile></tt:H264></trt:Configuration></trt:GetVideoEncoderConfigurationResponse>" + SoapEnd;
    private const string Media1OptionsResponse = Media1SoapStart +
        "<trt:GetVideoEncoderConfigurationOptionsResponse><trt:Options>" +
        "<tt:QualityRange><tt:Min>1</tt:Min><tt:Max>5</tt:Max></tt:QualityRange><tt:H264>" +
        "<tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>" +
        "<tt:ResolutionsAvailable><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:ResolutionsAvailable>" +
        "<tt:GovLengthRange><tt:Min>1</tt:Min><tt:Max>300</tt:Max></tt:GovLengthRange>" +
        "<tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>30</tt:Max></tt:FrameRateRange>" +
        "<tt:EncodingIntervalRange><tt:Min>1</tt:Min><tt:Max>1</tt:Max></tt:EncodingIntervalRange>" +
        "<tt:H264ProfilesSupported>Main</tt:H264ProfilesSupported><tt:BitrateRange><tt:Min>64</tt:Min>" +
        "<tt:Max>8192</tt:Max></tt:BitrateRange></tt:H264></trt:Options></trt:GetVideoEncoderConfigurationOptionsResponse>" + SoapEnd;
    private const string Media1EmptyResponse = Media1SoapStart + "<trt:SetVideoEncoderConfigurationResponse/>" + SoapEnd;
    private const string ImagingSoapStart = "<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' " +
        "xmlns:timg='http://www.onvif.org/ver20/imaging/wsdl' xmlns:tt='http://www.onvif.org/ver10/schema'><s:Body>";
    private const string ImagingSettingsResponse = ImagingSoapStart +
        "<timg:GetImagingSettingsResponse><timg:ImagingSettings><tt:Brightness>50</tt:Brightness>" +
        "<tt:WhiteBalance><tt:Mode>AUTO</tt:Mode><tt:CrGain>1</tt:CrGain><tt:CbGain>1</tt:CbGain>" +
        "</tt:WhiteBalance></timg:ImagingSettings></timg:GetImagingSettingsResponse>" + SoapEnd;
    private const string ImagingOptionsResponse = ImagingSoapStart +
        "<timg:GetOptionsResponse><timg:ImagingOptions><tt:Brightness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Brightness>" +
        "<tt:WhiteBalance><tt:Mode>AUTO</tt:Mode><tt:Mode>MANUAL</tt:Mode>" +
        "<tt:YrGain><tt:Min>0</tt:Min><tt:Max>4</tt:Max></tt:YrGain>" +
        "<tt:YbGain><tt:Min>0</tt:Min><tt:Max>4</tt:Max></tt:YbGain>" +
        "</tt:WhiteBalance></timg:ImagingOptions></timg:GetOptionsResponse>" + SoapEnd;
    private const string ImagingEmptyResponse = ImagingSoapStart + "<timg:SetImagingSettingsResponse/>" + SoapEnd;
    private const string HostnameResponse = DeviceSoapStart +
        "<td:GetHostnameResponse xmlns:tt='http://www.onvif.org/ver10/schema'><td:HostnameInformation>" +
        "<tt:FromDHCP>false</tt:FromDHCP><tt:Name>camera-01</tt:Name>" +
        "</td:HostnameInformation></td:GetHostnameResponse>" + SoapEnd;
    private const string DeviceEmptyResponse = DeviceSoapStart + "<td:Response/>" + SoapEnd;
    private const string DeviceInformationResponse = DeviceSoapStart +
        "<td:GetDeviceInformationResponse><td:Manufacturer>Hikvision</td:Manufacturer>" +
        "<td:Model>DS-2CD2143G0-I</td:Model><td:FirmwareVersion>V5.6.3</td:FirmwareVersion>" +
        "<td:SerialNumber>DS-2CD2143G012345</td:SerialNumber><td:HardwareId>88</td:HardwareId>" +
        "</td:GetDeviceInformationResponse>" + SoapEnd;
    private const string HostnameDhcpResponse = DeviceSoapStart +
        "<td:SetHostnameFromDHCPResponse><td:RebootNeeded>true</td:RebootNeeded></td:SetHostnameFromDHCPResponse>" + SoapEnd;
    private const string UpdatedHostnameResponse = DeviceSoapStart + "<td:GetHostnameResponse><td:HostnameInformation>" +
        "<tt:FromDHCP xmlns:tt='http://www.onvif.org/ver10/schema'>false</tt:FromDHCP>" +
        "<tt:Name xmlns:tt='http://www.onvif.org/ver10/schema'>loading-dock-01</tt:Name>" +
        "</td:HostnameInformation></td:GetHostnameResponse>" + SoapEnd;
    private const string NamedScopesResponse = DeviceSoapStart + "<td:GetScopesResponse>" +
        "<td:Scopes><tt:ScopeDef xmlns:tt='http://www.onvif.org/ver10/schema'>Fixed</tt:ScopeDef>" +
        "<tt:ScopeItem xmlns:tt='http://www.onvif.org/ver10/schema'>onvif://www.onvif.org/Profile/T</tt:ScopeItem></td:Scopes>" +
        "<td:Scopes><tt:ScopeDef xmlns:tt='http://www.onvif.org/ver10/schema'>Configurable</tt:ScopeDef>" +
        "<tt:ScopeItem xmlns:tt='http://www.onvif.org/ver10/schema'>onvif://www.onvif.org/location/office</tt:ScopeItem></td:Scopes>" +
        "<td:Scopes><tt:ScopeDef xmlns:tt='http://www.onvif.org/ver10/schema'>Configurable</tt:ScopeDef>" +
        "<tt:ScopeItem xmlns:tt='http://www.onvif.org/ver10/schema'>onvif://www.onvif.org/name/Loading%20Dock</tt:ScopeItem></td:Scopes>" +
        "</td:GetScopesResponse>" + SoapEnd;
}
