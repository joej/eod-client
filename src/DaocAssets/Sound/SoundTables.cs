// SPDX-License-Identifier: GPL-3.0-or-later
//
// The install's sound tables (sounds/cc_*.csv; roadmap M12, docs/sound.md). cc_sounds.csv is the master list: one row
// per sound file with how to play it. Other tables name structures built from those sounds: g_ groups (one member at
// random; "s_null" is a silent choice; "*02" is the previous member's name with its trailing number replaced), q_ queues
// (members in turn, an interval apart; "s_silence(n)" waits n ms), l_ loops (start, body, stop), mp3_ music. The sheets
// per thing (creatures, weapons, spells, doors, events, interface) are rows of such names, by the row's number.
// A field of "*" or nothing takes the "#,0,s_default" row's value. As retail's client does (daoc-re 2026-11-21): names
// match exactly (case-sensitive) and only in the name tables, never by file name; a member list has its whitespace
// removed; "*NN" replaces the last len(NN) characters of the previous member's name; a member that resolves to nothing
// is left out of its list when the tables load ("s_null" is built in and stays, a silent choice); r1 and r2 belong to
// the file, taken from the first row that names it.

using System.Globalization;

namespace DaocAssets.Sound;

/// <summary>A min-max range; a single value is both.</summary>
public readonly record struct SoundRange(float Min, float Max)
{
    public float Pick(Random rng) => Min + (float)rng.NextDouble() * (Max - Min);

    /// <summary>"1.0-1.1", ".97 - 1.03", "1"; null for "*" or empty.</summary>
    public static SoundRange? Parse(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text == "*")
            return null;
        int dash = text.IndexOf('-', 1);
        if (dash < 0)
            return Num(text) is float v ? new SoundRange(v, v) : null;
        return Num(text[..dash]) is float a && Num(text[(dash + 1)..]) is float b ? new SoundRange(a, b) : null;
    }

    /// <summary>A pan: "C" centre, ".9L-.9R", "L"/"R" with an optional amount; −1 left .. +1 right.</summary>
    public static SoundRange? ParsePan(string? text)
    {
        text = text?.Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(text) || text == "*")
            return null;
        string[] ends = text.Split('-');
        float? a = PanEnd(ends[0]), b = ends.Length > 1 ? PanEnd(ends[1]) : a;
        return a is float x && b is float y ? new SoundRange(Math.Min(x, y), Math.Max(x, y)) : null;
    }

    private static float? PanEnd(string end)
    {
        if (end.Equals("C", StringComparison.OrdinalIgnoreCase))
            return 0;
        char side = char.ToUpperInvariant(end[^1]);
        if (side is not ('L' or 'R'))
            return null;
        float amount = end.Length > 1 ? Num(end[..^1]) ?? 1 : 1;
        return side == 'L' ? -amount : amount;
    }

    internal static float? Num(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;
}

/// <summary>One cc_sounds row, defaults filled in.</summary>
public sealed record SoundRow(int Id, string Name, string File, SoundRange Rate, SoundRange Volume, SoundRange Pan,
    SoundRange OffsetMs, float R1, float R2, string? Polygroup);

/// <summary>
/// A cc_sounds POLYGROUPS row: at most Limit voices (capped at 10). Only a cutoff-2 group limits anything: starting a
/// sound past its limit stops its oldest ones. A cutoff-1 group would refuse new sounds, but retail never counts its
/// voices, so it never does (daoc-re 2026-11-21).
/// </summary>
public sealed record Polygroup(string Name, int Limit, int Cutoff)
{
    public const int Steals = 2, MaxLimit = 10;

    /// <summary>Whether starting sounds in the group stops its oldest ones past the limit.</summary>
    public bool Limits => Cutoff == Steals && Limit > 0;
}

/// <summary>A cc_queues row: members in turn; IntervalMs between them (−1: end to end).</summary>
public sealed record SoundQueue(string Name, IReadOnlyList<string> Members, SoundRange IntervalMs);

/// <summary>A cc_loops row: Start once, Body repeated, Stop when it ends; DurationMs −1 = until stopped.</summary>
public sealed record SoundLoop(string Name, string? Start, string Body, string? Stop, int DurationMs);

public sealed class SoundTables
{
    private readonly Dictionary<string, SoundRow> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, SoundRow> _byId = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SoundQueue> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SoundLoop> _loops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mp3Files = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _mp3Ids = [];
    private readonly Dictionary<string, Polygroup> _polygroups = new(StringComparer.Ordinal);
    public const string Null = "s_null";

    /// <summary>Every cc_sounds row, by name.</summary>
    public IReadOnlyDictionary<string, SoundRow> Sounds => _byName;
    public IReadOnlyDictionary<string, Polygroup> Polygroups => _polygroups;

    /// <summary>cc_music: the row the server's 0xEF type 2 names → an mp3_ name.</summary>
    public SoundSheet Music { get; private set; } = SoundSheet.Empty;
    public SoundSheet Creatures { get; private set; } = SoundSheet.Empty;
    public SoundSheet Weapons { get; private set; } = SoundSheet.Empty;
    public SoundSheet Spells { get; private set; } = SoundSheet.Empty;
    public SoundSheet Doors { get; private set; } = SoundSheet.Empty;
    public SoundSheet Events { get; private set; } = SoundSheet.Empty;
    public SoundSheet Interface { get; private set; } = SoundSheet.Empty;

    /// <summary>Reads the tables from a folder holding cc_sounds.csv and the rest (any case); missing tables are empty.</summary>
    public static SoundTables Load(string folder)
    {
        string? Text(string name)
        {
            string? path = Directory.EnumerateFiles(folder, "*.csv")
                .FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
            return path is null ? null : InstallFiles.ReadAllText(path, System.Text.Encoding.Latin1);
        }
        return Parse(Text);
    }

    public static SoundTables Parse(Func<string, string?> text)
    {
        var t = new SoundTables();
        t.ReadSounds(text("cc_sounds.csv") ?? "");
        foreach (string[] f in Rows(text("cc_groups.csv")))
            if (f.Length > 2 && f[1].Length > 0)
                t._groups[f[1].Trim()] = Members(f[2]);
        foreach (string[] f in Rows(text("cc_queues.csv")))
            if (f.Length > 3 && f[1].Length > 0)
                t._queues[f[1].Trim()] = new SoundQueue(f[1].Trim(), Members(f[2]), SoundRange.Parse(f[3]) ?? new(0, 0));
        foreach (string[] f in Rows(text("cc_loops.csv")))
            if (f.Length > 5 && f[1].Length > 0 && f[3].Trim().Length > 0)
                t._loops[f[1].Trim()] = new SoundLoop(f[1].Trim(), Blank(f[2]), f[3].Trim(), Blank(f[4]),
                    int.TryParse(f[5].Trim(), out int d) ? d : -1);
        foreach (string[] f in Rows(text("cc_mp3s.csv")))
            if (f.Length > 3 && int.TryParse(f[1], out int id) && f[2].Trim().Length > 0)
            {
                t._mp3Files[f[2].Trim()] = f[3].Trim();
                t._mp3Ids[id] = f[2].Trim();
            }
        t.DropUnresolved();
        t.Music = SoundSheet.Parse(text("cc_music.csv"), nameColumn: false);
        t.Creatures = SoundSheet.Parse(text("cc_creatures.csv"));
        t.Weapons = SoundSheet.Parse(text("cc_weapons.csv"));
        t.Spells = SoundSheet.Parse(text("cc_spells.csv"));
        t.Doors = SoundSheet.Parse(text("cc_doors.csv"));
        t.Events = SoundSheet.Parse(text("CC_events.csv"));
        t.Interface = SoundSheet.Parse(text("cc_interface.csv"));
        return t;
    }

    private void ReadSounds(string csv)
    {
        string[] defaults = [];
        var rows = new List<string[]>();
        foreach (string[] f in Rows(csv, keepMarked: true))
        {
            if (f[0] == "#" && f.Length > 5 && f[1].Length == 0 && f[2].Length > 0)
                _polygroups[f[2].Trim()] = new Polygroup(f[2].Trim(), Math.Min(Polygroup.MaxLimit, Int(f[4], 0)), Int(f[5], 0));
            else if (f[0] == "#" && f.Length > 2 && f[2].Trim().Equals("s_default", StringComparison.OrdinalIgnoreCase))
                defaults = f;
            else if (f[0].Length == 0 && f.Length > 3 && int.TryParse(f[1], out _))
                rows.Add(f);
        }
        string Field(string[] f, int i) =>
            i < f.Length && f[i].Trim() is { Length: > 0 } v && v != "*" ? v : i < defaults.Length ? defaults[i].Trim() : "";
        // r1/r2 per file: the first row naming the file sets them (retail stores them when it first meets the file name;
        // that this is the first row in file order rather than the first played is inferred).
        var ranges = new Dictionary<string, (float R1, float R2)>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] f in rows)
        {
            (float r1, float r2) = ranges.TryGetValue(f[3].Trim(), out var known)
                ? known
                : ranges[f[3].Trim()] = (SoundRange.Num(Field(f, 8)) ?? 0, SoundRange.Num(Field(f, 9)) ?? 1000);
            var row = new SoundRow(int.Parse(f[1], CultureInfo.InvariantCulture), f[2].Trim(), f[3].Trim(),
                SoundRange.Parse(Field(f, 4)) ?? new(1, 1),
                SoundRange.Parse(Field(f, 5)) ?? new(1, 1),
                SoundRange.ParsePan(Field(f, 6)) ?? new(0, 0),
                SoundRange.Parse(Field(f, 7)) ?? new(0, 0),
                r1, r2,
                Blank(Field(f, 12)));
            // Most rows (2,000 of 4,226) have id 0 and are reached by name only; two ids appear twice, and the first row
            // (the file lists the newest version first) wins (ours).
            if (row.Id != 0)
                _byId.TryAdd(row.Id, row);
            if (row.Name.Length > 0)
                _byName.TryAdd(row.Name, row);
        }
    }

    // A member that resolves to nothing leaves its group or queue, as retail does when its tables load (resolved here
    // after every table is read; retail's order: inferred). Loop parts that resolve to nothing are dropped.
    private void DropUnresolved()
    {
        bool Resolves(string m) => m == Null || SoundPlanner.Silence(m) is not null || Resolves1(m);
        bool Resolves1(string m) => (int.TryParse(m, out int id) ? Sound(id) : Sound(m)) is not null ||
                                    _groups.ContainsKey(m) || _queues.ContainsKey(m) || _loops.ContainsKey(m);
        foreach ((string name, IReadOnlyList<string> members) in _groups.ToList())
            _groups[name] = members.Where(Resolves).ToList();
        foreach ((string name, SoundQueue q) in _queues.ToList())
            _queues[name] = q with { Members = q.Members.Where(Resolves).ToList() };
        foreach ((string name, SoundLoop l) in _loops.ToList())
            _loops[name] = l with
            {
                Start = l.Start is string a && Resolves1(a) ? a : null,
                Stop = l.Stop is string b && Resolves1(b) ? b : null,
            };
    }

    public SoundRow? Sound(int id) => _byId.GetValueOrDefault(id);
    public SoundRow? Sound(string name) => _byName.GetValueOrDefault(name.Trim());
    public IReadOnlyList<string>? Group(string name) => _groups.GetValueOrDefault(name.Trim());
    public SoundQueue? Queue(string name) => _queues.GetValueOrDefault(name.Trim());
    public SoundLoop? Loop(string name) => _loops.GetValueOrDefault(name.Trim());

    /// <summary>An mp3_ name's file, or null.</summary>
    public string? Mp3File(string name) => _mp3Files.GetValueOrDefault(name.Trim());

    /// <summary>A cc_mp3s id's mp3_ name (zones.dat entry_music holds these ids), or null.</summary>
    public string? Mp3Name(int id) => _mp3Ids.GetValueOrDefault(id);

    // --- csv -------------------------------------------------------------------------------------------------------

    /// <summary>The data rows of a table: comment rows (";", "!") skipped; "#" rows only with keepMarked.</summary>
    internal static IEnumerable<string[]> Rows(string? csv, bool keepMarked = false)
    {
        if (csv is null)
            yield break;
        foreach (string raw in csv.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] is ';' or '!' || (line[0] == '#' && !keepMarked))
                continue;
            yield return Split(line);
        }
    }

    /// <summary>One csv line's fields; a field in double quotes may hold commas ("" is a quote).</summary>
    internal static string[] Split(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    field.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
                field.Append(c);
        }
        fields.Add(field.ToString());
        return [.. fields];
    }

    /// <summary>
    /// A member list, whitespace removed: "s_A_01, *02, *03" → s_A_01, s_A_02, s_A_03 ("*NN" replaces the last len(NN)
    /// characters of the previous member's name).
    /// </summary>
    internal static IReadOnlyList<string> Members(string list)
    {
        var members = new List<string>();
        foreach (string part in list.Split(','))
        {
            string m = string.Concat(part.Where(c => !char.IsWhiteSpace(c)));
            if (m.Length == 0)
                continue;
            if (m[0] == '*' && members.Count > 0)
            {
                string last = members[^1], tail = m[1..];
                m = last[..Math.Max(0, last.Length - tail.Length)] + tail;
            }
            members.Add(m);
        }
        return members;
    }

    private static string? Blank(string s) => s.Trim() is { Length: > 0 } v && v != "*" ? v : null;
    private static int Int(string s, int fallback) => int.TryParse(s.Trim(), out int v) ? v : fallback;
}

/// <summary>A per-thing sheet (cc_creatures, cc_weapons, …): row number → its sound names, counted after the name column.</summary>
public sealed class SoundSheet
{
    public static readonly SoundSheet Empty = new([]);
    private readonly Dictionary<int, string[]> _rows;

    private SoundSheet(Dictionary<int, string[]> rows) => _rows = rows;

    public IReadOnlyCollection<int> RowIds => _rows.Keys;

    /// <summary>Column <paramref name="column"/> (0-based, after the name) of row <paramref name="row"/>, or null when empty.</summary>
    public string? Get(int row, int column) =>
        _rows.TryGetValue(row, out string[]? f) && column < f.Length && f[column].Trim() is { Length: > 0 } v ? v : null;

    internal static SoundSheet Parse(string? csv, bool nameColumn = true)
    {
        var rows = new Dictionary<int, string[]>();
        foreach (string[] f in SoundTables.Rows(csv))
            if (int.TryParse(f[0], out int id) && f.Length > 1)
                rows.TryAdd(id, f[(nameColumn ? 2 : 1)..]);
        return new SoundSheet(rows);
    }
}
