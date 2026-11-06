// SPDX-License-Identifier: GPL-3.0-or-later
//
// Converts Catacombs figures: one models/cata_fig<N>.glb per race and sex (fig3 figure N), with its
// options file, reading each archive, skeleton, part and clip once.
internal sealed class CataConverter : IDisposable
{
    private readonly DaocAssets.Figures.MonsterTables _tables;
    private readonly AssetLibrary _library;
    private readonly string _models, _fig3;
    private readonly Dictionary<string, MpkArchive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NifModel?> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KfaClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string?> _figures = [];
    private readonly string[] _boneNames;
    private readonly Dictionary<string, string> _animFiles;
    public readonly HashSet<string> Gathered = new(StringComparer.OrdinalIgnoreCase);
    public int MissingParts, Failed;

    public CataConverter(string gamedata, DaocAssets.Figures.MonsterTables tables, AssetLibrary library, string models)
    {
        (_tables, _library, _models) = (tables, library, models);
        string install = Path.GetDirectoryName(Path.GetFullPath(gamedata))!;
        _fig3 = Path.Combine(install, "figures", "fig3");
        string animNode = Path.Combine(install, "figures", "animnode.dat");
        _boneNames = File.Exists(animNode) ? File.ReadAllLines(animNode, System.Text.Encoding.Latin1).Select(l => l.Trim()).ToArray() : [];
        _animFiles = Directory.Exists(Path.Combine(install, "anims"))
            ? Directory.EnumerateFiles(Path.Combine(install, "anims")).ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(models);
    }

    public int FigureCount => _figures.Count(f => f.Value is not null);

    private byte[]? Member(string archive, string name)
    {
        if (!_archives.TryGetValue(archive, out MpkArchive? mpk))
            _archives[archive] = mpk = File.Exists(Path.Combine(_fig3, archive)) ? MpkArchive.Open(Path.Combine(_fig3, archive)) : null;
        return mpk is not null && mpk.TryGetEntry(name, out MpkEntry e) ? mpk.Extract(e) : null;
    }

    private NifModel? Part(DaocAssets.Figures.CataPart part)
    {
        string key = $"fig{part.Archive:000}.mpk/{part.Nif}";
        if (!_parts.TryGetValue(key, out NifModel? model))
        {
            try { model = Member($"fig{part.Archive:000}.mpk", part.Nif) is byte[] b ? NifFile.Read(b) : null; }
            catch (NifFormatException) { model = null; }
            _parts[key] = model;
        }
        return model;
    }

    /// <summary>The race figure for <paramref name="spec"/>, built on first use (its file name, or null).</summary>
    public string? Figure(DaocAssets.Figures.CataSpec spec)
    {
        if (_figures.TryGetValue(spec.Figure, out string? done))
            return done;
        string? result = null;
        try
        {
            IReadOnlyList<NifJoint>? skeleton = Member($"sfig{spec.SkeletonArchive:000}.mpk", spec.Skeleton) is byte[] sb ? NifFile.ReadSkeleton(sb) : null;
            if (skeleton is null)
                throw new NifFormatException($"no skeleton {spec.Skeleton} in sfig{spec.SkeletonArchive:000}.mpk");
            DaocAssets.Figures.CataLooks? looks = _tables.Looks(spec.Figure);
            var parts = CataParts(spec, looks, Part, _ => MissingParts++);
            var clips = new Dictionary<string, KfaClip>();
            foreach ((string clip, string kfa) in spec.Clips)
            {
                if (!_clips.TryGetValue(kfa, out KfaClip? k))
                {
                    try { k = _animFiles.TryGetValue(kfa, out string? path) ? KfaClip.Read(File.ReadAllBytes(path), _boneNames) : null; }
                    catch (NifFormatException) { k = null; }
                    _clips[kfa] = k;
                }
                if (k is not null) clips[clip] = DaocAssets.Figures.CatacombsFigure.RotationsOnly(k);
            }
            NifModel figure = DaocAssets.Figures.CatacombsFigure.Assemble(skeleton, parts);
            string file = $"cata_fig{spec.Figure}.glb";
            string outPath = Path.Combine(_models, file);
            GltfExporter.SaveGlb(figure, outPath + ".tmp", clips);
            File.Move(outPath + ".tmp", outPath, overwrite: true);
            foreach (string skin in parts.Select(p => p.Skin).OfType<string>())
                if (Gathered.Add(skin)) _library.GatherTexture(skin);
            if (looks is not null)
                WriteLooks(spec, looks, _models, _library, Gathered);
            result = file;
        }
        catch (Exception e) when (e is NifFormatException or InvalidOperationException or ArgumentException)
        {
            Failed++;
            Console.Error.WriteLine($"  cata figure {spec.Figure} ({spec.Name}): {e.Message}");
        }
        _figures[spec.Figure] = result;
        return result;
    }

    /// <summary>models/catacombs.csv ("id,scale,figure,fig3") and models/cata_models.json (each model's
    /// default skin and mesh variant per slot, and its hair style), from the converted models; with
    /// <paramref name="merge"/>, added to the existing files.</summary>
    public void WriteModels(IEnumerable<(int Id, DaocAssets.Figures.CataSpec Spec)> models, bool merge = false)
    {
        string csvPath = Path.Combine(_models, "catacombs.csv"), jsonPath = Path.Combine(_models, "cata_models.json");
        var rows = new SortedDictionary<int, string>();
        var entries = new SortedDictionary<int, System.Text.Json.Nodes.JsonNode?>();
        if (merge && File.Exists(csvPath))
            foreach (string line in File.ReadAllLines(csvPath).Skip(1))
                if (int.TryParse(line.Split(',')[0], out int id)) rows[id] = line;
        if (merge && File.Exists(jsonPath) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jsonPath)) is System.Text.Json.Nodes.JsonObject old)
            foreach (var kv in old)
                if (int.TryParse(kv.Key, out int id)) entries[id] = kv.Value?.DeepClone();
        foreach ((int id, DaocAssets.Figures.CataSpec spec) in models)
        {
            if (_figures.GetValueOrDefault(spec.Figure) is null) continue;
            rows[id] = DaocAssets.Figures.FigureTable.Row(new(id, spec.Scale, Path.GetFileNameWithoutExtension(spec.Skeleton), spec.Figure));
            foreach (string skin in spec.Parts.Select(p => p.Skin).OfType<string>())
                if (Gathered.Add(skin)) _library.GatherTexture(skin);
            entries[id] = System.Text.Json.JsonSerializer.SerializeToNode(new
            {
                skins = spec.Parts.Where(p => p.Skin is not null)
                    .ToDictionary(p => DaocAssets.Figures.CataSlots.Names[p.Slot], p => p.Skin),
                defaults = spec.DefaultVariants.ToDictionary(kv => DaocAssets.Figures.CataSlots.Names[kv.Key], kv => kv.Value),
                hair = spec.DefaultHair,
                tint = spec.TintedSlots,
            });
        }
        File.WriteAllText(csvPath, DaocAssets.Figures.FigureTable.CatacombsHeader + "\n" + string.Concat(rows.Values.Select(r => r + "\n")));
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in entries) obj[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
        File.WriteAllText(jsonPath, obj.ToJsonString());
    }

    public void Dispose()
    {
        foreach (MpkArchive? a in _archives.Values) a?.Dispose();
    }

    // A Catacombs figure's parts, labelled for the client: each slot's part (head, body, ...) except the
    // hair, then every hair style of the race as hair<n> (from 0), the row's own style wearing its hair skin
    // and the others their first colour; the client shows one and re-dresses it from fig3_<figure>.json.
    private static List<(NifModel Part, string? Skin, string Label)> CataParts(DaocAssets.Figures.CataSpec spec,
        DaocAssets.Figures.CataLooks? looks, Func<DaocAssets.Figures.CataPart, NifModel?> load, Action<string> missing)
    {
        var list = new List<(NifModel, string?, string)>();
        foreach (DaocAssets.Figures.CataPart part in spec.Parts.Where(p => p.Slot != 8 && !spec.Variants.ContainsKey(p.Slot)))
        {
            if (load(part) is not { } model) { missing(part.Nif); continue; }
            list.Add((model, part.Skin, DaocAssets.Figures.CataSlots.Names[part.Slot]));
        }
        // Every mesh variant of the worn slots as <slot><n> (from 1), wearing the row's skin for that slot;
        // the client shows the one the worn item (or the row) picks. A cloak's texture is on its second UV set;
        // its first and third carry a guild emblem's pattern and logo (TEXCOORD_2 and 3: Godot's CUSTOM0).
        foreach ((int slot, IReadOnlyList<DaocAssets.Figures.CataPart?> variants) in spec.Variants)
        {
            string? skin = spec.Parts.FirstOrDefault(p => p.Slot == slot)?.Skin;
            for (int v = 0; v < variants.Count; v++)
            {
                if (variants[v] is not { } part) continue;
                if (load(part) is not { } model) { missing(part.Nif); continue; }
                if (slot == 7)
                    model = new NifModel
                    {
                        Version = model.Version, Joints = model.Joints,
                        Meshes = model.Meshes.Select(m => m.UvSets is { Length: > 1 } uv
                            ? m with { TexCoords = uv[1], ExtraTexCoords = uv.Length > 2 ? [uv[0], uv[2]] : [uv[0]] } : m).ToList(),
                    };
                list.Add((model, skin, $"{DaocAssets.Figures.CataSlots.Names[slot]}{v + 1}"));
            }
        }
        string? rowHair = spec.Parts.FirstOrDefault(p => p.Slot == 8)?.Skin;
        for (int s = 0; s < spec.HairStyles.Count; s++)
        {
            if (spec.HairStyles[s] is not { } style) continue;
            if (load(style) is not { } model) { missing(style.Nif); continue; }
            string? skin = s == spec.DefaultHair && rowHair is not null ? rowHair
                : looks is not null && s < looks.HairColours.Count && looks.HairColours[s].Count > 0 ? looks.HairColours[s][0].Texture : null;
            list.Add((model, skin, $"hair{s}"));
        }
        return list;
    }

    // models/fig3_<figure>.json: a Catacombs race's appearance options for the client (faces, eye colours,
    // skin tints, and per hair style its node and colours), indexed as the appearance bytes count except the
    // hair: "hair"[s] is hair style s + 1, as the byte counts them from 1. Every texture it names is gathered
    // into the library.
    private static void WriteLooks(DaocAssets.Figures.CataSpec spec, DaocAssets.Figures.CataLooks looks, string modelsDir,
        AssetLibrary library, HashSet<string> gathered)
    {
        static double[] Rgb(System.Numerics.Vector3 v) => [Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3)];
        int styles = Math.Max(spec.HairStyles.Count, looks.HairColours.Count);
        var json = new
        {
            faces = looks.Faces,
            eyes = looks.Eyes,
            skins = looks.SkinTints.Select(Rgb),
            hair = Enumerable.Range(0, styles).Select(s => new
            {
                node = s < spec.HairStyles.Count && spec.HairStyles[s] is not null ? $"hair{s}" : null,
                colours = s < looks.HairColours.Count
                    ? looks.HairColours[s].Select(c => new { tex = c.Texture, tint = Rgb(c.Tint) }).ToArray() : [],
            }),
            default_hair = spec.DefaultHair,
            female = spec.Skeleton.Contains("_f_", StringComparison.OrdinalIgnoreCase),
            prefix = spec.Skeleton.Replace("_skeleton.nif", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(),
            variants = spec.Variants.ToDictionary(kv => DaocAssets.Figures.CataSlots.Names[kv.Key],
                kv => kv.Value.Select((p, i) => p is null ? 0 : i + 1).Where(n => n > 0).ToArray()),
            defaults = spec.DefaultVariants.ToDictionary(kv => DaocAssets.Figures.CataSlots.Names[kv.Key], kv => kv.Value),
        };
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Combine(modelsDir, $"fig3_{spec.Figure}.json"), System.Text.Json.JsonSerializer.Serialize(json));
        foreach (string t in looks.Faces.Concat(looks.Eyes).Concat(looks.HairColours.SelectMany(h => h.Select(c => c.Texture))).OfType<string>())
            if (gathered.Add(t) && library.GatherTexture(t) is null)
                Console.Error.WriteLine($"  look texture {t}: not found");
    }
}
