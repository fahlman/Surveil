using System.Text.Json;

namespace Surveil.Core;

internal static class ConfigMigration
{
    public static SurveilConfig Deserialize(string json, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array) return MigrateOriginalArray(root);
        if (root.TryGetProperty("network", out _)) return MigrateOctetTemplate(root);
        if (RangesAreStrings(root)) return MigrateBareRanges(root);
        // Legacy "buildings" key (object ranges) → the current "sites" schema.
        if (root.TryGetProperty("buildings", out _)) return MigrateCurrentBuildings(root);
        return JsonSerializer.Deserialize<SurveilConfig>(json, options) ?? new SurveilConfig();
    }

    private static SurveilConfig MigrateCurrentBuildings(JsonElement root)
    {
        var config = new SurveilConfig();
        foreach (var item in root.GetProperty("buildings").EnumerateArray())
        {
            var site = new Site { Name = Text(item, "name"), Notes = Text(item, "notes") };
            if (item.TryGetProperty("ranges", out var ranges))
                site.Ranges.AddRange(ranges.EnumerateArray()
                    .Select(range => new NetworkRange { Name = Text(range, "name"), Cidr = Text(range, "cidr") }));
            config.Sites.Add(site);
        }
        return config;
    }

    private static bool RangesAreStrings(JsonElement root)
    {
        if (!root.TryGetProperty("buildings", out var buildings)) return false;
        foreach (var building in buildings.EnumerateArray())
            if (building.TryGetProperty("ranges", out var ranges) && ranges.GetArrayLength() > 0)
                return ranges[0].ValueKind == JsonValueKind.String;
        return false;
    }

    private static SurveilConfig MigrateBareRanges(JsonElement root)
    {
        var config = new SurveilConfig();
        foreach (var item in root.GetProperty("buildings").EnumerateArray())
        {
            var building = new Site
            {
                Name = Text(item, "name"),
                Notes = Text(item, "notes")
            };
            if (item.TryGetProperty("ranges", out var ranges))
                building.Ranges.AddRange(ranges.EnumerateArray().Select(range =>
                {
                    var cidr = range.GetString() ?? "";
                    return new NetworkRange { Name = FloorNameFromCidr(cidr), Cidr = cidr };
                }));
            config.Sites.Add(building);
        }
        return config;
    }

    private static SurveilConfig MigrateOriginalArray(JsonElement root)
    {
        var config = new SurveilConfig();
        foreach (var item in root.EnumerateArray())
        {
            var octet = item.GetProperty("octet").GetInt32();
            var building = new Site { Name = Text(item, "name"), Notes = Text(item, "notes") };
            if (Boolean(item, "basement")) building.Ranges.Add(FloorRange(octet, 68));
            if (Boolean(item, "ground")) building.Ranges.Add(FloorRange(octet, 69));
            var floors = item.TryGetProperty("floors", out var value) ? value.GetInt32() : 0;
            for (var floor = 1; floor <= floors; floor++) building.Ranges.Add(FloorRange(octet, 60 + floor));
            config.Sites.Add(building);
        }
        return config;
    }

    private static SurveilConfig MigrateOctetTemplate(JsonElement root)
    {
        var tokens = root.GetProperty("network").GetProperty("octets").EnumerateArray()
            .Select(item => item.GetString() ?? "").ToArray();
        var codes = root.GetProperty("levels").EnumerateArray().ToDictionary(
            item => Text(item, "label"), item => item.GetProperty("code").GetInt32());
        var config = new SurveilConfig();
        foreach (var item in root.GetProperty("buildings").EnumerateArray())
        {
            var octet = item.GetProperty("octet").GetInt32();
            var building = new Site { Name = Text(item, "name"), Notes = Text(item, "notes") };
            if (item.TryGetProperty("levels", out var levels))
                foreach (var level in levels.EnumerateArray().Select(value => value.GetString() ?? ""))
                    if (codes.TryGetValue(level, out var code))
                        building.Ranges.Add(new NetworkRange { Name = level, Cidr = ExpandTokens(tokens, octet, code) });
            config.Sites.Add(building);
        }
        if (root.TryGetProperty("subnets", out var subnets) && subnets.GetArrayLength() > 0)
            config.Sites.Add(new Site
            {
                Name = "Subnets",
                Ranges = subnets.EnumerateArray()
                .Select(value => new NetworkRange { Cidr = value.GetString() ?? "" }).ToList()
            });
        return config;
    }

    private static string ExpandTokens(string[] tokens, int building, int level) => string.Join('.',
        tokens.Select(token => token.Trim().ToLowerInvariant() switch
        {
            "building" => building.ToString(),
            "level" => level.ToString(),
            "host" => "0",
            _ => token
        })) + "/24";

    private static NetworkRange FloorRange(int building, int code) => new()
    {
        Name = FloorName(code),
        Cidr = $"10.{building}.{code}.0/24"
    };

    private static string FloorNameFromCidr(string cidr) =>
        cidr.Split('.').Skip(2).FirstOrDefault() is { } part && int.TryParse(part.Split('/')[0], out var code)
            ? FloorName(code) : "";

    private static string FloorName(int code) => code switch
    {
        61 => "First Floor",
        62 => "Second Floor",
        63 => "Third Floor",
        64 => "Fourth Floor",
        65 => "Fifth Floor",
        66 => "Sixth Floor",
        67 => "Seventh Floor",
        68 => "Basement",
        69 => "Ground Floor",
        _ => ""
    };

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    private static bool Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
