// SPDX-License-Identifier: GPL-3.0-or-later
//
// Which sound row a figure or weapon uses (roadmap M12, docs/sound.md): gamedata.mpk monsters.csv column 15 "Sound Set"
// gives a model id its cc_creatures row; objects.csv column 21 "Sound Set" gives a weapon model its cc_weapons row, and
// column 19 "Combat Stance" says whether it has an attack sound (stances 5, 6 and 8 don't). `mpk sounds` writes both to
// library/sounds/soundsets.csv ("creature,<model>,<row>" and "weapon,<model>,<row>,<stance>") for the client.

using System.Text;

namespace DaocAssets.Sound;

public sealed class SoundSets
{
    private const int CreatureColumn = 15, WeaponColumn = 21, StanceColumn = 19;
    private readonly Dictionary<int, int> _creatures = [];
    private readonly Dictionary<int, (int Row, int Stance)> _weapons = [];

    /// <summary>A model id's cc_creatures row, or null.</summary>
    public int? Creature(int model) => _creatures.TryGetValue(model, out int row) ? row : null;

    /// <summary>A weapon model's cc_weapons row and combat stance, or null.</summary>
    public (int Row, int Stance)? Weapon(int model) => _weapons.TryGetValue(model, out var w) ? w : null;

    /// <summary>Whether a weapon of this stance makes an attack sound (objects.csv Combat Stance 5, 6 and 8 don't).</summary>
    public static bool HasAttackSound(int stance) => stance is not (5 or 6 or 8);

    public int Creatures => _creatures.Count;
    public int Weapons => _weapons.Count;

    /// <summary>From the install's monsters.csv and objects.csv (two header rows each, then rows by id).</summary>
    public static SoundSets FromTables(string monstersCsv, string objectsCsv)
    {
        var s = new SoundSets();
        foreach (string[] f in DataRows(monstersCsv))
            if (Int(f, 0) is int id && Int(f, CreatureColumn) is int row and > 0)
                s._creatures[id] = row;
        foreach (string[] f in DataRows(objectsCsv))
            if (Int(f, 0) is int id && Int(f, WeaponColumn) is int row and > 0)
                s._weapons[id] = (row, Int(f, StanceColumn) ?? 0);
        return s;
    }

    public string ToCsv()
    {
        var b = new StringBuilder("kind,model,row,stance\n");
        foreach ((int model, int row) in _creatures.OrderBy(kv => kv.Key))
            b.Append($"creature,{model},{row},\n");
        foreach ((int model, (int row, int stance)) in _weapons.OrderBy(kv => kv.Key))
            b.Append($"weapon,{model},{row},{stance}\n");
        return b.ToString();
    }

    public static SoundSets FromCsv(string csv)
    {
        var s = new SoundSets();
        foreach (string line in csv.Split('\n').Skip(1))
        {
            string[] f = line.Trim().Split(',');
            if (f.Length < 3 || !int.TryParse(f[1], out int model) || !int.TryParse(f[2], out int row))
                continue;
            if (f[0] == "creature")
                s._creatures[model] = row;
            else if (f[0] == "weapon")
                s._weapons[model] = (row, f.Length > 3 && int.TryParse(f[3], out int st) ? st : 0);
        }
        return s;
    }

    private static IEnumerable<string[]> DataRows(string csv) =>
        csv.Split('\n').Select(l => SoundTables.Split(l.TrimEnd('\r'))).Where(f => f.Length > 0 && int.TryParse(f[0], out _));

    private static int? Int(string[] f, int i) => i < f.Length && int.TryParse(f[i].Trim(), out int v) ? v : null;
}
