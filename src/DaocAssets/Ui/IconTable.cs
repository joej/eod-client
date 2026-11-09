// SPDX-License-Identifier: GPL-3.0-or-later
//
// From the number the server gives a skill, style or spell to the picture the client draws, through the
// install's gamedata.mpk tables (column meanings: daoc-re RE/54):
//
//   icons.csv   ID → baseIconId (a cell of the icon sheets, IconSheets), borderId (a frame of brdr_0.bmp, -1 none),
//               then eight arrow overlays and a "spell" column (not used here).
//   spells.csv  ID (the number the server sends for a spell) → Icon (an icons.csv ID).
//   styles.csv  ID (the number the server sends for a style) → Icon (an icons.csv ID).
//
// Abilities and realm abilities send an icons.csv ID directly. Checked 2026-11-09: Sprint 409 → base 1602 (the
// skills sheet), Amethyst Shield spell 1 → 1000 → base 91, Recenter style 110 → 654 → base 555 (a combat
// sheet). Retail draws the style arrows (up left, …) too; not yet.

using System.Globalization;

namespace DaocAssets.Ui;

/// <summary>Which table the server's number for an entry is looked up in.</summary>
public enum IconKind { Icon, Spell, Style }

public sealed class IconTable
{
    private readonly Dictionary<int, (int Base, int Border)> _icons;
    private readonly Dictionary<int, int> _spells, _styles;

    private IconTable(Dictionary<int, (int, int)> icons, Dictionary<int, int> spells, Dictionary<int, int> styles) =>
        (_icons, _spells, _styles) = (icons, spells, styles);

    public static IconTable Empty { get; } = new([], [], []);

    public int Count => _icons.Count;

    /// <summary>Reads the three tables' text (header rows and ';' comment rows are skipped).</summary>
    public static IconTable Parse(string iconsCsv, string spellsCsv, string stylesCsv)
    {
        var icons = new Dictionary<int, (int, int)>();
        foreach (string[] c in Rows(iconsCsv))
            if (c.Length > 3 && Int(c[0]) is int id && Int(c[2]) is int b)
                icons.TryAdd(id, (b, Int(c[3]) ?? -1));
        return new IconTable(icons, Pairs(spellsCsv), Pairs(stylesCsv));
    }

    /// <summary>The base picture (an icon-sheet number) and border frame (-1 none) for the server's number, or null.</summary>
    public (int Base, int Border)? Resolve(IconKind kind, int number)
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
