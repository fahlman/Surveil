using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task JsonStoreRoundTripsConfigurationAndInventory()
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new JsonStore(directory);
            var config = new SurveilConfig
            {
                Sites = [new Site {
                Name = "Example", Ranges = [new NetworkRange { Name = "Main", Cidr = "192.168.10.0/24" }]
            }]
            };
            await store.SaveConfigAsync(config);
            Assert.Equal("Example", (await store.LoadConfigAsync()).Sites[0].Name);
            await store.SaveInventoryAsync(new Inventory { LastScan = 123, Cameras = [Record("192.168.10.5")] });
            Assert.Equal((ulong)123, (await store.LoadInventoryAsync()).LastScan);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory).Select(Path.GetFileName),
                name => name?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MigratesEveryLegacyConfigurationShape()
    {
        var directory = TemporaryDirectory();
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
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LoadsLegacyBuildingsFileAsSites()
    {
        var directory = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, JsonStore.LegacyConfigFileName),
                "{\"buildings\":[{\"name\":\"Hall\",\"ranges\":[{\"name\":\"Main\",\"cidr\":\"10.20.0.0/24\"}],\"notes\":\"\"}]}");
            var config = await new JsonStore(directory).LoadConfigAsync();
            Assert.Equal("Hall", config.Sites.Single().Name);
            Assert.Equal("10.20.0.0/24", config.Sites.Single().Ranges.Single().Cidr);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string TemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"surveil-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static CameraRecord Record(string ip) => new()
    {
        Ip = ip,
        Site = "Hall",
        Area = "First Floor",
        FirstSeen = 100,
        LastSeen = 100,
    };
}
