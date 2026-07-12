using System.Net;

namespace Surveil.Core;

public sealed class SurveilConfig
{
    public List<Site> Sites { get; set; } = [];
}

public sealed class Site
{
    public string Name { get; set; } = "";
    public List<NetworkRange> Ranges { get; set; } = [];
    public string Notes { get; set; } = "";
}

public sealed class NetworkRange
{
    public string Name { get; set; } = "";
    public string Cidr { get; set; } = "";
}

public sealed class Inventory
{
    public ulong LastScan { get; set; }
    public List<CameraRecord> Cameras { get; set; } = [];
}

public sealed class CameraRecord
{
    public string Ip { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public ulong FirstSeen { get; set; }
    public ulong LastSeen { get; set; }
}

public sealed class FoundCamera
{
    public string Ip { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
}

public sealed class CameraStatus
{
    public string Ip { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public ulong FirstSeen { get; set; }
    public ulong LastSeen { get; set; }
    public string Status { get; set; } = "";
}

internal readonly record struct ParsedRange(uint Network, uint Broadcast, int Prefix)
{
    public bool Contains(uint address) => address >= Network && address <= Broadcast;
    public ulong AddressCount => 1UL << (32 - Prefix);
    public ulong HostCount => Prefix >= 31 ? AddressCount : AddressCount - 2;
}

internal static class IpAddress
{
    public static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) throw new FormatException("only IPv4 addresses are supported");
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromUInt32(uint value) => new([
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    ]);

    public static bool IsPrivate(uint value)
    {
        var first = value >> 24;
        var second = (value >> 16) & 0xff;
        return first == 10 || (first == 172 && second is >= 16 and <= 31) ||
               (first == 192 && second == 168);
    }
}
