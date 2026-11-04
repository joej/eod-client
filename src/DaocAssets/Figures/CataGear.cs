// SPDX-License-Identifier: GPL-3.0-or-later
//
// Held and worn item models on Catacombs figures (roadmap M7b): weapons, shields and helms. Each is an
// item NIF (items/ in the install) hung on one of the figure skeleton's attachment bones. The tables,
// in gamedata.mpk:
//   objects.csv  an item's NIF ("NIF #", column 2: an items.csv row), "On Belt" (17: sheathed on the
//                hip or back when not drawn), "Shield" (18), "Combat Stance" (19), "Left Hand" (20);
//                a helm's Catacombs models per race and sex ("Cata Nif1"-"Nif12", 79-90: items.csv rows
//                such as albHelm01_Bri_m, albHelm01_brit_f, ... for its realm's six races)
//   items.csv    row -> NIF name (column 2); "Cata Helm Hair" (23), what a helm does to the hair (1-5,
//                not decoded yet: open question W33)
// An item NIF carries its own attachment frames: nodes "held" (the grip), "belt" and "back" (where it
// hangs sheathed). Which bone and frame the client uses (OfflineDAoC's findings, GPL-3.0, citing the
// client): a weapon in use in the hand's "Bip01 R/L Held" by its grip, a shield on "Bip01 L Shield";
// out of combat a sheathable weapon hangs by its belt frame on the opposite hip ("Bip01 L Belt" for the
// right hand) or, two-handed and ranged, by its back frame ("Bip01 L/R Back"); unsheathable items
// (staves, most polearms and bows) stay in the hand; a helm on "Bip01 Helmet".

using System.Globalization;

namespace DaocAssets.Figures;

/// <summary>An item model a figure can hold or wear: its NIF, flags, and per race and sex its helm NIF
/// (key: the figure's prefix, "bri_m").</summary>
public sealed record GearItem(int Model, string? Nif, bool Belt, bool Shield, int Stance, bool LeftHand)
{
    public IReadOnlyDictionary<string, string> Helms { get; init; } = new Dictionary<string, string>();
    /// <summary>items.csv "Cata Helm Hair" of the helms (0 when none).</summary>
    public int HelmHair { get; init; }
    /// <summary>A texture drawn in place of the held model's base texture (objects.csv "Apply Texture",
    /// column 22, a pskins row: the round wooden shield's sh_wood01 over the emblem pattern its NIF names,
    /// a file the install doesn't have), and the Catacombs helms' (column 91, "Cata Helm Texture").</summary>
    public string? Texture { get; init; }
    public string? HelmTexture { get; init; }
}

public static class CataGear
{
    /// <summary>Every item with a held model (a stance, a shield or a belt flag) or Catacombs helms.</summary>
    public static Dictionary<int, GearItem> Parse(string objectsCsv, string itemsCsv, string pskinsCsv = "")
    {
        var pskins = new Dictionary<int, string>();
        foreach (string[] c in Rows(pskinsCsv))
            if (c.Length > 2 && int.TryParse(c[0], out int id) && c[2].Trim().Length > 0)
                pskins.TryAdd(id, c[2].Trim());
        string? Skin(string[] row, int col) => col < row.Length && int.TryParse(row[col], out int id) && id > 0
            && pskins.TryGetValue(id, out string? t) ? t : null;
        var items = new Dictionary<int, string[]>();
        foreach (string[] c in Rows(itemsCsv))
            if (c.Length > 2 && int.TryParse(c[0], out int id) && c[2].Trim().Length > 0)
                items.TryAdd(id, c);
        string? Nif(string cell) => int.TryParse(cell, out int id) && items.TryGetValue(id, out string[]? r) ? r[2].Trim() + ".nif" : null;
        int HelmHair(string cell) => int.TryParse(cell, out int id) && items.TryGetValue(id, out string[]? r) && r.Length > 23
            && int.TryParse(r[23], out int h) ? h : 0;

        var gear = new Dictionary<int, GearItem>();
        foreach (string[] r in Rows(objectsCsv))
        {
            if (r.Length <= 20 || !int.TryParse(r[0], out int model))
                continue;
            int Num(int col) => col < r.Length && int.TryParse(r[col], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
            var helms = new Dictionary<string, string>();
            int helmHair = 0;
            for (int col = 79; col <= 90 && col < r.Length; col++)
                if (Nif(r[col]) is { } helm && RaceKey(helm) is { } key)
                {
                    helms.TryAdd(key, helm);
                    helmHair = Math.Max(helmHair, HelmHair(r[col]));
                }
            bool held = Num(19) > 0 || Num(18) > 0 || Num(17) > 0;
            if (!held && helms.Count == 0)
                continue;
            gear[model] = new GearItem(model, held ? Nif(r[2]) : null, Num(17) == 1, Num(18) > 0, Num(19), Num(20) == 1)
            { Helms = helms, HelmHair = helmHair, Texture = held ? Skin(r, 22) : null, HelmTexture = Skin(r, 91) };
        }
        return gear;
    }

    // "albHelm01_Bri_m.nif" -> "bri_m", "albHelm01_brit_f.nif" -> "bri_f": the last two name parts, the
    // race cut to the three letters a figure's prefix uses. Null when the name doesn't end that way.
    internal static string? RaceKey(string nif)
    {
        string[] parts = Path.GetFileNameWithoutExtension(nif).Split('_');
        if (parts.Length < 3) return null;
        string race = parts[^2].ToLowerInvariant(), sex = parts[^1].ToLowerInvariant();
        return race.Length >= 3 && sex is "m" or "f" ? $"{race[..3]}_{sex}" : null;
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Select(l => l.TrimEnd('\r').Split(','));
}
