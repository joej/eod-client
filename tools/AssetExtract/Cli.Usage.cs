// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk usage: which install files (and members of its .mpk/.npk archives) a full conversion never opens. A run with
// DAOC_FILE_LOG set (InstallFiles) records what the converter opened, one log per process; this lists the whole install against it and groups
// what was never opened by folder and name pattern (digit runs as #), each group either explained in the known list
// (tools/usage-known.txt: "pattern<TAB>why", the pattern as the report prints it, * for any run of characters) or left
// for review. `make usage` runs both steps. The report describes the owner's client files: write it under private/.

using System.Text;
using System.Text.RegularExpressions;
using DaocAssets.Mpk;

internal static partial class Cli
{
    private static int UsageCmd(string[] args)
    {
        if (args.Length < 2 || Options.Parse(args, 2, ["--log", "--known", "--out"]) is not { } o || o["--log"] is not string logPath)
            return Usage();
        string client = Path.GetFullPath(args[1]).TrimEnd('/');
        var openedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The run's logs: one per process (InstallFiles), "<log>.<process id>".
        string logDir = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        foreach (string line in Directory.EnumerateFiles(logDir, Path.GetFileName(logPath) + ".*").SelectMany(File.ReadLines))
        {
            string[] f = line.TrimStart('\uFEFF').Split('\t');
            if (f.Length < 2)
                continue;
            openedFiles.Add(f[0]);
            if (f[1].Length > 0)
                openedMembers.Add(f[0] + "\t" + f[1]);
        }
        var known = new List<(Regex Pattern, string Pattern0, string Why)>();
        if (o["--known"] is string knownPath && File.Exists(knownPath))
            foreach (string line in File.ReadLines(knownPath))
                if (line.Length > 0 && !line.StartsWith('#') && line.Split('\t', 2) is [string p, string why])
                    known.Add((new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase), p, why.Trim()));

        // Every unit the install holds: a loose file, or an archive member (an archive itself counts as its members).
        int files = 0, members = 0, usedFiles = 0, usedMembers = 0;
        var groups = new Dictionary<string, (int Count, long Bytes, List<string> Examples)>(StringComparer.OrdinalIgnoreCase);
        var opened = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Unused(string group, string example, long bytes)
        {
            (int c, long b, List<string> ex) = groups.TryGetValue(group, out var g) ? g : (0, 0L, new List<string>());
            if (ex.Count < 3)
                ex.Add(example);
            groups[group] = (c + 1, b + bytes, ex);
        }
        void Used(string group) => opened[group] = opened.GetValueOrDefault(group) + 1;
        foreach (string path in Directory.EnumerateFiles(client, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            string rel = path[(client.Length + 1)..];
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".mpk" or ".npk")
            {
                MpkArchive mpk;
                try { mpk = MpkArchive.Open(path); }
                catch (Exception e) when (e is MpkFormatException or IOException or InvalidDataException)
                {
                    files++;
                    if (openedFiles.Contains(path)) { usedFiles++; Used(Archive(rel) + " (unreadable archive)"); }
                    else Unused(Archive(rel) + " (unreadable archive)", rel, new FileInfo(path).Length);
                    continue;
                }
                using (mpk)
                    foreach (MpkEntry e in mpk.Entries)
                    {
                        members++;
                        if (openedMembers.Contains(path + "\t" + e.Name)) { usedMembers++; Used(Archive(rel) + " : " + Group(e.Name)); continue; }
                        Unused(Archive(rel) + " : " + Group(e.Name), rel + " : " + e.Name, e.UncompressedSize);
                    }
                continue;
            }
            files++;
            if (openedFiles.Contains(path)) { usedFiles++; Used(Group(rel)); continue; }
            Unused(Group(rel), rel, new FileInfo(path).Length);
        }

        var sb = new StringBuilder();
        // Kinds of file never opened at all first (what the patch files were), then kinds only partly opened.
        var rows = groups.OrderBy(g => opened.ContainsKey(g.Key)).ThenByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        // A kind of file nothing opens (what the patch files were) must be explained; a kind partly opened is listed for
        // information: its other files belong to models or zones not converted, which the zone audits cover.
        var partly = rows.Where(g => opened.ContainsKey(g.Key) && !known.Any(k => k.Pattern.IsMatch(g.Key))).ToList();
        var review = rows.Where(g => !opened.ContainsKey(g.Key) && !known.Any(k => k.Pattern.IsMatch(g.Key))).ToList();
        string summary = $"{files} loose files ({usedFiles} opened) and {members} archive members ({usedMembers} opened); " +
            $"{rows.Sum(g => g.Value.Count)} never opened, in {rows.Count} groups ({rows.Count(g => !opened.ContainsKey(g.Key))} of which " +
            $"nothing at all is opened): {rows.Count - review.Count - partly.Count} explained, {partly.Count} partly opened, {review.Count} to review.";
        sb.AppendLine("# Install files the converter never opens").AppendLine().AppendLine(summary).AppendLine();
        sb.AppendLine("Groups: folder and name with digit runs as `#`; an archive's members after ` : `. Explain a group in the known " +
            "list (`pattern<TAB>why`, `*` for any characters) once it's understood: used, or why it doesn't matter.").AppendLine();
        void Table(string title, IEnumerable<KeyValuePair<string, (int Count, long Bytes, List<string> Examples)>> list, bool why)
        {
            sb.AppendLine($"## {title}").AppendLine();
            sb.AppendLine(why ? "| Group | Never opened | Opened | MB | Why | Examples |" : "| Group | Never opened | Opened | MB | Examples |");
            sb.AppendLine(why ? "|---|---:|---:|---:|---|---|" : "|---|---:|---:|---:|---|");
            foreach ((string key, (int count, long bytes, List<string> ex)) in list)
            {
                string reason = why ? " " + known.First(k => k.Pattern.IsMatch(key)).Why + " |" : "";
                sb.AppendLine($"| `{key}` | {count} | {opened.GetValueOrDefault(key)} | {bytes / 1048576.0:0.#} |{reason} {string.Join(", ", ex.Select(e => $"`{e}`"))} |");
            }
            sb.AppendLine();
        }
        Table("To review: never opened, not explained", review, why: false);
        Table("Partly opened (for information)", partly, why: false);
        Table("Explained", rows.Except(review).Except(partly), why: true);
        string report = sb.ToString();
        if (o["--out"] is string outPath)
            File.WriteAllText(outPath, report);
        else
            Console.Write(report);
        Console.WriteLine(summary);
        return 0;
    }

    // A file's group: its folder with digit runs as '#', then "*" and its extension, lower case; a patch file keeps its
    // form ("zones/zone026/nifs/cam23_p00.npk" -> "zones/zone#/nifs/*_p#.npk").
    private static string Group(string path)
    {
        path = path.Replace('\\', '/').ToLowerInvariant();
        int slash = path.LastIndexOf('/');
        string dir = slash < 0 ? "" : Regex.Replace(path[..(slash + 1)], "[0-9]+", "#");
        string name = path[(slash + 1)..], ext = Path.GetExtension(name);
        string stem = name[..^ext.Length];
        foreach (string special in SpecialNames)
            if (Regex.IsMatch(stem, special))
                return dir + Regex.Replace(Regex.Replace(stem, "[0-9]+", "#"), "^.*?(?=_p#$)", "*") + ext;
        return dir + "*" + ext;
    }

    // Name forms kept apart from the rest of their folder: model patch files.
    private static readonly string[] SpecialNames = ["_p[0-9]{2}$"];

    // An archive's part of a group: a model's own .npk as its folder's ("zones/nifs/*.npk"), a shared .mpk by its name
    // with digit runs as '#' ("zones/zone#/lod#.mpk"), which is what tells a zone's archives apart.
    private static string Archive(string path) =>
        path.EndsWith(".npk", StringComparison.OrdinalIgnoreCase) ? Group(path) : Pattern(path);

    private static string Pattern(string path) => Regex.Replace(path.Replace('\\', '/').ToLowerInvariant(), "[0-9]+", "#");
}
