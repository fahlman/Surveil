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

/// <summary>What identifies a camera: its manufacturer and serial number, read over ONVIF. Its IP
/// address is only where it was last seen and can change at any time.</summary>
public sealed record CameraIdentity(string Manufacturer, string Model, string SerialNumber)
{
    /// <summary>The comparison key: manufacturer + serial, ignoring case and surrounding whitespace.</summary>
    public string Key => KeyOf(Manufacturer, SerialNumber) ?? throw new InvalidOperationException("No serial number.");

    /// <summary>Null when there's no serial number to identify by.</summary>
    public static string? KeyOf(string manufacturer, string serialNumber) =>
        string.IsNullOrWhiteSpace(serialNumber) ? null
            : $"{manufacturer.Trim().ToUpperInvariant()}|{serialNumber.Trim().ToUpperInvariant()}";
}

public sealed class CameraRecord
{
    /// <summary>Where the camera was last seen — not its identity; see <see cref="IdentityKey"/>.</summary>
    public string Ip { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public ulong FirstSeen { get; set; }
    public ulong LastSeen { get; set; }

    /// <summary>Manufacturer + serial; null for a record saved before cameras were identified.</summary>
    public string? IdentityKey() => CameraIdentity.KeyOf(Manufacturer, SerialNumber);
}

/// <summary>A device that answered a scan, with its identity if it could be read.</summary>
public sealed class FoundCamera
{
    public string Ip { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public CameraIdentity? Identity { get; set; }

    /// <summary>Why <see cref="Identity"/> is null (e.g. sign-in failed).</summary>
    public string Problem { get; set; } = "";
}

public enum CameraPresenceStatus
{
    Unknown,
    New,
    Present,
    Absent,
    Discovered,
    Offline,
    /// <summary>A known camera found at a different address than last time.</summary>
    Moved,
    /// <summary>A known camera's last address answered, but the device there couldn't be identified.</summary>
    Unverified,
    /// <summary>A device answered but couldn't be identified; it isn't tracked in the inventory.</summary>
    Unidentified,
}

public sealed class CameraStatus
{
    public string Ip { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public ulong FirstSeen { get; set; }
    public ulong LastSeen { get; set; }
    public CameraPresenceStatus Presence { get; set; }

    /// <summary>For <see cref="CameraPresenceStatus.Moved"/>: the address it was last seen at.</summary>
    public string PreviousIp { get; set; } = "";

    /// <summary>For unverified or unidentified devices: why identification failed.</summary>
    public string Problem { get; set; } = "";

    /// <summary>Compatibility/display representation; domain code should use <see cref="Presence"/>.</summary>
    public string Status
    {
        get => Presence == CameraPresenceStatus.Unknown ? "" : Presence.ToString().ToLowerInvariant();
        set => Presence = Enum.TryParse<CameraPresenceStatus>(value, true, out var parsed)
            ? parsed : CameraPresenceStatus.Unknown;
    }
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
