// SPDX-License-Identifier: GPL-3.0-or-later
//
// The small item window's eight lines (retail's mini_info, as daoc-re RE/74 section 3.3 describes it): all from the item
// data the client already holds (0x02), nothing asked of the server. The wording follows the client's own strings
// ("Slash Weapon", "Factor: n", "Con: n%  Dur: n%", ...). What value1 and value2 hold per kind of item is OpenDAoC's
// PacketLib1124.WriteItemData (@f0aaa1e, GPL-3.0): a shield's size and DPS, a stack's count, a weapon's DPS and speed, an
// armour's factor and absorption.

using DaocProtocol.Packets;

namespace DaocProtocol.World;

public static class ItemInfo
{
    /// <summary>
    /// The item window's lines: 0 the name (coloured by <see cref="ConColors.OfItem"/>), 1–3 beside the icon, 4–7 below.
    /// Unused lines are empty.
    /// </summary>
    public static string[] MiniInfo(InventoryItem item)
    {
        var l = new string[8];
        l[0] = item.Name;
        switch (item.ObjectType)
        {
            case 16:   // thrown: value1 DPS, value2 the count
                (l[1], l[2], l[3]) = ("Thrown Weapon", $"{DamageName(item.DamageType)} Damage", $"Dps: {item.Value1 / 10.0:0.0}  Num: {item.Value2}");
                break;
            case >= 1 and <= 28:
                (l[1], l[2], l[3]) = ($"{WeaponSkill(item.ObjectType)} Weapon", $"{DamageName(item.DamageType)} Damage",
                    $"Dps: {item.Value1 / 10.0:0.0}  Spd: {item.Value2 / 10.0:0.0}");
                break;
            case >= 32 and <= 38:
                (l[1], l[2], l[3]) = ($"{item.Kind} Armor", $"Factor: {item.Value1}", $"Absorb: {item.Value2}%");
                break;
            case 42:   // value1 the size, value2 the DPS
                (l[1], l[2]) = ($"{ShieldSize(item.Value1)} Shield".TrimStart(), $"Dps: {item.Value2 / 10.0:0.0}");
                break;
            case 43 or 44:   // value1 the count
                (l[1], l[2]) = ($"{item.Value1} {(item.ObjectType == 43 ? "Arrows" : "Bolts")}", $"{DamageName(item.DamageType)} Damage");
                break;
            case 45:
                l[1] = "Instrument";
                break;
            case 46:
                (l[1], l[2]) = ("Poison", $"{item.Value1} count");
                break;
            case 47:
                l[1] = "Alchemy Tincture";
                break;
            case 48:
                l[1] = "Spellcraft Gem";
                break;
            case 41:
                l[1] = "Magical Item";
                break;
        }
        if (item.Level > 0)
        {
            if (l[1] is null)
                l[1] = $"Level {item.Level} Item";
            else if (l[2] is null)
                l[2] = $"Level {item.Level} Item";
            else if (l[3] is null)
                l[3] = $"Level {item.Level} Item";
        }
        if (item.WeightTenths > 0)
            l[4] = $"Weight: {item.WeightTenths / 10.0:0.0} lbs";
        if (item.Quality > 0 || item.Condition > 0)
        {
            l[5] = $"Con: {item.Condition}%  Dur: {item.Durability}%";
            l[6] = $"Qua: {item.Quality}%  Bon: {item.Bonus}%";
        }
        if (item.IsWeapon && item.Hand == 2)
            l[7] = "Usable in Left Hand";
        else if (item.BonusLevel > 0)
            l[7] = $"Bonus Level: {item.BonusLevel}";
        for (int i = 0; i < l.Length; i++)
            l[i] ??= "";
        return l;
    }

    /// <summary>The weapon skill a weapon's object type trains, as the client names it in the item window.</summary>
    public static string WeaponSkill(byte objectType) => objectType switch
    {
        2 => "Crush", 3 => "Slash", 4 => "Thrust", 5 => "Archery", 6 => "Two Handed", 7 => "Polearm", 8 => "Staff",
        9 => "Longbow", 10 => "Crossbow", 11 => "Sword", 12 => "Hammer", 13 => "Axe", 14 => "Spear", 15 => "Composite Bow",
        16 => "Thrown", 17 => "Left Axe", 18 => "Recurve Bow", 19 => "Blades", 20 => "Blunt", 21 => "Piercing",
        22 => "Large", 23 => "Celtic Spear", 24 => "Flexible", 25 => "Hand to Hand", 26 => "Scythe", 27 => "Fist Wraps",
        28 => "Mauler Staff", _ => "Generic",
    };

    private static string DamageName(byte damageType) => damageType switch
    {
        1 => "Crush", 2 => "Slash", 3 => "Thrust", _ => "Magic",
    };

    private static string ShieldSize(byte size) => size switch
    {
        1 => "Small", 2 => "Medium", 3 => "Large", _ => "",
    };
}
