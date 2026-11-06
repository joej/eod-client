// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk audit, worldaudit: coverage reports over the whole install (owner-run; reports go under private/).

internal static partial class Cli
{
    // audit <client-dir> --out FILE.md: surveys every NIF and .kfa animation (also NIF format) in the
    // install (loose, or inside .mpk/.npk archives) and writes the coverage report. Owner-run; the
    // report describes the owner's client files, so keep it out of the repo (private/).
    private static int AuditCmd(string[] args)
    {
        string root = args[1];
        if (Options.Parse(args, 2, ["--out", "--index"]) is not { } o || o["--out"] is not { } outPath || !Directory.Exists(root))
            return Usage();

        var audit = new NifAudit();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".nif" or ".kfa" or ".mpk" or ".npk")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        int done = 0;
        foreach (string path in files)
        {
            string rel = Path.GetRelativePath(root, path);
            string folder = rel.Contains(Path.DirectorySeparatorChar) ? rel[..rel.IndexOf(Path.DirectorySeparatorChar)] : "(root)";
            if (Path.GetExtension(path).ToLowerInvariant() is ".nif" or ".kfa")
            {
                Survey(audit, folder, rel, () => File.ReadAllBytes(path));
            }
            else
            {
                try
                {
                    using MpkArchive mpk = MpkArchive.Open(path);
                    foreach (MpkEntry e in mpk.Entries)
                        if (e.Name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                            || e.Name.EndsWith(".kfa", StringComparison.OrdinalIgnoreCase))
                            Survey(audit, folder, $"{rel}:{e.Name}", () => mpk.Extract(e));
                }
                catch (Exception e) when (e is MpkFormatException or IOException or InvalidDataException)
                {
                    Console.Error.WriteLine($"  archive skipped: {rel}: {e.Message}");
                }
            }
            if (++done % 500 == 0)
                Console.WriteLine($"  {done}/{files.Count} files, {audit.Files} NIFs");
        }

        string report = audit.ToMarkdown("NIF coverage audit");
        string gamedata = Path.Combine(root, "gamedata.mpk");
        report += o["--index"] is { } indexPath && File.Exists(gamedata)
            ? NameClashes(AssetIndex.Load(indexPath), gamedata)
            : "\n## Name clashes\n\nNot checked: run with `--index private/asset-index/index.json`.\n";
        File.WriteAllText(outPath, report);
        Console.WriteLine($"Surveyed {audit.Files} NIF(s) ({audit.Unreadable} unreadable, " +
            $"{audit.BoundaryMismatches} block-scan mismatches) -> {Path.GetFullPath(outPath)}");
        return 0;
    }

    // worldaudit <client-dir> --out FILE.md: inventories every zone folder's data (zone kinds, archive
    // members, SECTOR.DAT sections/keys, CSV headers). Owner-run; keep the report under private/.
    private static int WorldAuditCmd(string[] args)
    {
        string root = args[1];
        string zonesDir = Path.Combine(root, "zones");
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not { } outPath || !Directory.Exists(zonesDir))
            return Usage();

        var audit = new DaocAssets.Zone.ZoneAudit();
        var zoneDirs = Directory.GetDirectories(zonesDir)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^zone\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        foreach (string dir in zoneDirs)
        {
            var archives = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var open = new Dictionary<string, MpkArchive>(StringComparer.OrdinalIgnoreCase);
            var loose = new List<string>();
            foreach (string f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (Path.GetExtension(f).Equals(".mpk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        MpkArchive mpk = MpkArchive.Open(f);
                        open[name] = mpk;
                        archives[name] = mpk.Entries.Select(e => e.Name).ToList();
                    }
                    catch (Exception e) when (e is MpkFormatException or IOException or InvalidDataException)
                    {
                        Console.Error.WriteLine($"  archive skipped: {name}: {e.Message}");
                    }
                }
                else
                {
                    loose.Add(name);
                }
            }
            foreach (string sub in Directory.GetDirectories(dir))
                loose.Add(Path.GetFileName(sub) + "/");
            try
            {
                audit.AddZone(Path.GetFileName(dir), archives, (a, m) => open[a].Extract(m), loose);
            }
            finally
            {
                foreach (MpkArchive m in open.Values) m.Dispose();
            }
        }
        File.WriteAllText(outPath, audit.ToMarkdown("World data audit"));
        Console.WriteLine($"Surveyed {audit.Zones} zone folder(s) -> {Path.GetFullPath(outPath)}");
        return 0;
    }

    private static void Survey(NifAudit audit, string folder, string source, Func<byte[]> load)
    {
        try
        {
            audit.Add(folder, source, NifSurvey.Read(load()));
        }
        catch (Exception e) when (e is NifFormatException or IOException or InvalidDataException or OverflowException)
        {
            audit.AddUnreadable(folder, source, e.Message);
        }
    }

    // The install reuses file names across folders for different files, and the index keeps zone files first.
    // For each kind of asset the converter requests by name (from gamedata.mpk's tables), how many names have a
    // different file in that kind's own folder than the default: the converter reads those from its folder and
    // stores them apart (AssetLibrary.Scoped). A rise here is new clashes; "not in its folder" are names whose
    // only copies are elsewhere (read from there).
    private static string NameClashes(AssetIndex index, string gamedataPath)
    {
        using MpkArchive gd = MpkArchive.Open(gamedataPath);
        IEnumerable<string> Column(string file, int col, string suffix = "") =>
            System.Text.Encoding.Latin1.GetString(gd.Extract(file)).Split('\n')
                .Select(l => l.TrimEnd('\r').Split(','))
                .Where(c => c.Length > col && int.TryParse(c[0], out _) && c[col].Trim().Length > 0)
                .Select(c => c[col].Trim() + suffix).Distinct(StringComparer.OrdinalIgnoreCase);
        var kinds = new (string Kind, string[] Folders, IEnumerable<string> Names, bool Texture)[]
        {
            ("figure NIFs (monnifs.csv)", FigureFolders, Column("monnifs.csv", 2, ".nif"), false),
            ("creature skins (skins.csv)", FigureFolders, Column("skins.csv", 2), true),
            ("effect NIFs (moneffectnifs.csv)", EffectFolders, Column("moneffectnifs.csv", 2, ".nif"), false),
            ("item NIFs (items.csv)", ItemFolders, Column("items.csv", 2, ".nif"), false),
            ("item skins (pskins.csv)", ItemFolders, Column("pskins.csv", 2), true),
        };
        var sb = new System.Text.StringBuilder("\n## Name clashes\n\n| Asset kind | Names | Different file in its own folder | Same file as the default | Not in its folder | Not in the install |\n|---|---:|---:|---:|---:|---:|\n");
        var examples = new List<string>();
        foreach (var (kind, folders, names, texture) in kinds)
        {
            int total = 0, differ = 0, same = 0, elsewhere = 0, missing = 0;
            foreach (string n in names)
            {
                total++;
                byte[]? def = texture ? index.ReadTexture(n)?.Bytes : index.Read(n);
                if (def is null) { missing++; continue; }
                AssetLocation? mine = texture ? null : index.Locate(n, folders);
                bool inFolder = texture
                    ? new[] { ".dds", ".tga", ".bmp" }.Any(e => InFolders(index, index.Locate(Path.ChangeExtension(n, e), folders), folders))
                    : InFolders(index, mine, folders);
                if (!inFolder) { elsewhere++; continue; }
                byte[]? own = texture ? index.ReadTexture(n, folders)?.Bytes : index.Read(n, folders);
                if (own is null || own.AsSpan().SequenceEqual(def)) { same++; continue; }
                differ++;
                if (examples.Count < 20) examples.Add($"{n} ({kind.Split(' ')[0]} {kind.Split(' ')[1]})");
            }
            sb.Append($"| {kind} | {total} | {differ} | {same} | {elsewhere} | {missing} |\n");
        }
        if (examples.Count > 0)
            sb.Append("\nExamples of a different file: ").Append(string.Join(", ", examples)).Append(".\n");
        return sb.ToString();
    }

    private static bool InFolders(AssetIndex index, AssetLocation? loc, string[] folders) =>
        folders.Any(f => InFolder(index, loc, f));
}
