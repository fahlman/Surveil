using System.Collections.ObjectModel;
using Surveil.App.ViewModels;

namespace Surveil.App.Services;

public sealed record CameraFilterState(bool Active, string Summary, bool UnmappedVisible);

/// <summary>Builds and applies faceted camera filters independently of page workflow state.</summary>
public static class CameraFacetService
{
    private static readonly (string Name, Func<CameraItem, IEnumerable<string>> Values)[] Definitions =
    {
        ("Manufacturer", camera => Single(camera.Manufacturer)),
        ("Model", camera => Single(camera.ModelName)),
        ("Codec", camera => camera.Codecs),
        ("Resolution", camera => Single(camera.ResolutionBucket)),
        ("Frame rate", camera => Single(camera.FrameRateBucket)),
        ("Bitrate", camera => Single(camera.BitrateBucket)),
        ("Capability", camera => camera.Capabilities),
        ("ONVIF", camera => Single(camera.MediaGen)),
        ("Sign-in", camera => Single(camera.SignInLabel)),
        ("Location", camera => Single(camera.LocationLabel)),
    };

    public static void Rebuild(ObservableCollection<FacetGroup> facets, IReadOnlyList<CameraItem> cameras,
        Action selectionChanged)
    {
        var selected = facets.ToDictionary(group => group.Name, group => group.SelectedValues.ToHashSet());
        facets.Clear();
        foreach (var (name, values) in Definitions)
        {
            var distinct = cameras.SelectMany(values).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct().OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0) continue;
            var keep = selected.TryGetValue(name, out var previous) ? previous : [];
            var group = new FacetGroup(name);
            foreach (var value in distinct)
                group.Options.Add(new FacetOption(value)
                {
                    IsSelected = keep.Contains(value),
                    SelectionChanged = selectionChanged,
                });
            group.RefreshHeader();
            facets.Add(group);
        }
    }

    public static CameraFilterState Apply(IReadOnlyList<CameraItem> cameras, IReadOnlyList<SiteItem> sites,
        IReadOnlyList<CameraItem> unmapped, IReadOnlyList<FacetGroup> facets)
    {
        var active = facets.Where(group => group.HasSelection)
            .Select(group => (Definition: Definitions.First(definition => definition.Name == group.Name),
                Selected: group.SelectedValues.ToHashSet())).ToList();
        var filtering = active.Count > 0;
        var visible = 0;
        foreach (var camera in cameras)
        {
            camera.IsVisible = !filtering || active.All(filter =>
                filter.Definition.Values(camera).Any(filter.Selected.Contains));
            if (camera.IsVisible) visible++;
        }

        foreach (var site in sites)
        {
            var siteHasMatch = false;
            foreach (var range in site.Children)
            {
                var rangeHasMatch = range.Cameras.Any(camera => camera.IsVisible);
                range.IsVisible = !filtering || rangeHasMatch;
                siteHasMatch |= rangeHasMatch;
            }
            site.IsVisible = !filtering || siteHasMatch;
        }

        foreach (var group in facets)
        {
            var others = active.Where(filter => filter.Definition.Name != group.Name).ToList();
            var definition = Definitions.First(item => item.Name == group.Name);
            foreach (var option in group.Options)
                option.Count = cameras.Count(camera => others.All(filter =>
                    filter.Definition.Values(camera).Any(filter.Selected.Contains)) &&
                    definition.Values(camera).Contains(option.Value));
            group.RefreshHeader();
        }

        var summary = filtering ? $"{visible} of {cameras.Count} cameras" :
            $"{cameras.Count} camera{(cameras.Count == 1 ? "" : "s")}";
        return new CameraFilterState(filtering, summary,
            filtering ? unmapped.Any(camera => camera.IsVisible) : unmapped.Count > 0);
    }

    private static IEnumerable<string> Single(string? value) => value is null ? [] : [value];
}
