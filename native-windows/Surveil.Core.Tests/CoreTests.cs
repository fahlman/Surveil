using System.Net;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class CoreTests
{
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
}
