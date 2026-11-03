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
        return Parse(Text("monsters.csv"), Text("monnifs.csv"), Text("skins.csv"), Text("anims.csv"), Text("animnifs.csv"));
    }

    /// <summary>Parses the CSV texts (rows keyed by their numeric ID column; headers skipped).
    /// <c>anims.csv</c> (animation set -> clip ids) and <c>animnifs.csv</c> (clip id -> .kfa) are optional.</summary>
    public static MonsterTables Parse(string monstersCsv, string monnifsCsv, string skinsCsv,
        string animsCsv = "", string animnifsCsv = "")
    {
        static Dictionary<int, string[]> ById(string csv)
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
        Dictionary<int, string[]> skins = ById(skinsCsv);
        return new MonsterTables(ById(monstersCsv), ById(monnifsCsv),
            skins.Where(kv => kv.Value.Length > 2 && kv.Value[2].Trim().Length > 0)
                 .ToDictionary(kv => kv.Key, kv => kv.Value[2].Trim()),
            ById(animsCsv), ById(animnifsCsv));
    }

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
        // The face (column 18, "Face"): a classic head's face texture is <race>_<sex>hed<n>
        // (b_mhed1..4: Briton male), chosen by the face number. Provisional: faces run 1-8 against
        // four textures per race and sex, so the number is folded onto them (how the other half
        // differs, perhaps by head shape, is unknown).
        int face = m.Length > 18 && int.TryParse(m[18], out int f) ? f : 0;
        string nifName = n[2].Trim();
        if (face > 0 && nifName.Length > 1)
        {
            string guess = $"{nifName[0]}_{nifName[^1]}hed{(face - 1) % 4 + 1}.tga";
            if (_skins.Values.Contains(guess, StringComparer.OrdinalIgnoreCase))
                skins["Face"] = guess;
        }
        float scale = m.Length > 12 && float.TryParse(m[12], NumberStyles.Float, CultureInfo.InvariantCulture, out float sc) && sc > 0
            ? sc / 100f : 1f;
        // The figure's animation set (monnifs column 3) -> a clip id per action -> its .kfa file.
        var clips = new Dictionary<string, string>();
        if (int.TryParse(n[3], out int set) && _anims.TryGetValue(set, out string[]? a))
            foreach ((string clip, int col) in ClipColumns)
                if (col < a.Length && int.TryParse(a[col], out int id) && id > 0
                    && _animnifs.TryGetValue(id, out string[]? kfa) && kfa.Length > 2 && kfa[2].Trim().Length > 0)
                    clips[clip] = kfa[2].Trim();
        return new FigureSpec(modelId, m[1].Trim(), nifName + ".nif", version, skins, scale, face) { Clips = clips };
    }
}

public static class MonsterFigure
{
    /// <summary>
    /// Dresses a classic figure: of each slot's variants only variant 1 stays visible (hidden ones
    /// become helpers, which the exporter drops), wearing that slot's skin. Head variants A and B both
    /// stay (A the face, B the hair); low-detail bodies (Lod*) and, unless the model has
    /// a cloak skin, cloaks are hidden.
    /// </summary>
    public static NifModel Dress(NifModel figure, FigureSpec spec)
    {
        var meshes = new List<NifMesh>(figure.Meshes.Count);
        foreach (NifMesh mesh in figure.Meshes)
        {
            (string slot, string variant) = SlotOf(mesh.ShapeName);
            bool shown = slot switch
            {
                "" => !mesh.Helper,                    // not a part shape: leave as read
                "Lod" => false,
                "Cloak" => variant == "1" && spec.Skins.ContainsKey("Cloak"),
                _ => variant == "1",
            };
            // HeadA is the face (its skin from the face number), HeadB the hair (the "Head" skin).
            string skinSlot = slot switch { "HeadA" => "Face", "HeadB" => "Head", _ => slot };
            string? skin = shown && spec.Skins.TryGetValue(skinSlot, out string? s) ? s : null;
            meshes.Add(mesh with
            {
                Helper = mesh.Helper || !shown,
                TextureFile = skin ?? mesh.TextureFile,
            });
        }
        return new NifModel { Version = figure.Version, Joints = figure.Joints, Meshes = meshes };
    }

    // "Body1b:3" -> ("Body", "1b"); "HeadA1:19" -> ("HeadA", "1"); "Lod1a" -> ("Lod", "1a"); else ("", "").
    internal static (string Slot, string Variant) SlotOf(string shapeName)
    {
        string part = shapeName.Split(':')[0];
        foreach (string slot in new[] { "HeadA", "HeadB", "LBody", "Body", "Arms", "Gloves", "Legs", "Boots", "Cloak", "Lod" })
            if (part.StartsWith(slot, StringComparison.Ordinal) && part.Length > slot.Length && char.IsDigit(part[slot.Length]))
                return (slot, part[slot.Length..]);
        return ("", "");
    }
}
