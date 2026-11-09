// SPDX-License-Identifier: GPL-3.0-or-later
//
// Where an icon number is drawn: the install's icons/ folder holds 320 x 320 sheets of 100 icons each (32 x 32,
// ten to a row, numbered left to right, top to bottom), named by kind and first number: spl_0 … spl_400 and
// spl_1800 (spells), cbt_500 … cbt_800 (combat styles), itm_1200 … itm_1500 (items), skl_1600 (skills). Which
// sheet a number falls in is read off
// the file names, so a number with no sheet (900–1199, 1700s) has no icon. These numbers are icons.csv's
// "baseIconId": the server's own numbers go through gamedata's tables first (IconTable).

namespace DaocAssets.Ui;

public static class IconSheets
{
    public const int IconSize = 32, PerRow = 10, PerSheet = 100;

    // The sheet kinds; other numbered bitmaps there (wgsm_0, wglg_100: 130 x 10 strips) aren't icon sheets.
    private static readonly HashSet<string> Kinds = new(["spl", "cbt", "itm", "skl"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The sheets an install has, from their file names ("spl_100.bmp" → first number 100).</summary>
    public static Dictionary<int, string> Index(IEnumerable<string> fileNames)
    {
        var sheets = new Dictionary<int, string>();
        foreach (string path in fileNames)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int us = name.LastIndexOf('_');
            if (us > 0 && Kinds.Contains(name[..us]) && Path.GetExtension(path).Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name[(us + 1)..], out int first) && first % PerSheet == 0)
                sheets.TryAdd(first, Path.GetFileName(path));
        }
        return sheets;
    }

    /// <summary>The sheet and the icon's top-left pixel in it, or null when no sheet holds that number.</summary>
    public static (string Sheet, int X, int Y)? Locate(IReadOnlyDictionary<int, string> sheets, int icon)
    {
        if (icon < 0 || !sheets.TryGetValue(icon / PerSheet * PerSheet, out string? sheet))
            return null;
        int cell = icon % PerSheet;
        return (sheet, cell % PerRow * IconSize, cell / PerRow * IconSize);
    }
}
