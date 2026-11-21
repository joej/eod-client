// SPDX-License-Identifier: GPL-3.0-or-later
//
// Two sets of zone ambience the player can choose between (roadmap M12, docs/sound.md "Classic and current zone
// sounds"), both built so that every line plays:
//   - Classic: the zones' sounds.dat as classic DAoC wrote them, with their numbers looked up in the classic client's
//     own sound table (sounds/sndfiles.csv of an early install: id, file, two distances), then in cc_sounds for the
//     numbers added after it. The OpenDAoC install's sounds.dat files are of this kind: written for those numbers, they
//     go silent under today's tables alone (daoc-re, 2026-11-21).
//   - Current: retail 1.130's sounds.dat, rewritten for today's cc_ tables.
// In both sets a number the set's own table lacks is looked up in the other (retail's files still use some classic
// numbers), and a name that resolves to nothing tries Aliases: the install's near misses of real groups. A line that
// still resolves to nothing is left out (ours: retail keeps it as a silent slot that still takes its turns; the sets are
// meant to play). Left out today: numbers Mythic's own early table marks removed (506, 509) or never had (241-244). A classic sound plays as the classic client's table says: its file at rate 1,
// full volume, centred, no offset, heard fully to its first distance and fading to its second.

using System.Globalization;

namespace DaocAssets.Sound;

/// <summary>A classic client's sounds/sndfiles.csv: sound number → file and hearing distances.</summary>
public sealed class ClassicSoundFiles
{
    /// <summary>Where the set's files sit in the library, under library/sounds.</summary>
    public const string Folder = "classic";
    private readonly Dictionary<int, SoundRow> _rows = [];

    public int Count => _rows.Count;
    public IEnumerable<SoundRow> Rows => _rows.Values;

    /// <summary>The row for a classic sound number, its file under <see cref="Folder"/>, or null when it has no file.</summary>
    public SoundRow? Row(int id) => _rows.GetValueOrDefault(id);

    /// <summary>Two header rows, then "ID,filename,min distance,max distance,streamed,frontiers,name,desc".</summary>
    public static ClassicSoundFiles Parse(string csv)
    {
        var c = new ClassicSoundFiles();
        foreach (string[] f in SoundTables.Rows(csv))
        {
            if (f.Length < 2 || !int.TryParse(f[0].Trim(), out int id) || f[1].Trim().Length == 0)
                continue;
            float r1 = f.Length > 2 && SoundRange.Num(f[2]) is float a ? a : 0;
            float r2 = f.Length > 3 && SoundRange.Num(f[3]) is float b && b > r1 ? b : Math.Max(r1 + 1, 1000);
            string file = f[1].Trim().ToLowerInvariant();
            c._rows.TryAdd(id, new SoundRow(id, $"classic_{id}", $"{Folder}/{file}", new(1, 1), new(1, 1), new(0, 0),
                new(0, 0), r1, r2, null));
        }
        return c;
    }
}

/// <summary>What one zone line plays in a set: a classic row, or a name the tables resolve.</summary>
public readonly record struct ZoneSound(SoundRow? Classic, string? Name)
{
    public bool Plays => Classic is not null || Name is not null;
}

public static class ZoneSoundSets
{
    public const string Classic = "classic", Current = "current";

    /// <summary>
    /// Ours: names the install's sounds.dat files use that no table defines, each with the real name it is a near
    /// miss of (the groups were renamed, "(dc)" became "(cd)"; s_combat3 is combat3.wav, s_Bed_Combat_Heavy).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["g_Chains_DungeonRattle(dc)"] = "g_ChainsDungeon_Rattle(cd)",
        ["g_Door_DungeonOpen(dc)"] = "g_DoorDungeon_OpenClose(cd)",
        ["g_Door_DungeonClose(dc)"] = "g_DoorDungeon_OpenClose(cd)",
        ["g_Earth_Avalanche(dc)"] = "g_Earth_AvalancheCaveIn(dc)",
        ["g_Earth_CaveIn_Large(dc)"] = "g_Earth_AvalancheCaveIn(dc)",
        ["g_Machine_DungeonCrank(dc)"] = "g_CrankDungeon(cd)",
        ["g_Machine_DungeonPulley(dc)"] = "g_PulleyDungeon(cd)",
        ["g_Machine_DungeonStomp(dc)"] = "g_StompDungeon(cd)",
        ["g_Wood_StressHeavy(c)"] = "g_WoodStress_Heavy(c)",
        ["g_Wood_StressHeavy(dc)"] = "g_WoodStress_Heavy(cd)",
        ["g_Wood_StressMedium(c)"] = "g_WoodStress_Medium(c)",
        ["g_Wood_StressMedium(dc)"] = "g_WoodStress_Medium(cd)",
        ["g_Wood_StressLight(c)"] = "g_WoodStress_Light(c)",
        ["g_Wood_StressLight(dc)"] = "g_WoodStress_Light(cd)",
        ["s_combat3"] = "s_Bed_Combat_Heavy",
    };

    /// <summary>
    /// What <paramref name="sound"/> plays in a set. A number: the set's own table first (the classic table when
    /// <paramref name="classicFirst"/>, else cc_sounds), then the other. A name: the tables (SoundPlanner), then its alias.
    /// Nothing resolves: <see cref="ZoneSound.Plays"/> false.
    /// </summary>
    public static ZoneSound Resolve(string sound, SoundPlanner planner, ClassicSoundFiles? classic, bool classicFirst)
    {
        sound = sound.Trim();
        if (int.TryParse(sound, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
        {
            SoundRow? old = classic?.Row(id);
            if (classicFirst && old is not null)
                return new ZoneSound(old, null);
            if (planner.Knows(sound))
                return new ZoneSound(null, sound);
            return old is not null ? new ZoneSound(old, null) : default;
        }
        if (planner.Knows(sound))
            return new ZoneSound(null, sound);
        return Aliases.TryGetValue(sound, out string? real) && planner.Knows(real) ? new ZoneSound(null, real) : default;
    }

    /// <summary>The regions with only the lines that play (see the file header).</summary>
    public static IReadOnlyList<ZoneSoundRegion> Playable(IReadOnlyList<ZoneSoundRegion> regions, SoundPlanner planner,
        ClassicSoundFiles? classic, bool classicFirst) =>
        regions.Select(r => r with { Sounds = r.Sounds.Where(l => Resolve(l.Sound, planner, classic, classicFirst).Plays).ToList() })
            .ToList();

    /// <summary>How many of the lines play.</summary>
    public static (int Lines, int Playing) Count(IReadOnlyList<ZoneSoundRegion> regions, SoundPlanner planner,
        ClassicSoundFiles? classic, bool classicFirst)
    {
        int lines = 0, playing = 0;
        foreach (ZoneSoundLine l in regions.SelectMany(r => r.Sounds))
        {
            lines++;
            playing += Resolve(l.Sound, planner, classic, classicFirst).Plays ? 1 : 0;
        }
        return (lines, playing);
    }
}
