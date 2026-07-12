using System.Net;
using System.Net.Sockets;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class CoreTests
{
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
            var config = new SurveilConfig { Buildings = [new Building {
                Name = "Example", Ranges = [new NetworkRange { Name = "Main", Cidr = "192.168.10.0/24" }]
            }]};
            await store.SaveConfigAsync(config);
            Assert.Equal("Example", (await store.LoadConfigAsync()).Buildings[0].Name);
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
    public void ExpandsPrivateRangesAndRejectsHugeScans()
    {
        var addresses = NetworkRanges.ExpandPrivate(["192.168.10.0/30", "192.168.11.0/30"]);
        Assert.Equal(4, addresses.Count);
        Assert.Throws<InvalidOperationException>(() => NetworkRanges.ExpandPrivate(["10.0.0.0/8"]));
    }

    [Fact]
    public void ValidatesOverlappingRanges()
    {
        var config = new SurveilConfig { Buildings = [new Building {
            Name = "Example", Ranges = [
                new NetworkRange { Name = "One", Cidr = "192.168.10.0/24" },
                new NetworkRange { Name = "Two", Cidr = "192.168.10.0/25" }
            ]
        }]};
        Assert.Contains("overlaps", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config)).Message);
    }

    [Fact]
    public void LocatesBuildingAndArea()
    {
        var config = new SurveilConfig { Buildings = [new Building {
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
        Ip = ip, Building = "Hall", Area = "First Floor", FirstSeen = 100, LastSeen = 100
    };
    private static FoundCamera Found(string ip) => new() { Ip = ip, Building = "Hall", Area = "First Floor" };

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
