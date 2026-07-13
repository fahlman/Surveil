using System.Net;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class NetworkMapIndexTests
{
    [Fact]
    public void LocatesWithoutExpandingTheRange()
    {
        var config = new SurveilConfig
        {
            Sites = [new Site
            {
                Name = "North",
                Ranges = [new NetworkRange { Name = "Second", Cidr = "10.42.64.0/18" }],
            }],
        };

        var location = new NetworkMapIndex(config).Locate(IPAddress.Parse("10.42.100.25"));

        Assert.NotNull(location);
        Assert.Equal("North", location.Site);
        Assert.Equal("Second", location.Area);
        Assert.Equal(0, location.SiteIndex);
        Assert.Equal(0, location.RangeIndex);
    }

    [Theory]
    [InlineData("10.0.0.0/24", true)]
    [InlineData("not-a-range", false)]
    [InlineData("10.0.0.0/99", false)]
    public void ReportsRangeValidity(string value, bool expected) =>
        Assert.Equal(expected, NetworkRanges.IsValid(value));
}
