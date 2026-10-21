using System.Net;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class NetworkAndInventoryTests
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
        var config = new SurveilConfig
        {
            Sites = [new Site {
            Name = "Example", Ranges = [
                new NetworkRange { Name = "One", Cidr = "192.168.10.0/24" },
                new NetworkRange { Name = "Two", Cidr = "192.168.10.0/25" }
            ]
        }]
        };
        Assert.Contains("overlaps", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config)).Message);
    }

    [Fact]
    public void LocatesSiteAndArea()
    {
        var config = new SurveilConfig
        {
            Sites = [new Site {
            Name = "Example Hall", Ranges = [new NetworkRange { Name = "Second Floor", Cidr = "10.200.62.0/24" }]
        }]
        };
        Assert.Equal(("Example Hall", "Second Floor"), NetworkRanges.Locate(config, IPAddress.Parse("10.200.62.137")));
    }

    [Fact]
    public void MatchesCamerasByIdentityNotAddress()
    {
        var previous = new Inventory
        {
            LastScan = 100,
            Cameras =
            [
                Record("192.168.50.5", "S-A"),  // still at .5
                Record("192.168.50.6", "S-B"),  // moves to .8
                Record("192.168.50.7", "S-C"),  // gone
                Record("192.168.99.9", "S-E"),  // outside this scan
            ],
        };

        var (inventory, statuses) = Diff(previous,
            [Found("192.168.50.5", "S-A"), Found("192.168.50.8", "S-B"), Found("192.168.50.9", "S-D")]);

        Assert.Equal(CameraPresenceStatus.Present, statuses.Single(item => item.SerialNumber == "S-A").Presence);
        var moved = statuses.Single(item => item.SerialNumber == "S-B");
        Assert.Equal(CameraPresenceStatus.Moved, moved.Presence);
        Assert.Equal(("192.168.50.8", "192.168.50.6"), (moved.Ip, moved.PreviousIp));
        Assert.Equal(CameraPresenceStatus.Absent, statuses.Single(item => item.SerialNumber == "S-C").Presence);
        Assert.Equal(CameraPresenceStatus.New, statuses.Single(item => item.SerialNumber == "S-D").Presence);
        Assert.DoesNotContain(statuses, item => item.SerialNumber == "S-E");

        var record = inventory.Cameras.Single(camera => camera.SerialNumber == "S-B");
        Assert.Equal(("192.168.50.8", (ulong)100), (record.Ip, record.FirstSeen));  // same camera: history kept
        Assert.Equal(5, inventory.Cameras.Count);
    }

    [Fact]
    public void ReportsAReplacementAtTheSameAddressAsMissingPlusNew()
    {
        var (_, statuses) = Diff(new Inventory { Cameras = [Record("192.168.50.5", "OLD")] },
            [Found("192.168.50.5", "REPLACEMENT")]);

        Assert.Equal(CameraPresenceStatus.Absent, statuses.Single(item => item.SerialNumber == "OLD").Presence);
        Assert.Equal(CameraPresenceStatus.New, statuses.Single(item => item.SerialNumber == "REPLACEMENT").Presence);
    }

    [Fact]
    public void UnidentifiedDeviceAtAKnownAddressIsUnverifiedNotMissing()
    {
        var (inventory, statuses) = Diff(new Inventory { Cameras = [Record("192.168.50.5", "S-A")] },
            [Unidentified("192.168.50.5", "sign-in failed")]);

        var status = Assert.Single(statuses);
        Assert.Equal(CameraPresenceStatus.Unverified, status.Presence);
        Assert.Equal(("S-A", "sign-in failed"), (status.SerialNumber, status.Problem));
        Assert.Equal((ulong)100, Assert.Single(inventory.Cameras).LastSeen);  // not confirmed, so not "seen"
    }

    [Fact]
    public void ReportsUnidentifiedDevicesWithoutTrackingThem()
    {
        var (inventory, statuses) = Diff(new Inventory(), [Unidentified("192.168.50.9", "no ONVIF service")]);

        Assert.Equal(CameraPresenceStatus.Unidentified, Assert.Single(statuses).Presence);
        Assert.Empty(inventory.Cameras);
    }

    [Fact]
    public void AdoptsAPreIdentityRecordsHistoryWhenACameraIsIdentifiedThere()
    {
        var legacy = Record("192.168.50.5", serial: "");  // saved before cameras were identified
        legacy.FirstSeen = 50;

        var (inventory, statuses) = Diff(new Inventory { Cameras = [legacy] }, [Found("192.168.50.5", "S-A")]);

        Assert.Equal(CameraPresenceStatus.Present, Assert.Single(statuses).Presence);
        var record = Assert.Single(inventory.Cameras);
        Assert.Equal(("S-A", (ulong)50), (record.SerialNumber, record.FirstSeen));
    }

    [Fact]
    public void DoesNotTrustASerialReportedByTwoAddresses()
    {
        var (inventory, statuses) = Diff(new Inventory(),
            [Found("192.168.50.5", "DUP"), Found("192.168.50.6", "DUP")]);

        Assert.Equal(CameraPresenceStatus.New, statuses.Single(item => item.Ip == "192.168.50.5").Presence);
        var second = statuses.Single(item => item.Ip == "192.168.50.6");
        Assert.Equal(CameraPresenceStatus.Unidentified, second.Presence);
        Assert.Contains("192.168.50.5", second.Problem);
        Assert.Single(inventory.Cameras);
    }

    private static readonly ISet<IPAddress> Scanned = NetworkRanges.ExpandPrivate(["192.168.50.0/28"]).ToHashSet();

    private static (Inventory Inventory, IReadOnlyList<CameraStatus> Statuses) Diff(
        Inventory previous, IEnumerable<FoundCamera> found) => InventoryComparer.Diff(previous, found, Scanned, 200);

    private static CameraRecord Record(string ip, string serial) => new()
    {
        Ip = ip,
        Manufacturer = "Acme",
        SerialNumber = serial,
        Site = "Hall",
        Area = "First Floor",
        FirstSeen = 100,
        LastSeen = 100
    };

    private static FoundCamera Found(string ip, string serial) => new()
    {
        Ip = ip, Site = "Hall", Area = "First Floor", Identity = new CameraIdentity("Acme", "Cam", serial),
    };

    private static FoundCamera Unidentified(string ip, string problem) => new()
    {
        Ip = ip, Site = "Hall", Area = "First Floor", Problem = problem,
    };
}
