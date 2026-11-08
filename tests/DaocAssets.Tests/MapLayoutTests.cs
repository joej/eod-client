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

        [region011]
        zone_count=1
        zone_width=256
        zone_height=256
        ; Camelot
        zone0_number=026
        zone0_left=0
        zone0_top=0
        zone0_offsetx=21900
        zone0_offsety=13350
        zone0_width=11200
        zone0_height=11200
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
    public void ACityMapCoversOnlyItsPartOfTheZone()
    {
        // Camelot City (zone 26, corner 8192, 8192): the owner at the tunnel from Camelot Hills, x 31899 y 31382,
        // shows by the map's "Camelot Hills" exit (bottom left), not mid-map as a whole-zone map would put it.
        MapZone city = MapLayout.Find(MapLayout.Parse(Dat), 11, 26)!;
        (float across, float down) = city.Fraction(31899 - 8192, 31382 - 8192, 65536, 65536);
        Assert.Equal((41, 224), ((int)(across * 256), (int)(down * 256)));   // 41.3, 224.9
        (float x, float y) = city.Local(across, down, 65536, 65536);
        Assert.Equal(31899f - 8192, x, 0.01f);
        Assert.Equal(31382f - 8192, y, 0.01f);
        // Without the keys, the whole zone.
        Assert.Equal((0.5f, 0.25f), MapLayout.Parse(Dat)[0].Zones[0].Fraction(32768, 16384, 65536, 65536));
    }

    [Fact]
    public void APointOfAZoneLandsInItsSquare()
    {
        RegionMap albion = MapLayout.Parse(Dat)[0];
        Assert.Equal((176f + 19f, 84f + 38f), MapLayout.OnRegionMap(albion, 0, 0.5f, 1f));
        Assert.Null(MapLayout.OnRegionMap(albion, 7, 0.5f, 0.5f));
    }
}
