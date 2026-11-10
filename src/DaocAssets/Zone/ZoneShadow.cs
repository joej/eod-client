// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's ground shading (W8; daoc-re RE/65, RE/66, 2026-11-09): shadow.pcx in its dat archive, 256 x 256, one
// texel per heightmap cell (256 units) in terrain.pcx's orientation. Each terrain vertex takes the colour v/255
// (grey) from its texel, multiplying the ground texture; mainland zones are mostly 255 with patches down to about
// 100. Nothing else uses it: models and figures aren't shaded by it.
//
// At load the client darkens it under tree clusters: for each tree of a cluster fixture (TreeClusters.ShadowPoints)
// it subtracts this kernel from the 3 x 3 texels around the tree, clamped at 0:
//      10  58  10
//      58 151  58
//      10  58  10
// Single trees leave no stamp. Which texel is the middle one (nearest, or the one the point falls in) isn't known;
// we take the nearest.
//
// Written beside zone.csv as shadow.pgm (binary greymap: "P5 W H 255" then W x H bytes), which the client loads as
// a texture and any image viewer opens.

using System.Text;

namespace DaocAssets.Zone;

public static class ZoneShadow
{
    private static readonly int[,] Kernel = { { 10, 58, 10 }, { 58, 151, 58 }, { 10, 58, 10 } };

    /// <summary>The zone's shadow.pcx, or null when it has none (cities, some dungeons).</summary>
    public static Pcx? Read(ZoneArchive zone) =>
        zone.Read("shadow.pcx") is byte[] bytes ? Pcx.Read(bytes) : null;

    /// <summary>
    /// Subtracts the tree kernel around each point (zone-local region units) from <paramref name="shadow"/>,
    /// <paramref name="cellSize"/> units per texel. Returns how many points landed on the map.
    /// </summary>
    public static int Stamp(Pcx shadow, IEnumerable<(float X, float Y)> localPoints, float cellSize = 256)
    {
        int stamped = 0;
        foreach ((float x, float y) in localPoints)
        {
            int cx = (int)MathF.Round(x / cellSize), cy = (int)MathF.Round(y / cellSize);
            if (cx < 0 || cy < 0 || cx >= shadow.Width || cy >= shadow.Height)
                continue;
            stamped++;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = cx + dx, py = cy + dy;
                if (px < 0 || py < 0 || px >= shadow.Width || py >= shadow.Height)
                    continue;
                int i = py * shadow.Width + px;
                shadow.Pixels[i] = (byte)Math.Max(0, shadow.Pixels[i] - Kernel[dy + 1, dx + 1]);
            }
        }
        return stamped;
    }

    /// <summary>The map as a binary PGM ("P5").</summary>
    public static byte[] ToPgm(Pcx shadow)
    {
        byte[] header = Encoding.ASCII.GetBytes($"P5\n{shadow.Width} {shadow.Height}\n255\n");
        var bytes = new byte[header.Length + shadow.Pixels.Length];
        header.CopyTo(bytes, 0);
        shadow.Pixels.CopyTo(bytes, header.Length);
        return bytes;
    }

    /// <summary>Reads a PGM written by <see cref="ToPgm"/>: width, height and the bytes; null if it isn't one.</summary>
    public static (int Width, int Height, byte[] Pixels)? ReadPgm(byte[] pgm)
    {
        // Four whitespace-separated header fields, then the raster after exactly one whitespace byte.
        var fields = new List<string>();
        int p = 0;
        while (fields.Count < 4 && p < pgm.Length)
        {
            while (p < pgm.Length && char.IsWhiteSpace((char)pgm[p])) p++;
            int start = p;
            while (p < pgm.Length && !char.IsWhiteSpace((char)pgm[p])) p++;
            fields.Add(Encoding.ASCII.GetString(pgm, start, p - start));
        }
        p++;
        if (fields.Count < 4 || fields[0] != "P5" || !int.TryParse(fields[1], out int w) || !int.TryParse(fields[2], out int h)
            || fields[3] != "255" || w <= 0 || h <= 0 || pgm.Length - p < w * h)
            return null;
        return (w, h, pgm[p..(p + w * h)]);
    }
}
