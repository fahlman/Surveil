using System.Net;

namespace Surveil.Core;

public static class NetworkRanges
{
    public const int MaxScanAddresses = 65_534;

    internal static ParsedRange Parse(string specification)
    {
        var parts = specification.Trim().Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new FormatException($"invalid IPv4 address '{specification}'");

        var prefix = parts.Length == 1 ? 32 :
            int.TryParse(parts[1], out var parsed) && parsed is >= 0 and <= 32
                ? parsed
                : throw new FormatException($"invalid CIDR prefix '{specification}'");
        var value = IpAddress.ToUInt32(address);
        var mask = prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);
        var network = value & mask;
        return new ParsedRange(network, network | ~mask, prefix);
    }

    public static bool IsValid(string specification)
    {
        try
        {
            _ = Parse(specification);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<IPAddress> ExpandPrivate(IEnumerable<string> specifications)
    {
        var addresses = new SortedSet<uint>();
        foreach (var specification in specifications)
        {
            var range = Parse(specification);
            if (range.HostCount > MaxScanAddresses)
                throw new InvalidOperationException($"scan exceeds the safety limit of {MaxScanAddresses} addresses");

            var first = range.Prefix >= 31 ? range.Network : range.Network + 1;
            var last = range.Prefix >= 31 ? range.Broadcast : range.Broadcast - 1;
            for (ulong value = first; value <= last; value++)
            {
                var address = (uint)value;
                if (IpAddress.IsPrivate(address)) addresses.Add(address);
                if (addresses.Count > MaxScanAddresses)
                    throw new InvalidOperationException($"scan exceeds the safety limit of {MaxScanAddresses} addresses");
            }
        }

        if (addresses.Count == 0)
            throw new InvalidOperationException("selected targets contain no private IPv4 addresses");
        return addresses.Select(IpAddress.FromUInt32).ToArray();
    }

    public static (string Site, string Area)? Locate(SurveilConfig config, IPAddress address)
    {
        var value = IpAddress.ToUInt32(address);
        foreach (var site in config.Sites)
            foreach (var range in site.Ranges)
                if (Parse(range.Cidr).Contains(value)) return (site.Name, range.Name);
        return null;
    }
}

/// <summary>A parsed, allocation-free lookup from IPv4 address to an arbitrary mapped value.</summary>
public sealed class IpRangeMap<T> where T : class
{
    private readonly IReadOnlyList<(ParsedRange Range, T Value)> entries;

    public IpRangeMap(IEnumerable<(string Cidr, T Value)> ranges)
    {
        entries = ranges.Select(item => (NetworkRanges.Parse(item.Cidr), item.Value)).ToArray();
    }

    public T? Find(IPAddress address)
    {
        var value = IpAddress.ToUInt32(address);
        foreach (var entry in entries)
            if (entry.Range.Contains(value)) return entry.Value;
        return default;
    }

    public bool TryFind(IPAddress address, out T value)
    {
        var match = Find(address);
        if (match is not null)
        {
            value = match;
            return true;
        }
        value = default!;
        return false;
    }
}

public sealed record NetworkLocation(int SiteIndex, int RangeIndex, string Site, string Area, string Cidr);

/// <summary>An immutable index of the configured network map; every CIDR is parsed once.</summary>
public sealed class NetworkMapIndex
{
    private readonly IpRangeMap<NetworkLocation> ranges;

    public NetworkMapIndex(SurveilConfig config)
    {
        ranges = new IpRangeMap<NetworkLocation>(
            config.Sites.SelectMany((site, siteIndex) => site.Ranges.Select((range, rangeIndex) =>
                (range.Cidr, new NetworkLocation(siteIndex, rangeIndex, site.Name, range.Name, range.Cidr)))));
    }

    public NetworkLocation? Locate(IPAddress address) => ranges.Find(address);
}
