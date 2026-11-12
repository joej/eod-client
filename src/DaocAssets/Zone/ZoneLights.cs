// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's fixed coloured lights: torches, fires, lamps (daoc-re RE/69). Each zone's lights.csv (csv archive first,
// ZoneArchive) lists "x, y, z, type" in zone units; type -1 is skipped. The type is a row of the install's
// zones/textures/LIGHTDEF.CSV: two header lines, then "id, name, r, g, b, c5, range, c7" ("9,fire-light,255,100,0,
// 10,256,1"); the range is 256, 512 (BIG…) or 1024 (GIANT…) units, 256 when empty or 0. c7 set = attenuation 1/(d/r)^2,
// else 1/(d/r) (RE/82); every row of the install's file has it set, so only the quadratic one is carried. c5 (always
// 10) isn't known. The client makes one light per row as the area loads; they are night lamps, lit 17:00-7:00.

using System.Globalization;

namespace DaocAssets.Zone;

/// <summary>One fixed light: region position, colour (0–255) and range in region units.</summary>
public readonly record struct ZoneLight(float X, float Y, float Z, byte R, byte G, byte B, float Range);

public static class ZoneLights
{
    public const float DefaultRange = 256;

    /// <summary>The light kinds of LIGHTDEF.CSV: id → colour and range.</summary>
    public static Dictionary<int, (byte R, byte G, byte B, float Range)> ParseDefinitions(string lightdefCsv)
    {
        var defs = new Dictionary<int, (byte, byte, byte, float)>();
        foreach (string[] c in Rows(lightdefCsv))
            if (c.Length >= 5 && Int(c[0]) is int id && Int(c[2]) is int r && Int(c[3]) is int g && Int(c[4]) is int b)
            {
                float range = c.Length > 6 && Int(c[6]) is int v && v > 0 ? v : DefaultRange;
                defs.TryAdd(id, ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), range));
            }
        return defs;
    }

    /// <summary>A zone's lights.csv, in region coordinates (the zone's offset added); rows of an unknown type or -1 skipped.</summary>
    public static List<ZoneLight> Parse(string lightsCsv, IReadOnlyDictionary<int, (byte R, byte G, byte B, float Range)> defs,
        int offsetX, int offsetY)
    {
        var lights = new List<ZoneLight>();
        foreach (string[] c in Rows(lightsCsv))
            if (c.Length >= 4 && Float(c[0]) is float x && Float(c[1]) is float y && Float(c[2]) is float z
                && Int(c[3]) is int type && type >= 0 && defs.TryGetValue(type, out var d))
                lights.Add(new ZoneLight(offsetX + x, offsetY + y, z, d.R, d.G, d.B, d.Range));
        return lights;
    }

    /// <summary>lights.csv beside zone.csv: a header, then "x,y,z,r,g,b,range" per light (region units).</summary>
    public static string ToCsv(IEnumerable<ZoneLight> lights)
    {
        var sb = new System.Text.StringBuilder("x,y,z,r,g,b,range\n");
        foreach (ZoneLight l in lights)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{l.X:0},{l.Y:0},{l.Z:0},{l.R},{l.G},{l.B},{l.Range:0}\n"));
        return sb.ToString();
    }

    /// <summary>Reads a lights.csv written by <see cref="ToCsv"/>.</summary>
    public static List<ZoneLight> ReadCsv(string csv)
    {
        var lights = new List<ZoneLight>();
        foreach (string[] c in Rows(csv).Skip(1))
            if (c.Length >= 7 && Float(c[0]) is float x && Float(c[1]) is float y && Float(c[2]) is float z
                && Int(c[3]) is int r && Int(c[4]) is int g && Int(c[5]) is int b && Float(c[6]) is float range)
                lights.Add(new ZoneLight(x, y, z, (byte)r, (byte)g, (byte)b, range));
        return lights;
    }

    /// <summary>The install's LIGHTDEF.CSV (zones/textures/, any case), found from a zone archive's path, or null.</summary>
    public static string? FindDefinitions(string zoneDatMpk)
    {
        for (string? dir = Path.GetDirectoryName(Path.GetFullPath(zoneDatMpk)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string? zones = Child(dir, "zones");
            string? textures = zones is null ? null : Child(zones, "textures");
            if (textures is not null && Directory.EnumerateFiles(textures).FirstOrDefault(f =>
                    Path.GetFileName(f).Equals("lightdef.csv", StringComparison.OrdinalIgnoreCase)) is string found)
                return found;
        }
        return null;

        static string? Child(string dir, string name) => Directory.Exists(dir)
            ? Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.Split(',').Select(s => s.Trim()).ToArray());

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static float? Float(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
}
