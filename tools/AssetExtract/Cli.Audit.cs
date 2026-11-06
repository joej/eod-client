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
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not { } outPath || !Directory.Exists(root))
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

        File.WriteAllText(outPath, audit.ToMarkdown("NIF coverage audit"));
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
}
