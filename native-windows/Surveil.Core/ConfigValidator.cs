namespace Surveil.Core;

public static class ConfigValidator
{
    public static void Validate(SurveilConfig config)
    {
        var buildingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedRanges = new List<(string Label, ParsedRange Range)>();

        foreach (var building in config.Buildings)
        {
            var name = building.Name.Trim();
            if (name.Length == 0) throw new InvalidOperationException("every building needs a name");
            if (!buildingNames.Add(name)) throw new InvalidOperationException($"duplicate building name: {name}");
            var areaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var networkRange in building.Ranges)
            {
                var area = networkRange.Name.Trim();
                if (area.Length == 0) throw new InvalidOperationException($"every range in {name} needs a name");
                if (!areaNames.Add(area)) throw new InvalidOperationException($"duplicate range name in {name}: {area}");
                var parsed = NetworkRanges.Parse(networkRange.Cidr);
                if (!IpAddress.IsPrivate(parsed.Network) || !IpAddress.IsPrivate(parsed.Broadcast))
                    throw new InvalidOperationException($"range '{networkRange.Cidr}' in {name} is not entirely private");
                var overlap = parsedRanges.FirstOrDefault(existing =>
                    existing.Range.Contains(parsed.Network) || parsed.Contains(existing.Range.Network));
                if (overlap != default)
                    throw new InvalidOperationException($"range '{networkRange.Cidr}' in {name} overlaps {overlap.Label}");
                parsedRanges.Add(($"{networkRange.Cidr} in {name}", parsed));
            }
        }
    }
}
