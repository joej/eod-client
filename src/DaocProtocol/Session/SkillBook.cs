// SPDX-License-Identifier: GPL-3.0-or-later
//
// Assembles the server's paged 0x16 lists into whole ones: the usable-skill list (subcode 1) arrives in pages
// of up to ~1400 bytes, the last one marked; the spell lines (subcode 2) arrive one per packet, then a footer.
// A page starting at index 0, or line 0, starts its list again (the server resends everything when a skill
// changes: a level, a trained spec, a new style).

using DaocProtocol.Packets;

namespace DaocProtocol.Session;

/// <summary>A use request: its code (0xBB a skill, 0x7D a list spell) and its two bytes, for the name found.</summary>
public readonly record struct UseTarget(byte Code, byte First, byte Second, string Name);

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

    /// <summary>
    /// How to use the skill or list spell called <paramref name="name"/> (first match, any case): a 0xBB with
    /// its list index and type (a specialisation: its index in the whole list and type 0; anything else: its
    /// index from the first entry that isn't a specialisation, and its page as the type), or a 0x7D with the
    /// entry's level and its line's number. Null if neither list has it.
    /// </summary>
    public UseTarget? Find(string name)
    {
        int specs = SpecCount;
        for (int i = 0; i < Skills.Count; i++)
            if (Skills[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Skills[i].Page == SkillPage.Specialization
                    ? new UseTarget(UseRequest.SkillCode, (byte)i, 0, Skills[i].Name)
                    : new UseTarget(UseRequest.SkillCode, (byte)(i - specs), (byte)Skills[i].Page, Skills[i].Name);
        for (int l = 0; l < SpellLines.Count; l++)
            foreach (LineEntry e in SpellLines[l].Entries)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return new UseTarget(UseRequest.SpellCode, e.Level, (byte)l, e.Name);
        return null;
    }

    private int SpecCount => Skills.TakeWhile(s => s.Page == SkillPage.Specialization).Count();

    // Cooldowns by what they apply to (page and id, or line id and entry id), not by list position: the
    // server resends the list (twice at world entry) and positions may move. Values: when usable again (ms).
    private readonly Dictionary<(SkillPage, ushort), long> _skillReady = [];
    private readonly Dictionary<(ushort Line, ushort Entry), long> _spellReady = [];

    /// <summary>Records a 0xD6 received at <paramref name="nowMs"/> (any monotonic clock in milliseconds).</summary>
    public void Apply(DisableSkills d, long nowMs)
    {
        int specs = SpecCount;
        foreach ((ushort index, ushort seconds) in d.Skills)
            if (specs + index < Skills.Count && Skills[specs + index] is SkillEntry s)
                _skillReady[(s.Page, s.Id)] = nowMs + seconds * 1000L;
        foreach ((byte line, byte entry) in d.Spells)
            if (line < SpellLines.Count && entry < SpellLines[line].Entries.Count)
                _spellReady[(SpellLines[line].LineId, SpellLines[line].Entries[entry].Id)] = nowMs + d.SpellSeconds * 1000L;
    }

    /// <summary>Seconds until the named skill or spell can be used again (0: now).</summary>
    public double CooldownLeft(string name, long nowMs)
    {
        long ready = 0;
        foreach (SkillEntry s in Skills.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            ready = Math.Max(ready, _skillReady.GetValueOrDefault((s.Page, s.Id)));
        foreach (SpellLinePage line in SpellLines)
            foreach (LineEntry e in line.Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                ready = Math.Max(ready, _spellReady.GetValueOrDefault((line.LineId, e.Id)));
        return Math.Max(0, (ready - nowMs) / 1000.0);
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
