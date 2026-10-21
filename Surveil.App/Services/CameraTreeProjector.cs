using System.Collections.ObjectModel;
using System.Net;
using Surveil.App.ViewModels;
using Surveil.Core;

namespace Surveil.App.Services;

/// <summary>Projects inventory/discovery records into the editable site tree.</summary>
public static class CameraTreeProjector
{
    public static IReadOnlyList<CameraItem> All(IReadOnlyList<SiteItem> sites,
        IReadOnlyList<CameraItem> unmapped) =>
        sites.SelectMany(site => site.Children).SelectMany(range => range.Cameras).Concat(unmapped).ToList();

    public static IpRangeMap<NetworkRangeItem> BuildRangeIndex(IReadOnlyList<SiteItem> sites) => new(
        sites.SelectMany(site => site.Children)
            .Where(range => NetworkRanges.IsValid(range.Cidr))
            .Select(range => (range.Cidr, range)));

    public static void PopulateInventory(IReadOnlyList<SiteItem> sites,
        ObservableCollection<CameraItem> unmapped, Inventory inventory, Action selectionChanged)
    {
        foreach (var range in sites.SelectMany(site => site.Children)) range.Cameras.Clear();
        unmapped.Clear();
        var index = BuildRangeIndex(sites);
        foreach (var record in inventory.Cameras)
        {
            var status = new CameraStatus
            {
                Ip = record.Ip,
                Manufacturer = record.Manufacturer,
                Model = record.Model,
                SerialNumber = record.SerialNumber,
                Site = record.Site,
                Area = record.Area,
                FirstSeen = record.FirstSeen,
                LastSeen = record.LastSeen,
                Presence = CameraPresenceStatus.Present,
            };
            var range = IPAddress.TryParse(record.Ip, out var address) ? index.Find(address) : null;
            Add(range?.Cameras ?? unmapped, status, null, selectionChanged);
        }
    }

    /// <summary>Replace each scanned range's cameras with the scan's statuses. Missing cameras and
    /// devices that couldn't be identified stay in the tree so the reason shows, but count
    /// separately from the identified cameras that responded.</summary>
    public static ScanTally ReplaceScanResults(IReadOnlyList<NetworkRangeItem> scannedRanges,
        IEnumerable<CameraStatus> statuses, Action selectionChanged)
    {
        foreach (var range in scannedRanges) range.Cameras.Clear();
        var index = new IpRangeMap<NetworkRangeItem>(scannedRanges.Select(range => (range.Cidr, range)));
        int responding = 0, added = 0, moved = 0, missing = 0, unidentified = 0;
        foreach (var status in statuses)
        {
            var range = IPAddress.TryParse(status.Ip, out var address) ? index.Find(address) : null;
            if (range is null || Add(range.Cameras, status, null, selectionChanged) is null) continue;
            switch (status.Presence)
            {
                case CameraPresenceStatus.Absent: missing++; break;
                case CameraPresenceStatus.Unverified or CameraPresenceStatus.Unidentified: unidentified++; break;
                default:
                    responding++;
                    if (status.Presence == CameraPresenceStatus.New) added++;
                    if (status.Presence == CameraPresenceStatus.Moved) moved++;
                    break;
            }
        }
        return new ScanTally(responding, added, moved, missing, unidentified);
    }

    public static CameraItem? Add(ObservableCollection<CameraItem> cameras, CameraStatus camera, Uri? endpoint,
        Action selectionChanged)
    {
        if (cameras.Any(item => item.Ip == camera.Ip)) return null;
        var item = new CameraItem(camera, endpoint) { SelectionChanged = selectionChanged };
        cameras.Add(item);
        return item;
    }
}

/// <summary>What a scan placed in the tree: identified cameras that responded (and how many of those
/// are new or moved), known cameras that didn't, and devices that couldn't be identified.</summary>
public readonly record struct ScanTally(int Responding, int New, int Moved, int Missing, int Unidentified);
