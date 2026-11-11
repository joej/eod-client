// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk sky: a zone's sky and fog.

internal static partial class Cli
{
    // sky <dat<NNN>.mpk> --out DIR [--name albion] [--skympk zones/sky/sky.mpk] [--index index.json
    // --library LIBDIR] (name and sky.mpk default to the client's choice via zones.dat): the zone's sky as DIR/sky.csv ("key,value"; colours "r g b a" in 0..1): every
    // weather condition and time of day from sky_<name>.dat (canopy and cloud gradients, ambient and sun
    // light, distance-fog colour), its cloud layers and sun, and SECTOR.DAT's [fog] clip and amount.
    // The zones/ folder holding zones.mpk: this one, or the install's (two levels up from Tutorial/zones).
    private static string InstallZonesDir(string zonesDir)
    {
        for (string? dir = zonesDir; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "zones.mpk")))
                return dir;
            string? sibling = Directory.Exists(dir) ? Directory.EnumerateDirectories(dir)
                .FirstOrDefault(d => Path.GetFileName(d).Equals("zones", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(d, "zones.mpk"))) : null;
            if (sibling is not null)
                return sibling;
        }
        return zonesDir;
    }

    private static int SkyCmd(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        string datMpk = args[1];
        if (Options.Parse(args, 2, ["--out", "--skympk", "--name", "--index", "--library"]) is not { } o)
            return Usage();
        string? skyMpk = o["--skympk"], name = o["--name"], outDir = o["--out"], indexPath = o["--index"], libDir = o["--library"];
        // Default to the client's own choice: zones.dat beside the zone folders, keyed by the zone id
        // taken from the archive's folder (zones/zoneNNN/datNNN.mpk).
        // A tutorial zone (Tutorial/zones/zoneNNN) uses the install's zones/ for zones.mpk and the skies.
        string zonesDir = InstallZonesDir(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(datMpk))!)!);
        skyMpk ??= Path.Combine(zonesDir, "sky", "sky.mpk");
        if (name is null)
        {
            string zoneFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(datMpk))!);
            string zonesMpk = Path.Combine(zonesDir, "zones.mpk");
            if (!int.TryParse(zoneFolder.AsSpan(4), out int zoneId) || !File.Exists(zonesMpk))
            {
                Console.Error.WriteLine("sky: pass --name, or point at zones/zoneNNN/datNNN.mpk inside an install.");
                return 2;
            }
            using MpkArchive zm = MpkArchive.Open(zonesMpk);
            var zonesDat = IniFile.Parse(System.Text.Encoding.Latin1.GetString(zm.Extract("zones.dat")));
            name = Path.GetFileNameWithoutExtension(ZoneSky.SkyFile(zonesDat, zoneId)).Replace("sky_", "", StringComparison.OrdinalIgnoreCase);
        }
        if (outDir is null || (indexPath is null) != (libDir is null))
            return Usage();

        SkyDefinition sky;
        using (MpkArchive skies = MpkArchive.Open(skyMpk))
        {
            MpkEntry? e = skies.Entries.FirstOrDefault(x => x.Name.Equals($"sky_{name}.dat", StringComparison.OrdinalIgnoreCase));
            if (e is null)
            {
                Console.Error.WriteLine($"sky: no sky_{name}.dat in {skyMpk}. Available: " +
                    string.Join(", ", skies.Entries.Select(x => Path.GetFileNameWithoutExtension(x.Name).Replace("sky_", ""))));
                return 1;
            }
            sky = SkyDefinition.Parse(System.Text.Encoding.Latin1.GetString(skies.Extract(e)));
        }
        IniFile sector;
        // SECTOR.DAT as the client reads it: the csv archive's copy first (DaocAssets.Zone.ZoneArchive).
        using (var zone = DaocAssets.Zone.ZoneArchive.Open(datMpk))
            sector = IniFile.Parse(zone.Text("SECTOR.DAT") ?? "");

        static string C(System.Numerics.Vector4 c) => FormattableString.Invariant($"{c.X:0.###} {c.Y:0.###} {c.Z:0.###} {c.W:0.###}");
        static string C3(System.Numerics.Vector3 c) => FormattableString.Invariant($"{c.X:0.###} {c.Y:0.###} {c.Z:0.###} 1");
        var lines = new List<string> { "key,value", $"sky,{name}" };
        // The client's defaults when a key is missing: clip 640, amount 0 (W17 note).
        lines.Add(FormattableString.Invariant($"fog.clip,{sector.Float("fog", "clip") ?? 640}"));
        lines.Add(FormattableString.Invariant($"fog.amount,{sector.Float("fog", "amount") ?? 0}"));
        if (sector.Color("fog", "red") is null && sector.Float("fog", "red") is float fr)
            lines.Add(FormattableString.Invariant($"fog.sector_color,{fr / 255:0.###} {(sector.Float("fog", "green") ?? 0) / 255:0.###} {(sector.Float("fog", "blue") ?? 0) / 255:0.###} 1"));
        int n = 0;
        foreach (CloudLayer l in sky.CloudLayers)
            lines.Add(FormattableString.Invariant($"cloud{n++},{l.Texture} {l.Tile} {l.Speed}"));
        lines.Add(FormattableString.Invariant($"sun,{sky.SunTexture} {sky.SunScale} {sky.EclipticAngle}"));
        lines.Add($"sun.noon_color,{C(sky.SunDiskNoon)}");
        foreach (string cond in SkyDefinition.Conditions)
            foreach (string time in SkyDefinition.Times)
            {
                SkyState st = sky.State(cond, time);
                string k = $"{cond}.{time}";
                foreach (var (part, g) in new[] { ("canopy", st.Canopy), ("clouds", st.Clouds) })
                {
                    lines.Add($"{k}.{part}.zenith,{C(g.Zenith)}");
                    lines.Add($"{k}.{part}.nadir,{C(g.Nadir)}");
                    for (int i = 0; i < 4; i++)
                    {
                        lines.Add($"{k}.{part}.east{i + 1},{C(g.East[i])}");
                        lines.Add($"{k}.{part}.west{i + 1},{C(g.West[i])}");
                    }
                }
                lines.Add($"{k}.ambient,{C3(st.AmbientLight)}");
                lines.Add(FormattableString.Invariant($"{k}.ambient_amount,{st.AmbientAmount}"));
                lines.Add($"{k}.sun,{C3(st.SunLight)}");
                lines.Add(FormattableString.Invariant($"{k}.sun_amount,{st.SunAmount}"));
                lines.Add($"{k}.fog,{C3(st.DistanceFog)}");
            }
        Directory.CreateDirectory(outDir);
        string csv = Path.Combine(outDir, "sky.csv");
        File.WriteAllLines(csv, lines);
        Console.WriteLine($"Sky: sky_{name}.dat, {sky.CloudLayers.Count} cloud layer(s), fog clip {sector.Float("fog", "clip")} " +
            $"amount {sector.Float("fog", "amount")} -> {Path.GetFullPath(csv)}");

        if (indexPath is not null && libDir is not null)
        {
            var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir);
            foreach (string stem in sky.CloudLayers.Select(l => l.Texture).Append(sky.SunTexture).Where(t => t.Length > 0).Distinct())
            {
                bool got = new[] { ".dds", ".tga", ".bmp" }.Any(ext => library.GatherTexture(stem + ext) is not null);
                Console.WriteLine($"  texture {stem}: {(got ? "gathered" : "MISSING")}");
            }
        }
        return 0;
    }
}
