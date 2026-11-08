// SPDX-License-Identifier: GPL-3.0-or-later
//
// The client's map pictures (ui/maps): z<NNN>.dds for each zone (256 x 256, the whole zone), r<NNN>.dds for a
// region (Albion, Midgard, ...), and regions.dat, which places each zone on its region's picture: per
// [regionNNN], zone_width/zone_height (a zone's size in pixels on that picture) and for each zone k
// zone<k>_number, zone<k>_left, zone<k>_top (its corner) and zone<k>_desc (the description the map window
// shows). The file's own comment: without an entry, 50 x 50 pixels at 0, 0. A city's or dungeon's picture
// covers only part of its zone: zone<k>_offsetx/_offsety/_width/_height give that part in world units, from the
// zone's corner (Camelot City: 21900, 13350, 11200 x 11200; 299 zones have them). The pip is then
// (local - offset) / size across the picture (daoc-re RE/57, 2026-11-07).

namespace DaocAssets.Zone;

/// <summary>Where one zone sits on a region map, in that picture's pixels, and its description.</summary>
public sealed record MapZone(int Zone, int Left, int Top, string Description)
{
    /// <summary>The part of the zone the picture shows, in world units from the zone's corner; 0 width = the whole zone.</summary>
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>A zone-local point (world units from the zone's corner) as 0..1 across and down the picture;
    /// <paramref name="zoneWidth"/>/<paramref name="zoneHeight"/> are the zone's size, used when the file gives none.</summary>
    public (float Across, float Down) Fraction(float localX, float localY, int zoneWidth, int zoneHeight) =>
        ((localX - OffsetX) / (Width > 0 ? Width : zoneWidth), (localY - OffsetY) / (Height > 0 ? Height : zoneHeight));

    /// <summary>The inverse of <see cref="Fraction"/>.</summary>
    public (float X, float Y) Local(float across, float down, int zoneWidth, int zoneHeight) =>
        (OffsetX + across * (Width > 0 ? Width : zoneWidth), OffsetY + down * (Height > 0 ? Height : zoneHeight));
}

/// <summary>A region map: each zone's square on it (all <see cref="ZoneWidth"/> x <see cref="ZoneHeight"/> pixels).</summary>
public sealed record RegionMap(int Region, int ZoneWidth, int ZoneHeight, IReadOnlyList<MapZone> Zones);

public static class MapLayout
{
    public static IReadOnlyList<RegionMap> Parse(string regionsDat)
    {
        var maps = new List<RegionMap>();
        foreach ((string section, Dictionary<string, string> keys) in IniFile.Parse(regionsDat).Sections)
        {
            if (!section.StartsWith("region", StringComparison.OrdinalIgnoreCase) || !int.TryParse(section.AsSpan(6), out int region))
                continue;
            int Int(string key, int fallback) => keys.TryGetValue(key, out string? v) && int.TryParse(v.Trim(), out int n) ? n : fallback;
            int count = Int("zone_count", 0);
            var zones = new List<MapZone>();
            for (int k = 0; k < count; k++)
                if (Int($"zone{k}_number", -1) is int zone and >= 0)
                    zones.Add(new MapZone(zone, Int($"zone{k}_left", 0), Int($"zone{k}_top", 0),
                        keys.TryGetValue($"zone{k}_desc", out string? d) ? d.Trim() : "")
                    {
                        OffsetX = Int($"zone{k}_offsetx", 0), OffsetY = Int($"zone{k}_offsety", 0),
                        Width = Int($"zone{k}_width", 0), Height = Int($"zone{k}_height", 0),
                    });
            maps.Add(new RegionMap(region, Int("zone_width", 50), Int("zone_height", 50), zones));
        }
        return maps.OrderBy(m => m.Region).ToList();
    }

    /// <summary>A point of zone <paramref name="zone"/> (0..1 across and down it) on region map <paramref name="map"/>,
    /// in that picture's pixels; null when the map doesn't show the zone.</summary>
    /// <summary>Zone <paramref name="zone"/>'s entry: in <paramref name="region"/>'s map first, else any map that lists it.</summary>
    public static MapZone? Find(IReadOnlyList<RegionMap> maps, int region, int zone) =>
        maps.FirstOrDefault(m => m.Region == region)?.Zones.FirstOrDefault(z => z.Zone == zone)
        ?? maps.SelectMany(m => m.Zones).FirstOrDefault(z => z.Zone == zone);

    public static (float X, float Y)? OnRegionMap(RegionMap map, int zone, float across, float down) =>
        map.Zones.FirstOrDefault(z => z.Zone == zone) is { } z
            ? (z.Left + across * map.ZoneWidth, z.Top + down * map.ZoneHeight)
            : null;
}
