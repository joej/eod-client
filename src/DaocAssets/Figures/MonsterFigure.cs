// SPDX-License-Identifier: GPL-3.0-or-later
//
// Classic figures (roadmap M7b): a character or creature as the client draws one for a server model id.
// The tables are in the install's gamedata.mpk:
//   monsters.csv  model id -> figure number (column "#") and a skin id per body slot
//                 (Body, Head, Arms, Gloves, Lbody, Legs, Boots, Cloak, Helm), then a scale
//   monnifs.csv   figure number -> NIF name (bcommonm = Briton male) and figure version
//                 (0/1 classic, 3 Catacombs)
//   skins.csv     skin id -> texture file (m1_bodym.tga), found in figures/skins/skinNNN.mpk
// A classic figure file holds every part variant as its own skinned shape, named "<Slot><n>[b]:<id>"
// (Body1:0, LBody1:6, HeadA1:19, Lod1a:…). The figure shows one variant per slot, each wearing its
// slot's skin; the rest are hidden. Which variant number a model uses isn't known yet (the monsters.csv
// "Multi" columns are the likely source): variant 1 is used. Owner-run: reads the owner's install.

using System.Globalization;
using DaocAssets.Mpk;
using DaocAssets.Nif;

namespace DaocAssets.Figures;

/// <summary>A server model id resolved to its figure file and per-slot skins.</summary>
public sealed record FigureSpec(int ModelId, string Name, string Nif, int Version,
    IReadOnlyDictionary<string, string> Skins, float Scale, int Face = 0)
{
    /// <summary>Clip name (idle, walk, run, back) -> .kfa file in anims/, from the figure's animation set.</summary>
    public IReadOnlyDictionary<string, string> Clips { get; init; } = new Dictionary<string, string>();
}

public sealed class MonsterTables
{
    // monsters.csv slot columns, in file order (after ID, name, #).
    private static readonly string[] Slots = ["Body", "Head", "Arms", "Gloves", "LBody", "Legs", "Boots", "Cloak", "Helm"];

    private readonly Dictionary<int, string[]> _monsters;
    private readonly Dictionary<int, string[]> _monnifs;
    private readonly Dictionary<int, string> _skins;
    private readonly Dictionary<int, string[]> _anims;
    private readonly Dictionary<int, string[]> _animnifs;
    private readonly Dictionary<(int, int), string[]> _fig3map = [];
    private readonly Dictionary<int, string[]> _fig3parts = [];
    private readonly Dictionary<(int, int), string[]> _hairColours = [];

    // anims.csv columns of the clips the client view uses (ID, name, Walk, Back, Run, ..., Idle at 10).
    private static readonly (string Clip, int Column)[] ClipColumns = [("walk", 2), ("back", 3), ("run", 4), ("idle", 10)];

    private MonsterTables(Dictionary<int, string[]> monsters, Dictionary<int, string[]> monnifs, Dictionary<int, string> skins,
        Dictionary<int, string[]> anims, Dictionary<int, string[]> animnifs) =>
        (_monsters, _monnifs, _skins, _anims, _animnifs) = (monsters, monnifs, skins, anims, animnifs);

    /// <summary>Reads the three tables from the install's <c>gamedata.mpk</c>.</summary>
    public static MonsterTables Read(string gamedataMpk)
    {
        using MpkArchive mpk = MpkArchive.Open(gamedataMpk);
        string Text(string name) => mpk.TryGetEntry(name, out MpkEntry e)
            ? System.Text.Encoding.Latin1.GetString(mpk.Extract(e))
            : throw new MpkFormatException($"gamedata.mpk has no {name}.");
        return Parse(Text("monsters.csv"), Text("monnifs.csv"), Text("skins.csv"), Text("anims.csv"), Text("animnifs.csv"))
            .WithFig3(Text("fig3map.csv"), Text("fig3parts.csv"), Text("fig3haircolormap.csv"));
    }

    /// <summary>Adds the Catacombs tables: <c>fig3map.csv</c> ((figure, slot) -> part id per variant)
    /// and <c>fig3parts.csv</c> (part id -> file and archive).</summary>
    public MonsterTables WithFig3(string fig3mapCsv, string fig3partsCsv, string fig3hairCsv = "")
    {
        // fig3haircolormap.csv: figure, style, description, then (skin, tint, ?) per hair colour.
        foreach (string raw in fig3hairCsv.Split('\n'))
        {
            string[] c = raw.TrimEnd('\r').Split(',');
            if (c.Length > 3 && int.TryParse(c[0], out int figure) && int.TryParse(c[1], out int style))
                _hairColours.TryAdd((figure, style), c[3..].Select(x => x.Trim()).ToArray());
        }
        foreach (string raw in fig3mapCsv.Split('\n'))
        {
            string[] c = raw.TrimEnd('\r').Split(',');
            if (c.Length > 3 && int.TryParse(c[0], out int figure) && int.TryParse(c[1], out int slot))
                _fig3map.TryAdd((figure, slot), c[3..].Select(x => x.Trim()).ToArray());
        }
        foreach ((int id, string[] row) in ById(fig3partsCsv))
            _fig3parts.TryAdd(id, row);
        return this;
    }

    /// <summary>The Catacombs version of server model id <paramref name="modelId"/> (monsters.csv "Cata
    /// Nif"), or null if it has none. Needs <see cref="WithFig3"/>.</summary>
    public CataSpec? ResolveCatacombs(int modelId) =>
        _monsters.TryGetValue(modelId, out string[]? m)
            ? CatacombsFigure.Resolve(modelId, m, _monnifs, _skins, _fig3map, _fig3parts, ClipsOf, _hairColours)
            : null;

    // The figure's animation set (monnifs column 3) -> a clip id per action -> its .kfa file.
    private Dictionary<string, string> ClipsOf(string[] monnif)
    {
        var clips = new Dictionary<string, string>();
        if (monnif.Length > 3 && int.TryParse(monnif[3], out int set) && _anims.TryGetValue(set, out string[]? a))
            foreach ((string clip, int col) in ClipColumns)
                if (col < a.Length && int.TryParse(a[col], out int id) && id > 0
                    && _animnifs.TryGetValue(id, out string[]? kfa) && kfa.Length > 2 && kfa[2].Trim().Length > 0)
                    clips[clip] = kfa[2].Trim();
        return clips;
    }

    /// <summary>Parses the CSV texts (rows keyed by their numeric ID column; headers skipped).
    /// <c>anims.csv</c> (animation set -> clip ids) and <c>animnifs.csv</c> (clip id -> .kfa) are optional.</summary>
    public static MonsterTables Parse(string monstersCsv, string monnifsCsv, string skinsCsv,
        string animsCsv = "", string animnifsCsv = "")
    {
        Dictionary<int, string[]> skins = ById(skinsCsv);
        return new MonsterTables(ById(monstersCsv), ById(monnifsCsv),
            skins.Where(kv => kv.Value.Length > 2 && kv.Value[2].Trim().Length > 0)
                 .ToDictionary(kv => kv.Key, kv => kv.Value[2].Trim()),
            ById(animsCsv), ById(animnifsCsv));
    }

    private static Dictionary<int, string[]> ById(string csv)
    {
        var d = new Dictionary<int, string[]>();
        foreach (string raw in csv.Split('\n'))
        {
            string[] c = raw.TrimEnd('\r').Split(',');
            if (c.Length > 1 && int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                d.TryAdd(id, c);
        }
        return d;
    }

    /// <summary>Every model id in monsters.csv, in order.</summary>
    public IEnumerable<int> ModelIds => _monsters.Keys.Order();

    /// <summary>The figure for server model id <paramref name="modelId"/>, or null if a table lacks it.</summary>
    public FigureSpec? Resolve(int modelId)
    {
        if (!_monsters.TryGetValue(modelId, out string[]? m) || m.Length < 3 + Slots.Length
            || !int.TryParse(m[2], out int figure) || !_monnifs.TryGetValue(figure, out string[]? n) || n.Length < 3)
            return null;
        var skins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int s = 0; s < Slots.Length; s++)
            if (int.TryParse(m[3 + s], out int skin) && skin > 0 && _skins.TryGetValue(skin, out string? file))
                skins[Slots[s]] = file;
        int version = n.Length > 12 && int.TryParse(n[12], out int v) ? v : 0;
        // The face (column 18, "Face") is a skin id like the slot columns: 8 = b_mhed1.tga "Briton Male
        // Head", 1606 = HO_fhed1.tga "Half Ogre Female Head 1"; 0 = none.
        int face = m.Length > 18 && int.TryParse(m[18], out int f) ? f : 0;
        if (face > 0 && _skins.TryGetValue(face, out string? faceSkin))
            skins["Face"] = faceSkin;
        string nifName = n[2].Trim();
        float scale = m.Length > 12 && float.TryParse(m[12], NumberStyles.Float, CultureInfo.InvariantCulture, out float sc) && sc > 0
            ? sc / 100f : 1f;
        return new FigureSpec(modelId, m[1].Trim(), nifName + ".nif", version, skins, scale, face) { Clips = ClipsOf(n) };
    }
}

public static class MonsterFigure
{
    /// <summary>
    /// Dresses a classic figure: of each slot's variants only variant 1 stays visible (hidden ones
    /// become helpers, which the exporter drops), wearing that slot's skin. Head variants A and B both
    /// stay (A the face, B the hair) unless neither has a skin or texture; low-detail bodies (Lod*) and,
    /// unless the model has a cloak skin, cloaks are hidden.
    /// </summary>
    public static NifModel Dress(NifModel figure, FigureSpec spec)
    {
        var meshes = new List<NifMesh>(figure.Meshes.Count);
        // A full player figure (every variant of every slot: 44 parts) whose low-detail bodies aren't
        // named Lod*: HSharF/M, NSvartalfF/M and BHalfOgreM carry them as four whole-body "Editable Mesh"
        // shapes where the others have Lod1a-d. The small class figures (BScoutM: 5 parts and 2
        // unnamed shapes) are left as read.
        var slots = figure.Meshes.Select(m => SlotOf(m.ShapeName).Slot).ToList();
        bool unnamedAreLods = slots.Count(s => s.Length > 0) >= FullFigureParts && !slots.Contains("Lod");
        foreach (NifMesh mesh in figure.Meshes)
        {
            (string slot, string variant) = SlotOf(mesh.ShapeName);
            bool shown = slot switch
            {
                "" => !mesh.Helper && !unnamedAreLods,  // not a part shape: leave as read
                "Lod" => false,
                "Cloak" => variant == "1" && spec.Skins.ContainsKey("Cloak"),
                _ => variant == "1",
            };
            // Both head parts wear the hair skin ("Head") on UV set 0, with the face skin ("Face") laid
            // over it on set 1. A creature is one shape, not named parts (Wolf.nif's "Editable Mesh"):
            // it wears the Body skin.
            bool head = slot is "HeadA" or "HeadB";
            string skinSlot = head ? "Head" : slot == "" ? "Body" : slot;
            string? face = head && mesh.UvSets is { Length: > 1 } && spec.Skins.TryGetValue("Face", out string? fs) ? fs : null;
            // A head part with no skin and no texture of its own is left off: "Headless Elf" (model 2222)
            // sets Head and Face to 0.
            if (head && mesh.TextureFile is null && face is null && !spec.Skins.ContainsKey(skinSlot))
                shown = false;
            string? skin = shown && (slot.Length > 0 || mesh.TextureFile is null)
                && spec.Skins.TryGetValue(skinSlot, out string? s) ? s : null;
            meshes.Add(mesh with
            {
                Helper = mesh.Helper || !shown,
                TextureFile = skin ?? mesh.TextureFile,
                DecalTextureFile = shown ? face : null,
                DecalTexCoords = shown && face is not null ? mesh.UvSets![1] : null,
            });
        }
        return new NifModel { Version = figure.Version, Joints = figure.Joints, Meshes = meshes };
    }

    private const int FullFigureParts = 20;

    // A classic head is drawn the way multi-texturing would: each head part (HeadA, HeadB) wears the hair
    // skin on UV set 0 and the face skin on set 1 over it, through the face's alpha (213 of the install's
    // 218 <race>_<sex>hed textures cut out ~30%: brows, cheek marks, the rim). Evidence: both parts carry
    // the same set 1; drawn on set 0 the face wrapped round the head (owner, 2026-11-03); retail's troll
    // (Erikstaad market) shows its hair skin's colour in the face's brow and cheek cut-outs, while on
    // NTrollM the hair part lies ~0.44 NIF units outside the face, so a separate face shell can't be on
    // top there. OfflineDAoC's GPL pipeline also draws the face from set 1 ('HeadA1@1').

    // The number after the colon is one part numbering shared by every classic figure (surveyed over the
    // install's figures: each id carries the same part name in 30-51 files, a handful of outliers aside).
    // Some figures (HFirbolgF, HSharM/F) name their shapes only "Figure:<id>"; the id gives the part.
    private static readonly string[] PartIds =
    [
        "Body1", "Body1b", "Body2", "Body2b", "Body3", "Body3b", "LBody1", "LBody2", "LBody3", "Arms1",      //  0- 9
        "Arms2", "Arms3", "Arms4", "Gloves1", "Legs1", "Boots1", "Boots2", "Cloak1", "Cloak2", "HeadA1",     // 10-19
        "HeadA2", "HeadA3", "HeadA4", "HeadA5", "HeadB1", "HeadB2", "HeadB3", "HeadB4", "HeadB5", "Cloak3",  // 20-29
        "Body4", "Body4b", "Arms5", "Arms6", "Gloves2", "Gloves3", "Legs2", "Boots3", "Legs3", "LBody4",     // 30-39
        "Lod1a", "Lod1b", "Lod1c", "Lod1d", "Body5", "Body5b", "Legs4", "Boots4", "Boots5",                  // 40-48
    ];

    // "Body1b:3" -> ("Body", "1b"); "HeadA1:19" -> ("HeadA", "1"); "Lod1a" -> ("Lod", "1a");
    // "Figure:6" -> ("LBody", "1") by its part id; else ("", "").
    internal static (string Slot, string Variant) SlotOf(string shapeName)
    {
        string[] split = shapeName.Split(':');
        (string, string)? named = Named(split[0]);
        if (named is { } n) return n;
        if (split.Length == 2 && split[0] == "Figure" && int.TryParse(split[1], out int id) && id >= 0 && id < PartIds.Length
            && Named(PartIds[id]) is { } byId)
            return byId;
        return ("", "");

        static (string, string)? Named(string part)
        {
            foreach (string slot in new[] { "HeadA", "HeadB", "LBody", "Body", "Arms", "Gloves", "Legs", "Boots", "Cloak", "Lod" })
                if (part.StartsWith(slot, StringComparison.OrdinalIgnoreCase) && part.Length > slot.Length && char.IsDigit(part[slot.Length]))
                    return (slot, part[slot.Length..]);
            return null;
        }
    }
}
