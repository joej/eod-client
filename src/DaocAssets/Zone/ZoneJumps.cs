// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's jump points: the doorways (a city gate, a dungeon entrance, a portal) that send the player to
// another place, from the zone archive's zonejump.csv. Rows are "row, name, x1, y1, x2, y2, z1, z2,
// zone point, ...": a line on the ground from (x1, y1) to (x2, y2), zone-local, standing from z1 to z2
// (region Z), and the id of the server's zone point it uses. Checked 2026-11-07 against OpenDAoC's
// zonepoint table: Camelot Hills' CamCity row names zone point 6, whose recorded source (551980, 512082,
// region 1) is that line's middle plus the zone's offset, and whose target is Camelot City (region 10);
// TombEnt names 24 (the Tomb of Mithra), DFEntrance 81 (Darkness Falls). Walking through the line is
// the client's to notice; it then sends the id (C->S 0x90), and the server moves the player.

using System.Globalization;

namespace DaocAssets.Zone;

/// <summary>A doorway line in region-global coordinates and the server zone point it uses.</summary>
public sealed record ZoneJump(ushort ZonePoint, string Name, float X1, float Y1, float X2, float Y2, float Z1, float Z2);

public static class ZoneJumps
{
    /// <summary>The rows of a zonejump.csv, moved to region coordinates by the zone's offset. Rows without
    /// a numeric zone point (or with too few columns) are skipped.</summary>
    public static List<ZoneJump> Parse(string zoneJumpCsv, int offsetX, int offsetY)
    {
        var jumps = new List<ZoneJump>();
        foreach (string raw in zoneJumpCsv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 9 || !ushort.TryParse(c[8].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort point))
                continue;
            var v = new float[6];
            bool ok = true;
            for (int k = 0; k < 6; k++)
                ok &= float.TryParse(c[2 + k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
            if (ok)
                jumps.Add(new ZoneJump(point, c[1].Trim(), offsetX + v[0], offsetY + v[1], offsetX + v[2], offsetY + v[3], v[4], v[5]));
        }
        return jumps;
    }

    /// <summary>jumps.csv beside zone.csv: a header, then "zonepoint,name,x1,y1,x2,y2,z1,z2" per line.</summary>
    public static string ToCsv(IEnumerable<ZoneJump> jumps)
    {
        var sb = new System.Text.StringBuilder("zonepoint,name,x1,y1,x2,y2,z1,z2\n");
        foreach (ZoneJump j in jumps)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{j.ZonePoint},{j.Name},{j.X1:0},{j.Y1:0},{j.X2:0},{j.Y2:0},{j.Z1:0},{j.Z2:0}\n"));
        return sb.ToString();
    }

    /// <summary>Reads a jumps.csv written by <see cref="ToCsv"/>.</summary>
    public static List<ZoneJump> ReadCsv(string csv)
    {
        var jumps = new List<ZoneJump>();
        foreach (string raw in csv.Split('\n').Skip(1))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 8 || !ushort.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort point))
                continue;
            float F(int i) => float.Parse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            jumps.Add(new ZoneJump(point, c[1], F(2), F(3), F(4), F(5), F(6), F(7)));
        }
        return jumps;
    }

    /// <summary>
    /// Whether a step from (ax, ay) to (bx, by) at height z passes through the doorway: the step and the
    /// line cross, and z is between the line's floor and top (with <paramref name="slack"/> units of
    /// leeway, since a character's Z follows the ground and a doorway's floor is drawn at it). Either
    /// direction counts: the install lists two-way gates twice with the ends swapped, and which way a
    /// one-way row fires on is not known (open question W47). Retail's doorway is exactly between the two heights; the
    /// slack is ours, kept on purpose: our character stands on our ground, which can sit a little under the doorway's floor.
    /// </summary>
    public static bool Crosses(ZoneJump j, float ax, float ay, float bx, float by, float z, float slack = 64)
    {
        if (z < j.Z1 - slack || z > j.Z2 + slack)
            return false;
        static float Side(float px, float py, float qx, float qy, float rx, float ry) =>
            (qx - px) * (ry - py) - (qy - py) * (rx - px);
        float s1 = Side(j.X1, j.Y1, j.X2, j.Y2, ax, ay), s2 = Side(j.X1, j.Y1, j.X2, j.Y2, bx, by);
        float s3 = Side(ax, ay, bx, by, j.X1, j.Y1), s4 = Side(ax, ay, bx, by, j.X2, j.Y2);
        return (s1 > 0) != (s2 > 0) && (s3 > 0) != (s4 > 0);
    }
}
