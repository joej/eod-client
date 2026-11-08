// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>ui/maps/regions.dat: each zone's place on its region's map picture (the install's own numbers).</summary>
public sealed class MapLayoutTests
{
    private const string Dat = """
        ; regions.dat
        [region001]
        zone_count=2
        zone_width=38
        zone_height=38
        ;region_desc=The center of culture
        ; camelot hills
        zone0_number=0
        zone0_left=176
        zone0_top=84
        zone0_desc=Nestled by the majestic City of Camelot
        zone1_number=1
        zone1_left=176
        zone1_top=122

        [region010]
        zone_count=1
        zone0_number=26
        """;

    [Fact]
    public void EachZoneHasItsSquareOnTheRegionMap()
    {
        IReadOnlyList<RegionMap> maps = MapLayout.Parse(Dat);
        RegionMap albion = maps[0];
        Assert.Equal((1, 38, 38), (albion.Region, albion.ZoneWidth, albion.ZoneHeight));
        Assert.Equal(new MapZone(0, 176, 84, "Nestled by the majestic City of Camelot"), albion.Zones[0]);
        Assert.Equal(new MapZone(1, 176, 122, ""), albion.Zones[1]);
        // The file's defaults: 50 x 50 at 0, 0.
        Assert.Equal((50, 50), (maps[1].ZoneWidth, maps[1].ZoneHeight));
        Assert.Equal(new MapZone(26, 0, 0, ""), Assert.Single(maps[1].Zones));
    }

    [Fact]
    public void APointOfAZoneLandsInItsSquare()
    {
        RegionMap albion = MapLayout.Parse(Dat)[0];
        Assert.Equal((176f + 19f, 84f + 38f), MapLayout.OnRegionMap(albion, 0, 0.5f, 1f));
        Assert.Null(MapLayout.OnRegionMap(albion, 7, 0.5f, 0.5f));
    }
}
