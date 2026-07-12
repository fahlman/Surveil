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
