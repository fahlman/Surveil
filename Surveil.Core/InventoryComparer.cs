using System.Net;

namespace Surveil.Core;

/// <summary>Compares a scan with the saved inventory by camera identity (manufacturer + serial),
/// not by address: a known camera found at a new IP is the same camera, moved.</summary>
public static class InventoryComparer
{
    public static (Inventory Inventory, IReadOnlyList<CameraStatus> Statuses) Diff(
        Inventory previous, IEnumerable<FoundCamera> found, ISet<IPAddress> scanned, ulong now)
    {
        var known = new Dictionary<string, CameraRecord>();
        var legacy = new Dictionary<string, CameraRecord>();  // saved before cameras were identified, by last address
        foreach (var record in previous.Cameras)
        {
            if (record.IdentityKey() is { } key) known.TryAdd(key, record);
            else legacy.TryAdd(record.Ip, record);
        }

        var records = new List<CameraRecord>();
        var statuses = new List<CameraStatus>();
        var claimedBy = new Dictionary<string, string>();          // identity → the address that reported it
        var unidentified = new Dictionary<string, FoundCamera>();  // address → device that couldn't be identified

        foreach (var camera in found)
        {
            if (camera.Identity is not { } identity)
            {
                unidentified.TryAdd(camera.Ip, camera);
                continue;
            }
            if (claimedBy.TryGetValue(identity.Key, out var firstIp))
            {
                // Two addresses reporting one manufacturer + serial: the serial can't be trusted for the second.
                unidentified.TryAdd(camera.Ip, new FoundCamera
                {
                    Ip = camera.Ip, Site = camera.Site, Area = camera.Area,
                    Problem = $"reports the same serial number as {firstIp}",
                });
                continue;
            }
            claimedBy[identity.Key] = camera.Ip;

            // A record saved before cameras were identified hands its history to the camera identified
            // at its address: a one-time migration, and the only matching ever done by address.
            legacy.Remove(camera.Ip, out var unkeyed);
            var presence = unkeyed is null ? CameraPresenceStatus.New : CameraPresenceStatus.Present;
            var firstSeen = unkeyed?.FirstSeen ?? now;
            var previousIp = "";
            if (known.Remove(identity.Key, out var old))
            {
                firstSeen = old.FirstSeen;
                presence = old.Ip == camera.Ip ? CameraPresenceStatus.Present : CameraPresenceStatus.Moved;
                if (presence == CameraPresenceStatus.Moved) previousIp = old.Ip;
            }

            var record = new CameraRecord
            {
                Ip = camera.Ip,
                Manufacturer = identity.Manufacturer,
                Model = identity.Model,
                SerialNumber = identity.SerialNumber,
                Site = camera.Site,
                Area = camera.Area,
                FirstSeen = firstSeen,
                LastSeen = now,
            };
            records.Add(record);
            statuses.Add(Status(record, presence, previousIp: previousIp));
        }

        // Known cameras seen nowhere in this scan. If their last address was scanned they're missing,
        // unless a device answered there that couldn't be identified — it might be them. When several
        // were last seen at that address, the most recently seen one is the likeliest match.
        foreach (var old in known.Values.OrderByDescending(record => record.LastSeen))
        {
            records.Add(old);
            if (!InScope(old.Ip, scanned)) continue;
            statuses.Add(unidentified.Remove(old.Ip, out var device)
                ? Status(old, CameraPresenceStatus.Unverified, problem: device.Problem)
                : Status(old, CameraPresenceStatus.Absent));
        }

        // Unmatched pre-identity records are kept, unreported, until a camera is identified at their address.
        records.AddRange(legacy.Values);
        statuses.AddRange(unidentified.Values.Select(device => new CameraStatus
        {
            Ip = device.Ip,
            Site = device.Site,
            Area = device.Area,
            Presence = CameraPresenceStatus.Unidentified,
            Problem = device.Problem,
        }));

        records.Sort((left, right) => CompareIp(left.Ip, right.Ip));
        statuses.Sort((left, right) => CompareIp(left.Ip, right.Ip));
        return (new Inventory { LastScan = now, Cameras = records }, statuses);
    }

    private static CameraStatus Status(CameraRecord record, CameraPresenceStatus presence,
        string previousIp = "", string problem = "") => new()
    {
        Ip = record.Ip,
        Manufacturer = record.Manufacturer,
        Model = record.Model,
        SerialNumber = record.SerialNumber,
        Site = record.Site,
        Area = record.Area,
        FirstSeen = record.FirstSeen,
        LastSeen = record.LastSeen,
        Presence = presence,
        PreviousIp = previousIp,
        Problem = problem,
    };

    private static bool InScope(string ip, ISet<IPAddress> scanned) =>
        IPAddress.TryParse(ip, out var address) && scanned.Contains(address);

    private static int CompareIp(string left, string right) =>
        IpAddress.ToUInt32(IPAddress.Parse(left)).CompareTo(IpAddress.ToUInt32(IPAddress.Parse(right)));
}
