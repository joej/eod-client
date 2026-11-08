// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's edge walls: what keeps a player from walking or swimming off the map. Neither the retail
// client nor the server checks "is there a zone here"; instead each zone has hand-drawn invisible walls in
// bound.csv (in the zone's csvNNN.mpk, else its datNNN.mpk; the two copies differ in 35 zones, and the csv
// one is the one the client reads). One wall per line: "closed, count, x1, y1, x2, y2, ...", zone-local
// units, count 1-98 (other lines are skipped), closed = 1 joins the last point to the first. Each segment
// is a vertical wall of any height (land, water and falling alike), never drawn, and only the local player
// collides with it. Tutorial zones 27-29 are each one closed loop 500-2,000 units inside the zone square;
// most other zones have open lines along coasts and map edges, and a few none at all.
//
// The collision (Step) follows the retail client's rules (daoc-re, from the 1.130 client, 2026-11-08; not
// yet compared in a live game, open question W54): a step into one wall slides along it, v - 1.02 (v.n) n
// with n the wall's horizontal normal, keeping the movement along it and easing 2% away; moving away from
// a wall is never blocked; at a corner each wall's slide is tried and the first that hits no wall is taken,
// else the player stops where they were. No message, no push-back.

using System.Globalization;

namespace DaocAssets.Zone;

/// <summary>One edge-wall segment, region coordinates.</summary>
public readonly record struct BoundSegment(float X1, float Y1, float X2, float Y2);

public static class ZoneBounds
{
    private const int MaxPoints = 98;
    private const float SlideFactor = 1.02f;

    /// <summary>The walls of a bound.csv, moved to region coordinates by the zone's offset.</summary>
    public static List<BoundSegment> Parse(string boundCsv, int offsetX, int offsetY)
    {
        var segments = new List<BoundSegment>();
        foreach (string raw in boundCsv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 2 || !int.TryParse(c[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int closed)
                || !int.TryParse(c[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count < 1 || count > MaxPoints || c.Length < 2 + 2 * count)
                continue;
            var points = new (float X, float Y)[count];
            bool ok = true;
            for (int i = 0; i < count && ok; i++)
                ok = float.TryParse(c[2 + 2 * i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out points[i].X)
                    & float.TryParse(c[3 + 2 * i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out points[i].Y);
            if (!ok)
                continue;
            for (int i = 0; i + 1 < count; i++)
                segments.Add(Segment(points[i], points[i + 1], offsetX, offsetY));
            if (closed == 1 && count > 2)
                segments.Add(Segment(points[^1], points[0], offsetX, offsetY));
        }
        return segments;
    }

    private static BoundSegment Segment((float X, float Y) a, (float X, float Y) b, int ox, int oy) =>
        new(ox + a.X, oy + a.Y, ox + b.X, oy + b.Y);

    /// <summary>bounds.csv beside zone.csv: a header, then "x1,y1,x2,y2" per segment (region units).</summary>
    public static string ToCsv(IEnumerable<BoundSegment> segments)
    {
        var sb = new System.Text.StringBuilder("x1,y1,x2,y2\n");
        foreach (BoundSegment s in segments)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{s.X1:0},{s.Y1:0},{s.X2:0},{s.Y2:0}\n"));
        return sb.ToString();
    }

    /// <summary>Reads a bounds.csv written by <see cref="ToCsv"/>.</summary>
    public static List<BoundSegment> ReadCsv(string csv)
    {
        var segments = new List<BoundSegment>();
        foreach (string raw in csv.Split('\n').Skip(1))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 4)
                continue;
            var v = new float[4];
            bool ok = true;
            for (int k = 0; k < 4; k++)
                ok &= float.TryParse(c[k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
            if (ok)
                segments.Add(new BoundSegment(v[0], v[1], v[2], v[3]));
        }
        return segments;
    }

    /// <summary>
    /// Where a horizontal step from (fx, fy) to (tx, ty) ends against the walls: the target if it crosses
    /// none, a slide along the wall it runs into, or the start (a corner, or a slide that would hit a wall).
    /// </summary>
    public static (float X, float Y) Step(IReadOnlyList<BoundSegment> walls, float fx, float fy, float tx, float ty)
    {
        float vx = tx - fx, vy = ty - fy;
        if (vx == 0 && vy == 0)
            return (tx, ty);
        List<BoundSegment>? hit = null;
        foreach (BoundSegment w in walls)
            if (Crosses(w, fx, fy, tx, ty))
                (hit ??= []).Add(w);
        if (hit is null)
            return (tx, ty);
        foreach (BoundSegment w in hit)
        {
            (float nx, float ny) = Normal(w, fx, fy);
            float d = vx * nx + vy * ny;
            if (d >= 0)
                continue;   // a wall the step leaves isn't one it runs into
            float sx = fx + vx - SlideFactor * d * nx, sy = fy + vy - SlideFactor * d * ny;
            if (!walls.Any(o => Crosses(o, fx, fy, sx, sy)))
                return (sx, sy);
        }
        return (fx, fy);
    }

    // The wall's unit normal on the side of point (px, py).
    private static (float X, float Y) Normal(BoundSegment w, float px, float py)
    {
        float dx = w.X2 - w.X1, dy = w.Y2 - w.Y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len == 0)
            return (0, 0);
        float nx = -dy / len, ny = dx / len;
        return (px - w.X1) * nx + (py - w.Y1) * ny >= 0 ? (nx, ny) : (-nx, -ny);
    }

    // Whether the step a -> b reaches or passes the wall. Touching counts (a step that ends on the wall is
    // into it); starting on the wall and moving off it does not.
    private static bool Crosses(BoundSegment w, float ax, float ay, float bx, float by)
    {
        static float Side(float px, float py, float qx, float qy, float rx, float ry) =>
            (qx - px) * (ry - py) - (qy - py) * (rx - px);
        float s1 = Side(w.X1, w.Y1, w.X2, w.Y2, ax, ay), s2 = Side(w.X1, w.Y1, w.X2, w.Y2, bx, by);
        if (s1 == 0 || (s1 > 0) == (s2 > 0) && s2 != 0)
            return false;
        float s3 = Side(ax, ay, bx, by, w.X1, w.Y1), s4 = Side(ax, ay, bx, by, w.X2, w.Y2);
        return (s3 > 0) != (s4 > 0) || s3 == 0 || s4 == 0;
    }
}
