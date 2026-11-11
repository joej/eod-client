// SPDX-License-Identifier: GPL-3.0-or-later
//
// What the client writes about a spell or style: the small window's lines (retail's mini_info: "Magical Ability" or
// "Combat Style", "%s Level", "Target: %s", "%d Conc. Pts", "Opening: %s", "Positional: %s", "Previous Style Req.") and the
// long text of the info window, built from the server's 0xD8 record in the order and wording the client uses (as described
// by daoc-re RE/74 and RE/77). The record's keys are OpenDAoC's (SpellDelve.GetSpellString / GetStyleString, @f0aaa1e).
// Where the description left a choice open (the duration's time format, how "%s Level" is filled, the split of the
// "Medium/Low Penalty" and "High/Very High Bonus" words, the stealth line), ours is marked "(ours)".

using DaocProtocol.Packets;

namespace DaocProtocol.World;

public enum SkillKind { Spell, Style, Song }

/// <summary>A spell's or style's opening requirement as its 0x16 entry gives it (Special): a word for the result or
/// position, or the name of the style that must come before.</summary>
public sealed record StyleOpening(string Label, string Value);

/// <summary>A spell or style the player has: what its 0x16 entry says. <see cref="Id"/> is the server's id for it (the
/// delve request's id and the record's Index).</summary>
public sealed record SkillInfo(SkillKind Kind, ushort Id, string Name, byte Level, ushort Icon, StyleOpening? Opening = null)
{
    /// <summary>The delve request for it, and the kind of record that answers.</summary>
    public ushort DelveType => Kind switch { SkillKind.Style => DetailRequest.StyleDelve, SkillKind.Song => DetailRequest.SongDelve, _ => DetailRequest.SpellDelve };

    /// <summary>A song's text is its Spell record (the server sends a Song record that only points at it, then the Spell).</summary>
    public string RecordKind => Kind == SkillKind.Style ? "Style" : "Spell";
}

/// <summary>One line of the long text: a label (white) and a value (light grey, or <see cref="Colour"/>); a line with no
/// label is plain text.</summary>
public sealed record DelveLine(string Label, string Value, (byte R, byte G, byte B)? Colour = null)
{
    public override string ToString() => Label.Length == 0 ? Value : $"{Label}: {Value}";
}

public static class DelveText
{
    private static readonly string[] Offensive = ["", "You Miss", "You Hit", "Target Parries", "Target Blocks", "Target Evades", "You Fumble", "Style"];
    private static readonly string[] Defensive = ["", "Target Misses", "Target Hits", "You Parry", "You Block", "You Evade", "Target Fumbles", "Target Style"];
    private static readonly string[] Positions = ["Behind target", "To side of target", "In front of target"];

    /// <summary>
    /// A style's opening from its 0x16 Special word (OpenDAoC PacketLib1112): 0x100 none; a low byte under 100 the result
    /// of our last attack (7: a style, whose index from the first non-specialisation entry + 100 is the high byte), 100+ the
    /// result of the enemy's last attack, 200+ a position (0 behind, 1 side, 2 front).
    /// </summary>
    public static StyleOpening? Opening(ushort special, Func<int, string?> styleAtIndex)
    {
        if (special == 0x100)
            return null;
        int low = special & 0xFF, high = special >> 8;
        if (low >= 200)
            return low - 200 < Positions.Length ? new StyleOpening("Positional", Positions[low - 200]) : null;
        if (low >= 100)
            return low - 100 is int d and > 0 and < 8 ? new StyleOpening("Opening", Defensive[d]) : null;
        if (low == 7)
            return new StyleOpening("Previous Style Req.", styleAtIndex(high - 100) ?? "");
        return low is > 0 and < 8 ? new StyleOpening("Opening", Offensive[low]) : null;
    }

    /// <summary>The small window's eight lines (0 the name, 1–3 beside the icon, 4–7 below); the target and concentration
    /// lines need the record and are empty until it comes.</summary>
    public static string[] MiniInfo(SkillInfo s, DelveRecord? record)
    {
        var l = new string[8];
        l[0] = s.Name;
        l[1] = s.Kind == SkillKind.Style ? "Combat Style" : "Magical Ability";
        l[2] = $"{Ordinal(s.Level)} Level";   // "%s Level", filled with the ordinal (ours)
        if (s.Kind == SkillKind.Style)
        {
            if (s.Opening is StyleOpening o)
            {
                if (o.Label == "Previous Style Req.")
                    (l[3], l[4]) = (o.Label, o.Value);
                else
                    l[3] = $"{o.Label}: {o.Value}";
            }
        }
        else if (record is not null)
        {
            l[3] = $"Target: {TargetWord(record)}";
            if (record.Int("concentration_points") > 0)
                l[4] = $"{record.Int("concentration_points")} Conc. Pts";
        }
        for (int i = 0; i < l.Length; i++)
            l[i] ??= "";
        return l;
    }

    /// <summary>A spell record's long text, in the client's order.</summary>
    public static List<DelveLine> Spell(DelveRecord r)
    {
        var lines = new List<DelveLine>();
        void Add(string label, string value) => lines.Add(new DelveLine(label, value));
        if (r.Int("level") > 0)
            Add("Level", r.Int("level").ToString());
        int duration = r.Int("duration");
        switch (r.Int("dur_type"))
        {
            case 0 or 2 when duration > 0 && duration != 0xFFFF: Add("Duration", Time(duration)); break;
            case 3: Add("Duration", "BY LEVEL"); break;
            case 4: Add("Duration", "Concentration"); break;
            case 5: Add("Duration", "Focus"); break;
        }
        int conc = r.Int("concentration_points"), cost = r.Int("power_cost");
        if (conc > 0)
            Add("Concentration cost", conc.ToString());
        if (cost != 0)
        {
            string label = r.Int("cost_type") switch { 1 => "Delayed health cost", 2 => "Health cost", 3 => "Endurance cost", _ => "Power cost" };
            string value = cost < 0 ? $"{-cost}%" : cost.ToString();   // OpenDAoC: a negative cost is a percentage
            if (r.Int("round_cost") > 0)
                value += $" (+{r.Int("round_cost")} per tick)";
            Add(label, value);
        }
        int range = r.Int("range"), radius = r.Int("radius");
        string target = TargetWord(r);
        Add("Target", radius > 0 && target != "Point-blank Area" ? target + " area effect" : target);
        if (range > 0)
            Add("Range", range.ToString());
        if (radius > 0)
            Add("Area radius", radius.ToString());
        // OpenDAoC sends cast_timer as the cast time less 2 s, and only for casts of 2 s or more.
        Add("Casting time", r.Int("instant") != 0 ? "Instant" : $"{(r.Int("cast_timer") + 2000) / 1000.0:0.0} seconds");
        if (r.Int("timer_value") > 0)
            Add("Recast", Time(r.Int("timer_value")));
        if (MagicType(r.Int("damage_type")) is string magic)
            Add("Magic Type", magic);
        if (r.Get("delve_string") is { Length: > 0 } text)
        {
            lines.Add(new DelveLine("", ""));
            foreach (string line in text.Split('\n'))
                lines.Add(new DelveLine("", line));
        }
        return lines;
    }

    /// <summary>A style record's long text. <paramref name="spell"/> finds the record of the spell the style fires (its
    /// SpecialNumber), shown after it; null while it hasn't come.</summary>
    public static List<DelveLine> Style(DelveRecord r, Func<ushort, DelveRecord?> spell)
    {
        var lines = new List<DelveLine>();
        void Add(string label, string value) => lines.Add(new DelveLine(label, value));
        if (r.Get("OpeningStyle") is string openingStyle)
            Add("Opening", openingStyle);
        else
        {
            int type = r.Int("OpeningType"), result = r.Int("OpeningResult");
            if (type == 2)
                Add("Opening", r.Int("OpeningNumber") is int p and >= 0 and < 3 ? Positions[p] : "Positional");
            else if (result is > 0 and < 8)
                Add("Opening", (type == 1 ? Defensive : Offensive)[result]);
        }
        if (r.Int("Hidden") != 0)
            Add("Opening", "Stealthed");   // the client's stealth line (wording ours)
        if (r.Get("Weapon") is string weapon)
            Add("Weapon Type", weapon);
        if (r.Get("FollowupStyle") is string followups)
            Add("Follow-up Style(s)", followups);
        Add("Endurance Cost", r.Int("Fatigue") switch { <= 0 => "None", <= 5 => "Low", <= 10 => "Medium", <= 15 => "High", _ => "Very High" });
        // (50 − Level) × LevelBonus + OpeningDamage; OpenDAoC sends no LevelBonus.
        Add("Damage", r.Int("OpeningDamage") switch
        {
            < 5 => "None", < 51 => "Very Low", < 100 => "Low", < 150 => "Medium", < 191 => "High", < 500 => "Very High", _ => "Devastating",
        });
        lines.Add(Modifier("To Hit", r.Int("AttackMod")));
        lines.Add(Modifier("Defense", r.Int("DefensiveMod")));
        if (r.Int("SpecialNumber") is int special and not 0)
        {
            lines.Add(new DelveLine("", ""));
            if (spell((ushort)special) is DelveRecord s)
            {
                Add("Special", s.Name);
                lines.AddRange(Spell(s));
            }
            else
                Add("Special", "a spell effect");
        }
        return lines;
    }

    /// <summary>The style's to-hit or defence bonus as the client words and colours it.</summary>
    public static DelveLine Modifier(string label, int v) => v switch
    {
        < -19 => new(label, "Very High Penalty", (173, 41, 242)),
        < -14 => new(label, "High Penalty", (245, 0, 0)),
        < -9 => new(label, "Medium Penalty", (254, 131, 49)),   // "Medium/Low Penalty": the split is ours
        < -4 => new(label, "Low Penalty", (254, 131, 49)),
        < 5 => new(label, "No Bonus", (255, 255, 255)),
        < 10 => new(label, "Low Bonus", (99, 99, 251)),
        < 15 => new(label, "Medium Bonus", (148, 239, 123)),
        < 20 => new(label, "High Bonus", (0, 255, 0)),          // "High/Very High Bonus": the split is ours
        _ => new(label, "Very High Bonus", (0, 255, 0)),
    };

    public static string TargetWord(DelveRecord r)
    {
        int target = r.Int("target");
        if (target == 1 && r.Int("range") == 0 && r.Int("radius") > 0)
            return "Point-blank Area";
        return target switch
        {
            0 => "Self", 1 => "Targeted", 2 => "Passive", 3 => "Group", 4 => "Object or Keeps", 6 => "Controlled", 7 => "Realm",
            8 => "Dead", 9 => "Area", 10 => "Enemy realm players and controlled pets", 11 => "Enemy realm controlled pets",
            12 => "Enemy realm only", 13 => "Battle Group", 14 => "Monster", 15 => "Mounted enemy realm players",
            16 => "Owner Group", 17 => "Enemy realm players", _ => "Unknown",
        };
    }

    public static string? MagicType(int damageType) => damageType switch
    {
        1 => "Crush", 2 => "Slash", 3 => "Thrust", 5 => "Siege", 10 => "Heat", 11 => "Spirit", 12 => "Cold", 13 => "Matter",
        16 => "Body", 20 => "Energy", 21 => "Essence", _ => null,
    };

    /// <summary>Seconds as "N sec" or "M:SS min" (ours: the client's format is unknown).</summary>
    public static string Time(int seconds) => seconds < 60 ? $"{seconds} sec" : $"{seconds / 60}:{seconds % 60:00} min";

    public static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };
}
