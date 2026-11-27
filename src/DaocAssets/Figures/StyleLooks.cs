// SPDX-License-Identifier: GPL-3.0-or-later
//
// What a combat style looks like, from the install's styles.csv (gamedata; the library keeps a copy in icons/), read as the
// retail client reads it (daoc-re RE/75 "Styles", RE/82 "Weapon trail"): row = the style id the server sends in 0xBC
// (1-15999); columns from 0: 2 Icon, 3 Animation (a canims slot, 0 = the weapon's usual swing), 4 Imagenum (the trail's
// stylemap.csv texture), 5 Duration (ms), 6 Buildup (ms), 7-9 Red, Green, Blue (the trail's colour), 10 Hit Effect,
// 11 "anytime style". An anytime style is first swapped for the generic style row of the weapon's Combat Stance. A style
// id with no row draws with the client's defaults: Imagenum 3 (no stylemap row, so an untextured trail), 1000 ms, 200 ms,
// white.

using System.Globalization;
using DaocAssets.Sound;

namespace DaocAssets.Figures;

/// <summary>One style's swing and trail.</summary>
public sealed record StyleLook(int Id, int Animation, int Imagenum, int DurationMs, int BuildupMs, byte Red, byte Green, byte Blue,
    int HitEffect, bool Anytime)
{
    /// <summary>The client's look for a style id with no row.</summary>
    public static StyleLook Default(int id) => new(id, 0, 3, 1000, 200, 255, 255, 255, 0, false);
}

public sealed class StyleLooks
{
    private readonly Dictionary<int, StyleLook> _styles = [];

    public int Count => _styles.Count;

    /// <summary>The row a style id draws with: its own, or for an anytime style the weapon stance's generic row (objects.csv
    /// Combat Stance); the defaults when there is none.</summary>
    public StyleLook Find(int styleId, int stance)
    {
        if (!_styles.TryGetValue(styleId, out StyleLook? look))
            return StyleLook.Default(styleId);
        if (look.Anytime && _styles.TryGetValue(GenericRow(stance), out StyleLook? generic))
            return generic;
        return look;
    }

    /// <summary>The generic style row per weapon stance (RE/75).</summary>
    public static int GenericRow(int stance) => stance switch
    {
        2 => 292, 3 => 308, 4 => 238, 9 or 10 or 11 => 369, 13 => 374, 14 => 389, _ => 252,
    };

    public static StyleLooks Parse(string stylesCsv)
    {
        var t = new StyleLooks();
        foreach (string raw in stylesCsv.Split('\n'))
        {
            string[] c = SoundTables.Split(raw.TrimEnd('\r'));
            if (c.Length < 10 || !int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;
            int Int(int col) => col < c.Length && int.TryParse(c[col].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            byte Byte(int col) => (byte)Math.Clamp(Int(col), 0, 255);
            t._styles.TryAdd(id, new StyleLook(id, Int(3), Int(4), Int(5), Int(6), Byte(7), Byte(8), Byte(9), Int(10), Int(11) != 0));
        }
        return t;
    }
}
