// SPDX-License-Identifier: GPL-3.0-or-later
//
// Dyes and guild emblems on worn items (roadmap M7b). gamedata.mpk tables:
//   tints.csv     dye id -> red, green, blue for classic, expansion and Catacombs models (columns 2-4, 5-7,
//                 8-10). Players are Catacombs figures, so the Catacombs colour; the client multiplies the
//                 item's texture by it (as OfflineDAoC, GPL-3.0, found).
//   patterns.csv  set 1: the 16 primary emblem colours, set 2: the 8 secondary ones (set, id, name, r, g, b).
//   insignia.csv  logo id -> its picture in the install's insignia/ folder (black on white).
// An emblem's background is one of four 2 x 4 pixel patterns of the two colours, built by the client;
// the masks below (1 = the secondary colour, rows top to bottom) are as OfflineDAoC reports them.

using System.Globalization;

namespace DaocAssets.Figures;

public sealed record ItemColours(
    IReadOnlyDictionary<int, (byte R, byte G, byte B)> Dyes,
    IReadOnlyList<(byte R, byte G, byte B)> Primary,
    IReadOnlyList<(byte R, byte G, byte B)> Secondary,
    IReadOnlyDictionary<int, string> Logos)
{
    /// <summary>The four emblem patterns, 2 pixels wide and 4 tall, row by row: 1 = the secondary colour.</summary>
    public static readonly int[][] PatternMasks =
    [
        [0, 1, 1, 0, 1, 0, 1, 0],
        [0, 1, 0, 1, 0, 1, 1, 0],
        [1, 1, 1, 1, 0, 0, 1, 0],
        [0, 0, 0, 0, 0, 0, 0, 0],
    ];

    public static ItemColours Parse(string tintsCsv, string patternsCsv, string insigniaCsv)
    {
        static byte B(string s) => byte.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b) ? b : (byte)255;
        var dyes = new SortedDictionary<int, (byte, byte, byte)>();
        foreach (string[] c in Rows(tintsCsv))
            if (c.Length > 10 && int.TryParse(c[0], out int id))
                dyes[id] = (B(c[8]), B(c[9]), B(c[10]));
        var primary = new (byte, byte, byte)[16];
        var secondary = new (byte, byte, byte)[8];
        foreach (string[] c in Rows(patternsCsv))
            if (c.Length > 5 && int.TryParse(c[0], out int set) && int.TryParse(c[1], out int i))
            {
                if (set == 1 && i is >= 0 and < 16) primary[i] = (B(c[3]), B(c[4]), B(c[5]));
                else if (set == 2 && i is >= 0 and < 8) secondary[i] = (B(c[3]), B(c[4]), B(c[5]));
            }
        var logos = new SortedDictionary<int, string>();
        foreach (string[] c in Rows(insigniaCsv))
            if (c.Length > 2 && int.TryParse(c[0], out int id) && c[2].Trim().Length > 0)
                logos[id] = c[2].Trim();
        return new ItemColours(dyes, primary, secondary, logos);
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Select(l => l.TrimEnd('\r').Split(','));
}
