using System.Collections.ObjectModel;
using Surveil.App.Services;
using Surveil.App.ViewModels;
using Surveil.Core;
using Xunit;

namespace Surveil.App.Tests;

public sealed class CameraFacetServiceTests
{
    [Fact]
    public void AppliesSelectedFacetAndHidesEmptyTreeBranches()
    {
        var sites = new ObservableCollection<SiteItem>
        {
            new(new Site
            {
                Name = "North",
                Ranges = [new NetworkRange { Name = "Main", Cidr = "10.0.0.0/24" }],
            }),
        };
        var range = sites[0].Children[0];
        range.Cameras.Add(Camera("10.0.0.1", "Axis"));
        range.Cameras.Add(Camera("10.0.0.2", "i-PRO"));
        var facets = new ObservableCollection<FacetGroup>();
        var cameras = range.Cameras.ToList();
        CameraFacetService.Rebuild(facets, cameras, () => { });
        facets.Single(group => group.Name == "Manufacturer").Options
            .Single(option => option.Value == "Axis").IsSelected = true;

        var state = CameraFacetService.Apply(cameras, sites, [], facets);

        Assert.True(state.Active);
        Assert.True(cameras[0].IsVisible);
        Assert.False(cameras[1].IsVisible);
        Assert.True(sites[0].IsVisible);
        Assert.Equal("1 of 2 cameras", state.Summary);
    }

    private static CameraItem Camera(string ip, string manufacturer)
    {
        var camera = new CameraItem(new CameraStatus
        {
            Ip = ip,
            Presence = CameraPresenceStatus.Discovered,
        });
        camera.ApplyFeatures(new CameraFeatures(
            new OnvifDeviceInformation(manufacturer, "Model", "1", ip, ""),
            OnvifMediaGeneration.Media2, ["media"], []));
        return camera;
    }
}
