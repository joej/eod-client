// SPDX-License-Identifier: GPL-3.0-or-later
//
// What a spell's cast looks like on the caster, from the install's spells.csv (gamedata; the library keeps a copy in
// icons/), read as the retail client reads it (daoc-re RE/78 "Tables" and "Cast sequence"): row = the spell id the server
// sends in 0x72 and 0x1B; columns from 0: 3-5 Anim1/2/3 (anims.csv slots, carried by the figures as clips "a<slot>"),
// 18 Play Anims, 20 Remove Effect on Cast (letters A, B, 2, 3). The effect columns (6-17, 19) are M12's.

using System.Globalization;

namespace DaocAssets.Figures;

/// <summary>One spell's cast animations: Anim1 plays when the cast starts, Anim2 and Anim3 follow; 0 = none.</summary>
public sealed record SpellCastLook(int Anim1, int Anim2, int Anim3, int PlayAnims, string Remove)
{
    /// <summary>Play Anims 1: the landing (0x1B) also plays the cast on the caster, with the bolt time as its length.</summary>
    public bool CastOnLanding => PlayAnims == 1;

    /// <summary>Play Anims 2: no animations while the caster moves.</summary>
    public bool StillOnly => PlayAnims == 2;

    /// <summary>A spell with Remove letters holds Anim3 until the landing (0x1B).</summary>
    public bool Anim3OnLanding => Remove.Length > 0;
}

public sealed class SpellCasts
{
    private readonly Dictionary<int, SpellCastLook> _spells = [];

    public int Count => _spells.Count;

    public SpellCastLook? Find(int spellId) => _spells.GetValueOrDefault(spellId);

    public static SpellCasts Parse(string spellsCsv)
    {
        var t = new SpellCasts();
        foreach (string raw in spellsCsv.Split('\n'))
        {
            string[] c = raw.TrimEnd('\r').Split(',');
            if (c.Length < 6 || !int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;
            int Int(int col) => col < c.Length && int.TryParse(c[col].Trim(), out int v) ? v : 0;
            t._spells.TryAdd(id, new SpellCastLook(Int(3), Int(4), Int(5), Int(18), c.Length > 20 ? c[20].Trim() : ""));
        }
        return t;
    }
}
