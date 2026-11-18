// SPDX-License-Identifier: GPL-3.0-or-later
//
// Where a keep component stands, which model it uses and which of its branches show. PROVISIONAL: these are the server's
// rules and our reading of the models, not yet the retail client's (asked of daoc-re 2026-11-18; replace each with the
// answer and remove its "inferred"):
//   - position: the keep's x/y plus the component's offsets × 148 units, turned by the keep's heading in degrees, as
//     OpenDAoC computes it for its own range checks (GameKeepComponent.LoadFromDatabase) — inferred for the client;
//   - facing: the component's quarter turns × 90° plus the keep's heading (same source) — inferred;
//   - model: fr_tiers' TIER column = the build height sent in 0x6C (height 0 → tier 1), falling back to the highest
//     tier below that the type has — inferred (gaheris saw region 163's keeps draw right at height 3 with the retail
//     client, and as white shells at 1 and 2);
//   - branches: "!visible_damaged" / "!collidee_damaged" when the component is broken (0x6C flag bit 0), their
//     "_undamaged" twins otherwise, "!..._static" always; "!sclimb_visible_tierN_<realm>" only for the tier and realm
//     shown — inferred from the names.

namespace DaocAssets.Keeps;

public static class KeepLayout
{
    /// <summary>Units per step of a component's x/y offset (OpenDAoC's 148).</summary>
    public const double Step = 148;

    /// <summary>A component's position in region units and its facing in degrees (clockwise from north, as a fixture's
    /// angle), from its keep's position and heading (degrees) and its own offsets and quarter turns.</summary>
    public static (double X, double Y, double Degrees) Place(double keepX, double keepY, double keepHeading, int x, int y, int quarters)
    {
        double a = keepHeading * Math.PI / 180;
        double wx = keepX + (x * Step * Math.Cos(a) + y * Step * Math.Sin(a));
        double wy = keepY - (y * Step * Math.Cos(a) - x * Step * Math.Sin(a));
        return (wx, wy, ((quarters * 90 + keepHeading) % 360 + 360) % 360);
    }

    /// <summary>The tier (1-based) drawn for a build height: the height itself (0 → 1), or the highest tier below it the
    /// type has a model for; null when it has none.</summary>
    public static int? Tier(IReadOnlyList<string?> tiers, int height)
    {
        for (int t = Math.Min(Math.Max(height, 1), tiers.Count); t >= 1; t--)
            if (tiers[t - 1] is not null)
                return t;
        return null;
    }

    /// <summary>Whether a mesh under these "!" branches (the converter's ';'-joined list; null = none) shows for a
    /// component of this realm (1 Albion, 2 Midgard, 3 Hibernia) and tier, broken or not.</summary>
    public static bool Shows(string? branches, int realm, int tier, bool broken)
    {
        if (branches is null)
            return true;
        string realmName = realm switch { 1 => "alb", 2 => "mid", 3 => "hib", _ => "" };
        foreach (string raw in branches.Split(';'))
        {
            string b = raw.ToLowerInvariant();
            int colon = b.IndexOf(':');
            if (colon > 0)
                b = b[..colon];
            if (b.EndsWith("_damaged") && !broken || b.EndsWith("_undamaged") && broken)
                return false;
            int t = b.IndexOf("_tier", StringComparison.Ordinal);
            if (t >= 0 && b.Length >= t + 10)
            {
                // ..._tier3_hib
                if (!int.TryParse(b.AsSpan(t + 5, 1), out int bt) || bt != tier || !b.EndsWith("_" + realmName))
                    return false;
            }
        }
        return true;
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
