// SPDX-License-Identifier: GPL-3.0-or-later
//
// What the client's zone data contains, independent of what the converter reads: the kinds of zone,
// every archive and file pattern, every SECTOR.DAT section and key, every CSV's columns, and the
// shape of binary files we don't parse. `mpk worldaudit` runs it over an install so the roadmap is
// built from an inventory of the world rather than from what we happened to notice
// (docs/world-coverage.md). Like NifAudit, the report describes the owner's files and stays private.

using System.Text;
using System.Text.RegularExpressions;

namespace DaocAssets.Zone;

public sealed partial class ZoneAudit
{
    /// <summary>Files the converter reads today (normalised names; see <see cref="Pattern"/>).</summary>
    public static readonly IReadOnlySet<string> UsedFiles = new HashSet<string>
    {
        "nifs.csv", "fixtures.csv", "terrain.pcx", "offset.pcx", "sector.dat", "bound.csv", "lights.csv",
        "shadow.pcx",
    };

    /// <summary>SECTOR.DAT keys the converter reads today, as "section.key" (normalised). Keep in step
    /// with ZoneTerrain, ZoneWater and `mpk sky` (fog): the audit's review list is everything in a
    /// section we read that is NOT here, so a key missing from this list shows up as unreviewed.</summary>
    public static readonly IReadOnlySet<string> UsedSectorKeys = new HashSet<string>
    {
        "terrain.scalefactor", "terrain.offsetfactor", "terrain.flip_x", "terrain.flip_y", "terrain.use_texture",
        "river#.name", "river#.type", "river#.height", "river#.texture", "river#.multitexture",
        "river#.flow", "river#.color", "river#.left#", "river#.right#", "river#.bankpoints",
        "fog.clip", "fog.amount", "fog.red", "fog.green", "fog.blue",
    };

    /// <summary>CSV columns the converter reads, by file pattern: column index -> what it is used
    /// for (ZoneLayout). Every other column of these files is on the review list.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> UsedCsvColumns =
        new Dictionary<string, IReadOnlyDictionary<int, string>>
        {
            ["fixtures.csv"] = new Dictionary<int, string>
            {
                [1] = "model (NIF #)", [3] = "x", [4] = "y", [5] = "z (unless On Ground)", [6] = "angle",
                [7] = "scale", [8] = "Collide (0: not solid)", [11] = "On Ground", [15] = "3D Angle", [16] = "3D Axis X",
                [17] = "3D Axis Y", [18] = "3D Axis Z",
            },
            ["nifs.csv"] = new Dictionary<int, string> { [0] = "NIF #", [1] = "label", [2] = "model file" },
            // ZoneJumps: the doorway line, its floor and top, and the server zone point it uses.
            ["zonejump.csv"] = new Dictionary<int, string>
            {
                [1] = "name", [2] = "x1", [3] = "y1", [4] = "x2", [5] = "y2", [6] = "z1 (floor)", [7] = "z2 (top)",
                [8] = "zone point id",
            },
        };

    private sealed class FileStats
    {
        public readonly HashSet<string> Zones = [];
        public readonly HashSet<string> Archives = [];
        public long MinSize = long.MaxValue, MaxSize;
        public readonly Dictionary<string, int> Heads = [];   // leading bytes (hex) -> files
        public readonly Dictionary<string, int> CsvHeaders = [];
        public long CsvRows;
        public string[] ColumnNames = [];
        public readonly List<Dictionary<string, int>> ColumnValues = [];
    }

    private sealed class KeyStats
    {
        public readonly HashSet<string> Zones = [];
        public readonly List<string> Examples = [];
        public readonly Dictionary<string, int> Values = [];
    }

    private readonly Dictionary<string, FileStats> _files = [];
    private readonly Dictionary<string, HashSet<string>> _sections = [];
    private readonly Dictionary<string, KeyStats> _keys = [];
    private readonly Dictionary<string, HashSet<string>> _kinds = [];
    private readonly Dictionary<string, HashSet<string>> _zoneFolders = [];   // non-archive files

    public int Zones => _kinds.Values.Sum(z => z.Count);

    /// <summary>Digit runs become '#', and case is folded: "river00" -> "river#", "tex03-00.bmp" ->
    /// "tex#-#.bmp", so per-zone and per-index names group together.</summary>
    public static string Pattern(string name) => Digits().Replace(name.ToLowerInvariant(), "#");

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>Classifies a zone by the files its data archive holds.</summary>
    public static string Kind(IEnumerable<string> datMembers)
    {
        var m = datMembers.Select(x => x.ToLowerInvariant()).ToHashSet();
        if (m.Contains("terrain.pcx")) return "outdoor (heightmap)";
        if (m.Contains("skycity.chunk")) return "city (skycity chunks)";
        if (m.Contains("dungeon.chunk")) return "dungeon (chunks)";
        if (m.Count == 0) return "no data archive";
        return "other";
    }

    /// <summary>Records one zone. <paramref name="archives"/> maps an archive name (e.g. dat000.mpk)
    /// to its members; <paramref name="read"/> loads a member's bytes on demand.</summary>
    public void AddZone(string zone, IReadOnlyDictionary<string, IReadOnlyList<string>> archives,
        Func<string, string, byte[]> read, IEnumerable<string> looseFiles)
    {
        string? dat = archives.Keys.FirstOrDefault(a => Pattern(a) == "dat#.mpk");
        string kind = Kind(dat is null ? [] : archives[dat]);
        if (!_kinds.TryGetValue(kind, out var zs)) _kinds[kind] = zs = [];
        zs.Add(zone);
        foreach (string f in looseFiles)
        {
            string p = Pattern(f);
            if (!_zoneFolders.TryGetValue(p, out var z)) _zoneFolders[p] = z = [];
            z.Add(zone);
        }

        foreach ((string archive, IReadOnlyList<string> members) in archives)
        {
            string ap = Pattern(archive);
            foreach (string member in members)
            {
                string mp = Pattern(member);
                // Texture/terrain/LOD archives hold hundreds of per-tile files: record the pattern
                // without opening each one.
                bool inspect = ap == "dat#.mpk" || ap == "csv#.mpk";
                if (!_files.TryGetValue(mp, out FileStats? s)) _files[mp] = s = new FileStats();
                s.Zones.Add(zone);
                s.Archives.Add(ap);
                if (!inspect)
                    continue;
                byte[] bytes;
                try { bytes = read(archive, member); }
                catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
                s.MinSize = Math.Min(s.MinSize, bytes.Length);
                s.MaxSize = Math.Max(s.MaxSize, bytes.Length);
                if (mp == "sector.dat")
                    AddSector(zone, bytes);
                else if (mp.EndsWith(".csv"))
                    AddCsv(s, bytes);
                else if (!mp.EndsWith(".pcx"))
                    Bump(s.Heads, Head(bytes));
            }
        }
    }

    private void AddSector(string zone, byte[] bytes)
    {
        string section = "";
        foreach (string raw in Encoding.Latin1.GetString(bytes).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.Contains(']'))
            {
                section = Pattern(line[1..line.IndexOf(']')].Trim());
                if (!_sections.TryGetValue(section, out var z)) _sections[section] = z = [];
                z.Add(zone);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = $"{section}.{Pattern(line[..eq].Trim())}";
            if (!_keys.TryGetValue(key, out KeyStats? k)) _keys[key] = k = new KeyStats();
            k.Zones.Add(zone);
            string value = line[(eq + 1)..].Trim();
            if (k.Examples.Count < 3 && !k.Examples.Contains(value))
                k.Examples.Add(value.Length > 40 ? value[..40] + "…" : value);
            CountValue(k.Values, value);
        }
    }

    private static void AddCsv(FileStats s, byte[] bytes)
    {
        string[] lines = Encoding.Latin1.GetString(bytes).Split('\n')
            .Select(l => l.TrimEnd('\r')).Where(l => l.Trim(',', ' ').Length > 0).ToArray();
        if (lines.Length == 0)
            return;
        // DAoC CSVs often carry a group line above the column names; keep the first two lines.
        string header = string.Join(" / ", lines.Take(2).Select(l => l.Length > 160 ? l[..160] + "…" : l));
        Bump(s.CsvHeaders, header);
        s.CsvRows += Math.Max(0, lines.Length - 2);
        // Column names from the second header line; values per column from the data rows.
        if (lines.Length >= 2 && s.ColumnNames.Length == 0)
            s.ColumnNames = lines[1].Split(',').Select(c => c.Trim()).ToArray();
        foreach (string line in lines.Skip(2))
        {
            string[] cells = line.Split(',');
            for (int c = 0; c < cells.Length; c++)
            {
                while (s.ColumnValues.Count <= c) s.ColumnValues.Add([]);
                CountValue(s.ColumnValues[c], cells[c].Trim());
            }
        }
    }

    // Distinct values with counts, capped so free-text columns don't grow without bound.
    private static void CountValue(Dictionary<string, int> values, string value)
    {
        if (values.ContainsKey(value) || values.Count < 200)
            Bump(values, value);
        else
            Bump(values, "(other)");
    }

    // "0 ×546, 1 ×990" — the most common values; "(n distinct)" when there are many.
    private static string Distribution(Dictionary<string, int> values, int top = 6)
    {
        string list = string.Join(", ", values.OrderByDescending(kv => kv.Value).Take(top)
            .Select(kv => $"`{Md(kv.Key.Length == 0 ? "(empty)" : kv.Key.Length > 24 ? kv.Key[..24] + "…" : kv.Key)}` ×{kv.Value}"));
        return values.Count > top ? $"{list} … ({values.Count}{(values.ContainsKey("(other)") ? "+" : "")} distinct)" : list;
    }

    // A value a field we read probably doesn't mean literally: zero, empty, negative.
    private static IEnumerable<string> Degenerate(Dictionary<string, int> values) =>
        values.Where(kv => kv.Key.Length == 0 || kv.Key == "0" || kv.Key.StartsWith('-'))
            .Select(kv => $"`{Md(kv.Key.Length == 0 ? "(empty)" : kv.Key)}` ×{kv.Value}");

    private static string Head(byte[] bytes)
    {
        var sb = new StringBuilder();
        foreach (byte b in bytes.Take(12))
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return sb.ToString();
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    /// <summary>The report as Markdown.</summary>
    public string ToMarkdown(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"{Zones} zone folder(s).").AppendLine();
        sb.AppendLine("| Zone kind | Zones |").AppendLine("|---|---:|");
        foreach (var (kind, zones) in _kinds.OrderByDescending(kv => kv.Value.Count))
            sb.AppendLine($"| {kind} | {zones.Count} |");
        sb.AppendLine();

        AppendFieldReview(sb);

        sb.AppendLine("## Files in zone archives").AppendLine();
        sb.AppendLine("\"Used\" means the converter reads it today. Sizes and leading bytes are recorded " +
            "for files in the dat/csv archives.").AppendLine();
        sb.AppendLine("| File (pattern) | Used | Zones | Archive | Size | Leading bytes / CSV header |");
        sb.AppendLine("|---|---|---:|---|---|---|");
        foreach (var (name, s) in _files.OrderBy(kv => UsedFiles.Contains(kv.Key) ? 1 : 0)
                     .ThenByDescending(kv => kv.Value.Zones.Count))
        {
            string size = s.MaxSize == 0 ? "-" : s.MinSize == s.MaxSize ? $"{s.MinSize}" : $"{s.MinSize}–{s.MaxSize}";
            string detail = s.CsvHeaders.Count > 0
                ? $"{s.CsvRows} rows; " + string.Join(" ‖ ", s.CsvHeaders.OrderByDescending(kv => kv.Value).Take(2).Select(kv => $"`{Md(kv.Key)}` ×{kv.Value}"))
                : string.Join(", ", s.Heads.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"`{Md(kv.Key)}` ×{kv.Value}"));
            sb.AppendLine($"| {Md(name)} | {(UsedFiles.Contains(name) ? "yes" : "no")} | {s.Zones.Count} | " +
                $"{string.Join(", ", s.Archives.Order())} | {size} | {detail} |");
        }
        sb.AppendLine();

        if (_zoneFolders.Count > 0)
        {
            sb.AppendLine("## Loose files in zone folders").AppendLine();
            foreach (var (name, zones) in _zoneFolders.OrderByDescending(kv => kv.Value.Count))
                sb.AppendLine($"- {Md(name)}: {zones.Count} zone(s)");
            sb.AppendLine();
        }

        sb.AppendLine("## SECTOR.DAT").AppendLine();
        sb.AppendLine("| Section.key | Used | Zones | Examples |").AppendLine("|---|---|---:|---|");
        foreach (var (section, zones) in _sections.OrderByDescending(kv => kv.Value.Count))
        {
            sb.AppendLine($"| **[{Md(section)}]** | | {zones.Count} | |");
            foreach (var (key, k) in _keys.Where(kv => kv.Key.StartsWith(section + ".", StringComparison.Ordinal))
                         .OrderByDescending(kv => kv.Value.Zones.Count))
                sb.AppendLine($"| {Md(key)} | {(UsedSectorKeys.Contains(key) ? "yes" : "no")} | {k.Zones.Count} | " +
                    $"{string.Join(" · ", k.Examples.Select(e => $"`{Md(e)}`"))} |");
        }
        return sb.ToString();
    }

    // The fields-level part of the report: for every file the converter reads, what each field holds,
    // which fields it ignores (to review: "On Ground" sat here unread), and degenerate values in the
    // fields it does read (to review: sizex=0 made a zone 0 units wide).
    private void AppendFieldReview(StringBuilder sb)
    {
        sb.AppendLine("## Fields in the files we read").AppendLine();
        sb.AppendLine("Every column and key of the files the converter reads. **Ignored** fields are a review " +
            "list: each should be understood and either used or recorded as irrelevant. **Check** marks a " +
            "field we read that holds zero, empty or negative values, which may not mean what they say.").AppendLine();
        foreach (var (file, cols) in UsedCsvColumns)
        {
            if (!_files.TryGetValue(file, out FileStats? s) || s.ColumnValues.Count == 0)
                continue;
            sb.AppendLine($"### {file}").AppendLine();
            sb.AppendLine("| # | Column | Converter | Values |").AppendLine("|---:|---|---|---|");
            for (int c = 0; c < s.ColumnValues.Count; c++)
            {
                string name = c < s.ColumnNames.Length ? s.ColumnNames[c] : "";
                string use = cols.TryGetValue(c, out string? u) ? $"reads: {u}" : "**ignored**";
                string check = cols.ContainsKey(c) && Degenerate(s.ColumnValues[c]).Any()
                    ? " **check:** " + string.Join(", ", Degenerate(s.ColumnValues[c])) : "";
                sb.AppendLine($"| {c} | {Md(name)} | {use}{check} | {Distribution(s.ColumnValues[c])} |");
            }
            sb.AppendLine();
        }

        // SECTOR.DAT: the sections the converter reads some key of.
        var readSections = UsedSectorKeys.Select(k => k[..k.IndexOf('.')]).ToHashSet();
        sb.AppendLine("### SECTOR.DAT (sections we read)").AppendLine();
        sb.AppendLine("| Key | Zones | Converter | Values |").AppendLine("|---|---:|---|---|");
        foreach (var (key, k) in _keys.Where(kv => readSections.Contains(kv.Key[..kv.Key.IndexOf('.')]))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            bool used = UsedSectorKeys.Contains(key);
            string check = used && Degenerate(k.Values).Any() ? " **check:** " + string.Join(", ", Degenerate(k.Values)) : "";
            sb.AppendLine($"| {Md(key)} | {k.Zones.Count} | {(used ? "reads" : "**ignored**")}{check} | {Distribution(k.Values)} |");
        }
        sb.AppendLine();
    }

    private static string Md(string s) => s.Replace("|", "\\|").Replace("`", "'");
}
