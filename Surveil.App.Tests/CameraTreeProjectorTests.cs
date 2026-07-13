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
}
