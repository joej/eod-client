// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// Terrain: PCX decoding (crafted image, no fixture) and the height formula / mesh against the real
/// Camelot Hills data when the git-ignored fixtures are present (skipped otherwise, see LiveFixture). The
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
    public void RejectsDimensionsTheDataCannotHold()
    {
        // 65536 x 65536 from a 128-byte header and two bytes of data: refused before allocating 4 GB.
        var h = new byte[130];
        h[0] = 0x0A; h[2] = 1; h[3] = 8;
        W16(h, 8, 0xFFFF); W16(h, 10, 0xFFFF);
        h[65] = 1; W16(h, 66, 0xFFFF);
        Assert.Throws<FormatException>(() => Pcx.Read(h));
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

    [LiveTheory("live-terrain/terrain.pcx", "live-terrain/SECTOR.DAT")]
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
            ZoneTerrain terrain = ZoneTerrain.Read(tmp, sizeX: 1, sizeY: 1);
            var tiles = terrain.BuildTiledMesh(0, 0, 4);

            Assert.Equal(16, tiles.Count);
            Assert.Equal("tex00-00", tiles[0].Name);
            Assert.Equal("tex03-03.dds", tiles[^1].TextureFile);
            // texXX-YY is column (cell X), then row (cell Y). Verified on Camelot Hills (2026-11-02):
            // only this reading makes the 64 tiles continue across their edges, and the assembled
            // ground then matches the zone's shademap.pcx (r = 0.76; transposed or mirrored ~0).
            var east = tiles.Single(t => t.Vertices.All(v => v.X >= 2 * 8192f / 7 - 1 && v.X <= 4 * 8192f / 7 + 1)
                                         && t.Vertices.All(v => -v.Y <= 2 * 8192f / 7 + 1));
            Assert.Equal("tex01-00.dds", east.TextureFile);

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

            // Terrain vertices carry region-global X and a PRE-NEGATED region Y, so that the shared
            // exporter's (x,y,z)->(x,z,-y) map lands the ground in the client's ToGodot frame (+Y),
            // under the player, instead of reflected across the X axis. X stays >= 0; Y stays <= 0.
            Assert.All(tiles, t => Assert.All(t.Vertices, v =>
            {
                Assert.True(v.X >= 0f, $"region X should be non-negative, was {v.X}");
                Assert.True(v.Y <= 0f, $"region Y should be pre-negated (<= 0), was {v.Y}");
            }));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void ExportedGroundFacesUpAndIsLitByItsSlope()
    {
        // A tilted plane, rising along both region X (east) and region Y (south): cell (x, y) holds
        // x + 2y. Checked in the exported glTF itself (Y up, Z = region Y), because the axis maps
        // between heightmap, mesh and file are where the winding and normals once went wrong: every
        // triangle faced down and every normal pointed straight up whatever the slope.
        byte[] mpk = MpkBuilder.Build(
            ("terrain.pcx", PcxOf(8, 8, (x, y) => (byte)(x + 2 * y))), ("offset.pcx", FlatPcx(8, 8, value: 0)),
            ("SECTOR.DAT", System.Text.Encoding.Latin1.GetBytes("[terrain]\nscalefactor=8\noffsetfactor=32\n")));
        string tmp = Path.Combine(Path.GetTempPath(), "daoc-terr-" + Guid.NewGuid().ToString("N") + ".mpk");
        try
        {
            File.WriteAllBytes(tmp, mpk);
            ZoneTerrain terrain = ZoneTerrain.Read(tmp, sizeX: 1, sizeY: 1);
            var model = new DaocAssets.Nif.NifModel
            {
                Version = new DaocAssets.Nif.NifVersion(0, 0, 0, 0),
                Meshes = [.. terrain.BuildTiledMesh(16384, 8192, tiles: 4)],
            };
            SharpGLTF.Schema2.ModelRoot gltf = DaocAssets.Nif.GltfExporter.BuildScene(model).ToGltf2();

            // The plane in glTF axes: height rises 8 units per cell east (+X) and 16 per cell south (+Z).
            float step = 8192f / 7;
            var expected = System.Numerics.Vector3.Normalize(new(-8 / step, 1, -16 / step));
            // The tiled mesh runs one cell past the heightmap (its last row and column take the edge's
            // heights: open question W20), so the plane holds only inside the map.
            float lastX = (16384 + 7 * step) * 0.025f + 0.01f, lastZ = (8192 + 7 * step) * 0.025f + 0.01f;
            int triangles = 0;
            foreach (var (a, b, c, _) in SharpGLTF.Schema2.Toolkit.EvaluateTriangles(gltf.DefaultScene))
            {
                System.Numerics.Vector3 pa = a.GetGeometry().GetPosition(), pb = b.GetGeometry().GetPosition(),
                    pc = c.GetGeometry().GetPosition();
                if (new[] { pa, pb, pc }.Any(p => p.X > lastX || p.Z > lastZ))
                    continue;
                triangles++;
                // glTF front faces are counter-clockwise: this is the side the renderer lights.
                var face = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(pb - pa, pc - pa));
                Assert.True(System.Numerics.Vector3.Dot(face, expected) > 0.9999f, $"face {face} vs ground {expected}");
                foreach (var v in new[] { a, b, c })
                {
                    Assert.True(v.GetGeometry().TryGetNormal(out System.Numerics.Vector3 nrm));
                    Assert.True(System.Numerics.Vector3.Dot(nrm, expected) > 0.9999f, $"normal {nrm} vs ground {expected}");
                }
            }
            Assert.Equal(7 * 7 * 2, triangles);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TileSeamsShareTheirNormals()
    {
        // A bumpy heightmap: every vertex on a tile edge must get the same normal in each tile it
        // belongs to, or the lighting steps at the seam.
        byte[] mpk = MpkBuilder.Build(
            ("terrain.pcx", PcxOf(8, 8, (x, y) => (byte)((x * 37 + y * 11) % 23))), ("offset.pcx", FlatPcx(8, 8, value: 0)),
            ("SECTOR.DAT", System.Text.Encoding.Latin1.GetBytes("[terrain]\nscalefactor=8\noffsetfactor=32\n")));
        string tmp = Path.Combine(Path.GetTempPath(), "daoc-terr-" + Guid.NewGuid().ToString("N") + ".mpk");
        try
        {
            File.WriteAllBytes(tmp, mpk);
            ZoneTerrain terrain = ZoneTerrain.Read(tmp, sizeX: 1, sizeY: 1);
            var byPosition = new Dictionary<System.Numerics.Vector3, System.Numerics.Vector3>();
            int shared = 0;
            foreach (var tile in terrain.BuildTiledMesh(0, 0, tiles: 4))
                for (int i = 0; i < tile.Vertices.Length; i++)
                {
                    Assert.Equal(1f, tile.Normals![i].Length(), 4);
                    if (byPosition.TryGetValue(tile.Vertices[i], out var seen))
                    {
                        shared++;
                        Assert.Equal(seen, tile.Normals[i]);
                    }
                    else
                        byPosition[tile.Vertices[i]] = tile.Normals[i];
                }
            Assert.True(shared > 0, "the tiles share their edge vertices");
            Assert.True(byPosition.Values.Distinct().Count() > 1, "a bumpy ground has more than one normal");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // A flat w x h, 8-bit RLE PCX whose every pixel is `value` (one run per row).
    [Fact]
    public void HeightAtInterpolatesBetweenCellsAlongTheRightAxes()
    {
        // A ramp rising along X only (cell value = column): an object's ground height must follow X,
        // not Y (a swapped lookup would put every On Ground object at the wrong height).
        byte[] mpk = MpkBuilder.Build(
            ("terrain.pcx", RampPcx(8, 8)), ("offset.pcx", FlatPcx(8, 8, value: 0)),
            ("SECTOR.DAT", System.Text.Encoding.Latin1.GetBytes("[terrain]\nscalefactor=8\noffsetfactor=32\nsizex=1\nsizey=1\n")));
        string tmp = Path.Combine(Path.GetTempPath(), "daoc-terr-" + Guid.NewGuid().ToString("N") + ".mpk");
        try
        {
            File.WriteAllBytes(tmp, mpk);
            ZoneTerrain terrain = ZoneTerrain.Read(tmp, sizeX: 1, sizeY: 1);
            float step = 8192f / 7;                                  // 8 cells span the zone's 8192 units
            Assert.Equal(0f, terrain.HeightAt(0, 5000), 3);
            Assert.Equal(3 * 8f, terrain.HeightAt(3 * step, 100), 3);  // on cell 3
            Assert.Equal(3.5f * 8f, terrain.HeightAt(3.5f * step, 4000), 3);   // halfway to cell 4
            Assert.Equal(7 * 8f, terrain.HeightAt(99999, 0), 3);      // clamped at the far edge
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TheHeightmapSpansTheZoneTablesSizeNotSectorSize()
    {
        // Dartmoor and others write sizex=0 (read literally, the zone was 0 units wide and its terrain
        // collapsed to a point); Snowdonia writes 4 (its terrain covered half the zone). The zone
        // table's size (8 for every zone) is the extent.
        byte[] mpk = MpkBuilder.Build(
            ("terrain.pcx", FlatPcx(8, 8, value: 1)), ("offset.pcx", FlatPcx(8, 8, value: 0)),
            ("SECTOR.DAT", System.Text.Encoding.Latin1.GetBytes("[sectorsize]\nsizex=0\nsizey=0\n[terrain]\nscalefactor=8\noffsetfactor=32\n")));
        string tmp = Path.Combine(Path.GetTempPath(), "daoc-terr-" + Guid.NewGuid().ToString("N") + ".mpk");
        try
        {
            File.WriteAllBytes(tmp, mpk);
            ZoneTerrain terrain = ZoneTerrain.Read(tmp);
            Assert.Equal(8 * 8192, terrain.RegionWidth);
            Assert.Equal(8 * 8192, terrain.RegionHeight);
            File.WriteAllBytes(tmp, MpkBuilder.Build(
                ("terrain.pcx", FlatPcx(8, 8, value: 1)), ("offset.pcx", FlatPcx(8, 8, value: 0)),
                ("SECTOR.DAT", System.Text.Encoding.Latin1.GetBytes("[sectorsize]\nsizex=4\nsizey=4\n"))));
            Assert.Equal(8 * 8192, ZoneTerrain.Read(tmp).RegionWidth);
            Assert.Equal(4 * 8192, ZoneTerrain.Read(tmp, sizeX: 4, sizeY: 4).RegionWidth);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // An 8-bit PCX whose cell (x, y) holds x.
    // A w x h, 8-bit RLE PCX with cell (x, y) = value(x, y) (each pixel its own run of 1).
    private static byte[] PcxOf(int w, int h, Func<int, int, byte> value)
    {
        var head = new byte[128];
        head[0] = 0x0A; head[2] = 1; head[3] = 8;
        W16(head, 8, w - 1); W16(head, 10, h - 1);
        head[65] = 1; W16(head, 66, w);
        var body = new List<byte>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                body.Add(0xC1);
                body.Add(value(x, y));
            }
        return [.. head, .. body];
    }

    private static byte[] RampPcx(int w, int h)
    {
        var head = new byte[128];
        head[0] = 0x0A; head[2] = 1; head[3] = 8;
        W16(head, 8, w - 1); W16(head, 10, h - 1);
        head[65] = 1; W16(head, 66, w);
        var body = new List<byte>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                body.Add(0xC1);                 // run of 1 (values below 0xC0 could be literal; keep it uniform)
                body.Add((byte)x);
            }
        return [.. head, .. body];
    }

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
