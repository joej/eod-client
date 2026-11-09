// SPDX-License-Identifier: GPL-3.0-or-later
//
// From the number the server gives a skill, style or spell to the picture the client draws, through the
// install's gamedata.mpk tables (column meanings: daoc-re RE/54):
//
//   icons.csv   ID → baseIconId (a cell of the icon sheets, IconSheets), borderId (a frame of brdr_0.bmp, -1 none),
//               then seven style marks (up left, up, up right, right, down right, down, left: a 10 x 10 cell of
//               wgsm_0.bmp, -1 none) and a "spell" mark (not drawn yet: its cells, 100 and up, are in wglg_100).
//   spells.csv  ID (the number the server sends for a spell) → Icon (an icons.csv ID).
//   styles.csv  ID (the number the server sends for a style) → Icon (an icons.csv ID).
//
// Abilities and realm abilities send an icons.csv ID directly. Checked 2026-11-09: Sprint 409 → base 1602 (the
// skills sheet), Amethyst Shield spell 1 → 1000 → base 91, Recenter style 110 → 654 → base 555 (a combat
// sheet). Retail draws the base, then the border over it, then the marks over both, each at its place on the 32 x 32
// icon (daoc-re RE/68): a 3 x 3 grid of 10-pixel cells with the centre free. The marks say how a style opens (from
// the side, behind, in front; after a block, parry or evade; a step of a chain) and what it does to threat.

using System.Globalization;

namespace DaocAssets.Ui;

/// <summary>Which table the server's number for an entry is looked up in.</summary>
public enum IconKind { Icon, Spell, Style }

/// <summary>A style mark: a cell of wgsm_0.bmp (10 x 10) and where it goes on the 32 x 32 icon.</summary>
public readonly record struct IconMark(int Cell, int X, int Y);

/// <summary>What the client draws for an entry: a base icon, a border frame (-1 none) and the style marks.</summary>
public sealed record IconPicture(int Base, int Border, IReadOnlyList<IconMark> Marks);

public sealed class IconTable
{
    private readonly Dictionary<int, IconPicture> _icons;
    private readonly Dictionary<int, int> _spells, _styles;

    // Where each mark column (icons.csv columns 4–10) is drawn: up left, up, up right, right, down right, down, left.
    private static readonly (int X, int Y)[] MarkPlaces = [(0, 0), (11, 0), (22, 0), (22, 11), (22, 22), (11, 22), (0, 11)];

    private IconTable(Dictionary<int, IconPicture> icons, Dictionary<int, int> spells, Dictionary<int, int> styles) =>
        (_icons, _spells, _styles) = (icons, spells, styles);

    public static IconTable Empty { get; } = new([], [], []);

    public int Count => _icons.Count;

    /// <summary>Reads the three tables' text (header rows and ';' comment rows are skipped).</summary>
    public static IconTable Parse(string iconsCsv, string spellsCsv, string stylesCsv)
    {
        var icons = new Dictionary<int, IconPicture>();
        foreach (string[] c in Rows(iconsCsv))
            if (c.Length > 3 && Int(c[0]) is int id && Int(c[2]) is int b)
            {
                var marks = new List<IconMark>();
                for (int m = 0; m < MarkPlaces.Length && 4 + m < c.Length; m++)
                    if (Int(c[4 + m]) is int cell and >= 0 and < 100)
                        marks.Add(new IconMark(cell, MarkPlaces[m].X, MarkPlaces[m].Y));
                icons.TryAdd(id, new IconPicture(b, Int(c[3]) ?? -1, marks));
            }
        return new IconTable(icons, Pairs(spellsCsv), Pairs(stylesCsv));
    }

    /// <summary>The picture (an icon-sheet number, a border frame, style marks) for the server's number, or null.</summary>
    public IconPicture? Resolve(IconKind kind, int number)
    {
        int? icon = kind switch
        {
            IconKind.Spell => _spells.TryGetValue(number, out int s) ? s : null,
            IconKind.Style => _styles.TryGetValue(number, out int t) ? t : null,
            _ => number,
        };
        return icon is int i && _icons.TryGetValue(i, out var picture) ? picture : null;
    }

    // ID (column 0) → Icon (column 2).
    private static Dictionary<int, int> Pairs(string csv)
    {
        var map = new Dictionary<int, int>();
        foreach (string[] c in Rows(csv))
            if (c.Length > 2 && Int(c[0]) is int id && Int(c[2]) is int icon)
                map.TryAdd(id, icon);
        return map;
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != ';').Select(l => l.Split(','));

    private static int? Int(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
}
