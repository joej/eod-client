// SPDX-License-Identifier: GPL-3.0-or-later
//
// Assembles the server's paged 0x16 lists into whole ones: the usable-skill list (subcode 1) arrives in pages
// of up to ~1400 bytes, the last one marked; the spell lines (subcode 2) arrive one per packet, then a footer.
// A page starting at index 0, or line 0, starts its list again (the server resends everything when a skill
// changes: a level, a trained spec, a new style).

using DaocProtocol.Packets;

namespace DaocProtocol.Session;

/// <summary>The character's skills and spell lines as the server last sent them in full.</summary>
public sealed class SkillBook
{
    private readonly List<SkillEntry> _building = [];
    private readonly List<SpellLinePage> _linesBuilding = [];

    /// <summary>The usable skills in the server's order (its index is how other entries refer to one).</summary>
    public IReadOnlyList<SkillEntry> Skills { get; private set; } = [];

    /// <summary>A list caster's spell lines, each with its spells (and the abilities and styles listed with it).</summary>
    public IReadOnlyList<SpellLinePage> SpellLines { get; private set; } = [];

    /// <summary>Adds a page; true when it completed the list (<see cref="Skills"/> is then the new list).</summary>
    public bool Add(SkillListPage page)
    {
        if (page.FirstIndex == 0)
            _building.Clear();
        if (page.FirstIndex != _building.Count)
        {
            // A page out of order (one went missing): drop what we have until the list starts again.
            _building.Clear();
            return false;
        }
        _building.AddRange(page.Entries);
        if (page.MoreFollow)
            return false;
        Skills = [.. _building];
        _building.Clear();
        return true;
    }

    /// <summary>Adds a spell line or the footer; true at the footer (<see cref="SpellLines"/> is then the new set).</summary>
    public bool Add(SpellLinePage page)
    {
        if (page.IsFooter)
        {
            SpellLines = [.. _linesBuilding];
            _linesBuilding.Clear();
            return true;
        }
        if (page.LineIndex == 0)
            _linesBuilding.Clear();
        _linesBuilding.Add(page);
        return false;
    }
}
