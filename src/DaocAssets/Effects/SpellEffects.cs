// SPDX-License-Identifier: GPL-3.0-or-later
//
// What a spell looks like, from three gamedata tables, read as the retail client reads them (daoc-re RE/78 "Tables"):
// - spells.csv (row = spell id): columns 6-9 the caster's effects (Effect1A, Effect1B, Effect2, Effect3), 10-13 the
//   Projectile, Target, Buildup and Pulse effects, 14 the Sound Set, 15-16 the Radius and Resist effects, 17 the
//   Projectile Lag (ms), 19 Focus CasterFX (a MonEffectSets id). Effects are speffects.csv ids.
// - speffects.csv (row = effect id): 2 the NIF id, 3 the attach node, 4 Duration (ms), 5 Rate (% of playback speed, 0 =
//   100), 6 Track Angle, 7 Auto Scale, 8 Spin Axis, 9 Angle (degrees), 10-14 Focus Effect, Freq (ms), Speed (units/s),
//   Timeout (ms), TargetFX.
// - spnifs.csv (NIF ids under 1500): 2 the file name without extension, 4 the folder (1 phousing/effects, 2
//   frontiers/nifs, else effects). NIF ids 1500-1999 are the weapon trail, 2000-9999 an item model, 10000 and up a
//   MonEffects NIF (moneffectnifs.csv, id - 10000).

using System.Globalization;

namespace DaocAssets.Effects;

/// <summary>A spell's effects (speffects ids; 0 = none) and its projectile lag.</summary>
public sealed record SpellVisual(int Effect1A, int Effect1B, int Effect2, int Effect3, int Projectile, int Target,
    int Buildup, int Pulse, int SoundSet, int Radius, int Resist, int ProjectileLagMs, int FocusCasterFx);

/// <summary>One speffects.csv row.</summary>
public sealed record SpEffect(int Id, string Name, int Nif, string Node, int DurationMs, int Rate, int TrackAngle,
    bool AutoScale, int SpinAxis, float Angle, int FocusEffect, int FocusFreqMs, float FocusSpeed, int FocusTimeoutMs,
    int TargetFx)
{
    /// <summary>Playback speed: Rate % (0 = 100).</summary>
    public float Speed => Rate <= 0 ? 1f : Rate / 100f;

    /// <summary>Attached to the figure's root ("rootnode" or no node).</summary>
    public bool OnRoot => Node.Length == 0 || Node.Equals("rootnode", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One spnifs.csv row: the effect NIF's file name and folder.</summary>
public sealed record SpNif(int Id, string File, int Folder)
{
    /// <summary>The NIF's path in the install, as the client builds it.</summary>
    public string Path => Folder switch
    {
        1 => $"phousing/effects/{File}.nif",
        2 => $"frontiers/nifs/{File}.nif",
        _ => $"effects/{File}.nif",
    };
}

public static class SpellEffectTables
{
    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n').Select(l => l.TrimEnd('\r').Split(','))
            .Where(c => c.Length > 1 && int.TryParse(c[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));

    private static int Int(string[] c, int col) =>
        col < c.Length && int.TryParse(c[col].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static float Float(string[] c, int col) =>
        col < c.Length && float.TryParse(c[col].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;

    private static string Str(string[] c, int col) => col < c.Length ? c[col].Trim() : "";

    public static Dictionary<int, SpellVisual> ParseSpells(string spellsCsv)
    {
        var d = new Dictionary<int, SpellVisual>();
        foreach (string[] c in Rows(spellsCsv))
            d.TryAdd(Int(c, 0), new SpellVisual(Int(c, 6), Int(c, 7), Int(c, 8), Int(c, 9), Int(c, 10), Int(c, 11),
                Int(c, 12), Int(c, 13), Int(c, 14), Int(c, 15), Int(c, 16), Int(c, 17), Int(c, 19)));
        return d;
    }

    public static Dictionary<int, SpEffect> ParseEffects(string speffectsCsv)
    {
        var d = new Dictionary<int, SpEffect>();
        foreach (string[] c in Rows(speffectsCsv))
            d.TryAdd(Int(c, 0), new SpEffect(Int(c, 0), Str(c, 1), Int(c, 2), Str(c, 3), Int(c, 4), Int(c, 5), Int(c, 6),
                Int(c, 7) != 0, Int(c, 8), Float(c, 9), Int(c, 10), Int(c, 11), Float(c, 12), Int(c, 13), Int(c, 14)));
        return d;
    }

    public static Dictionary<int, SpNif> ParseNifs(string spnifsCsv)
    {
        var d = new Dictionary<int, SpNif>();
        foreach (string[] c in Rows(spnifsCsv))
            if (Str(c, 2) is { Length: > 0 } file)
                d.TryAdd(Int(c, 0), new SpNif(Int(c, 0), file, Int(c, 4)));
        return d;
    }
}
