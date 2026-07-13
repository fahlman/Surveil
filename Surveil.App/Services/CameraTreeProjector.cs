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

    public static CameraItem? Add(ObservableCollection<CameraItem> cameras, CameraStatus camera, Uri? endpoint,
        Action selectionChanged)
    {
        if (cameras.Any(item => item.Ip == camera.Ip)) return null;
        var item = new CameraItem(camera, endpoint) { SelectionChanged = selectionChanged };
        cameras.Add(item);
        return item;
    }
}
