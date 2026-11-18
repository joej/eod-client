// SPDX-License-Identifier: GPL-3.0-or-later
//
// Where a keep component stands, which model it uses and which of its branches show, as the retail client builds a keep
// (daoc-re RE/92, read from the 1.130 client, 2026-11-18; [S] there unless marked):
//   - position: the keep's x/y plus the component's signed offsets × 150 units, the whole keep turned by its heading in
//     degrees, each piece by its quarter turns; the keep stands on the terrain at its x/y (0x69 has no z). The keep root
//     is a π turn about x then the heading about z, which puts a piece at (kx + 150(x cos h + y sin h),
//     ky + 150(x sin h − y cos h)): +x east, +y north at heading 0, OpenDAoC's own formula (daoc-re's added note). The
//     clickable "Keep Piece" object (keep x − 150x, keep y + 150y) is a mirror of that, not the drawn layout;
//   - model: fr_tiers row = the skin, TIER column = the height byte + 1 (0–4 → TIER 1–5), a raised tower (flag bit 2 on a
//     keep id with a high byte) at height 4; a missing cell falls back to the highest tier below it (ours);
//   - branches: "_undamaged" nodes while not broken (flag bit 0), "_damaged" ones when broken; "!scl…" climbing
//     variants only when the piece is climbable (flag bit 1), and then only the one for the keep's level tier (levels
//     0–2/3–5/6–8/9+ → digit 1–4) and realm; a realm's "!<realm>_cake…" toppings only on its own realm's keep
//     (inferred); "!…_static" and the rest always.

namespace DaocAssets.Keeps;

public static class KeepLayout
{
    /// <summary>Units per step of a component's x/y offset (retail 150; OpenDAoC's server uses 148).</summary>
    public const double Step = 150;

    /// <summary>A component's position in region units and its facing in degrees (clockwise from north, as a fixture's
    /// angle), from its keep's position and heading (degrees) and its own offsets and quarter turns.</summary>
    public static (double X, double Y, double Degrees) Place(double keepX, double keepY, double keepHeading, int x, int y, int quarters)
    {
        double a = keepHeading * Math.PI / 180;
        double wx = keepX + (x * Step * Math.Cos(a) + y * Step * Math.Sin(a));
        double wy = keepY - (y * Step * Math.Cos(a) - x * Step * Math.Sin(a));
        return (wx, wy, ((quarters * 90 + keepHeading) % 360 + 360) % 360);
    }

    /// <summary>The TIER column (1-based) for a component: its height byte + 1, clamped to 0–4; a raised tower (flag bit 2
    /// on a keep id with a high byte) at height 4.</summary>
    public static int TierColumn(int height, int keepId, bool raised) =>
        (raised && keepId >> 8 != 0 ? 4 : Math.Clamp(height, 0, 4)) + 1;

    /// <summary>The tier drawn for a TIER column: that one, or the highest below it the type has a model for (ours: retail
    /// reads the cell as it is); null when it has none.</summary>
    public static int? Tier(IReadOnlyList<string?> tiers, int column)
    {
        for (int t = Math.Min(Math.Max(column, 1), tiers.Count); t >= 1; t--)
            if (tiers[t - 1] is not null)
                return t;
        return null;
    }

    /// <summary>The keep's level tier, as its climbing variants and textures choose it: levels 0–2 → 0, 3–5 → 1, 6–8 → 2,
    /// 9 and up → 3.</summary>
    public static int LevelTier(int level) => level switch { <= 2 => 0, <= 5 => 1, <= 8 => 2, _ => 3 };

    /// <summary>Whether a mesh under these "!" branches (the converter's ';'-joined list; null = none) shows for a
    /// component of this realm (1 Albion, 2 Midgard, 3 Hibernia) and keep level tier (<see cref="LevelTier"/>), broken or
    /// not, climbable or not.</summary>
    public static bool Shows(string? branches, int realm, int levelTier, bool broken, bool climbable)
    {
        if (branches is null)
            return true;
        char realmLetter = realm switch { 1 => 'a', 2 => 'm', 3 => 'h', _ => '?' };
        foreach (string raw in branches.Split(';'))
        {
            string b = raw.ToLowerInvariant();
            int colon = b.IndexOf(':');
            if (colon > 0)
                b = b[..colon];
            if (b.EndsWith("_damaged") && !broken || b.EndsWith("_undamaged") && broken)
                return false;
            // "!alb_cake_undamaged": a realm's own topping, shown only on that realm's keep (inferred from the names; RE/92
            // names "!<realm>_cake_undamaged" among the nodes a break swaps).
            if (b.Length > 5 && b[0] == '!' && b.AsSpan(4).StartsWith("_cake") && b.AsSpan(1, 3) is var r
                && (r.SequenceEqual("alb") || r.SequenceEqual("mid") || r.SequenceEqual("hib")) && r[0] != realmLetter)
                return false;
            // "!sclimb_visible_tier3_hib": the client reads character 20 (the tier digit) and 22 (the realm letter).
            if (b.StartsWith("!scl") && (!climbable || b.Length < 23 || b[20] != (char)('1' + levelTier) || b[22] != realmLetter))
                return false;
        }
        return true;
    }

    /// <summary>A keep door's id, as the client names it (daoc-re RE/92 §6): it renames a piece's door001–door009 nodes
    /// "door7" + keep id low byte (3 digits) + keep id high byte (a tower's number, 1 digit) + component (2) + n (2), and a
    /// click sends those 9 digits. Null when the name isn't such a door or a part doesn't fit its digits.
    /// OpenDAoC builds its own from other parts (GameKeepDoor.GenerateDoorID: the owner keep's full id, and a keepposition
    /// template number for n), so its ids can differ, a tower's above all (its full id doesn't fit 3 digits); the one that
    /// matches the client is 7e8 + (KeepID &amp; 0xFF)·1e5 + (KeepID &gt;&gt; 8)·1e4 + component·100 + n (W67).</summary>
    public static uint? DoorId(int keepId, int componentId, string door)
    {
        if (!DaocAssets.Nif.NifFile.IsDoorNode(door))
            return null;
        long n = DaocAssets.Nif.NifFile.DoorNumber(door);
        int high = keepId >> 8;
        if (n is < 1 or > 9 || high > 9 || componentId is < 0 or > 99)
            return null;
        return (uint)(700_000_000 + (keepId & 0xFF) * 100_000 + high * 10_000 + componentId * 100 + n);
    }

    /// <summary>The door numbers the client gives a piece (daoc-re's keep-door note §3): for n = 1…9 it looks for a node
    /// named exactly "door00n" (any case) and stops at the first missing n, so suffixed names (door001s, Door001V) never
    /// count and nothing after a gap does. <paramref name="names"/>: the piece's door names (ours: its door animations,
    /// so a door node without keys counts as missing).</summary>
    public static IEnumerable<int> DoorNumbers(IEnumerable<string> names)
    {
        var have = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        for (int n = 1; n <= 9 && have.Contains($"door{n:000}"); n++)
            yield return n;
    }

    /// <summary>Reads library/keeps/pieces.csv (skin,name,tier,glb): each skin's glb per tier.</summary>
    public static Dictionary<int, string?[]> ReadPieces(string csv)
    {
        var rows = csv.Split('\n').Skip(1).Select(l => l.TrimEnd('\r').Split(',')).Where(r => r.Length >= 4
            && int.TryParse(r[0], out _) && int.TryParse(r[2], out _)).ToList();
        var result = new Dictionary<int, string?[]>();
        foreach (IGrouping<int, string[]> g in rows.GroupBy(r => int.Parse(r[0])))
        {
            var tiers = new string?[g.Max(r => int.Parse(r[2]))];
            foreach (string[] r in g)
                tiers[int.Parse(r[2]) - 1] = r[3];
            result[g.Key] = tiers;
        }
        return result;
    }
}
