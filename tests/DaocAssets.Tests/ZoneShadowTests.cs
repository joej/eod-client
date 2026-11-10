// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// The ground shading (shadow.pcx): trees of a cluster subtract retail's 3 x 3 kernel around their spot, clamped at
/// 0; the map goes to the client as a PGM.
/// </summary>
public sealed class ZoneShadowTests
{
    private static Pcx Flat(byte v, int size = 8) =>
        new() { Width = size, Height = size, Pixels = Enumerable.Repeat(v, size * size).ToArray() };

    [Fact]
    public void ATreeDarkensTheNearestTexelAndItsNeighbours()
    {
        Pcx map = Flat(255);
        // 3.4 cells east, 2.6 south: the nearest texel is (3, 3).
        Assert.Equal(1, ZoneShadow.Stamp(map, [(3.4f * 256, 2.6f * 256)]));
        Assert.Equal(255 - 151, map.At(3, 3));
        Assert.Equal(255 - 58, map.At(2, 3));
        Assert.Equal(255 - 58, map.At(3, 4));
        Assert.Equal(255 - 10, map.At(4, 4));
        Assert.Equal(255 - 10, map.At(2, 2));
        Assert.Equal(255, map.At(5, 3));
        Assert.Equal(255, map.At(3, 1));
    }

    [Fact]
    public void StampsAddUpAndClampAtBlack()
    {
        Pcx map = Flat(200);
        ZoneShadow.Stamp(map, [(512, 512), (512, 512)]);
        Assert.Equal(0, map.At(2, 2));             // 200 − 302
        Assert.Equal(200 - 116, map.At(1, 2));     // two edge stamps
    }

    [Fact]
    public void PointsOffTheMapAreSkippedAndEdgesKeepTheirPart()
    {
        Pcx map = Flat(255);
        Assert.Equal(1, ZoneShadow.Stamp(map, [(0, 0), (-1000, 50), (99999, 0)]));
        Assert.Equal(255 - 151, map.At(0, 0));
        Assert.Equal(255 - 10, map.At(1, 1));
    }

    [Fact]
    public void ThePgmRoundTrips()
    {
        Pcx map = Flat(255, 4);
        map.Pixels[5] = 100;
        byte[] pgm = ZoneShadow.ToPgm(map);
        Assert.StartsWith("P5\n4 4\n255\n", System.Text.Encoding.ASCII.GetString(pgm));
        var read = ZoneShadow.ReadPgm(pgm);
        Assert.NotNull(read);
        Assert.Equal((4, 4), (read.Value.Width, read.Value.Height));
        Assert.Equal(map.Pixels, read.Value.Pixels);
        Assert.Null(ZoneShadow.ReadPgm(pgm[..^1]));   // short raster
        Assert.Null(ZoneShadow.ReadPgm("P6\n4 4\n255\n"u8.ToArray()));
    }
}
