// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk: list and extract members of a DAoC MPK/NPK client archive. Owner-run tooling — point it
// at your own client install. The extracted files are the client's content, not ours; they
// never belong in this repo or a release (see CLAUDE.md, "Game client and assets").

using DaocAssets;
using DaocAssets.Library;
using DaocAssets.Mpk;
using DaocAssets.Nif;
using DaocAssets.Zone;

return Cli.Run(args);

internal static class Cli
{
    // --size W H: the zone's size in 8192-unit sectors from the zone table (default 8 x 8, which every
    // zone is). Terrain and water span this, not SECTOR.DAT's [sectorsize] (see ZoneTerrain.Read).
    private static (int X, int Y) ZoneSize = (8, 8);

    public static int Run(string[] args)
    {
        if (args.Length < 2)
            return Usage();

        string command = args[0];

        if (command == "textures")
            return Textures(args);

        if (command == "zone")
            return ZoneCmd(args);

        if (command == "index")
            return IndexCmd(args);

        if (command == "terrain")
            return TerrainCmd(args);

        if (command == "audit")
            return AuditCmd(args);

        if (command == "worldaudit")
            return WorldAuditCmd(args);

        if (command == "zonecheck")
            return ZoneCheckCmd(args);

        if (command == "water")
            return WaterCmd(args);

        if (command == "sky")
            return SkyCmd(args);

        string inputPath = args[1];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"No such file: {inputPath}");
            return 1;
        }

        if (command == "convert")
            return Convert(inputPath, args);

        if (command == "nifinfo")
            return NifInfo(inputPath);

        try
        {
            using var mpk = MpkArchive.Open(inputPath);
            return command switch
            {
                "list" => List(mpk),
                "extract" => Extract(mpk, args),
                _ => Usage(),
            };
        }
        catch (MpkFormatException e)
        {
            Console.Error.WriteLine($"{inputPath}: {e.Message}");
            return 1;
        }
    }

    // nifinfo <model.nif>: what the converter reads from one model: meshes (shape name, texture,
    // skinned or not, helper), the skeleton, and for skinned 10.1 files the bind-pose check.
    private static int NifInfo(string nifPath)
    {
        byte[] bytes = File.ReadAllBytes(nifPath);
        try
        {
            NifModel model = NifFile.Read(bytes);
            Console.WriteLine($"NIF {model.Version}: {model.Meshes.Count} mesh(es), {model.Joints.Count} joint(s)");
            foreach (NifMesh m in model.Meshes)
                Console.WriteLine($"  {m.ShapeName,-28} {m}" + (m.SkinJoints is { } sj ? $" ({sj.Length} bones)" : ""));
            for (int j = 0; j < model.Joints.Count; j++)
            {
                NifJoint joint = model.Joints[j];
                int depth = 0;
                for (int p = joint.Parent; p >= 0; p = model.Joints[p].Parent) depth++;
                Console.WriteLine($"  joint {j,3} {new string(' ', depth * 2)}{joint.Name}");
            }
            // Per skinned mesh: where the skin puts its vertices in the file's rest pose, against the
            // stored vertices. A part whose skinned box is far from its stored box binds wrongly.
            foreach (NifMesh m in model.Meshes.Where(m => m.SkinJoints is not null))
            {
                System.Numerics.Vector3[] skinned = model.Posed(m);
                string Box(System.Numerics.Vector3[] p) =>
                    $"({p.Min(q => q.X):F0},{p.Min(q => q.Y):F0},{p.Min(q => q.Z):F0})..({p.Max(q => q.X):F0},{p.Max(q => q.Y):F0},{p.Max(q => q.Z):F0})";
                Console.WriteLine($"  skin {m.ShapeName,-14} stored {Box(m.Vertices)} skinned {Box(skinned)}" +
                    $" unweighted {m.Skin!.Count(x => x.Length == 0)}");
            }
            if (Environment.GetEnvironmentVariable("NIFINFO_SKINS") == "1")
                foreach (string line in NifFile.SkinReport(bytes))
                    Console.WriteLine("  " + line);
            if (NifFile.BindPoseError(bytes) is { } err)
                Console.WriteLine($"Bind-pose check: max difference {err:G4} (near 0 = skeleton in bind pose)");
            return 0;
        }
        catch (NifFormatException e)
        {
            Console.Error.WriteLine($"{nifPath}: {e.Message}");
            return 1;
        }
    }

    private static int Convert(string nifPath, string[] args)
    {
        // convert <model.nif> [--out FILE.glb]
        string outPath = Path.ChangeExtension(nifPath, ".glb");
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else
                return Usage();
        }

        try
        {
            NifModel model = NifFile.Read(File.ReadAllBytes(nifPath));
            GltfExporter.SaveGlb(model, outPath);
            int verts = model.Meshes.Sum(m => m.Vertices.Length);
            int tris = model.Meshes.Sum(m => m.Triangles.Length);
            Console.WriteLine($"Converted NIF {model.Version}: {model.Meshes.Count} mesh(es), " +
                $"{verts} verts, {tris} tris -> {Path.GetFullPath(outPath)}");
            WarnDropped(File.ReadAllBytes(nifPath));
            return 0;
        }
        catch (NifFormatException e)
        {
            Console.Error.WriteLine($"{nifPath}: {e.Message}");
            return 1;
        }
    }

    // Says which of the file's block types the converter skips or only partly uses, so a gap shows
    // up at conversion time instead of as a wrong-looking model in the world.
    private static void WarnDropped(byte[] nif)
    {
        NifSurvey s = NifSurvey.Read(nif);
        foreach (var g in s.BlockTypes.GroupBy(t => t).OrderBy(g => g.Key))
        {
            NifCoverage c = NifSurvey.Coverage(g.Key, s.Version);
            if (c == NifCoverage.Handled)
                continue;
            string note = NifSurvey.CoverageNote(g.Key, s.Version);
            Console.WriteLine($"  {(c == NifCoverage.Unhandled ? "skipped" : "partial")} {g.Key} x{g.Count()}" +
                (note.Length > 0 ? $": {note}" : ""));
        }
    }

    // zonecheck <dat<NNN>.mpk> --zone DIR --library LIBDIR --offset X Y [--out FILE.md]: places every
    // object of a converted zone (DIR/zone.csv) as the client does and checks it against the terrain:
    // missing models, objects under the ground, sunk, floating, outside the zone, implausible sizes.
    // Exit code 1 when there are errors, so scripts can stop on them.
    private static int ZoneCheckCmd(string[] args)
    {
        if (args.Length < 2) return Usage();
        string datMpk = args[1];
        string? zoneDir = null, libDir = null, outPath = null, knownPath = null;
        int? offX = null, offY = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--zone" && i + 1 < args.Length) zoneDir = args[++i];
            else if (args[i] == "--known" && i + 1 < args.Length) knownPath = args[++i];
            else if (args[i] == "--library" && i + 1 < args.Length) libDir = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--size" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int zsx) && int.TryParse(args[i + 2], out int zsy))
            { ZoneSize = (zsx, zsy); i += 2; }
            else if (args[i] == "--offset" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int ox) && int.TryParse(args[i + 2], out int oy))
            { offX = ox; offY = oy; i += 2; }
            else return Usage();
        }
        if (zoneDir is null || libDir is null || offX is null || offY is null)
            return Usage();

        var terrain = DaocAssets.Zone.ZoneTerrain.Read(datMpk, ZoneSize.X, ZoneSize.Y);
        var placements = DaocAssets.Zone.ZoneCheck.ReadManifest(File.ReadAllText(Path.Combine(zoneDir, "zone.csv")));
        var cache = new Dictionary<string, DaocAssets.Zone.ModelBounds?>(StringComparer.OrdinalIgnoreCase);
        DaocAssets.Zone.ModelBounds? Bounds(string glb)
        {
            if (cache.TryGetValue(glb, out var b)) return b;
            string path = new[] { Path.Combine(libDir!, "overrides", glb), Path.Combine(libDir!, "models", glb), Path.Combine(libDir!, glb) }
                .FirstOrDefault(File.Exists) ?? "";
            return cache[glb] = path.Length == 0 ? null : DaocAssets.Zone.ZoneCheck.GlbBounds(path);
        }
        var report = DaocAssets.Zone.ZoneCheck.Check(placements,
            (x, y) => terrain.HeightAt(x - offX.Value, y - offY.Value), Bounds,
            offX.Value, offY.Value, terrain.RegionWidth, terrain.RegionHeight);
        if (knownPath is not null)
            report = DaocAssets.Zone.ZoneCheck.ApplyKnown(report, DaocAssets.Zone.ZoneCheck.ReadKnown(File.ReadAllText(knownPath)));
        Console.WriteLine($"Zone check: {report.Summary()}");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, report.ToMarkdown($"Zone check: {Path.GetFileName(Path.GetFullPath(zoneDir))}"));
            Console.WriteLine($"  report -> {Path.GetFullPath(outPath)}");
        }
        return report.Errors > 0 ? 1 : 0;
    }

    // audit <client-dir> --out FILE.md: surveys every NIF and .kfa animation (also NIF format) in the
    // install (loose, or inside .mpk/.npk archives) and writes the coverage report. Owner-run; the
    // report describes the owner's client files, so keep it out of the repo (private/).
    private static int AuditCmd(string[] args)
    {
        string root = args[1];
        string? outPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else
                return Usage();
        }
        if (outPath is null || !Directory.Exists(root))
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
        string? outPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else
                return Usage();
        }
        string zonesDir = Path.Combine(root, "zones");
        if (outPath is null || !Directory.Exists(zonesDir))
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

    private static int List(MpkArchive mpk)
    {
        Console.WriteLine($"{mpk.Entries.Count} member(s):");
        foreach (MpkEntry e in mpk.Entries)
            Console.WriteLine($"  {e.UncompressedSize,10}  {e.Name}");
        return 0;
    }

    private static int Extract(MpkArchive mpk, string[] args)
    {
        // extract <archive> [member] [--out DIR]
        string? member = null;
        string outDir = ".";
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outDir = args[++i];
            else if (!args[i].StartsWith('-'))
                member = args[i];
            else
                return Usage();
        }

        Directory.CreateDirectory(outDir);
        IEnumerable<MpkEntry> targets = member is null
            ? mpk.Entries
            : mpk.TryGetEntry(member, out MpkEntry one) ? [one] : [];
        if (member is not null && !targets.Any())
        {
            Console.Error.WriteLine($"No member '{member}' in archive.");
            return 1;
        }

        int n = 0;
        foreach (MpkEntry e in targets)
        {
            // Member names have no path separators (the directory is flat), but guard anyway so a
            // crafted name can't write outside outDir.
            string safe = Path.GetFileName(e.Name);
            File.WriteAllBytes(Path.Combine(outDir, safe), mpk.Extract(e));
            n++;
        }
        Console.WriteLine($"Extracted {n} file(s) to {Path.GetFullPath(outDir)}.");
        return 0;
    }

    private static int Textures(string[] args)
    {
        // textures <client-dir> <model.nif | dir-of-nifs> --out DIR
        if (args.Length < 3)
            return Usage();
        string clientDir = args[1];
        string modelArg = args[2];
        string? outDir = null;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outDir = args[++i];
            else
                return Usage();
        }
        if (outDir is null)
        {
            Console.Error.WriteLine("textures: --out DIR is required.");
            return 2;
        }

        string[] nifs =
            Directory.Exists(modelArg)
                ? Directory.GetFiles(modelArg, "*.nif", SearchOption.AllDirectories)
                : File.Exists(modelArg) ? [modelArg]
                : [];
        if (nifs.Length == 0)
        {
            Console.Error.WriteLine($"No .nif model found at: {modelArg}");
            return 1;
        }

        TextureResolver resolver;
        try
        {
            resolver = new TextureResolver(clientDir);
        }
        catch (DirectoryNotFoundException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        // Collect the unique texture names across all models first (one model's skin is often shared).
        var wanted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string nif in nifs)
        {
            try
            {
                foreach (string name in TextureResolver.TextureNames(nif))
                    wanted.Add(name);
            }
            catch (NifFormatException e)
            {
                Console.Error.WriteLine($"  (skipping {Path.GetFileName(nif)}: {e.Message})");
            }
        }

        if (wanted.Count == 0)
        {
            Console.WriteLine("No texture names referenced by the model(s); nothing to gather.");
            return 0;
        }

        int loose = 0, archived = 0, missing = 0;
        foreach (string name in wanted)
        {
            TextureResult r = resolver.Resolve(name, outDir);
            switch (r.Source)
            {
                case TextureSource.Loose: loose++; Console.WriteLine($"  loose    {r.Name}"); break;
                case TextureSource.Archive: archived++; Console.WriteLine($"  archive  {r.Name}  ({Path.GetFileName(r.FoundIn)})"); break;
                default: missing++; Console.WriteLine($"  MISSING  {r.Name}"); break;
            }
        }
        Console.WriteLine($"Gathered {loose + archived} of {wanted.Count} texture(s) " +
            $"({loose} loose, {archived} from archives, {missing} missing) -> {Path.GetFullPath(outDir)}");
        return missing == 0 ? 0 : 1;
    }

    private static int ZoneCmd(string[] args)
    {
        // zone <dat<NNN>.mpk> --index index.json --library LIBDIR --offset X Y --out DIR
        if (args.Length < 2)
            return Usage();
        string datMpk = args[1];
        int? offX = null, offY = null;
        string? outDir = null, indexPath = null, libDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i] == "--index" && i + 1 < args.Length) indexPath = args[++i];
            else if (args[i] == "--library" && i + 1 < args.Length) libDir = args[++i];
            else if (args[i] == "--size" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int zsx) && int.TryParse(args[i + 2], out int zsy))
            { ZoneSize = (zsx, zsy); i += 2; }
            else if (args[i] == "--offset" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int ox) && int.TryParse(args[i + 2], out int oy))
            { offX = ox; offY = oy; i += 2; }
            else
                return Usage();
        }
        if (outDir is null || libDir is null || indexPath is null || offX is null || offY is null)
        {
            Console.Error.WriteLine("zone: --index index.json, --library LIBDIR, --offset X Y and " +
                "--out DIR are required (offset = the zone's region offsetX/offsetY, i.e. zones.tsv x 8192).");
            return 2;
        }
        if (!File.Exists(datMpk)) { Console.Error.WriteLine($"No such zone archive: {datMpk}"); return 1; }
        if (!File.Exists(indexPath)) { Console.Error.WriteLine($"No such index: {indexPath} (run `mpk index` first)."); return 1; }

        try
        {
            var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir);
            ZoneExportReport r = ZoneExport.Export(library, datMpk, offX.Value, offY.Value, outDir, ZoneSize.X, ZoneSize.Y);
            Console.WriteLine($"Zone: {r.Fixtures} fixtures, {r.Models} distinct models -> " +
                $"{r.Converted} in library, {r.MissingModel} not found; " +
                $"{r.Placed} placements ({r.Grounded} stood on the terrain) written to {Path.GetFullPath(outDir)}/zone.csv " +
                $"(library: {Path.GetFullPath(libDir)})");
            foreach (string failure in r.Failures)
                Console.WriteLine($"  FAILED to convert {failure}");
            return r.Failed > 0 ? 1 : 0;
        }
        catch (Exception e) when (e is MpkFormatException or DirectoryNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine($"zone: {e.Message}");
            return 1;
        }
    }

    // water <dat<NNN>.mpk> --offset X Y --out DIR [--index index.json --library LIBDIR]: the zone's
    // rivers, lakes and swamps (SECTOR.DAT [riverNN]) as DIR/water.glb, one mesh per body named
    // waterNN, plus DIR/water.csv with each body's texture, overlay, flow and type for the client.
    private static int WaterCmd(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        string datMpk = args[1];
        int? offX = null, offY = null;
        string? outDir = null, indexPath = null, libDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i] == "--index" && i + 1 < args.Length) indexPath = args[++i];
            else if (args[i] == "--library" && i + 1 < args.Length) libDir = args[++i];
            else if (args[i] == "--size" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int zsx) && int.TryParse(args[i + 2], out int zsy))
            { ZoneSize = (zsx, zsy); i += 2; }
            else if (args[i] == "--offset" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int ox) && int.TryParse(args[i + 2], out int oy))
            { offX = ox; offY = oy; i += 2; }
            else
                return Usage();
        }
        if (outDir is null || offX is null || offY is null || (indexPath is null) != (libDir is null))
            return Usage();
        if (!File.Exists(datMpk)) { Console.Error.WriteLine($"No such zone archive: {datMpk}"); return 1; }

        using MpkArchive dat = MpkArchive.Open(datMpk);
        MpkEntry? sectorEntry = dat.Entries.FirstOrDefault(e => e.Name.Equals("SECTOR.DAT", StringComparison.OrdinalIgnoreCase));
        if (sectorEntry is null)
        {
            Console.Error.WriteLine("water: no SECTOR.DAT in this archive.");
            return 1;
        }
        var bodies = ZoneWater.Parse(System.Text.Encoding.Latin1.GetString(dat.Extract(sectorEntry)), ZoneSize.X);
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "water.csv"),
            [ZoneWater.CsvHeader, .. bodies.Select(ZoneWater.CsvRow)]);
        if (bodies.Count == 0)
        {
            Console.WriteLine($"No water in this zone -> {Path.GetFullPath(Path.Combine(outDir, "water.csv"))} (header only)");
            return 0;
        }
        var meshes = bodies.Select(b => ZoneWater.BuildMesh(b, offX.Value, offY.Value)).ToList();
        string glb = Path.Combine(outDir, "water.glb");
        GltfExporter.SaveGlb(new NifModel { Version = new NifVersion(0, 0, 0, 0), Meshes = meshes }, glb);
        foreach (WaterBody b in bodies)
            Console.WriteLine($"  {b.MeshName}: {b.Type,-6} \"{b.Name}\" height {b.Height}, {Math.Min(b.Left.Count, b.Right.Count)} bank pairs, " +
                $"texture {b.Texture}{(b.MultiTexture.Length > 0 ? "+" + b.MultiTexture : "")}, flow {b.Flow}");
        Console.WriteLine($"Water: {bodies.Count} bod{(bodies.Count == 1 ? "y" : "ies")} -> {Path.GetFullPath(glb)}");

        if (indexPath is not null && libDir is not null)
        {
            var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir);
            foreach (string stem in bodies.SelectMany(b => new[] { b.Texture, b.MultiTexture }).Where(t => t.Length > 0).Distinct())
                Console.WriteLine($"  texture {stem}: " + (library.GatherTexture(stem + ".dds") is not null ? "gathered" : "MISSING"));
        }
        return 0;
    }

    // sky <dat<NNN>.mpk> --out DIR [--name albion] [--skympk zones/sky/sky.mpk] [--index index.json
    // --library LIBDIR] (name and sky.mpk default to the client's choice via zones.dat): the zone's sky as DIR/sky.csv ("key,value"; colours "r g b a" in 0..1): every
    // weather condition and time of day from sky_<name>.dat (canopy and cloud gradients, ambient and sun
    // light, distance-fog colour), its cloud layers and sun, and SECTOR.DAT's [fog] clip and amount.
    private static int SkyCmd(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        string datMpk = args[1];
        string? skyMpk = null, name = null, outDir = null, indexPath = null, libDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i] == "--skympk" && i + 1 < args.Length) skyMpk = args[++i];
            else if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
            else if (args[i] == "--index" && i + 1 < args.Length) indexPath = args[++i];
            else if (args[i] == "--library" && i + 1 < args.Length) libDir = args[++i];
            else return Usage();
        }
        // Default to the client's own choice: zones.dat beside the zone folders, keyed by the zone id
        // taken from the archive's folder (zones/zoneNNN/datNNN.mpk).
        string zonesDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(datMpk))!)!;
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
        using (MpkArchive dat = MpkArchive.Open(datMpk))
        {
            MpkEntry? e = dat.Entries.FirstOrDefault(x => x.Name.Equals("SECTOR.DAT", StringComparison.OrdinalIgnoreCase));
            sector = IniFile.Parse(e is null ? "" : System.Text.Encoding.Latin1.GetString(dat.Extract(e)));
        }

        static string C(System.Numerics.Vector4 c) => FormattableString.Invariant($"{c.X:0.###} {c.Y:0.###} {c.Z:0.###} {c.W:0.###}");
        static string C3(System.Numerics.Vector3 c) => FormattableString.Invariant($"{c.X:0.###} {c.Y:0.###} {c.Z:0.###} 1");
        var lines = new List<string> { "key,value", $"sky,{name}" };
        lines.Add(FormattableString.Invariant($"fog.clip,{sector.Float("fog", "clip") ?? 0}"));
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

    private static int TerrainCmd(string[] args)
    {
        // terrain <dat<NNN>.mpk> --offset X Y --out FILE.glb [--tiles N] [--index index.json --library LIBDIR]
        if (args.Length < 2)
            return Usage();
        string datMpk = args[1];
        int? offX = null, offY = null;
        int tiles = 8;  // the painted ground is an 8x8 grid of texRR-CC tiles (verified on the install).
        string? outPath = null, indexPath = null, libDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--index" && i + 1 < args.Length) indexPath = args[++i];
            else if (args[i] == "--library" && i + 1 < args.Length) libDir = args[++i];
            else if (args[i] == "--tiles" && i + 1 < args.Length && int.TryParse(args[i + 1], out int tn)) { tiles = tn; i++; }
            else if (args[i] == "--size" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int zsx) && int.TryParse(args[i + 2], out int zsy))
            { ZoneSize = (zsx, zsy); i += 2; }
            else if (args[i] == "--offset" && i + 2 < args.Length
                     && int.TryParse(args[i + 1], out int ox) && int.TryParse(args[i + 2], out int oy))
            { offX = ox; offY = oy; i += 2; }
            else
                return Usage();
        }
        if (outPath is null || offX is null || offY is null)
        {
            Console.Error.WriteLine("terrain: --offset X Y and --out FILE.glb are required " +
                "(offset = the zone's region offsetX/offsetY, i.e. zones.tsv x 8192).");
            return 2;
        }
        if (!File.Exists(datMpk)) { Console.Error.WriteLine($"No such zone archive: {datMpk}"); return 1; }
        if ((indexPath is null) != (libDir is null))
        {
            Console.Error.WriteLine("terrain: pass --index and --library together to gather the ground tiles.");
            return 2;
        }

        try
        {
            ZoneTerrain terrain = ZoneTerrain.Read(datMpk, ZoneSize.X, ZoneSize.Y);
            var meshes = terrain.BuildTiledMesh(offX.Value, offY.Value, tiles);
            var model = new NifModel { Version = new NifVersion(0, 0, 0, 0), Meshes = [.. meshes] };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            GltfExporter.SaveGlb(model, outPath);

            int verts = 0, tris = 0;
            foreach (NifMesh m in meshes) { verts += m.Vertices.Length; tris += m.Triangles.Length; }
            Console.WriteLine($"Terrain: {terrain.Cells}x{terrain.Cells} heightmap, {meshes.Count} tile(s), " +
                $"{verts} verts, {tris} tris -> {Path.GetFullPath(outPath)}");

            // Gather the ground tiles into the library so the client textures the terrain like any model.
            if (indexPath is not null && libDir is not null)
            {
                if (!File.Exists(indexPath)) { Console.Error.WriteLine($"No such index: {indexPath} (run `mpk index` first)."); return 1; }
                var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir);
                int got = 0, miss = 0;
                foreach (NifMesh m in meshes)
                    if (m.TextureFile is { } tf)
                        if (library.GatherTexture(tf) is not null) got++; else miss++;
                Console.WriteLine($"  tiles: {got} gathered, {miss} missing -> {Path.GetFullPath(libDir)}/textures");
            }
            return 0;
        }
        catch (Exception e) when (e is MpkFormatException or FormatException)
        {
            Console.Error.WriteLine($"terrain: {e.Message}");
            return 1;
        }
    }

    private static int IndexCmd(string[] args)
    {
        // index <client-dir> --out index.json
        if (args.Length < 2)
            return Usage();
        string clientDir = args[1];
        string? outPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else
                return Usage();
        }
        if (outPath is null)
        {
            Console.Error.WriteLine("index: --out index.json is required.");
            return 2;
        }

        try
        {
            int last = 0;
            AssetIndex index = AssetIndex.Build(clientDir, n =>
            {
                if (n - last >= 200) { Console.Error.Write($"\r  indexed {n} archives…"); last = n; }
            });
            Console.Error.WriteLine();
            index.Save(outPath);
            (int loose, int archived) = index.Breakdown();
            Console.WriteLine($"Indexed {index.Count} assets ({loose} loose, {archived} in archives) " +
                $"-> {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (DirectoryNotFoundException e)
        {
            Console.Error.WriteLine($"index: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              mpk list     <archive.mpk>
              mpk extract  <archive.mpk> [member] [--out DIR]
              mpk convert  <model.nif> [--out FILE.glb]
              mpk nifinfo  <model.nif>
              mpk textures <client-dir> <model.nif | dir-of-nifs> --out DIR
              mpk index    <client-dir> --out index.json
              mpk zone     <dat<NNN>.mpk> --index index.json --library LIBDIR --offset X Y --out DIR [--size W H]
              mpk terrain  <dat<NNN>.mpk> --offset X Y --out FILE.glb [--size W H] [--tiles N] [--index index.json --library LIBDIR]
              mpk audit    <client-dir> --out FILE.md
              mpk worldaudit <client-dir> --out FILE.md
              mpk zonecheck <dat<NNN>.mpk> --zone DIR --library LIBDIR --offset X Y [--size W H] [--known FILE] [--out FILE.md]
              mpk water    <dat<NNN>.mpk> --offset X Y --out DIR [--size W H] [--index index.json --library LIBDIR]
              mpk sky      <dat<NNN>.mpk> --out DIR [--name albion] [--skympk <zones/sky/sky.mpk>] [--index index.json --library LIBDIR]

            With no member, extract writes every member. Default --out is the current directory.
            convert writes a glTF binary next to the .nif unless --out is given.
            textures gathers the .dds a model references (loose on disk or packed in an .mpk) into
            one folder, for the Godot client to load. Owner-run: point it at your own install.
            zone converts a zone's static objects and writes zone.csv (a placement manifest the
            Godot client loads). The offset is the zone's region offsetX/offsetY (zones.tsv x 8192).
            index walks the install once and records where every .nif/.dds lives (loose path or
            archive+member) to a JSON file, so later builds and lookups never re-scan.
            audit surveys every NIF (and .kfa animation) in the install and reports each block type and feature,
            how the converter treats it, and whether our byte-level decode of it holds. The report
            describes your client's files: write it under private/, not into the repo.
            """);
        return 2;
    }
}
