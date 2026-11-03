// SPDX-License-Identifier: GPL-3.0-or-later
//
// Builds a zone's ground geometry from the client's own terrain data (terrain.pcx + offset.pcx +
// SECTOR.DAT, all inside the zone's dat<NNN>.mpk). The height at a cell is, verified byte-for-byte
// against a live spawn (Eodarms in Camelot Hills, Z=2896):
//     Z = terrain[cell] * scalefactor + offset[cell] * offsetfactor
// with scalefactor/offsetfactor from SECTOR.DAT ([terrain] section). The two maps are 256x256; a
// zone is 8192 region units per grid cell of the world map, so the heightmap spans sizex*8192 units.
//
// Owner-run; the terrain data is the owner's client content and, like the rest of the library,
// never enters the repo or a release (see CLAUDE.md, "Game client and assets").

using System.Globalization;
using DaocAssets.Mpk;

namespace DaocAssets.Zone;

/// <summary>A zone's heightmap, with the scale constants and the region-coordinate mapping.</summary>
public sealed class ZoneTerrain
{
    private readonly Pcx _terrain;
    private readonly Pcx _offset;
    private readonly int _scaleFactor;
    private readonly int _offsetFactor;

    /// <summary>Region-unit width/height the heightmap spans (sizex/sizey from SECTOR.DAT × 8192).</summary>
    public int RegionWidth { get; }
    public int RegionHeight { get; }
    public int Cells => _terrain.Width; // square, == Height

    private ZoneTerrain(Pcx terrain, Pcx offset, int scaleFactor, int offsetFactor, int sizeX, int sizeY)
    {
        if (terrain.Width != offset.Width || terrain.Height != offset.Height)
            throw new FormatException("terrain.pcx and offset.pcx differ in size.");
        _terrain = terrain;
        _offset = offset;
        _scaleFactor = scaleFactor;
        _offsetFactor = offsetFactor;
        RegionWidth = sizeX * 8192;
        RegionHeight = sizeY * 8192;
    }

    /// <summary>
    /// Reads the terrain for the zone whose data archive is <paramref name="zoneDatMpk"/>. The heightmap
    /// spans the zone's size from the zone table (<paramref name="sizeX"/> x <paramref name="sizeY"/>
    /// sectors of 8192 units; every zone in the table is 8 x 8), NOT SECTOR.DAT's [sectorsize]: that
    /// says 4 in Snowdonia and one other zone and 0 in nine, and only the table's 8 makes the stored Z
    /// of Snowdonia's objects match the ground (median gap 58 units, against 6,016 with 4). What
    /// sizex means is open question W14.
    /// </summary>
    public static ZoneTerrain Read(string zoneDatMpk, int sizeX = 8, int sizeY = 8)
    {
        using MpkArchive dat = MpkArchive.Open(zoneDatMpk);
        Pcx terrain = Pcx.Read(ReadMember(dat, "terrain.pcx"));
        Pcx offset = Pcx.Read(ReadMember(dat, "offset.pcx"));
        string sector = System.Text.Encoding.Latin1.GetString(ReadMember(dat, "SECTOR.DAT"));

        (int scale, int off) = ParseFactors(sector);
        return new ZoneTerrain(terrain, offset, scale, off, sizeX, sizeY);
    }

    /// <summary>
    /// Ground height (region Z) at a zone-local position (region units from the zone's corner),
    /// interpolated between the four surrounding heightmap cells; outside the map, the nearest edge.
    /// </summary>
    public float HeightAt(float localX, float localY)
    {
        int n = Cells;
        float step = (float)RegionWidth / (n - 1);
        float fx = Math.Clamp(localX / step, 0, n - 1), fy = Math.Clamp(localY / step, 0, n - 1);
        int x0 = Math.Min((int)fx, n - 2), y0 = Math.Min((int)fy, n - 2);
        float tx = fx - x0, ty = fy - y0;
        float top = HeightAtCell(x0, y0) * (1 - tx) + HeightAtCell(x0 + 1, y0) * tx;
        float bottom = HeightAtCell(x0, y0 + 1) * (1 - tx) + HeightAtCell(x0 + 1, y0 + 1) * tx;
        return top * (1 - ty) + bottom * ty;
    }

    /// <summary>Height (region Z) at heightmap cell (cx, cy).</summary>
    public float HeightAtCell(int cx, int cy) =>
        _terrain.At(cx, cy) * _scaleFactor + _offset.At(cx, cy) * _offsetFactor;

    /// <summary>
    /// Builds the terrain as a grid of per-tile sub-meshes in region-global coordinates: the zone's
    /// painted ground is an <paramref name="tiles"/>×<paramref name="tiles"/> grid of texture tiles
    /// (<c>texXX-YY.dds</c> in the zone's tex archive: XX the tile column, i.e. the cell-X block, and YY
    /// the row; within a tile the image's columns run along +X and its rows along +Y), so each
    /// sub-mesh covers one tile's block of
    /// cells, carries local UVs 0..1 and names its tile as its <see cref="Nif.NifMesh.TextureFile"/>.
    /// The glTF exporter then emits one material per tile and the client textures each from the
    /// gathered <c>texXX-YY.dds</c> — the same per-shape texturing path buildings use. Tiles share
    /// their seam cells (vertices duplicated along tile edges) so each tile's UVs stay 0..1.
    ///
    /// Unlike a model (whose local vertices are meaningless until the client places it via its
    /// region→Godot map), terrain vertices are already region-global, so they must land in the exact
    /// frame the client uses for the player and placed objects: ToGodot = (X, Z, Y)/100. The shared
    /// exporter instead applies the model convention (x,y,z)→(x,z,-y), which would reflect the ground
    /// across the X axis (north↔south) and drop it thousands of units away from the player. We
    /// pre-negate the region Y here so the exporter's -Y cancels to +Y and terrain lands under the
    /// player; because negating one axis flips handedness, the winding is set opposite to BuildMesh's
    /// so faces still point up after export.
    /// </summary>
    public IReadOnlyList<Nif.NifMesh> BuildTiledMesh(int offsetX, int offsetY, int tiles)
    {
        int n = _terrain.Width;                  // cells per side (square), e.g. 256
        if (tiles < 1 || n % tiles != 0)
            // Fall back to a single mesh if the grid doesn't divide evenly (keeps a ground either way).
            return [BuildMesh(offsetX, offsetY)];

        int cellsPerTile = n / tiles;            // 256 / 8 = 32 cells (quads) per tile
        float step = (float)RegionWidth / (n - 1); // region units between adjacent cells
        var meshes = new List<Nif.NifMesh>(tiles * tiles);

        for (int tr = 0; tr < tiles; tr++)       // tile row  (cell-Y block)
        for (int tc = 0; tc < tiles; tc++)       // tile col  (cell-X block)
        {
            int cx0 = tc * cellsPerTile, cy0 = tr * cellsPerTile;
            int side = cellsPerTile + 1;          // vertices per side of this tile (shares seam cells)
            var verts = new System.Numerics.Vector3[side * side];
            var uvs = new System.Numerics.Vector2[side * side];
            for (int ly = 0; ly < side; ly++)
            for (int lx = 0; lx < side; lx++)
            {
                int cx = cx0 + lx, cy = cy0 + ly;
                int i = ly * side + lx;
                // Pre-negate region Y (see summary): exporter's -Y then yields the client's +Y.
                verts[i] = new System.Numerics.Vector3(offsetX + cx * step, -(offsetY + cy * step), HeightAtCell(cx, cy));
                uvs[i] = new System.Numerics.Vector2(lx / (float)cellsPerTile, ly / (float)cellsPerTile);
            }

            var tris = new (ushort, ushort, ushort)[cellsPerTile * cellsPerTile * 2];
            int t = 0;
            for (int ly = 0; ly < cellsPerTile; ly++)
            for (int lx = 0; lx < cellsPerTile; lx++)
            {
                ushort a = (ushort)(ly * side + lx);
                ushort b = (ushort)(ly * side + lx + 1);
                ushort c = (ushort)((ly + 1) * side + lx);
                ushort d = (ushort)((ly + 1) * side + lx + 1);
                // Opposite winding to BuildMesh: the pre-negated Y flips handedness, so this order
                // is the one that faces up after the exporter's Z-up→Y-up map. (Godot renders the
                // ground double-sided anyway, so this is belt-and-suspenders, but kept correct.)
                tris[t++] = (a, b, c);
                tris[t++] = (b, d, c);
            }

            meshes.Add(new Nif.NifMesh
            {
                Name = $"tex{tc:00}-{tr:00}",
                Vertices = verts,
                Normals = null,
                TexCoords = uvs,
                Triangles = tris,
                TextureFile = $"tex{tc:00}-{tr:00}.dds",
            });
        }
        return meshes;
    }

    /// <summary>The tile names this terrain references (<c>texXX-YY.dds</c>), for a gather step.</summary>
    public static IEnumerable<string> TileNames(int tiles)
    {
        for (int tr = 0; tr < tiles; tr++)
        for (int tc = 0; tc < tiles; tc++)
            yield return $"tex{tc:00}-{tr:00}.dds";
    }

    /// <summary>
    /// Builds the terrain as a grid mesh in region-global coordinates (the zone's offset added), so
    /// it sits in the same space as placed objects and the player. One vertex per heightmap cell,
    /// two triangles per cell quad. UVs span 0..1 across the zone (for a future terrain texture).
    /// Coordinates are region units; the Godot side applies the usual region→Godot scale and axis map.
    /// </summary>
    public Nif.NifMesh BuildMesh(int offsetX, int offsetY)
    {
        int n = _terrain.Width;                 // cells per side (square)
        float step = (float)RegionWidth / (n - 1); // region units between adjacent cells
        var verts = new System.Numerics.Vector3[n * n];
        var uvs = new System.Numerics.Vector2[n * n];

        for (int cy = 0; cy < n; cy++)
        for (int cx = 0; cx < n; cx++)
        {
            int i = cy * n + cx;
            float x = offsetX + cx * step;
            float y = offsetY + cy * step;
            float z = HeightAtCell(cx, cy);
            verts[i] = new System.Numerics.Vector3(x, y, z);
            uvs[i] = new System.Numerics.Vector2(cx / (float)(n - 1), cy / (float)(n - 1));
        }

        // Two triangles per cell. Winding is chosen so the exporter's Z-up→Y-up flip yields an
        // upward-facing surface; the Godot side renders both sides anyway for terrain.
        var tris = new (ushort, ushort, ushort)[(n - 1) * (n - 1) * 2];
        int t = 0;
        for (int cy = 0; cy < n - 1; cy++)
        for (int cx = 0; cx < n - 1; cx++)
        {
            ushort a = (ushort)(cy * n + cx);
            ushort b = (ushort)(cy * n + cx + 1);
            ushort c = (ushort)((cy + 1) * n + cx);
            ushort d = (ushort)((cy + 1) * n + cx + 1);
            tris[t++] = (a, c, b);
            tris[t++] = (b, c, d);
        }

        return new Nif.NifMesh
        {
            Name = "terrain",
            Vertices = verts,
            Normals = null,         // the exporter/Godot can shade flat; normals optional
            TexCoords = uvs,
            Triangles = tris,
        };
    }

    private static (int scale, int offset) ParseFactors(string ini)
    {
        int scale = Ini(ini, "scalefactor") ?? 1;
        int offset = Ini(ini, "offsetfactor") ?? 0;
        return (scale, offset);
    }

    // First "key=value" (case-insensitive key) whose value parses as an int, anywhere in the INI.
    private static int? Ini(string ini, string key)
    {
        foreach (string line in ini.Split('\n'))
        {
            string s = line.Trim();
            int eq = s.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!s[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(s[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
        }
        return null;
    }

    private static byte[] ReadMember(MpkArchive mpk, string name)
    {
        if (!mpk.TryGetEntry(name, out MpkEntry entry))
            throw new MpkFormatException($"Zone archive has no member '{name}'.");
        return mpk.Extract(entry);
    }
}
