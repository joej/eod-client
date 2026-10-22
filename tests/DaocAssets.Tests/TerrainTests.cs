// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// Terrain: PCX decoding (crafted image, no fixture) and the height formula / mesh against the real
/// Camelot Hills data when the git-ignored fixtures are present (skips cleanly otherwise). The
/// height formula Z = terrain*scalefactor + offset*offsetfactor is the one verified byte-for-byte
/// against the live spawn (Eodarms Z=2896 at cell (101,182): 162*8 + 50*32).
/// </summary>
public sealed class TerrainTests
{
    [Fact]
    public void DecodesAnRlePcx()
    {
        // Craft a 4x2, 8-bit, RLE PCX: row0 = four 0x11 (one run), row1 = 0x22,0x23,0x24,0x25 literals.
        var h = new byte[128];
        h[0] = 0x0A; h[2] = 1; h[3] = 8;             // manufacturer, encoding=RLE, bpp=8
        W16(h, 8, 3); W16(h, 10, 1);                  // xmax=3, ymax=1 -> 4x2 (xmin/ymin stay 0)
        h[65] = 1; W16(h, 66, 4);                     // planes=1, bytesPerLine=4

        var body = new List<byte>
        {
            0xC4, 0x11,                               // run of 4 x 0x11
            0x22, 0x23, 0x24, 0x25,                    // four literals
        };
        byte[] file = [.. h, .. body];

        Pcx pcx = Pcx.Read(file);
        Assert.Equal(4, pcx.Width);
        Assert.Equal(2, pcx.Height);
        Assert.Equal(0x11, pcx.At(0, 0));
        Assert.Equal(0x11, pcx.At(3, 0));
        Assert.Equal(0x22, pcx.At(0, 1));
        Assert.Equal(0x25, pcx.At(3, 1));
        Assert.Equal(0x25, pcx.At(99, 99)); // clamps to bounds
    }

    [Fact]
    public void RejectsNonPcx()
    {
        Assert.Throws<FormatException>(() => Pcx.Read(new byte[200])); // manufacturer byte != 0x0A
    }

    // --- live fixture: real Camelot Hills terrain data (dat000 members) ---

    public static IEnumerable<object[]> TerrainDir()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "live-terrain");
        if (File.Exists(Path.Combine(dir, "terrain.pcx")) && File.Exists(Path.Combine(dir, "SECTOR.DAT")))
            yield return [dir];
    }

    [Theory]
    [MemberData(nameof(TerrainDir))]
    public void HeightFormulaMatchesTheLiveSpawn(string dir)
    {
        Pcx terrain = Pcx.Read(File.ReadAllBytes(Path.Combine(dir, "terrain.pcx")));
        Pcx offset = Pcx.Read(File.ReadAllBytes(Path.Combine(dir, "offset.pcx")));
        Assert.Equal(256, terrain.Width);
        Assert.Equal(256, terrain.Height);

        // Camelot Hills [terrain] scalefactor=8, offsetfactor=32; cell (101,182) -> Eodarms' Z.
        int height = terrain.At(101, 182) * 8 + offset.At(101, 182) * 32;
        Assert.Equal(2896, height);
    }

    [Fact]
    public void BuildsOneTexturedSubMeshPerGroundTile()
    {
        // Craft an 8x8-cell zone (flat height) and split it into a 4x4 tile grid -> 16 sub-meshes,
        // each named for its tile texture (texRR-CC) with 2 cells (quads) -> 3x3 vertices and UVs 0..1.
        byte[] pcx = FlatPcx(8, 8, value: 10);
        byte[] zero = FlatPcx(8, 8, value: 0);
        byte[] sector = System.Text.Encoding.Latin1.GetBytes(
            "[terrain]\nscalefactor=8\noffsetfactor=32\nsizex=1\nsizey=1\n");
        byte[] mpk = MpkBuilder.Build(
            ("terrain.pcx", pcx), ("offset.pcx", zero), ("SECTOR.DAT", sector));

        string tmp = Path.Combine(Path.GetTempPath(), "daoc-terr-" + Guid.NewGuid().ToString("N") + ".mpk");
        try
        {
            File.WriteAllBytes(tmp, mpk);
            ZoneTerrain terrain = ZoneTerrain.Read(tmp);
            var tiles = terrain.BuildTiledMesh(0, 0, 4);

            Assert.Equal(16, tiles.Count);
            Assert.Equal("tex00-00", tiles[0].Name);
            Assert.Equal("tex03-03.dds", tiles[^1].TextureFile);

            foreach (var t in tiles)
            {
                Assert.Equal(9, t.Vertices.Length);     // 3x3 (2 quads per side, sharing seam cells)
                Assert.Equal(8, t.Triangles.Length);    // 2 quads x 2 triangles x 2x2 cells
                Assert.NotNull(t.TexCoords);
                // Each tile's UVs span the full 0..1 range (its own texture, not a global atlas).
                Assert.Contains(t.TexCoords!, uv => uv.X == 0f && uv.Y == 0f);
                Assert.Contains(t.TexCoords!, uv => uv.X == 1f && uv.Y == 1f);
                Assert.Equal("tex", t.TextureFile![..3]);
            }

            // Flat height = 10 * scalefactor(8) + 0 * offsetfactor(32) = 80 everywhere.
            Assert.All(tiles, t => Assert.All(t.Vertices, v => Assert.Equal(80f, v.Z)));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // A flat w x h, 8-bit RLE PCX whose every pixel is `value` (one run per row).
    private static byte[] FlatPcx(int w, int h, byte value)
    {
        var head = new byte[128];
        head[0] = 0x0A; head[2] = 1; head[3] = 8;
        W16(head, 8, w - 1); W16(head, 10, h - 1);
        head[65] = 1; W16(head, 66, w);
        var body = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                int run = Math.Min(w - x, 63);
                body.Add((byte)(0xC0 | run)); body.Add(value);
                x += run;
            }
        }
        return [.. head, .. body];
    }

    private static void W16(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
