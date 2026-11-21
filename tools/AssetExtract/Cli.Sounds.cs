// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk sounds: the install's sounds and their tables into the library (roadmap M12, docs/sound.md). Nothing is converted:
// the client decodes the .wav files (DaocAssets.Sound.WavFile) and plays the .mp3 files as they are.

internal static partial class Cli
{
    // sounds <client-dir> --out DIR:
    //   DIR/*.wav, *.mp3, *.csv     sounds/ (the cc_ tables and every file they name), names lower-cased;
    //   DIR/tutorial/, DIR/frontiers/  Tutorial/sounds and frontiers/sounds, where the client also looks for a file;
    //   DIR/zones/zoneNNN.dat       each zone's sounds.dat, from zones/, Tutorial/zones, frontiers/zones, phousing/zones;
    //   DIR/zones.dat               zones/zones.mpk's zones.dat (each region's entry_music).
    private static int SoundsCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not string outDir)
            return Usage();
        if (FindInstallFile(client, "sounds/cc_sounds.csv") is not string table)
        {
            Console.Error.WriteLine($"sounds: no sounds/cc_sounds.csv in {client}");
            return 1;
        }
        Directory.CreateDirectory(outDir);
        int files = CopySoundFolder(Path.GetDirectoryName(table)!, outDir);
        foreach ((string from, string to) in new[] { ("Tutorial/sounds", "tutorial"), ("frontiers/sounds", "frontiers") })
            if (FindInstallDir(client, from) is string dir)
                files += CopySoundFolder(dir, Path.Combine(outDir, to));

        string zonesOut = Path.Combine(outDir, "zones");
        Directory.CreateDirectory(zonesOut);
        int zones = 0;
        foreach (string root in new[] { "zones", "Tutorial/zones", "frontiers/zones", "phousing/zones" })
        {
            if (FindInstallDir(client, root) is not string dir)
                continue;
            foreach (string zoneDir in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(zoneDir).ToLowerInvariant();
                if (!name.StartsWith("zone") || !int.TryParse(name[4..], out int zone))
                    continue;
                string? dat = Directory.EnumerateFiles(zoneDir)
                    .FirstOrDefault(p => Path.GetFileName(p).Equals("sounds.dat", StringComparison.OrdinalIgnoreCase));
                if (dat is null)
                    continue;
                DaocAssets.InstallFiles.Copy(dat, Path.Combine(zonesOut, $"zone{zone:000}.dat"), overwrite: true);
                zones++;
            }
        }

        if (FindInstallFile(client, "zones/zones.mpk") is string zonesMpk)
        {
            using MpkArchive mpk = MpkArchive.Open(zonesMpk);
            File.WriteAllBytes(Path.Combine(outDir, "zones.dat"), mpk.Extract("zones.dat"));
        }
        Console.WriteLine($"sounds: {files} files, {zones} zones' sounds.dat → {outDir}");
        return SoundCheck(outDir);
    }

    // The library's sounds checked by result (DIR/soundcheck.md): every file a cc_sounds row or cc_mp3s names is there
    // and decodes, and every name the sheets, structures, zone sound regions and zones.dat use resolves. Exit 1 on a
    // file that won't decode; missing names are the install's own (listed, not fatal).
    private static int SoundCheck(string dir)
    {
        var t = DaocAssets.Sound.SoundTables.Load(dir);
        var planner = new DaocAssets.Sound.SoundPlanner(t);
        string[] folders = [dir, Path.Combine(dir, "tutorial"), Path.Combine(dir, "frontiers")];
        string? Find(string file) => folders.Select(f => Path.Combine(f, file.ToLowerInvariant())).FirstOrDefault(File.Exists);

        var missingFiles = new SortedSet<string>();
        var bad = new List<string>();
        int decoded = 0;
        foreach (var row in t.Sounds.Values)
        {
            if (Find(row.File) is not string path)
            {
                missingFiles.Add($"{row.Name} ({row.File})");
                continue;
            }
            try
            {
                DaocAssets.Sound.WavFile.Decode(File.ReadAllBytes(path));
                decoded++;
            }
            catch (InvalidDataException e)
            {
                bad.Add($"{row.File}: {e.Message}");
            }
        }

        var unknown = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Use(string? name, string where)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("s_null", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("s_silence", StringComparison.OrdinalIgnoreCase) || planner.Knows(name))
                return;
            if (name.StartsWith("mp3_", StringComparison.OrdinalIgnoreCase))
            {
                if (t.Mp3File(name) is string mp3 && Find(mp3) is not null)
                    return;
            }
            if (!unknown.TryGetValue(name, out var at))
                unknown[name] = at = [];
            at.Add(where);
        }
        foreach ((string sheetName, var sheet) in new[] { ("creatures", t.Creatures), ("weapons", t.Weapons), ("spells", t.Spells),
                     ("doors", t.Doors), ("events", t.Events), ("interface", t.Interface), ("music", t.Music) })
            foreach (int row in sheet.RowIds)
                for (int c = 0; c < 26; c++)
                    if (sheet.Get(row, c) is string n && (n.Contains('_') && !n.Contains(' ')))
                        Use(n, $"cc_{sheetName} row {row}");
        int zoneLines = 0;
        foreach (string zoneFile in Directory.EnumerateFiles(Path.Combine(dir, "zones"), "*.dat"))
            foreach (var region in DaocAssets.Sound.ZoneSounds.Parse(File.ReadAllText(zoneFile, System.Text.Encoding.Latin1)))
                foreach (var line in region.Sounds)
                {
                    zoneLines++;
                    Use(line.Sound, Path.GetFileNameWithoutExtension(zoneFile));
                }
        if (File.Exists(Path.Combine(dir, "zones.dat")))
        {
            var zonesDat = DaocAssets.Zone.IniFile.Parse(File.ReadAllText(Path.Combine(dir, "zones.dat"), System.Text.Encoding.Latin1));
            foreach ((string section, var keys) in zonesDat.Sections)
                if (keys.TryGetValue("entry_music", out string? m) && int.TryParse(m.Trim(), out int id) && id != 0)
                    Use(t.Mp3Name(id) ?? $"mp3 id {id}", $"zones.dat [{section}]");
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine("# Sound check").AppendLine()
            .AppendLine($"{t.Sounds.Count} cc_sounds rows: {decoded} files decode, {missingFiles.Count} files missing, {bad.Count} fail to decode.")
            .AppendLine($"{zoneLines} zone sound lines in {Directory.EnumerateFiles(Path.Combine(dir, "zones"), "*.dat").Count()} zones.")
            .AppendLine($"{unknown.Count} names used but not defined (the install's own tables).").AppendLine();
        report.AppendLine("## Files that fail to decode").AppendLine();
        foreach (string b in bad)
            report.AppendLine($"- {b}");
        report.AppendLine().AppendLine("## Rows whose file is missing").AppendLine();
        foreach (string m in missingFiles)
            report.AppendLine($"- {m}");
        report.AppendLine().AppendLine("## Names used but not defined").AppendLine();
        foreach ((string name, var at) in unknown)
            report.AppendLine($"- `{name}`: {string.Join(", ", at.Take(8))}{(at.Count > 8 ? $" and {at.Count - 8} more" : "")}");
        File.WriteAllText(Path.Combine(dir, "soundcheck.md"), report.ToString());
        Console.WriteLine($"soundcheck: {decoded} decode, {missingFiles.Count} files missing, {bad.Count} bad, {unknown.Count} unknown names → {Path.Combine(dir, "soundcheck.md")}");
        return bad.Count == 0 ? 0 : 1;
    }

    private static int CopySoundFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        int n = 0;
        foreach (string file in Directory.EnumerateFiles(from))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".wav" or ".mp3" or ".csv"))
                continue;
            DaocAssets.InstallFiles.Copy(file, Path.Combine(to, Path.GetFileName(file).ToLowerInvariant()), overwrite: true);
            n++;
        }
        return n;
    }

    private static string? FindInstallDir(string client, string relative)
    {
        string dir = client;
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string? match = Directory.EnumerateDirectories(dir)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            dir = match;
        }
        return dir;
    }
}
