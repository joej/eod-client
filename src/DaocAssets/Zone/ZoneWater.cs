// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's rivers, lakes and swamps, from its SECTOR.DAT. Each [riverNN] section gives a type
// (RIVER/LAKE/SWAMP), a name, the water surface height, a texture and a ripple overlay ("watery",
// "ripl"), a flow speed, a colour, and two bank lines: leftNN and rightNN points, "x,y,z" in units of
// the zone's 256-cell grid (the same grid as water.pcx). The banks sit on dry ground above the
// waterline, so a flat surface spanning them at the river's height meets the terrain at the shore.
// Verified on Camelot Hills (2026-11-01): river00 has 33 bank pairs, height 1916, banks at terrain
// height ~2150 either side of a ~1610 riverbed, and the bridge at cell (143, 199) between pair 9's
// banks. Not handled yet: extend_posx/posy/negx/negy (likely "continue to the zone edge"),
// tesselation, and the colour fields' exact encoding (kept raw).

using System.Globalization;
using System.Numerics;
using DaocAssets.Nif;

namespace DaocAssets.Zone;

/// <summary>One body of water from a zone's SECTOR.DAT.</summary>
public sealed record WaterBody
{
    /// <summary>The section number (river00 → 0).</summary>
    public required int Index { get; init; }
    public required string Name { get; init; }
    /// <summary>RIVER, LAKE or SWAMP (empty if the section doesn't say).</summary>
    public required string Type { get; init; }
    /// <summary>Surface height, region z units.</summary>
    public required float Height { get; init; }
    /// <summary>Base texture stem ("watery"), without extension.</summary>
    public required string Texture { get; init; }
    /// <summary>Overlay texture stem ("ripl"), or empty.</summary>
    public required string MultiTexture { get; init; }
    /// <summary>How fast the texture moves along the river (sign = direction; units unconfirmed).</summary>
    public required float Flow { get; init; }
    /// <summary>The raw "color" value, encoding unconfirmed.</summary>
    public required long Color { get; init; }
    /// <summary>Bank points in zone-local region units (grid cell × cell size).</summary>
    public required IReadOnlyList<Vector2> Left { get; init; }
    public required IReadOnlyList<Vector2> Right { get; init; }

    /// <summary>The name its surface mesh and water.csv row use: "water00".</summary>
    public string MeshName => $"water{Index:00}";
}

public static class ZoneWater
{
    /// <summary>Texture repeats every this many region units (≈12.8 m); a guess to tune by eye.</summary>
    public const float TileUnits = 512f;

    /// <summary>
    /// Parses every [riverNN] section of a SECTOR.DAT. Bank points are scaled from the 256-cell grid
    /// to region units using the zone's size ([sectorsize] sizex × 8192 / 256). Sections without at
    /// least two bank pairs (some zones carry empty placeholders) are skipped.
    /// </summary>
    public static IReadOnlyList<WaterBody> Parse(string sectorDat)
    {
        var sections = ReadSections(sectorDat);
        int sizeX = sections.TryGetValue("sectorsize", out var size) && size.TryGetValue("sizex", out string? sx)
            && int.TryParse(sx, out int n) && n > 0 ? n : 8;
        float cell = sizeX * 8192f / 256f;

        var bodies = new List<WaterBody>();
        foreach ((string name, Dictionary<string, string> keys) in sections)
        {
            if (!name.StartsWith("river", StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                continue;
            List<Vector2> left = Points(keys, "left", cell), right = Points(keys, "right", cell);
            if (Math.Min(left.Count, right.Count) < 2)
                continue;
            bodies.Add(new WaterBody
            {
                Index = index,
                Name = keys.GetValueOrDefault("name", ""),
                Type = keys.GetValueOrDefault("type", "").ToUpperInvariant(),
                Height = Float(keys.GetValueOrDefault("height")),
                Texture = Stem(keys.GetValueOrDefault("texture", "")),
                MultiTexture = Stem(keys.GetValueOrDefault("multitexture", "")),
                Flow = Float(keys.GetValueOrDefault("flow")),
                Color = long.TryParse(keys.GetValueOrDefault("color"), out long c) ? c : 0,
                Left = left,
                Right = right,
            });
        }
        return bodies.OrderBy(b => b.Index).ToList();
    }

    /// <summary>
    /// The water surface as a mesh in the same frame as the terrain mesh (NIF space: x east, −y,
    /// z up; region-global once the zone offset is added): quads between consecutive bank pairs at
    /// the surface height, facing up, double-sided (seen from below when swimming), alpha-blended.
    /// UVs run along the river (v, by distance down the centre line) and across it (u, by width), in
    /// <see cref="TileUnits"/>, so a flow can scroll v.
    /// </summary>
    public static NifMesh BuildMesh(WaterBody w, int offsetX, int offsetY)
    {
        int pairs = Math.Min(w.Left.Count, w.Right.Count);
        var verts = new Vector3[pairs * 2];
        var uvs = new Vector2[pairs * 2];
        var normals = new Vector3[pairs * 2];
        float along = 0;
        Vector2 prevMid = default;
        for (int i = 0; i < pairs; i++)
        {
            Vector2 l = w.Left[i], r = w.Right[i];
            Vector2 mid = (l + r) / 2;
            if (i > 0)
                along += Vector2.Distance(mid, prevMid);
            prevMid = mid;
            float width = Vector2.Distance(l, r);
            verts[2 * i] = new Vector3(offsetX + l.X, -(offsetY + l.Y), w.Height);
            verts[2 * i + 1] = new Vector3(offsetX + r.X, -(offsetY + r.Y), w.Height);
            uvs[2 * i] = new Vector2(0, along / TileUnits);
            uvs[2 * i + 1] = new Vector2(width / TileUnits, along / TileUnits);
            normals[2 * i] = normals[2 * i + 1] = Vector3.UnitZ;
        }

        var tris = new List<(ushort, ushort, ushort)>();
        for (int i = 0; i + 1 < pairs; i++)
        {
            ushort l0 = (ushort)(2 * i), r0 = (ushort)(2 * i + 1), l1 = (ushort)(2 * i + 2), r1 = (ushort)(2 * i + 3);
            AddUp(verts, tris, l0, r0, l1);
            AddUp(verts, tris, r0, r1, l1);
        }

        return new NifMesh
        {
            Name = w.MeshName,
            Vertices = verts,
            Normals = normals,
            TexCoords = uvs,
            Triangles = [.. tris],
            TextureFile = w.Texture.Length > 0 ? w.Texture + ".dds" : null,
            BaseColor = new Vector4(1, 1, 1, 0.75f),
            Alpha = SurfaceAlpha.Blend,
            DoubleSided = true,
        };
    }

    /// <summary>One line of water.csv per body (header: <see cref="CsvHeader"/>).</summary>
    public static string CsvRow(WaterBody w) => string.Join(",",
        w.MeshName, Csv(w.Name), w.Type, w.Height.ToString(CultureInfo.InvariantCulture), w.Texture,
        w.MultiTexture, w.Flow.ToString(CultureInfo.InvariantCulture), w.Color.ToString(CultureInfo.InvariantCulture));

    public const string CsvHeader = "mesh,name,type,height,texture,multitexture,flow,color";

    // Adds a triangle wound so its normal points up (+z), whatever order the banks run in.
    private static void AddUp(Vector3[] v, List<(ushort, ushort, ushort)> tris, ushort a, ushort b, ushort c)
    {
        float z = Vector3.Cross(v[b] - v[a], v[c] - v[a]).Z;
        if (z == 0)
            return;   // degenerate (coincident bank points)
        tris.Add(z > 0 ? (a, b, c) : (a, c, b));
    }

    private static List<Vector2> Points(Dictionary<string, string> keys, string prefix, float cell)
    {
        var points = new SortedDictionary<int, Vector2>();
        foreach ((string key, string value) in keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
                continue;
            string[] p = value.Split(',');
            if (p.Length >= 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                points[i] = new Vector2(x * cell, y * cell);
        }
        return [.. points.Values];
    }

    private static Dictionary<string, Dictionary<string, string>> ReadSections(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.Contains(']'))
            {
                string name = line[1..line.IndexOf(']')].Trim().ToLowerInvariant();
                current = sections.TryGetValue(name, out var existing) ? existing
                    : sections[name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (current is not null && eq > 0)
                current[line[..eq].Trim().ToLowerInvariant()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    private static float Float(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0;

    private static string Stem(string s) => Path.GetFileNameWithoutExtension(s.Trim());

    private static string Csv(string s) => s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
