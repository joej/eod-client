// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk figure, figures, cata: server model ids as the client draws them, and the item tables.

internal static partial class Cli
{
    // A server model id as the client draws it (classic figures): the figure file with one variant per
    // body slot, each wearing its skin from gamedata.mpk; written to the library as model<id>.glb.
    private static int FigureCmd(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int modelId)) return Usage();
        if (FigureOptions(args, 2) is not var (gamedata, indexPath, libDir)) return Usage();

        DaocAssets.Figures.FigureSpec? spec = DaocAssets.Figures.MonsterTables.Read(gamedata).Resolve(modelId);
        if (spec is null) { Console.Error.WriteLine($"figure: no model {modelId} in gamedata.mpk"); return 3; }
        if (spec.Version >= 3) { Console.Error.WriteLine($"figure: model {modelId} ({spec.Name}) is a Catacombs figure: use mpk cata"); return 2; }

        var index = AssetIndex.Load(indexPath);
        byte[]? bytes = FigureFile(index, spec.Nif);
        if (bytes is null) { Console.Error.WriteLine($"figure: {spec.Nif} is not among the install's figures"); return 3; }
        NifModel dressed = DaocAssets.Figures.MonsterFigure.Dress(NifFile.ReadFigure(bytes), spec);

        var library = new AssetLibrary(index, libDir).Scoped("figures", FigureFolders);
        foreach (string skin in spec.Skins.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            if (library.GatherTexture(skin) is null)
                Console.Error.WriteLine($"  skin {skin}: not found");
        foreach (string t in FigureTextures(dressed))
            library.GatherTexture(t);
        // The clips (anims/<name>.kfa), bone names from figures/animnode.dat beside the install's figures.
        var clips = new Dictionary<string, KfaClip>();
        string install = Path.GetDirectoryName(Path.GetFullPath(gamedata))!;
        string animNode = Path.Combine(install, "figures", "animnode.dat");
        if (File.Exists(animNode))
        {
            string[] boneNames = File.ReadAllLines(animNode, System.Text.Encoding.Latin1).Select(l => l.Trim()).ToArray();
            foreach ((string clip, string kfa) in spec.Clips)
            {
                string? path = Directory.EnumerateFiles(Path.Combine(install, "anims"))
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), kfa, StringComparison.OrdinalIgnoreCase));
                try { if (path is not null) clips[clip] = KfaClip.Read(File.ReadAllBytes(path), boneNames); }
                catch (NifFormatException e) { Console.Error.WriteLine($"  clip {clip} ({kfa}): {e.Message}"); }
            }
        }
        string outPath = Path.Combine(libDir, "models", $"model{modelId}.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        GltfExporter.SaveGlb(dressed, outPath, clips);
        int parts = dressed.Meshes.Count(m => !m.Helper);
        Console.WriteLine($"figure {modelId} ({spec.Name}): {spec.Nif}, {parts} parts, scale {spec.Scale:0.##}, " +
            $"skins {string.Join(" ", spec.Skins.Select(kv => kv.Key + "=" + kv.Value))}, " +
            $"clips {string.Join(" ", clips.Select(kv => $"{kv.Key}={spec.Clips[kv.Key]}({kv.Value.Duration:0.#}s)"))} -> {outPath}");
        return 0;
    }

    // Every classic-figure model id in monsters.csv as models/model<id>.glb, plus models/figures.csv
    // (id, scale, figure) for the client. Figure files and clips are read once each.
    private static int FiguresCmd(string[] args)
    {
        if (FigureOptions(args, 1) is not var (gamedata, indexPath, libDir)) return Usage();

        var tables = DaocAssets.Figures.MonsterTables.Read(gamedata);
        var index = AssetIndex.Load(indexPath);
        var library = new AssetLibrary(index, libDir).Scoped("figures", FigureFolders);
        string install = Path.GetDirectoryName(Path.GetFullPath(gamedata))!;
        string animNode = Path.Combine(install, "figures", "animnode.dat");
        string[] boneNames = File.Exists(animNode)
            ? File.ReadAllLines(animNode, System.Text.Encoding.Latin1).Select(l => l.Trim()).ToArray() : [];
        var animFiles = Directory.Exists(Path.Combine(install, "anims"))
            ? Directory.EnumerateFiles(Path.Combine(install, "anims")).ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var figures = new Dictionary<string, NifModel?>(StringComparer.OrdinalIgnoreCase);
        var clipCache = new Dictionary<string, KfaClip?>(StringComparer.OrdinalIgnoreCase);
        var skins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string models = Path.Combine(libDir, "models");
        Directory.CreateDirectory(models);
        var table = new System.Text.StringBuilder(DaocAssets.Figures.FigureTable.ClassicHeader + "\n");
        int made = 0, catacombs = 0, missing = 0, failed = 0;
        foreach (int id in tables.ModelIds)
        {
            DaocAssets.Figures.FigureSpec? spec = tables.Resolve(id);
            if (spec is null) { missing++; continue; }
            if (spec.Version >= 3) { catacombs++; continue; }
            if (!figures.TryGetValue(spec.Nif, out NifModel? figure))
            {
                try { figure = FigureFile(index, spec.Nif) is byte[] b ? NifFile.ReadFigure(b) : null; }
                catch (NifFormatException) { figure = null; }
                figures[spec.Nif] = figure;
            }
            if (figure is null || figure.Joints.Count == 0) { missing++; continue; }
            var clips = new Dictionary<string, KfaClip>();
            foreach ((string clip, string kfa) in spec.Clips)
            {
                if (!clipCache.TryGetValue(kfa, out KfaClip? k))
                {
                    try { k = animFiles.TryGetValue(kfa, out string? path) ? KfaClip.Read(File.ReadAllBytes(path), boneNames) : null; }
                    catch (NifFormatException) { k = null; }
                    clipCache[kfa] = k;
                }
                if (k is not null) clips[clip] = k;
            }
            try
            {
                NifModel dressed = DaocAssets.Figures.MonsterFigure.Dress(figure, spec);
                string outPath = Path.Combine(models, $"model{id}.glb");
                GltfExporter.SaveGlb(dressed, outPath + ".tmp", clips);
                File.Move(outPath + ".tmp", outPath, overwrite: true);
                foreach (string skin in spec.Skins.Values)
                    if (skins.Add(skin)) library.GatherTexture(skin);
                // Textures the visible shapes carry themselves (a part the table gives no skin, layers).
                foreach (string t in FigureTextures(dressed))
                    if (skins.Add(t)) library.GatherTexture(t);
                table.Append(DaocAssets.Figures.FigureTable.Row(new(id, spec.Scale, Path.GetFileNameWithoutExtension(spec.Nif))) + "\n");
                made++;
            }
            catch (Exception e) when (e is NifFormatException or InvalidOperationException or ArgumentException)
            {
                failed++;
                Console.Error.WriteLine($"  model {id} ({spec.Name}): {e.Message}");
            }
        }
        File.WriteAllText(Path.Combine(models, "figures.csv"), table.ToString());
        Console.WriteLine($"figures: {made} model ids converted ({figures.Count(f => f.Value is not null)} figure files, " +
            $"{clipCache.Count(c => c.Value is not null)} clips, {skins.Count} skins); {catacombs} whose own figure is a Catacombs one (skipped), " +
            $"{missing} without a figure, {failed} failed");

        int effectsFailed = WriteEffects(tables, models, library.Scoped("effects", EffectFolders));

        // Each model id's Catacombs version (how retail draws players): one figure per race and sex
        // (models/cata_fig<N>.glb), and the model's own default skins in models/cata_models.json.
        using var cata = new CataConverter(gamedata, tables, library, models);
        var cataModels = new List<(int, DaocAssets.Figures.CataSpec)>();
        foreach (int id in tables.ModelIds)
            if (tables.ResolveCatacombs(id) is { } cspec && cata.Figure(cspec) is not null)
                cataModels.Add((id, cspec));
        cata.WriteModels(cataModels);
        WriteCataItems(gamedata, models, library.Scoped("items", ItemFolders), []);
        WriteCataGear(gamedata, models, library.Scoped("items", ItemFolders), index);
        WriteItemColours(gamedata, models, library);
        int cataFailed = cata.Failed;
        Console.WriteLine($"figures: {cataModels.Count} model ids on {cata.FigureCount} Catacombs figures, " +
            $"{cata.MissingParts} parts missing, {cataFailed} failed");
        return failed == 0 && cataFailed == 0 && effectsFailed == 0 ? 0 : 1;
    }

    // models/effects.csv: the effects the client attaches to a model (monsters.csv "Mon Effect": glows, auras,
    // the teleporter's ground ring), one row per effect (model id, model scale, effect model, attach node,
    // visible range), each effect NIF converted into the library as models/<name>.glb. Returns the failures.
    private static int WriteEffects(DaocAssets.Figures.MonsterTables tables, string modelsDir, AssetLibrary library)
    {
        var table = new System.Text.StringBuilder("model,scale,effect,attach,range\n");
        var converted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int models = 0, rows = 0, failed = 0, absent = 0, unsupported = 0;
        foreach (int id in tables.ModelIds)
        {
            IReadOnlyList<DaocAssets.Figures.MonsterEffect> effects = tables.Effects(id);
            if (effects.Count == 0) continue;
            models++;
            float scale = tables.Resolve(id)?.Scale ?? 1f;
            foreach (DaocAssets.Figures.MonsterEffect e in effects)
            {
                if (!converted.TryGetValue(e.Nif, out string? stem))
                {
                    ModelResult r = library.GetModel(e.Nif, reconvert: true);
                    stem = r.GlbPath is null ? null : ModelStem(modelsDir, r.GlbPath);
                    // Not shipped in this install, or particles only (no triangles: our reader draws none):
                    // nothing to draw, not an error. Anything else is.
                    switch (r.Origin)
                    {
                        case AssetOrigin.Missing: absent++; break;
                        case AssetOrigin.Unsupported: unsupported++; break;
                        case AssetOrigin.Failed: failed++; Console.Error.WriteLine($"  effect (model {id}): {r.Error}"); break;
                    }
                    converted[e.Nif] = stem;
                }
                if (stem is null) continue;
                table.Append(FormattableString.Invariant($"{id},{scale},{stem},{e.Attach},{e.VisibleRange}\n"));
                rows++;
            }
        }
        File.WriteAllText(Path.Combine(modelsDir, "effects.csv"), table.ToString());

        // models/quest.csv: the glyph over a quest giver's head per state (state, effect model, attach node).
        var quest = new System.Text.StringBuilder("state,effect,attach\n");
        foreach ((string state, DaocAssets.Figures.MonsterEffect e) in tables.QuestGlyphs().OrderBy(kv => kv.Key))
        {
            ModelResult r = library.GetModel(e.Nif, reconvert: true);
            if (r.GlbPath is null) { failed++; Console.Error.WriteLine($"  quest glyph {state} ({e.Nif}): {r.Origin} {r.Error}"); continue; }
            quest.Append($"{state},{ModelStem(modelsDir, r.GlbPath)},{e.Attach}\n");
        }
        File.WriteAllText(Path.Combine(modelsDir, "quest.csv"), quest.ToString());
        Console.WriteLine($"effects: {rows} on {models} model ids, {converted.Count(c => c.Value is not null)} effect models; " +
            $"{absent} not in the install, {unsupported} not drawable (particles only), {failed} failed");
        return failed;
    }

    // A server model id's Catacombs figure (monsters.csv "Cata Nif"): its race's figure, models/cata_fig<N>.glb
    // (skeleton from figures/fig3/sfigNNN.mpk, every part and variant from figures/fig3/figNNN.mpk, bound
    // by bone name, with the race's clips), its options (fig3_<N>.json), and the model's own default skins
    // and variants as its entry in models/cata_models.json; plus the items table (cata_items.json).
    private static int CataCmd(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int modelId)) return Usage();
        if (FigureOptions(args, 2) is not var (gamedata, indexPath, libDir)) return Usage();

        var tables = DaocAssets.Figures.MonsterTables.Read(gamedata);
        DaocAssets.Figures.CataSpec? spec = tables.ResolveCatacombs(modelId);
        if (spec is null) { Console.Error.WriteLine($"cata: model {modelId} has no Catacombs figure in gamedata.mpk"); return 3; }
        var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir).Scoped("figures", FigureFolders);
        using var cata = new CataConverter(gamedata, tables, library, Path.Combine(libDir, "models"));
        string? made = cata.Figure(spec);
        if (made is null) return 1;
        cata.WriteModels([(modelId, spec)], merge: true);
        WriteCataItems(gamedata, Path.Combine(libDir, "models"), library.Scoped("items", ItemFolders), []);
        Console.WriteLine($"cata {modelId} ({spec.Name}): fig3 {spec.Figure} -> {made}; " +
            $"skins {string.Join(" ", spec.Parts.Select(p => $"{DaocAssets.Figures.CataSlots.Names[p.Slot]}={p.Skin}"))}");
        return 0;
    }

    // models/cata_gear.json: every held item and helm (CataGear): {"items": {model: {nif, belt, shield,
    // stance, left, helms: {prefix: nif}, helm_hair}}, "nifs": {nif: {held, belt, back}}}, each NIF
    // converted into the library (models/<nif>.glb) and its frames given as the transform that hangs the
    // converted model on a bone by that frame: undo the glTF conversion, then the frame's own placement
    // (row vectors, 12 floats: the 3x3 by rows, then the translation).
    private static void WriteCataGear(string gamedata, string modelsDir, AssetLibrary library, AssetIndex index)
    {
        using MpkArchive mpk = MpkArchive.Open(gamedata);
        string Text(string name) => System.Text.Encoding.Latin1.GetString(mpk.Extract(name));
        var gear = DaocAssets.Figures.CataGear.Parse(Text("objects.csv"), Text("items.csv"), Text("pskins.csv"));
        foreach (string t in gear.Values.SelectMany(g => new[] { g.Texture, g.HelmTexture }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            library.GatherTexture(t);
        System.Numerics.Matrix4x4.Invert(GltfExporter.NifToGltfMatrix, out var fromGltf);
        static double[] Row(System.Numerics.Matrix4x4 m) =>
            [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];
        var nifs = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // The converted file's own name, case kept (albHelm01_Bri_m.glb): a case-sensitive file system
        // finds it only by that.
        var stems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;
        foreach (string nif in gear.Values.SelectMany(g => g.Helms.Values.Prepend(g.Nif ?? "")).Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Reconverted every run: this step runs only when the converter changed (make figures), and the
            // item models must follow it (an emblem shield's third UV set).
            ModelResult converted = library.GetModel(nif, reconvert: true);
            if (converted.GlbPath is null || index.Read(nif, ItemFolders) is not byte[] bytes) { failed++; continue; }
            string stem = ModelStem(modelsDir, converted.GlbPath);
            stems[Path.GetFileNameWithoutExtension(nif)] = stem;
            NifModel model;
            try { model = NifFile.Read(bytes); }
            catch (NifFormatException) { failed++; continue; }
            // As the client hangs items (OfflineDAoC's findings, GPL-3.0, citing the client): sheathed, the
            // BELT / BACK node's transform is where the item's root sits on the bone (a two-hander's BACK, 12
            // units down its length, lifts the hilt over the shoulder); in the hand the item sits as it is,
            // unless its mesh is turned under the 'item' node and the HELD node turns it back.
            var frames = new Dictionary<string, double[]>();
            var identity = System.Numerics.Matrix4x4.Identity;
            foreach (string frame in new[] { "belt", "back" })
                frames[frame] = Row(fromGltf * (model.NodeWorlds.TryGetValue(frame, out var w) ? w : identity));
            var grip = identity;
            if (model.NodeWorlds.TryGetValue("held", out var held) && model.NodeWorlds.TryGetValue("item", out var itemNode)
                && !Near(Rotation(itemNode), identity) && Near(Rotation(held), Rotation(itemNode)))
                System.Numerics.Matrix4x4.Invert(held, out grip);
            frames["held"] = Row(fromGltf * grip);
            nifs[stem] = frames;
        }
        string? Stem(string? nif) => nif is not null && stems.TryGetValue(Path.GetFileNameWithoutExtension(nif), out string? s) ? s : null;
        var json = new
        {
            items = gear.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => new
            {
                nif = Stem(kv.Value.Nif),
                belt = kv.Value.Belt,
                shield = kv.Value.Shield,
                stance = kv.Value.Stance,
                left = kv.Value.LeftHand,
                helms = kv.Value.Helms.Where(h => Stem(h.Value) is not null).ToDictionary(h => h.Key, h => Stem(h.Value)),
                helm_hair = kv.Value.HelmHair,
                texture = kv.Value.Texture,
                helm_texture = kv.Value.HelmTexture,
            }),
            nifs,
        };
        File.WriteAllText(Path.Combine(modelsDir, "cata_gear.json"), System.Text.Json.JsonSerializer.Serialize(json));
        Console.WriteLine($"cata gear: {gear.Count} held items and helms, {nifs.Count} item models converted, {failed} failed");
    }

    // models/colours.json: dyes {id: [r, g, b]} (tints.csv, Catacombs colours), and guild emblems: primary and
    // secondary colours (patterns.csv), the four pattern masks and the logos {id: texture} (insignia.csv), every
    // logo's picture gathered into the library.
    private static void WriteItemColours(string gamedata, string modelsDir, AssetLibrary library)
    {
        using MpkArchive mpk = MpkArchive.Open(gamedata);
        string Text(string name) => System.Text.Encoding.Latin1.GetString(mpk.Extract(name));
        var c = DaocAssets.Figures.ItemColours.Parse(Text("tints.csv"), Text("patterns.csv"), Text("insignia.csv"));
        static int[] Rgb((byte R, byte G, byte B) t) => [t.R, t.G, t.B];
        int missing = 0;
        var logos = new SortedDictionary<int, string>();
        foreach ((int id, string file) in c.Logos)
        {
            if (library.GatherTexture(file) is null) { missing++; continue; }
            logos[id] = Path.GetFileNameWithoutExtension(file);
        }
        var json = new
        {
            dyes = c.Dyes.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => Rgb(kv.Value)),
            primary = c.Primary.Select(Rgb),
            secondary = c.Secondary.Select(Rgb),
            masks = DaocAssets.Figures.ItemColours.PatternMasks,
            logos = logos.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
        };
        File.WriteAllText(Path.Combine(modelsDir, "colours.json"), System.Text.Json.JsonSerializer.Serialize(json));
        Console.WriteLine($"colours: {c.Dyes.Count} dyes, {logos.Count} emblem logos ({missing} not in the install)");
    }

    // models/cata_items.json: every item's Catacombs look (CataItems), {model: {robe, name_variant, m: {slot:
    // [variant, texture]}, f: {...}}}, for the client to dress player figures; every texture is gathered.
    private static void WriteCataItems(string gamedata, string modelsDir, AssetLibrary library, HashSet<string> gathered)
    {
        using MpkArchive mpk = MpkArchive.Open(gamedata);
        string Text(string name) => System.Text.Encoding.Latin1.GetString(mpk.Extract(name));
        var items = DaocAssets.Figures.CataItems.Parse(Text("objects.csv"), Text("pskins.csv"));
        static object Side(IReadOnlyDictionary<string, DaocAssets.Figures.CataPiece> d) =>
            d.ToDictionary(kv => kv.Key, kv => new object[] { kv.Value.Variant, kv.Value.Texture });
        var json = items.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => new
        {
            robe = kv.Value.Robe,
            name_variant = kv.Value.NameVariant,
            m = Side(kv.Value.Male),
            f = Side(kv.Value.Female),
        });
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Combine(modelsDir, "cata_items.json"), System.Text.Json.JsonSerializer.Serialize(json));
        int missing = 0;
        foreach (string t in items.Values.SelectMany(i => i.Male.Values.Concat(i.Female.Values)).Select(p => p.Texture).Distinct(StringComparer.OrdinalIgnoreCase))
            if (gathered.Add(t) && library.GatherTexture(t) is null)
                missing++;
        Console.WriteLine($"cata items: {items.Count} items with a Catacombs look, {missing} of their textures not in the install");
    }

    // Install folders each kind of asset comes from first. Names are looked up across the whole install,
    // and the install reuses them for different files: giant.nif is the creature in figures/ and a statue in
    // zones/Nifs/GIANT.npk; a creature skin in figures/skins/*.mpk can share its name with a housing trophy's
    // loose texture. Zone conversion keeps the index's default (zone files first).
    internal static readonly string[] FigureFolders = ["figures/"];
    internal static readonly string[] ItemFolders = ["items/", "figures/"];
    internal static readonly string[] EffectFolders = ["effects/"];

    // A figure's NIF, only from the install's figures folder (loose or in an archive there).
    private static byte[]? FigureFile(AssetIndex index, string nif) =>
        InFolder(index, index.Locate(nif, FigureFolders), "figures/") ? index.Read(nif, FigureFolders) : null;

    // Every texture a dressed figure's visible shapes use (base, layers).
    private static IEnumerable<string> FigureTextures(NifModel dressed) =>
        dressed.Meshes.Where(m => !m.Helper)
            .SelectMany(m => new[] { m.TextureFile, m.DarkTextureFile, m.DetailTextureFile, m.DecalTextureFile, m.GlowTextureFile })
            .OfType<string>().Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    // A converted model's name for the client, relative to models/: "helm2", or "items/helm2" when the item's
    // file differs from the zone model of that name.
    private static string ModelStem(string modelsDir, string glbPath) =>
        Path.ChangeExtension(Path.GetRelativePath(modelsDir, glbPath), null)!.Replace('\\', '/');

    private static bool InFolder(AssetIndex index, AssetLocation? loc, string folder)
    {
        if (loc is not { } l) return false;
        string path = (l.LoosePath ?? l.Archive ?? "").Replace('\\', '/');
        string root = index.Root.Replace('\\', '/').TrimEnd('/') + "/";
        return path.StartsWith(root + folder, StringComparison.OrdinalIgnoreCase);
    }

    // --gamedata gamedata.mpk --index index.json --library LIBDIR, all three required.
    private static (string Gamedata, string Index, string Library)? FigureOptions(string[] args, int start) =>
        Options.Parse(args, start, ["--gamedata", "--index", "--library"]) is { } o
            && o["--gamedata"] is { } g && o["--index"] is { } i && o["--library"] is { } l ? (g, i, l) : null;

    private static System.Numerics.Matrix4x4 Rotation(System.Numerics.Matrix4x4 m) => m with { M41 = 0, M42 = 0, M43 = 0 };

    private static bool Near(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(a[r, c] - b[r, c]) > 1e-3f) return false;
        return true;
    }
}
