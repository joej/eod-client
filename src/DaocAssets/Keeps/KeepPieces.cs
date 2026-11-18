// SPDX-License-Identifier: GPL-3.0-or-later
//
// Keep pieces: the model of each keep component type at each build tier, from the install's frontiers/frontiers.mpk.
//   fr_tiers.csv   one row per component type: its number (the 0x6C skin: 0 gate, 9 wall, 10 main tower, 11 outpost
//                  tower, ... 27 corner tower, 30 main keep, 31 outpost), its name, and the TIER 1–5 columns: a row of
//                  fr_pieces, counted from 0 (fr_pieces' own ids start at 1)
//   fr_pieces.csv  id, description, file (the model, no extension, in frontiers/NIFS), then effects and raze columns
// Two title lines open each file (a count, then the column names). The 0-based piece index is read from the data: it
// makes every TIER column name a piece of that type (gate → 1gate…4gate, shortwall →
// shortWall_lvl1…4, outpost → outpost_lvl1…4 and 5-outpost_tower); checked by the tests.

namespace DaocAssets.Keeps;

/// <summary>A component type: its name and the model file of each build tier present (tier 1 first).</summary>
public sealed record KeepPieceType(int Skin, string Name, IReadOnlyList<string?> Tiers);

public static class KeepPieces
{
    /// <summary>Parses fr_tiers.csv and fr_pieces.csv into the component types by skin.</summary>
    public static Dictionary<int, KeepPieceType> Parse(string tiersCsv, string piecesCsv)
    {
        var files = new List<string>();
        foreach (string[] row in Rows(piecesCsv))
            if (row.Length >= 3 && int.TryParse(row[0], out _))
                files.Add(row[2].Trim());

        var types = new Dictionary<int, KeepPieceType>();
        foreach (string[] row in Rows(tiersCsv))
        {
            if (row.Length < 3 || !int.TryParse(row[0], out int skin))
                continue;
            var tiers = new List<string?>();
            for (int c = 2; c < Math.Min(row.Length, 7); c++)
                tiers.Add(int.TryParse(row[c], out int piece) && piece >= 0 && piece < files.Count && row[1].Trim().Length > 0
                    ? files[piece] : null);
            while (tiers.Count > 0 && tiers[^1] is null)
                tiers.RemoveAt(tiers.Count - 1);
            types[skin] = new KeepPieceType(skin, row[1].Trim(), tiers);
        }
        return types;
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Skip(2).Select(l => l.TrimEnd('\r').Split(','));
}
