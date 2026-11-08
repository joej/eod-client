// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk loading: the region loading screens (gamedata.mpk loading.dat) into the library.

using DaocAssets.Zone;

internal static partial class Cli
{
    // loading <client-dir> --out DIR: DIR/loading.csv (region, instance, widestyle, name, picture stems), each
    // picture as DIR/<archive stem>_<member>.<ext> (the 1024 x 256 banners in data/loading/*.mpk), and the
    // rest of the screen as the install has it: tips.txt (data/loading/tips.dat), loadbar1.tga and
    // loadbar2.tga (empty and filled bar) and the tip font (ui/fonts/MinionPro-Regular.ttf).
    private static int LoadingCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not string outDir)
            return Usage();
        Directory.CreateDirectory(outDir);

        string gamedata = FindInstallFile(client, "gamedata.mpk") ?? throw new FileNotFoundException("gamedata.mpk", client);
        IReadOnlyList<LoadingRegion> regions;
        using (MpkArchive g = MpkArchive.Open(gamedata))
            regions = LoadingScreens.Parse(System.Text.Encoding.Latin1.GetString(g.Extract("loading.dat")));

        var present = new HashSet<string>();
        int missingArchives = 0, missingPictures = 0;
        foreach (IGrouping<string, LoadingPicture> archive in regions.SelectMany(r => r.Pictures).Distinct().GroupBy(p => p.Archive, StringComparer.OrdinalIgnoreCase))
        {
            if (FindInstallFile(client, archive.Key) is not string path)
            {
                missingArchives++;
                string used = string.Join(", ", regions.Where(r => r.Pictures.Any(p => p.Archive.Equals(archive.Key, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => $"{r.Region} {r.Name}"));
                Console.Error.WriteLine($"loading: {archive.Key} not in the install (used by region {used}: the plain screen there)");
                continue;
            }
            using MpkArchive mpk = MpkArchive.Open(path);
            foreach (LoadingPicture p in archive)
            {
                MpkEntry? entry = mpk.Entries.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e.Name).Equals(p.Member, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    missingPictures++;
                    continue;
                }
                File.WriteAllBytes(Path.Combine(outDir, p.Stem + Path.GetExtension(entry.Name).ToLowerInvariant()), mpk.Extract(entry));
                present.Add(p.Stem);
            }
        }
        File.WriteAllText(Path.Combine(outDir, "loading.csv"), LoadingScreens.ToCsv(regions, present));

        int copied = 0;
        foreach ((string from, string to) in new[] { ("data/loading/tips.dat", "tips.txt"), ("data/loadbar1.tga", "loadbar1.tga"),
                     ("data/loadbar2.tga", "loadbar2.tga"), ("ui/fonts/minionpro-regular.ttf", "minionpro-regular.ttf") })
            if (FindInstallFile(client, from) is string file)
            {
                File.Copy(file, Path.Combine(outDir, to), overwrite: true);
                copied++;
            }
            else
                Console.Error.WriteLine($"loading: {from} not in the install");

        Console.WriteLine($"loading: {regions.Count} regions, {present.Count} pictures ({missingPictures} missing, " +
            $"{missingArchives} archives not in the install), {copied}/4 screen files");
        return 0;
    }

    // An install file by its install-relative path, matching each part's case loosely (the client's own
    // references disagree with the disk: data/loadbar1.tga is LOADBAR1.tga).
    private static string? FindInstallFile(string client, string relative)
    {
        string dir = client;
        string[] parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            string? match = (last ? Directory.EnumerateFiles(dir) : Directory.EnumerateDirectories(dir))
                .FirstOrDefault(p => Path.GetFileName(p).Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            dir = match;
        }
        return dir;
    }
}
