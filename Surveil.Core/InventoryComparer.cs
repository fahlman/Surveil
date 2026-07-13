using System.Net;

namespace Surveil.Core;

public static class InventoryComparer
{
    public static (Inventory Inventory, IReadOnlyList<CameraStatus> Statuses) Diff(
        Inventory previous, IEnumerable<FoundCamera> found, ISet<IPAddress> scanned, ulong now)
    {
        var prior = previous.Cameras.ToDictionary(camera => camera.Ip);
        var foundIps = new HashSet<string>();
        var records = new List<CameraRecord>();
        var statuses = new List<CameraStatus>();

        foreach (var camera in found)
        {
            foundIps.Add(camera.Ip);
            var exists = prior.TryGetValue(camera.Ip, out var old);
            var record = new CameraRecord
            {
                Ip = camera.Ip,
                Site = camera.Site,
                Area = camera.Area,
                FirstSeen = exists ? old!.FirstSeen : now,
                LastSeen = now
            };
            records.Add(record);
            statuses.Add(Status(record, exists ? CameraPresenceStatus.Present : CameraPresenceStatus.New));
        }

        foreach (var old in previous.Cameras.Where(camera => !foundIps.Contains(camera.Ip)))
        {
            if (IPAddress.TryParse(old.Ip, out var ip) && scanned.Contains(ip))
                statuses.Add(Status(old, CameraPresenceStatus.Absent));
            records.Add(old);
        }

        records.Sort((left, right) => CompareIp(left.Ip, right.Ip));
        statuses.Sort((left, right) => CompareIp(left.Ip, right.Ip));
        return (new Inventory { LastScan = now, Cameras = records }, statuses);
    }

    private static CameraStatus Status(CameraRecord record, CameraPresenceStatus status) => new()
    {
        Ip = record.Ip,
        Site = record.Site,
        Area = record.Area,
        FirstSeen = record.FirstSeen,
        LastSeen = record.LastSeen,
        Presence = status
    };

    private static int CompareIp(string left, string right) =>
        IpAddress.ToUInt32(IPAddress.Parse(left)).CompareTo(IpAddress.ToUInt32(IPAddress.Parse(right)));
}
