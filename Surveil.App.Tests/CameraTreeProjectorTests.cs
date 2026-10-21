using System.Collections.ObjectModel;
using Surveil.App.Services;
using Surveil.App.ViewModels;
using Surveil.Core;
using Xunit;

namespace Surveil.App.Tests;

public sealed class CameraTreeProjectorTests
{
    [Fact]
    public void PlacesMappedAndUnmappedInventoryWithoutExpandingRanges()
    {
        var sites = new ObservableCollection<SiteItem>
        {
            new(new Site
            {
                Name = "North",
                Ranges = [new NetworkRange { Name = "Main", Cidr = "10.10.0.0/16" }],
            }),
        };
        var unmapped = new ObservableCollection<CameraItem>();
        var inventory = new Inventory
        {
            Cameras =
            [
                new CameraRecord { Ip = "10.10.200.2" },
                new CameraRecord { Ip = "192.168.1.2" },
            ],
        };

        CameraTreeProjector.PopulateInventory(sites, unmapped, inventory, () => { });

        Assert.Equal("10.10.200.2", Assert.Single(sites[0].Children[0].Cameras).Ip);
        Assert.Equal("192.168.1.2", Assert.Single(unmapped).Ip);
    }

    [Fact]
    public void ScanResultsReplaceRangeCamerasAndCountMissingSeparately()
    {
        var range = new NetworkRangeItem(new NetworkRange { Name = "Main", Cidr = "192.168.10.0/24" });
        CameraTreeProjector.Add(range.Cameras, new CameraStatus { Ip = "192.168.10.99" }, null, () => { });
        CameraStatus Status(string ip, CameraPresenceStatus presence, string previousIp = "", string problem = "") =>
            new() { Ip = ip, Presence = presence, PreviousIp = previousIp, Problem = problem };

        var tally = CameraTreeProjector.ReplaceScanResults([range],
        [
            Status("192.168.10.5", CameraPresenceStatus.New),
            Status("192.168.10.6", CameraPresenceStatus.Present),
            Status("192.168.10.7", CameraPresenceStatus.Absent),
            Status("192.168.10.8", CameraPresenceStatus.Moved, previousIp: "192.168.10.3"),
            Status("192.168.10.9", CameraPresenceStatus.Unidentified, problem: "sign-in failed"),
            Status("10.0.0.1", CameraPresenceStatus.Present),  // outside every scanned range
        ], () => { });

        Assert.Equal(new ScanTally(Responding: 3, New: 1, Moved: 1, Missing: 1, Unidentified: 1), tally);
        Assert.Equal(["192.168.10.5", "192.168.10.6", "192.168.10.7", "192.168.10.8", "192.168.10.9"],
            range.Cameras.Select(camera => camera.Ip));
        var missing = Assert.Single(range.Cameras, camera => camera.IsMissing);
        Assert.Equal("192.168.10.7", missing.Ip);
        Assert.Equal("missing", missing.StatusLine);
        Assert.Equal("moved from 192.168.10.3", range.Cameras.Single(camera => camera.Ip == "192.168.10.8").StatusLine);
        Assert.Equal("not identified: sign-in failed", range.Cameras.Single(camera => camera.Ip == "192.168.10.9").StatusLine);
        Assert.Equal(["192.168.10.5", "192.168.10.6", "192.168.10.8"],
            range.Cameras.Where(camera => camera.ShouldIdentify).Select(camera => camera.Ip));
    }
}
