// SPDX-License-Identifier: GPL-3.0-or-later
//
// What one sound name plays (roadmap M12): a cc_sounds row by name or id, a g_ group (one member at random), a q_ queue
// (its members in turn), an l_ loop (its start, then its body), each resolved down to the sound files and when each
// starts. As retail's client does (daoc-re 2026-11-21): a queue interval runs from one member's start to the next's;
// −1 starts the next when the last ends; "s_silenceN" waits N ms in place of the interval; the first member starts at
// once. "s_null" plays nothing and takes no time. Ours: interval 0 starts every member together (retail: one a frame).

namespace DaocAssets.Sound;

/// <summary>One sound file to start <paramref name="AtMs"/> after the cue starts.</summary>
public readonly record struct PlannedSound(SoundRow Row, double AtMs);

public sealed class SoundPlanner(SoundTables tables, Random? rng = null)
{
    private const int MaxDepth = 8;
    private readonly Random _rng = rng ?? Random.Shared;

    public SoundTables Tables => tables;

    /// <summary>Whether <paramref name="name"/> names anything we can play.</summary>
    public bool Knows(string name) =>
        Row(name) is not null || tables.Group(name) is not null || tables.Queue(name) is not null || tables.Loop(name) is not null;

    /// <summary>An l_ loop's body (what repeats), or null when the name isn't a loop.</summary>
    public SoundLoop? Loop(string name) => tables.Loop(name);

    /// <summary>The sounds one play of <paramref name="name"/> starts, in time order; empty for an unknown name.</summary>
    /// <param name="durationMs">A row's length in ms, for queues played end to end (interval −1).</param>
    public List<PlannedSound> Plan(string name, Func<SoundRow, double>? durationMs = null)
    {
        var plan = new List<PlannedSound>();
        Add(name.Trim(), 0, plan, durationMs ?? (_ => 0), 0);
        plan.Sort((a, b) => a.AtMs.CompareTo(b.AtMs));
        return plan;
    }

    /// <summary>The plan for a cc_sounds id.</summary>
    public List<PlannedSound> Plan(int id) =>
        tables.Sound(id) is SoundRow row ? [new PlannedSound(row, 0)] : [];

    private SoundRow? Row(string name) =>
        int.TryParse(name, out int id) ? tables.Sound(id) : tables.Sound(name);

    // Adds what name plays from time at; returns when it ends (for end-to-end queues).
    private double Add(string name, double at, List<PlannedSound> plan, Func<SoundRow, double> length, int depth)
    {
        if (depth > MaxDepth || name.Length == 0 || name == SoundTables.Null)
            return at;
        if (Row(name) is SoundRow row)
        {
            plan.Add(new PlannedSound(row, at));
            return at + length(row);
        }
        if (tables.Group(name) is { Count: > 0 } members)
            return Add(members[_rng.Next(members.Count)], at, plan, length, depth + 1);
        if (tables.Queue(name) is SoundQueue queue)
        {
            double t = at, end = at;
            foreach (string member in queue.Members)
            {
                if (Silence(member) is double ms)
                {
                    t += ms;
                    continue;
                }
                double ends = Add(member, t, plan, length, depth + 1);
                end = Math.Max(end, ends);
                float interval = queue.IntervalMs.Pick(_rng);
                t = interval < 0 ? ends : t + interval;
            }
            return Math.Max(end, t);
        }
        if (tables.Loop(name) is SoundLoop loop)
        {
            double t = loop.Start is string start ? Add(start, at, plan, length, depth + 1) : at;
            return Add(loop.Body, t, plan, length, depth + 1);
        }
        return at;
    }

    /// <summary>"s_silence250" or "s_silence(250)" → 250 ms; null for anything else.</summary>
    internal static double? Silence(string member)
    {
        if (!member.StartsWith("s_silence", StringComparison.Ordinal))
            return null;
        string n = member["s_silence".Length..].Trim('(', ')');
        return SoundRange.Parse(n) is SoundRange r ? (r.Min + r.Max) / 2 : 0;
    }
}
