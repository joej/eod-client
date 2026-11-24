// SPDX-License-Identifier: GPL-3.0-or-later
//
// What the trainer window shows and sends, kept from the server's 0x7B sections (roadmap M13), with retail 1.130's rules
// (daoc-re, 2026-11-24): which section clears what, how the lines pair with their skills, what a level costs, Minimum
// Level, the new skills between the current and the target level, and the 0x53 request. No Godot here: the window draws it.

using DaocProtocol.Packets;

namespace DaocProtocol.Session;

public sealed class TrainerState
{
    public const int SliderRows = 8, MaxNames = 1200, MaxLevel = 50, MaxNewSkills = 20;

    private readonly List<TrainerSpec> _specs = [];
    private readonly List<TrainerRealmAbility> _realmList = [];
    private readonly string[] _names = new string[MaxNames];
    private byte[] _table = new byte[50];
    private List<TrainerSpecSkills> _skills = [];
    private List<TrainerRealmCosts> _realm = [];

    /// <summary>The lines with a slider, as the window was built (at the last code 0 or 1): the first 8 code-0 entries
    /// whose third byte isn't 0. Later sections that clear the spec list don't change a window already built.</summary>
    public IReadOnlyList<TrainerSpec> Lines { get; private set; } = [];

    /// <summary>The realm abilities of the new window (code 5).</summary>
    public IReadOnlyList<TrainerRealmCosts> Realm => _realm;

    /// <summary>Each slider line's target level; each realm ability's.</summary>
    public int[] Targets { get; } = new int[SliderRows];
    public int[] RealmTargets { get; private set; } = [];

    /// <summary>
    /// Applies a section. Codes 0, 2, 4 and unknown codes clear the spec list (code 0 then fills it); code 1 its own list;
    /// code 4 with start 0 the names. True when it opens (or refreshes) the window: codes 0 and 1.
    /// </summary>
    public bool Apply(TrainerWindow w)
    {
        if (w.Code is not (TrainerWindow.CodeRealm or TrainerWindow.CodeSkills or TrainerWindow.CodeRealmCosts))
            _specs.Clear();
        switch (w.Code)
        {
            case TrainerWindow.Code0Specs:
                _specs.AddRange(w.Specs.Take(40));
                Build();
                return true;
            case TrainerWindow.CodeRealm:
                _realmList.Clear();
                _realmList.AddRange(w.RealmAbilities);
                Build();
                return true;
            case TrainerWindow.CodeNames:
                if (w.NameStart == 0)
                    Array.Clear(_names);
                for (int i = 0; i < w.Names.Count && w.NameStart + i < MaxNames; i++)
                    _names[w.NameStart + i] = w.Names[i];
                return false;
            case TrainerWindow.CodeSkills:
                _table = w.LevelTable.Length == 50 ? w.LevelTable : new byte[50];
                _skills = w.SpecSkills.Take(SliderRows).ToList();
                return false;
            case TrainerWindow.CodeRealmCosts:
                _realm = w.RealmCosts.Take(50).ToList();
                RealmTargets = _realm.Select(r => (int)r.Level).ToArray();
                return false;
            default:
                return false;
        }
    }

    // Codes 0 and 1 (re)build the window from the spec list as it is now.
    private void Build()
    {
        Lines = _specs.Where(s => s.HasSlider).Take(SliderRows).ToList();
        Reset();
    }

    /// <summary>RESET: every line and realm ability back to its current level.</summary>
    public void Reset()
    {
        IReadOnlyList<TrainerSpec> lines = Lines;
        for (int i = 0; i < SliderRows; i++)
            Targets[i] = i < lines.Count ? lines[i].Current : 0;
        RealmTargets = _realm.Select(r => (int)r.Level).ToArray();
    }

    /// <summary>Line <paramref name="row"/>'s skills (code 3 block <paramref name="row"/>, paired by position).</summary>
    public TrainerSpecSkills? SkillsOf(int row) => row < _skills.Count ? _skills[row] : null;

    /// <summary>A skill's name: the code 4 name at its running index across all blocks, in order.</summary>
    public string NameOf(int row, int skill)
    {
        int index = _skills.Take(row).Sum(b => b.Skills.Count) + skill;
        return index < MaxNames ? _names[index] ?? "" : "";
    }

    /// <summary>The cost of raising a line from <paramref name="from"/> to <paramref name="to"/>: the sum of each level,
    /// levels up to the line's autotrain byte free.</summary>
    public int CostOf(int row, int from, int to)
    {
        int free = SkillsOf(row)?.Autotrain ?? 0;
        int cost = 0;
        for (int l = from + 1; l <= to; l++)
            if (l > free)
                cost += l;
        return cost;
    }

    /// <summary>Specialisation points the targets need.</summary>
    public int Needed
    {
        get
        {
            IReadOnlyList<TrainerSpec> lines = Lines;
            return Enumerable.Range(0, lines.Count).Sum(i => CostOf(i, lines[i].Current, Targets[i]));
        }
    }

    /// <summary>Realm points the realm targets need: each ability's per-level costs from its current level to its target.</summary>
    public int RealmNeeded => _realm.Select((r, i) => RealmCost(i)).Sum();

    public int RealmCost(int i)
    {
        TrainerRealmCosts r = _realm[i];
        int cost = 0;
        for (int l = r.Level; l < RealmTargets[i] && l < r.Costs.Count; l++)
            cost += r.Costs[l];
        return cost;
    }

    /// <summary>
    /// The larger of the highest target level and the lowest character level whose points (the code 3 table, summed from
    /// level 1) cover what all the targets cost from their free base.
    /// </summary>
    public int MinimumLevel
    {
        get
        {
            IReadOnlyList<TrainerSpec> lines = Lines;
            int highest = Enumerable.Range(0, lines.Count).Select(i => Targets[i]).DefaultIfEmpty(0).Max();
            int total = Enumerable.Range(0, lines.Count).Sum(i => CostOf(i, 0, Targets[i]));
            int have = 0, level = MaxLevel;
            for (int l = 1; l <= MaxLevel; l++)
            {
                have += _table[l - 1];
                if (have >= total)
                {
                    level = l;
                    break;
                }
            }
            return Math.Max(highest, level);
        }
    }

    /// <summary>The skills between a line's current level and its target (current &lt; level ≤ target), as (row, index).</summary>
    public IEnumerable<(int Row, int Skill, TrainerSkill Info)> NewSkills(int row)
    {
        IReadOnlyList<TrainerSpec> lines = Lines;
        if (row >= lines.Count || SkillsOf(row) is not { } block)
            yield break;
        for (int k = 0; k < block.Skills.Count; k++)
        {
            int at = block.Skills[k].AtLevel;
            if (at > lines[row].Current && at <= Targets[row])
                yield return (row, k, block.Skills[k]);
        }
    }

    /// <summary>"New Styles and Skills": every line's new skills together, lowest level first, at most 20.</summary>
    public IReadOnlyList<(int Row, int Skill, TrainerSkill Info)> AllNewSkills() =>
        Enumerable.Range(0, Lines.Count).SelectMany(NewSkills).OrderBy(s => s.Info.AtLevel).Take(MaxNewSkills).ToList();

    /// <summary>TRAIN's 0x53: every line (block i's index, its target, absolute) and every realm ability's target.</summary>
    public byte[] Request()
    {
        var specs = new List<(byte, byte)>();
        for (int i = 0; i < Math.Min(Lines.Count, _skills.Count); i++)
            specs.Add((_skills[i].Index, (byte)Targets[i]));
        return TrainRequest.Payload(specs, RealmTargets.Select(t => (byte)t).ToList());
    }
}
