// SPDX-License-Identifier: GPL-3.0-or-later
//
// Checks a dungeon's assembly against itself: portals.csv joins two rooms through a portal polygon in each ("roomA,
// portalNo, roomB": node portal<NN> of roomA's piece; daoc-re's dungeon note, 2026-11-10). Placed as the client places
// them (DungeonLayout), the two rooms' doorways must meet: roomA's portal node lands on one of roomB's. A wrong turn or
// origin sign moves whole rooms apart, so this tests the room transform without the server or a screen.

using System.Globalization;
using System.Numerics;

namespace DaocAssets.Zone;

/// <summary>One portals.csv link and how far its two doorways land apart once the rooms are placed.</summary>
/// <param name="Gap">Distance from roomA's portal node to the nearest portal node of roomB, in units (null: a node or
/// piece is missing).</param>
public readonly record struct PortalLink(int RoomA, int Portal, int RoomB, float? Gap);

public static class DungeonCheck
{
    /// <summary>portals.csv: line 1 is a count; then "roomA, portalNo, roomB[, n]" (the 4th column is not read).</summary>
    public static List<(int RoomA, int Portal, int RoomB)> ParsePortals(string csv)
    {
        var links = new List<(int, int, int)>();
        foreach (string line in csv.Split('\n').Skip(1))
        {
            string[] c = line.Split(',');
            if (c.Length >= 3 && Int(c[0]) is int a && Int(c[1]) is int n && Int(c[2]) is int b)
                links.Add((a, n, b));
        }
        return links;
    }

    /// <summary>
    /// Measures each link. <paramref name="portalNodes"/> gives a piece's portal nodes (lower-case name → position in
    /// NIF space), or null when the piece can't be read.
    /// </summary>
    public static List<PortalLink> Check(DungeonLayout layout, IEnumerable<(int RoomA, int Portal, int RoomB)> links,
        Func<string, IReadOnlyDictionary<string, Vector3>?> portalNodes)
    {
        var rooms = layout.Rooms.ToDictionary(r => r.Id);
        var result = new List<PortalLink>();
        foreach ((int a, int n, int b) in links)
        {
            float? gap = null;
            if (rooms.TryGetValue(a, out DungeonRoom ra) && rooms.TryGetValue(b, out DungeonRoom rb)
                && portalNodes(ra.Piece) is { } na && portalNodes(rb.Piece) is { } nb
                && na.TryGetValue($"portal{n:00}", out Vector3 pa) && nb.Count > 0)
            {
                Vector3 at = DungeonLayout.RegionPoint(ra, pa);
                gap = nb.Values.Min(p => Vector3.Distance(at, DungeonLayout.RegionPoint(rb, p)));
            }
            result.Add(new PortalLink(a, n, b, gap));
        }
        return result;
    }

    /// <summary>The portal nodes among a model's named nodes (NifModel.NodeWorlds): "portal01" … with their positions.</summary>
    public static Dictionary<string, Vector3> PortalNodes(IReadOnlyDictionary<string, Matrix4x4> nodeWorlds) =>
        nodeWorlds.Where(kv => kv.Key.StartsWith("portal", StringComparison.Ordinal) && kv.Key.Length > 6
                               && kv.Key[6..].All(char.IsAsciiDigit))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Translation);

    private static int? Int(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>
    /// How many points (region coordinates: where the server stands its NPCs) have a floor of the placed rooms under
    /// them, within <paramref name="below"/> units down (or <paramref name="above"/> up: a point a little inside the
    /// floor). <paramref name="roomTriangles"/> gives a piece's surfaces as NIF-space triangles, or null.
    /// </summary>
    public static (int OnFloor, int Points) Floors(DungeonLayout layout, IReadOnlyList<Vector3> points,
        Func<string, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)>?> roomTriangles, float below = 100, float above = 16)
    {
        var placed = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (DungeonRoom r in layout.Rooms)
            if (roomTriangles(r.Piece) is { } tris)
                foreach (var t in tris)
                    placed.Add((DungeonLayout.RegionPoint(r, t.A), DungeonLayout.RegionPoint(r, t.B), DungeonLayout.RegionPoint(r, t.C)));
        int on = 0;
        foreach (Vector3 p in points)
            if (placed.Any(t => HeightAt(t, p.X, p.Y) is float h && h <= p.Z + above && h >= p.Z - below))
                on++;
        return (on, points.Count);
    }

    // The triangle's height over (x, y), when (x, y) is inside its outline seen from above.
    private static float? HeightAt((Vector3 A, Vector3 B, Vector3 C) t, float x, float y)
    {
        float d = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
        if (MathF.Abs(d) < 1e-6f)
            return null;
        float u = ((t.B.Y - t.C.Y) * (x - t.C.X) + (t.C.X - t.B.X) * (y - t.C.Y)) / d;
        float v = ((t.C.Y - t.A.Y) * (x - t.C.X) + (t.A.X - t.C.X) * (y - t.C.Y)) / d;
        float w = 1 - u - v;
        return u < 0 || v < 0 || w < 0 ? null : u * t.A.Z + v * t.B.Z + w * t.C.Z;
    }
}
