// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Nif;
using DaocAssets.Zone;
using Xunit;

namespace DaocAssets.Tests;

/// <summary>Rivers, lakes and swamps from SECTOR.DAT ([riverNN]).</summary>
public class ZoneWaterTests
{
    private const string Sector = """
        [sectorsize]
        sizex=8
        sizey=8
        [river00]
        texture=watery
        multitexture=ripl
        flow=-50
        bankpoints=3
        height=1916
        left00=10,0,0
        left01=10,10,0
        left02=12,20,0
        right00=20,0,0
        right01=20,10,0
        right02=22,20,0
        name=Test River
        type=RIVER
        color=16776960
        [river01]
        texture=
        bankpoints=0
        height=0
        [river02]
        texture=watery.tga
        height=500
        left00=0,0,0
        left01=0,4,0
        right00=4,0,0
        right01=4,4,0
        type=lake
        """;

    [Fact]
    public void ParsesBodiesScalesBanksAndSkipsEmptySections()
    {
        var bodies = ZoneWater.Parse(Sector);
        Assert.Equal([0, 2], bodies.Select(b => b.Index));   // river01 has no banks
        WaterBody r = bodies[0];
        Assert.Equal(("Test River", "RIVER", 1916f, "watery", "ripl", -50f, 16776960L),
            (r.Name, r.Type, r.Height, r.Texture, r.MultiTexture, r.Flow, r.Color));
        // An 8-cell (65536-unit) zone on a 256-point grid: 256 units per point.
        Assert.Equal(new Vector2(2560, 0), r.Left[0]);
        Assert.Equal(new Vector2(5632, 5120), r.Right[2]);
        Assert.Equal("watery", bodies[1].Texture);           // extension dropped
        Assert.Equal("LAKE", bodies[1].Type);
    }

    [Fact]
    public void TheGridSpansTheZoneTablesSizeNotSectorSize()
    {
        // SECTOR.DAT's sizex=4 (Snowdonia) is not the zone's extent: the zone table's 8 is.
        Assert.Equal(ZoneWater.Parse(Sector)[0].Left[0], ZoneWater.Parse(Sector.Replace("sizex=8", "sizex=4"))[0].Left[0]);
        var bodies = ZoneWater.Parse(Sector, sizeX: 4);
        Assert.Equal(new Vector2(1280, 0), bodies[0].Left[0]);   // 4 * 8192 / 256 = 128 per point
    }

    [Fact]
    public void SurfaceSitsAtTheHeightInTheTerrainFrameAndFacesUp()
    {
        WaterBody r = ZoneWater.Parse(Sector)[0];
        NifMesh m = ZoneWater.BuildMesh(r, 548864, 483328);
        Assert.Equal("water00", m.Name);
        Assert.Equal(6, m.Vertices.Length);
        Assert.Equal(4, m.Triangles.Length);
        // Same frame as ZoneTerrain: (offsetX + x, -(offsetY + y), height).
        Assert.Equal(new Vector3(548864 + 2560, -(483328 + 0), 1916), m.Vertices[0]);
        Assert.All(m.Vertices, v => Assert.Equal(1916, v.Z));
        foreach (var (a, b, c) in m.Triangles)
            Assert.True(Vector3.Cross(m.Vertices[b] - m.Vertices[a], m.Vertices[c] - m.Vertices[a]).Z > 0);
        Assert.Equal((SurfaceAlpha.Blend, true, "watery.dds"), (m.Alpha, m.DoubleSided, m.TextureFile));
        // v runs down the river: 0 at the first pair, growing along the centre line.
        Assert.Equal(0, m.TexCoords![0].Y);
        Assert.True(m.TexCoords[4].Y > m.TexCoords[2].Y);
    }

    [Fact]
    public void BanksRunningTheOtherWayStillFaceUp()
    {
        string swapped = Sector.Replace("left", "TMP").Replace("right", "left").Replace("TMP", "right");
        NifMesh m = ZoneWater.BuildMesh(ZoneWater.Parse(swapped)[0], 0, 0);
        foreach (var (a, b, c) in m.Triangles)
            Assert.True(Vector3.Cross(m.Vertices[b] - m.Vertices[a], m.Vertices[c] - m.Vertices[a]).Z > 0);
    }

    [Fact]
    public void CsvRowCarriesWhatTheClientNeeds()
    {
        WaterBody r = ZoneWater.Parse(Sector)[0];
        Assert.Equal("mesh,name,type,height,texture,multitexture,flow,color", ZoneWater.CsvHeader);
        Assert.Equal("water00,Test River,RIVER,1916,watery,ripl,-50,16776960", ZoneWater.CsvRow(r));
    }

    [Fact]
    public void CamelotHillsRiverRunsUnderTheBridge()
    {
        // The owner's Camelot Hills SECTOR.DAT (git-ignored fixture). river00: 33 bank pairs at height
        // 1916; the bridge (zone-local 36608, 50944) lies between pair 9's banks.
        string path = Path.Combine(AppContext.BaseDirectory, "live-terrain", "SECTOR.DAT");
        if (!File.Exists(path))
            return;
        var bodies = ZoneWater.Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));
        WaterBody river = bodies.Single(b => b.Index == 0);
        Assert.Equal(("RIVER", 1916f), (river.Type, river.Height));
        Assert.Equal(33, Math.Min(river.Left.Count, river.Right.Count));
        Vector2 l = river.Left[9], r = river.Right[9];
        Assert.InRange(36608, Math.Min(l.X, r.X) - 1024, Math.Max(l.X, r.X) + 1024);
        Assert.InRange(50944, Math.Min(l.Y, r.Y), Math.Max(l.Y, r.Y));
    }
}
