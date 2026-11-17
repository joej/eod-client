// SPDX-License-Identifier: GPL-3.0-or-later
//
// A dungeon's water (roadmap M11): dungeon.water lists its planes, "id, 4 corners × (x, y, z), x, y, z, type" — the corners
// relative to the plane's position, which is in the dungeon's frame (DungeonLayout.FramePoint), as the rooms' are
// (daoc-re's dungeon notes, 2026-11-10 and 2026-11-11). Each plane is drawn as a 10 × 10 grid with its texture tiled once
// per 512 units. Type: 4 lake water, 6 lava, anything else (−1, 0) river-style water; the install's 32 planes in 11
// dungeons are −1 (23) and 4 (9). dungeon.water.csv ("room, plane") lists the rooms a plane belongs to: retail shows only
// the current room's planes; ours shows every plane (the room lookup isn't built).
// Checked 2026-11-17: each plane's surface is within a few units of the water height its rooms give in dungeon.place
// (column 10, region z), and the room floors lie under it (mpk dungeoncheck).

using System.Globalization;
using System.Numerics;
using DaocAssets.Nif;

namespace DaocAssets.Zone;

/// <summary>One water plane of a dungeon: its four corners (relative to <paramref name="At"/>), position in the
/// dungeon's frame, and type as written.</summary>
public sealed record DungeonWaterPlane(int Id, Vector3[] Corners, Vector3 At, int Type)
{
    /// <summary>water.csv's type: LAKE (4), LAVA (6), else RIVER.</summary>
    public string Kind => Type switch { 4 => "LAKE", 6 => "LAVA", _ => "RIVER" };

    /// <summary>The surface height, region z.</summary>
    public float Height => DungeonLayout.FramePoint(At + Corners[0]).Z;

    /// <summary>The mesh name in water.glb and water.csv: "water00".</summary>
    public string MeshName => $"water{Id:00}";
}

public static class DungeonWater
{
    /// <summary>The grid each plane is drawn as (cells per side).</summary>
    public const int Cells = 10;

    /// <summary>dungeon.water's rows; a row without a type (Cetus' Pit, 078) is type 0, one with under 16 numbers skipped.</summary>
    public static List<DungeonWaterPlane> Parse(string text)
    {
        var planes = new List<DungeonWaterPlane>();
        foreach (string line in text.Split('\n'))
        {
            var v = new List<float>();
            foreach (string cell in line.Split(','))
                if (float.TryParse(cell.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    v.Add(f);
                else
                    break;
            if (v.Count < 16)
                continue;
            var corners = new Vector3[4];
            for (int k = 0; k < 4; k++)
                corners[k] = new Vector3(v[1 + 3 * k], v[2 + 3 * k], v[3 + 3 * k]);
            planes.Add(new DungeonWaterPlane((int)v[0], corners, new Vector3(v[13], v[14], v[15]), v.Count > 16 ? (int)v[16] : 0));
        }
        return planes;
    }

    /// <summary>dungeon.water.csv: each plane's rooms.</summary>
    public static Dictionary<int, List<int>> ParseRooms(string csv)
    {
        var rooms = new Dictionary<int, List<int>>();
        foreach (string line in csv.Split('\n'))
        {
            string[] c = line.Split(',');
            if (c.Length >= 2 && int.TryParse(c[0].Trim(), out int room) && int.TryParse(c[1].Trim(), out int plane))
                (rooms.TryGetValue(plane, out var list) ? list : rooms[plane] = []).Add(room);
        }
        return rooms;
    }

    /// <summary>A plane's grid points in region coordinates, row by row ((Cells + 1)²), bilinear between its corners.</summary>
    public static Vector3[] RegionGrid(DungeonWaterPlane p)
    {
        var points = new Vector3[(Cells + 1) * (Cells + 1)];
        for (int j = 0; j <= Cells; j++)
            for (int i = 0; i <= Cells; i++)
            {
                float u = i / (float)Cells, w = j / (float)Cells;
                Vector3 a = Vector3.Lerp(p.Corners[0], p.Corners[1], u), b = Vector3.Lerp(p.Corners[3], p.Corners[2], u);
                points[j * (Cells + 1) + i] = DungeonLayout.FramePoint(p.At + Vector3.Lerp(a, b, w));
            }
        return points;
    }

    /// <summary>
    /// The plane as a water.glb mesh, in the frame <see cref="ZoneWater.BuildMesh"/> uses (NIF space: region x, −y, z):
    /// facing up, double-sided, alpha-blended (the client's water shader draws it), the texture tiled per 512 units.
    /// </summary>
    public static NifMesh BuildMesh(DungeonWaterPlane p)
    {
        Vector3[] grid = RegionGrid(p);
        int n = Cells + 1;
        var verts = new Vector3[grid.Length];
        var uvs = new Vector2[grid.Length];
        var normals = new Vector3[grid.Length];
        for (int k = 0; k < grid.Length; k++)
        {
            verts[k] = new Vector3(grid[k].X, -grid[k].Y, grid[k].Z);
            Vector3 d = grid[k] - grid[0];
            uvs[k] = new Vector2(Vector3.Dot(d, Unit(grid[n - 1] - grid[0])), Vector3.Dot(d, Unit(grid[(n - 1) * n] - grid[0])))
                / ZoneWater.TileUnits;
            normals[k] = Vector3.UnitZ;
        }
        var tris = new List<(ushort, ushort, ushort)>();
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++)
            {
                ushort a = (ushort)(j * n + i), b = (ushort)(a + 1), c = (ushort)(a + n), d = (ushort)(c + 1);
                AddUp(verts, tris, a, b, c);
                AddUp(verts, tris, b, d, c);
            }
        return new NifMesh
        {
            Name = p.MeshName,
            Vertices = verts,
            Normals = normals,
            TexCoords = uvs,
            Triangles = [.. tris],
            TextureFile = (p.Type == 6 ? ZoneWater.DefaultLavaTexture : ZoneWater.DefaultTexture) + ".dds",
            BaseColor = new Vector4(1, 1, 1, 0.85f),   // the client's vertex alpha above water (0.5 under it: not built)
            Alpha = SurfaceAlpha.Blend,
            DoubleSided = true,
        };
    }

    /// <summary>A water.csv row (<see cref="ZoneWater.CsvHeader"/>): no flow (dungeon planes don't scroll), no colour.</summary>
    public static string CsvRow(DungeonWaterPlane p, IReadOnlyList<int> rooms) => string.Join(",",
        p.MeshName, $"plane {p.Id}" + (rooms.Count > 0 ? " rooms " + string.Join(' ', rooms) : ""), p.Kind,
        p.Height.ToString("0.###", CultureInfo.InvariantCulture),
        p.Type == 6 ? ZoneWater.DefaultLavaTexture : ZoneWater.DefaultTexture, "", "0", "0");

    private static Vector3 Unit(Vector3 v) => v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.Zero;

    // Adds a triangle wound so its normal points up (+z).
    private static void AddUp(Vector3[] v, List<(ushort, ushort, ushort)> tris, ushort a, ushort b, ushort c)
    {
        float z = Vector3.Cross(v[b] - v[a], v[c] - v[a]).Z;
        if (z != 0)
            tris.Add(z > 0 ? (a, b, c) : (a, c, b));
    }
}
