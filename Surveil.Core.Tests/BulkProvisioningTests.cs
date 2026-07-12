using System.Collections.Concurrent;
using System.Net;
using Surveil.Core;
using Xunit;

namespace Surveil.Core.Tests;

public sealed class BulkProvisioningTests
{
    [Fact]
    public async Task DerivesIdentityFromBuildingMapThenAppliesAndVerifies()
    {
        var config = Config("Example Hall", "Second Floor", "10.200.62.0/24");
        var devices = new ConcurrentDictionary<string, RecordingDevice>();
        var service = new BulkProvisioningService(config, endpoint => devices[endpoint.Host] = new RecordingDevice());

        var targets = service.TargetsFromAddresses([IPAddress.Parse("10.200.62.137")]);

        var plan = Assert.Single(service.Plan(targets));
        Assert.Equal("Example Hall Second Floor", plan.Name);
        Assert.Equal("example-hall-second-floor-137", plan.Hostname);

        var result = Assert.Single(await service.ProvisionAsync(targets));
        Assert.True(result.Success);
        Assert.Equal("Example Hall", result.Building);
        Assert.Equal(
            ["name=Example Hall Second Floor", "hostname=example-hall-second-floor-137", "ntp=computer-zone"],
            result.Steps);

        var device = devices["10.200.62.137"];
        Assert.Equal("Example Hall Second Floor", device.Name);
        Assert.Equal("example-hall-second-floor-137", device.Hostname);
        Assert.True(device.NtpApplied);
    }

    [Fact]
    public async Task ReportsPerCameraFailuresWithoutStoppingTheBatch()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var service = new BulkProvisioningService(config, endpoint =>
            endpoint.Host.EndsWith(".66")
                ? new RecordingDevice { FailWith = new OnvifException(HttpStatusCode.Unauthorized, "denied") }
                : new RecordingDevice());

        var targets = service.TargetsFromAddresses(
            [IPAddress.Parse("10.200.62.65"), IPAddress.Parse("10.200.62.66")]);
        var results = await service.ProvisionAsync(targets);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(item => item.Address.ToString() == "10.200.62.65").Success);
        var failed = results.Single(item => item.Address.ToString() == "10.200.62.66");
        Assert.False(failed.Success);
        Assert.Contains("authentication", failed.Error ?? "");
        Assert.Empty(failed.Steps);
    }

    [Fact]
    public async Task SkipsCamerasOutsideTheBuildingMapByDefault()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var service = new BulkProvisioningService(config, _ => new RecordingDevice());

        var targets = service.TargetsFromAddresses([IPAddress.Parse("192.168.99.5")]);
        Assert.False(Assert.Single(targets).LocationKnown);

        Assert.Empty(await service.ProvisionAsync(targets));
        Assert.Single(service.Plan(targets, includeUnknownLocation: true));
    }

    [Fact]
    public async Task HonorsDisabledStepsAndReportsProgress()
    {
        var config = Config("Hall", "Main", "10.200.62.0/24");
        var devices = new ConcurrentDictionary<string, RecordingDevice>();
        var service = new BulkProvisioningService(config, endpoint => devices[endpoint.Host] = new RecordingDevice());
        var updates = new List<BulkProvisionProgress>();

        var targets = service.TargetsFromAddresses([IPAddress.Parse("10.200.62.10")]);
        var results = await service.ProvisionAsync(targets,
            new BulkProvisionOptions { SetHostname = false, SetNtp = false },
            new InlineProgress<BulkProvisionProgress>(updates.Add));

        var device = devices["10.200.62.10"];
        Assert.Equal("Hall Main", device.Name);
        Assert.Null(device.Hostname);
        Assert.False(device.NtpApplied);
        Assert.Equal(["name=Hall Main"], Assert.Single(results).Steps);
        Assert.Equal(new BulkProvisionProgress(1, 1, 1, 0), updates[^1]);
    }

    [Fact]
    public void SlugifiesPunctuationIntoASingleDnsSafeLabel()
    {
        var config = Config("Loading Dock", "Bay #2 / North", "10.200.63.0/24");
        var service = new BulkProvisioningService(config, _ => new RecordingDevice());

        var plan = Assert.Single(service.Plan(service.TargetsFromAddresses([IPAddress.Parse("10.200.63.5")])));
        Assert.Equal("loading-dock-bay-2-north-5", plan.Hostname);
    }

    private static SurveilConfig Config(string building, string area, string cidr) =>
        new() { Buildings = [new Building { Name = building, Ranges = [new NetworkRange { Name = area, Cidr = cidr }] }] };

    private sealed class RecordingDevice : IProvisionableDevice
    {
        public Exception? FailWith { get; init; }
        public string? Name { get; private set; }
        public string? Hostname { get; private set; }
        public bool NtpApplied { get; private set; }

        public Task SetNameAsync(string name, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            Name = name;
            return Task.CompletedTask;
        }

        public Task<bool> SetHostnameAsync(string hostname, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            Hostname = hostname;
            return Task.FromResult(false);
        }

        public Task SetNtpAsync(string? posixTimeZone, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            NtpApplied = true;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
