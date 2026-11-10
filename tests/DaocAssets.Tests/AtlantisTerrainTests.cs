// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>"Atlantis Terrain" layers from a ter archive's textures.csv (daoc-re RE/70): per patch, in order, each with
/// its mask file and channel. Rows from zone 1's file.</summary>
public sealed class AtlantisTerrainTests
{
    private const string Csv =
        "patch x,patch y,base texture filename,rotate,u translate,v translate,u scale,v scale,visible,num tiles,tileable\r\n" +
        "0,0,DarkGrass2,0.00, 0.00, 0.00,1.00,1.00,1,256,1\r\n" +
        "0,0,LightGrass2,0.00, 0.00, 0.00,1.00,1.00,1,256,1\r\n" +
        "0,0,ClayMud,0.00, 0.00, 0.00,1.00,1.00,1,256,1\r\n" +
        "0,0,clover_yellow,0.00, 0.00, 0.00,0.75,0.75,0,256,1\r\n" +
        "0,0,salisburyBaseMap,0.00, 0.00, 0.00,1.00,1.00,1,1,0\r\n\r\n" +
        "1,0,DarkGrass2,0.00, 0.00, 0.00,1.00,1.00,1,256,1\r\n";

    [Fact]
    public void LayersAreNumberedPerPatchAndFindTheirMaskChannel()
    {
        List<AtlantisLayer> layers = AtlantisTerrain.Parse(Csv);
        Assert.Equal(6, layers.Count);
        Assert.Equal([0, 1, 2, 3, 4, 0], layers.Select(l => l.Index));
        // Layer i: mask i / 3, channel i mod 3 (R, G, B).
        Assert.Equal(("patch0000-00.dds", 1), layers[1].Weight);
        Assert.Equal(("patch0000-01.dds", 0), layers[3].Weight);
        Assert.Equal(("patch0100-00.dds", 0), layers[5].Weight);
        // Flags: an invisible layer, a 0.75 scale, a whole-patch map stretched once and clamped.
        Assert.False(layers[3].Visible);
        Assert.Equal(0.75f, layers[3].UScale);
        Assert.Equal((1f, false), (layers[4].Tiles, layers[4].Tileable));
        Assert.Equal(layers, AtlantisTerrain.ReadCsv(AtlantisTerrain.ToCsv(layers) + "flip,0,1\n"));
    }
}
