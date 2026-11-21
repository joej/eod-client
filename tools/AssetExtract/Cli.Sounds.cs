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
    //   DIR/zones.dat               zones/zones.mpk's zones.dat (each region's entry_music);
    //   DIR/soundsets.csv           each model's cc_creatures row and each weapon's cc_weapons row (SoundSets).
    // With --classic EARLY (an install from before the cc_ tables, with sounds/sndfiles.csv) and --current RETAIL (a
    // retail install), the two zone sound sets the client lets the player choose (ZoneSoundSets):
    //   DIR/classic/sndfiles.csv, *.wav   the early table and its files (from EARLY, or this install by the same name);
    //   DIR/classic/zones/zoneNNN.dat     EARLY's sounds.dat, else this install's (written for the same numbers);
    //   DIR/current/zones/zoneNNN.dat     RETAIL's sounds.dat, else this install's.
    // A zone whose classic file plays nothing (zone277 here: four numbers no table has) takes its current one.
    private static int SoundsCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--out", "--classic", "--current"]) is not { } o || o["--out"] is not string outDir)
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

        if (FindInstallFile(client, "gamedata.mpk") is string gamedata)
        {
            using MpkArchive g = MpkArchive.Open(gamedata);
            var sets = DaocAssets.Sound.SoundSets.FromTables(System.Text.Encoding.Latin1.GetString(g.Extract("monsters.csv")),
                System.Text.Encoding.Latin1.GetString(g.Extract("objects.csv")));
            File.WriteAllText(Path.Combine(outDir, "soundsets.csv"), sets.ToCsv());
            Console.WriteLine($"sounds: {sets.Creatures} models with a creature sound set, {sets.Weapons} with a weapon sound set");
        }
        if (FindInstallFile(client, "zones/zones.mpk") is string zonesMpk)
        {
            using MpkArchive mpk = MpkArchive.Open(zonesMpk);
            File.WriteAllBytes(Path.Combine(outDir, "zones.dat"), mpk.Extract("zones.dat"));
        }
        Console.WriteLine($"sounds: {files} files, {zones} zones' sounds.dat → {outDir}");
        if (o["--current"] is string current && ZoneSoundsFrom(current, Path.Combine(outDir, "current", "zones"), zonesOut) is int cur)
            Console.WriteLine($"sounds: current set, {cur} zones from {current}");
        if (o["--classic"] is string early && ClassicSounds(early, client, outDir, zonesOut) is int cls)
            Console.WriteLine($"sounds: classic set, {cls} zones from {early}");
        return SoundCheck(outDir);
    }

    // Each zone's sounds.dat from an install into DIR as zoneNNN.dat; zones it doesn't have come from the library's own
    // (fallbackDir). Returns how many came from the install, or null when it has none.
    private static int? ZoneSoundsFrom(string install, string dir, string fallbackDir)
    {
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (string root in new[] { "zones", "Tutorial/zones", "frontiers/zones", "phousing/zones" })
        {
            if (FindInstallDir(install, root) is not string zonesDir)
                continue;
            foreach (string zoneDir in Directory.EnumerateDirectories(zonesDir))
            {
                string name = Path.GetFileName(zoneDir).ToLowerInvariant();
                if (!name.StartsWith("zone") || !int.TryParse(name[4..], out int zone))
                    continue;
                string? dat = Directory.EnumerateFiles(zoneDir)
                    .FirstOrDefault(p => Path.GetFileName(p).Equals("sounds.dat", StringComparison.OrdinalIgnoreCase));
                if (dat is null)
                    continue;
                DaocAssets.InstallFiles.Copy(dat, Path.Combine(dir, $"zone{zone:000}.dat"), overwrite: true);
                n++;
            }
        }
        if (n == 0)
        {
            Console.Error.WriteLine($"sounds: no zones' sounds.dat in {install}");
            return null;
        }
        foreach (string own in Directory.EnumerateFiles(fallbackDir, "zone*.dat"))
            if (!File.Exists(Path.Combine(dir, Path.GetFileName(own))))
                File.Copy(own, Path.Combine(dir, Path.GetFileName(own)));
        return n;
    }

    private static int? ClassicSounds(string early, string client, string outDir, string zonesOut)
    {
        if (FindInstallFile(early, "sounds/sndfiles.csv") is not string table)
        {
            Console.Error.WriteLine($"sounds: no sounds/sndfiles.csv in {early} (the classic set needs an install from before the cc_ tables)");
            return null;
        }
        string dir = Path.Combine(outDir, DaocAssets.Sound.ClassicSoundFiles.Folder);
        CopySoundFolder(Path.GetDirectoryName(table)!, dir);
        // Files the early table names that its own folder lacks, from this install by the same name.
        var classic = DaocAssets.Sound.ClassicSoundFiles.Parse(File.ReadAllText(table, System.Text.Encoding.Latin1));
        string? ownSounds = FindInstallDir(client, "sounds");
        foreach (var row in classic.Rows)
        {
            string target = Path.Combine(outDir, row.File);
            if (File.Exists(target) || ownSounds is null)
                continue;
            string? found = Directory.EnumerateFiles(ownSounds)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(Path.GetFileName(row.File), StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                DaocAssets.InstallFiles.Copy(found, target, overwrite: true);
        }
        string zones = Path.Combine(dir, "zones");
        if (ZoneSoundsFrom(early, zones, zonesOut) is not int n)
            return null;
        // A zone whose classic file plays nothing takes its current one, if there is a current set.
        var planner = new DaocAssets.Sound.SoundPlanner(DaocAssets.Sound.SoundTables.Load(outDir));
        string currentZones = Path.Combine(outDir, "current", "zones");
        foreach (string f in Directory.EnumerateFiles(zones, "zone*.dat"))
        {
            var regions = DaocAssets.Sound.ZoneSounds.Parse(File.ReadAllText(f, System.Text.Encoding.Latin1));
            (int lines, int playing) = DaocAssets.Sound.ZoneSoundSets.Count(regions, planner, classic, classicFirst: true);
            string replacement = Path.Combine(currentZones, Path.GetFileName(f));
            if (lines > 0 && playing == 0 && File.Exists(replacement))
                File.Copy(replacement, f, overwrite: true);
        }
        File.WriteAllText(Path.Combine(dir, "sndfiles.csv"), File.ReadAllText(table, System.Text.Encoding.Latin1), System.Text.Encoding.Latin1);
        return n;
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
        // The two zone sets: every line should play; the ones that don't are listed.
        int setsDropped = 0;
        foreach (string set in new[] { DaocAssets.Sound.ZoneSoundSets.Classic, DaocAssets.Sound.ZoneSoundSets.Current })
        {
            string zonesDir = Path.Combine(dir, set, "zones");
            if (!Directory.Exists(zonesDir))
                continue;
            string sndfiles = Path.Combine(dir, DaocAssets.Sound.ClassicSoundFiles.Folder, "sndfiles.csv");
            var classic = File.Exists(sndfiles)
                ? DaocAssets.Sound.ClassicSoundFiles.Parse(File.ReadAllText(sndfiles, System.Text.Encoding.Latin1)) : null;
            bool classicFirst = set == DaocAssets.Sound.ZoneSoundSets.Classic;
            int lines = 0, playing = 0;
            var dropped = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var noFile = new SortedSet<string>();
            var checkedFiles = new HashSet<string>();
            foreach (string f in Directory.EnumerateFiles(zonesDir, "zone*.dat"))
                foreach (var line in DaocAssets.Sound.ZoneSounds.Parse(File.ReadAllText(f, System.Text.Encoding.Latin1)).SelectMany(r => r.Sounds))
                {
                    lines++;
                    var z = DaocAssets.Sound.ZoneSoundSets.Resolve(line.Sound, planner, classic, classicFirst);
                    if (z.Plays)
                        playing++;
                    else
                        (dropped.TryGetValue(line.Sound, out var at) ? at : dropped[line.Sound] = []).Add(Path.GetFileNameWithoutExtension(f));
                    if (z.Classic is { } row && checkedFiles.Add(row.File))
                    {
                        string path = Path.Combine(dir, row.File);
                        if (!File.Exists(path))
                            noFile.Add(row.File);
                        else
                            try { DaocAssets.Sound.WavFile.Decode(File.ReadAllBytes(path)); }
                            catch (InvalidDataException e) { noFile.Add($"{row.File} ({e.Message})"); }
                    }
                }
            setsDropped += dropped.Count + noFile.Count;
            report.AppendLine().AppendLine($"## Zone sound set: {set}").AppendLine()
                .AppendLine($"{playing} of {lines} lines play; {lines - playing} are left out (they resolve to nothing). " +
                            $"{noFile.Count} classic files missing or not decoding.").AppendLine();
            foreach ((string name, var at) in dropped)
                report.AppendLine($"- left out `{name}`: {string.Join(", ", at.Take(8))}{(at.Count > 8 ? $" and {at.Count - 8} more" : "")}");
            foreach (string f in noFile)
                report.AppendLine($"- classic file missing or bad: `{f}`");
            Console.WriteLine($"soundcheck: {set} set, {playing} of {lines} zone lines play, {dropped.Count} names left out, {noFile.Count} classic files missing or bad");
        }
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
