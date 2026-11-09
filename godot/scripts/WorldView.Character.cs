// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: who the character is and what it has (roadmap M9): level, class, realm rank and titles (0x16
// subcode 3), XP and points (0x91), money (0xFA). Kept for the summary and menu-bar windows; until those
// exist, one status line shows them.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private PlayerInfo? _info;
    private PointsUpdate? _points;
    private MoneyUpdate? _money;
    private WeaponStats? _weapon;   // weapon damage, weapon skill, armour factor (the character sheet's combat page)

    private void SubscribeCharacter()
    {
        _bridge.PlayerInfoChanged += i =>
        {
            _info = i;
            GD.Print($"[world] character: {i.Name}, level {i.Level} {i.RaceName} {i.ClassName}, realm rank {i.RealmRank}" +
                     (PlayerInfo.Shown(i.GuildName).Length > 0 ? $", guild {i.GuildName}" : ""));
        };
        _bridge.PointsChanged += p =>
        {
            _points = p;
            GD.Print($"[world] points: XP {p.Experience}/{p.ExperienceForNextLevel} ({p.LevelPermill / 10.0:0.0}%), " +
                     $"realm points {p.RealmPoints}, bounty {p.BountyPoints}, spec points {p.SpecPoints}");
        };
        _bridge.MoneyChanged += m =>
        {
            _money = m;
            GD.Print($"[world] money: {Money(m)}");
        };
        // The skill and spell lists (for the quickbars and the spell and style sheets): kept in _bridge.Skills.
        _bridge.SkillsChanged += list => GD.Print($"[world] skills: {list.Count} (" +
            string.Join(", ", list.GroupBy(s => s.Page).Select(g => $"{g.Count()} {g.Key}")) + ")");
        _bridge.SpellLinesChanged += lines => GD.Print($"[world] spell lines: {lines.Count}" +
            (lines.Count == 0 ? "" : " (" + string.Join(", ", lines.Select(l => $"{l.LineName} {l.Entries.Count}")) + ")"));
        _bridge.WeaponStatsChanged += w => _weapon = w;
        _bridge.CraftingSkillsChanged += c => GD.Print($"[world] crafting skills: {c.Skills.Count}");
    }

    // "Lilillyn  L50 Paladin  RR 5L0  XP 9.1%  20g 0s 0c"
    private string CharacterLine
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (_info is PlayerInfo i)
                parts.Add($"{i.Name}  L{i.Level} {i.ClassName}  RR {i.RealmRank}");
            if (_points is PointsUpdate p)
                parts.Add($"XP {p.LevelPermill / 10.0:0.0}%");
            if (_money is MoneyUpdate m)
                parts.Add(Money(m));
            return parts.Count == 0 ? "" : "   " + string.Join("  ", parts);
        }
    }

    private static string Money(MoneyUpdate m) =>
        (m.Mithril > 0 ? $"{m.Mithril}m " : "") + (m.Platinum > 0 || m.Mithril > 0 ? $"{m.Platinum}p " : "") +
        $"{m.Gold}g {m.Silver}s {m.Copper}c";
}
