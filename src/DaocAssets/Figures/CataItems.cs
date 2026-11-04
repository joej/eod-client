// SPDX-License-Identifier: GPL-3.0-or-later
//
// Worn items on Catacombs figures (roadmap M7b). objects.csv (gamedata.mpk) gives each item its
// Catacombs look in its own columns: "Cata Status" (65), a pskins skin per slot (66 body, 67 female
// body, 68 arms, 69 gloves, 70 legs, 71 boots, 72 cloak) and mesh numbers (74 gloves, 75 boots, 78
// cloak); pskins.csv names each skin's texture (chnBody05_02_m.dds). Which mesh a piece is drawn on:
// - body: 7 for a robe (objects.csv column 15, "Lbody #", is 3), otherwise the item's extension, as the
//   client picks it (published by OfflineDAoC, GPL-3.0, with the client address it read); with no
//   extension (0, as on our server's starter gear) the texture name's number (chnBody05 = 5), as
//   OfflineDAoC's pipeline does. Column 73 ("Body#") is not a mesh number: it follows the material
//   (1 cloth, 6 chain, 12 plate, 16 robe, 18 scale, 19 studded; surveyed over every item).
// - gloves and boots: columns 74 / 75, which run 1-7 by kind (2 leather, 3 studded, 4 plate, 7 cloth)
//   where the texture names say 01 for nearly every glove and boot; else the name's number; else 1.
// - arms and legs: one mesh per race.
// - cloak: column 78, else 1. A cloak item without a Catacombs skin wears Cat_cloak.dds.
// Unconfirmed against retail (open question W31).

using System.Globalization;
using System.Text.RegularExpressions;

namespace DaocAssets.Figures;

/// <summary>One slot of a worn item: the mesh variant (0 = decided by the client, see
/// <see cref="CataItems"/>) and the texture.</summary>
public readonly record struct CataPiece(int Variant, string Texture);

/// <summary>An item's Catacombs look, per body slot name (body, arms, gloves, legs, boots, cloak), for
/// men and women (only the body differs). <see cref="NameVariant"/> is the body texture's number, the
/// body mesh when the item has no extension.</summary>
public sealed record CataItem(bool Robe, int NameVariant, IReadOnlyDictionary<string, CataPiece> Male,
    IReadOnlyDictionary<string, CataPiece> Female);

public static partial class CataItems
{
    /// <summary>Every item with a Catacombs look, keyed by its model id (objects.csv row).</summary>
    public static Dictionary<int, CataItem> Parse(string objectsCsv, string pskinsCsv)
    {
        var pskins = new Dictionary<int, string>();
        foreach (string[] c in Rows(pskinsCsv))
            if (c.Length > 2 && int.TryParse(c[0], out int id) && c[2].Trim().Length > 0)
                pskins.TryAdd(id, c[2].Trim());

        var items = new Dictionary<int, CataItem>();
        foreach (string[] r in Rows(objectsCsv))
        {
            if (r.Length <= 72 || !int.TryParse(r[0], out int model))
                continue;
            string? Skin(int col) => col < r.Length && int.TryParse(r[col], out int s) && pskins.TryGetValue(s, out string? t) ? t : null;
            int Num(int col) => col < r.Length && int.TryParse(r[col], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;
            bool status = r[65].Trim() == "1";
            var male = new Dictionary<string, CataPiece>();
            var female = new Dictionary<string, CataPiece>();
            int nameVariant = 0;
            if (status)
            {
                if (Skin(66) is { } bm) { male["body"] = new(0, bm); nameVariant = NameNumber(bm, "body"); }
                if (Skin(67) is { } bf) female["body"] = new(0, bf);
                else if (Skin(66) is { } bm2) female["body"] = new(0, bm2);
                foreach ((string slot, int col, int num) in new[] { ("arms", 68, 0), ("gloves", 69, 74), ("legs", 70, 0), ("boots", 71, 75) })
                    if (Skin(col) is { } t)
                    {
                        int v = slot is "arms" or "legs" ? 1 : Num(num) is > 0 and var n ? n : Math.Max(1, NameNumber(t, slot));
                        male[slot] = female[slot] = new(v, t);
                    }
            }
            // A cloak: its Catacombs skin (column 72), or the plain Catacombs cloak for a classic one (11).
            string? cloak = Skin(72) ?? (Num(11) > 0 ? "Cat_cloak.dds" : null);
            if (cloak is not null)
                male["cloak"] = female["cloak"] = new(Math.Max(1, Num(78)), cloak);
            if (male.Count > 0)
                items[model] = new CataItem(r.Length > 15 && r[15].Trim() == "3", nameVariant, male, female);
        }
        return items;
    }

    // "chnBody05_02_m.dds" -> 5 for "body"; 0 when the name carries no number for that part.
    internal static int NameNumber(string texture, string part)
    {
        string stem = part switch { "gloves" => "gloves?", "boots" => "boots?", _ => part };
        Match m = Regex.Match(texture, stem + @"(\d\d)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Select(l => l.TrimEnd('\r').Split(','));
}
