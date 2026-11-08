// SPDX-License-Identifier: GPL-3.0-or-later
//
// The client's map pictures (ui/maps): z<NNN>.dds for each zone (256 x 256, the whole zone), r<NNN>.dds for a
// region (Albion, Midgard, ...), and regions.dat, which places each zone on its region's picture: per
// [regionNNN], zone_width/zone_height (a zone's size in pixels on that picture) and for each zone k
// zone<k>_number, zone<k>_left, zone<k>_top (its corner) and zone<k>_desc (the description the map window
// shows). The file's own comment: without an entry, 50 x 50 pixels at 0, 0.

namespace DaocAssets.Zone;

/// <summary>Where one zone sits on a region map, in that picture's pixels, and its description.</summary>
public sealed record MapZone(int Zone, int Left, int Top, string Description);

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
                        keys.TryGetValue($"zone{k}_desc", out string? d) ? d.Trim() : ""));
            maps.Add(new RegionMap(region, Int("zone_width", 50), Int("zone_height", 50), zones));
        }
        return maps.OrderBy(m => m.Region).ToList();
    }

    /// <summary>A point of zone <paramref name="zone"/> (0..1 across and down it) on region map <paramref name="map"/>,
    /// in that picture's pixels; null when the map doesn't show the zone.</summary>
    public static (float X, float Y)? OnRegionMap(RegionMap map, int zone, float across, float down) =>
        map.Zones.FirstOrDefault(z => z.Zone == zone) is { } z
            ? (z.Left + across * map.ZoneWidth, z.Top + down * map.ZoneHeight)
            : null;
}
