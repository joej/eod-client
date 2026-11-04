// SPDX-License-Identifier: GPL-3.0-or-later
//
// Catacombs figures (roadmap M7b; open question W13): retail builds every player race from these. A
// Catacombs figure is a skeleton (figures/fig3/sfigNNN.mpk, "<nif>_skeleton.nif") plus one body part
// per slot from figures/fig3/figNNN.mpk, each part skinned to the skeleton's bones by name. The tables
// are in the install's gamedata.mpk:
//   monsters.csv  a classic model id's Catacombs version: "Cata Nif" (a monnifs row) and a skin id per
//                 slot (CatSkin Body, Lbody, Head, Arms, Gloves, Legs, Boots, Cloak, Hair); e.g. 32
//                 Briton Male -> 582 Cata Briton Male, CthBody01_ss_bri_M.dds, Bri_m_Head01.dds, ...
//   monnifs.csv   the figure's NIF name (bri_m), animation set, "Cata Index" (its fig3map figure) and
//                 "Skeleton Archive" (sfig001)
//   fig3map.csv   (figure, slot) -> the part ids of each variant: slot 0 head, 1 body, 2 lbody,
//                 3 legs, 4 boots, 5 arms, 6 gloves, 7 cloak, 8 hair, 9 full helm
//   fig3parts.csv part id -> file name and archive number
// A clothing skin's name carries the mesh variant it is drawn on (CthBody01 = body mesh 1, chnBody05 =
// 5), as OfflineDAoC's GPL pipeline (nif_character.py, cata_outfit) found; the approach is adapted
// from there. Not yet: cloaks (drawn on their second UV set), face morphs, eye colour, hair styles
// and colours from the player's customisation, worn equipment. Owner-run: reads the owner's install.

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using DaocAssets.Nif;

namespace DaocAssets.Figures;

/// <summary>One body part of a Catacombs figure: its file in <c>figures/fig3/fig&lt;Archive&gt;.mpk</c>
/// and the skin it wears.</summary>
public sealed record CataPart(int Slot, int Archive, string Nif, string? Skin);

/// <summary>A server model id's Catacombs figure: skeleton, parts, clips and scale. <see cref="Figure"/> is
/// its fig3 figure number (monnifs "Cata Index"), which keys the race's appearance options
/// (<see cref="CataLooks"/>); <see cref="HairStyles"/> holds every hair style's part (null: bald), and
/// <see cref="DefaultHair"/> the one the row wears.</summary>
public sealed record CataSpec(int ModelId, string Name, int SkeletonArchive, string Skeleton,
    IReadOnlyList<CataPart> Parts, float Scale)
{
    public int Figure { get; init; }
    public IReadOnlyList<CataPart?> HairStyles { get; init; } = [];
    public int DefaultHair { get; init; }

    /// <summary>Every mesh variant of the worn slots (fig3map slots 1 body, 3 legs, 4 boots, 5 arms,
    /// 6 gloves, 7 cloak), variant n at index n - 1 (null: the race has none), for dressing the figure in
    /// worn items; and the variant the row itself wears per slot (0 for none: no cloak).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<CataPart?>> Variants { get; init; } = new Dictionary<int, IReadOnlyList<CataPart?>>();
    public IReadOnlyDictionary<int, int> DefaultVariants { get; init; } = new Dictionary<int, int>();

    /// <summary>Clip name (idle, walk, run, back) -> .kfa file in anims/.</summary>
    public IReadOnlyDictionary<string, string> Clips { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A Catacombs race's appearance options, each list indexed as the character's appearance bytes count
/// (from 0): face textures (fig3facemap), eye colour textures multiplied over the face
/// (fig3eyecolormap), skin tints (fig3skincolormap -> fig3tints RGB, 0..1), and per hair style its
/// colours, each a texture and a tint (fig3haircolormap).
/// </summary>
public sealed record CataLooks(
    IReadOnlyList<string?> Faces, IReadOnlyList<string?> Eyes, IReadOnlyList<Vector3> SkinTints,
    IReadOnlyList<IReadOnlyList<(string? Texture, Vector3 Tint)>> HairColours)
{
    /// <summary>The race's face sliders, in its creation screen's order (fig3descriptions "Morphs":
    /// Nose, Eyes, Lips, Jaw for a Briton).</summary>
    public IReadOnlyList<string> SliderLabels { get; init; } = [];
}

/// <summary>A face slider: its label and the head's morph targets at its two ends (indices into the
/// head mesh's targets, -1 when the head lacks that end). 0 on the slider is fully <see cref="Low"/>,
/// 8 fully <see cref="High"/>, 4 neither.</summary>
public sealed record CataSlider(string Label, int Low, int High);

/// <summary>Body slot names, as the mesh names a figure's parts carry (fig3map slot numbers 0..9).</summary>
public static class CataSlots
{
    public static readonly string[] Names = ["head", "body", "lbody", "legs", "boots", "arms", "gloves", "cloak", "hair", "helm"];
}

public static partial class CatacombsFigure
{
    /// <summary>fig3map slots worn items dress: body, legs, boots, arms, gloves, cloak.</summary>
    public static readonly int[] WornSlots = [1, 3, 4, 5, 6, 7];

    // monsters.csv columns (0-based) of a model's Catacombs version.
    internal const int CataNifColumn = 67;
    private static readonly (int Slot, int Column)[] SkinColumns =
        [(1, 68), (2, 69), (0, 70), (5, 71), (6, 72), (3, 73), (4, 74), (8, 76)];

    /// <summary>
    /// The Catacombs figure of <paramref name="modelId"/>, or null when the model has none or a table
    /// lacks a row. Each slot with a skin gets one part: the variant its skin names (else variant 1).
    /// </summary>
    internal static CataSpec? Resolve(int modelId, string[] monster, Dictionary<int, string[]> monnifs,
        Dictionary<int, string> skins, Dictionary<(int Figure, int Slot), string[]> fig3map,
        Dictionary<int, string[]> fig3parts, Func<string[], IReadOnlyDictionary<string, string>> clips,
        Dictionary<(int Figure, int Style), string[]>? hairColours = null)
    {
        if (monster.Length <= CataNifColumn || !int.TryParse(monster[CataNifColumn], out int nifRow)
            || !monnifs.TryGetValue(nifRow, out string[]? n) || n.Length < 16
            || !int.TryParse(n[13], out int figure) || !int.TryParse(n[15], out int skeletonArchive))
            return null;
        var parts = new List<CataPart>();
        foreach ((int slot, int column) in SkinColumns)
        {
            if (column >= monster.Length || !int.TryParse(monster[column], out int skinId) || skinId <= 0
                || !skins.TryGetValue(skinId, out string? skin) || !fig3map.TryGetValue((figure, slot), out string[]? variants))
                continue;
            int variant = VariantOf(skin);
            string? partId = variant <= variants.Length && variants[variant - 1].Length > 0 ? variants[variant - 1]
                : variants.FirstOrDefault(v => v.Length > 0);
            if (partId is null || !int.TryParse(partId, out int pid) || !fig3parts.TryGetValue(pid, out string[]? p)
                || p.Length < 6 || !int.TryParse(p[5], out int archive))
                continue;
            parts.Add(new CataPart(slot, archive, p[2].Trim() + ".nif", skin));
        }
        // No hair skin in the row (Celt Female 1, Lurikeen Male 1): hair style 1 in colour 1 from
        // fig3haircolormap ((figure, style) -> per colour: skin, tint, ?), as for a new character.
        if (!parts.Any(p => p.Slot == 8) && hairColours is not null && hairColours.TryGetValue((figure, 1), out string[]? hc)
            && hc.Length > 0 && int.TryParse(hc[0], out int hairSkin) && skins.TryGetValue(hairSkin, out string? hairFile)
            && fig3map.TryGetValue((figure, 8), out string[]? hairs) && hairs.Length > 0 && int.TryParse(hairs[0], out int hid)
            && fig3parts.TryGetValue(hid, out string[]? hp) && hp.Length >= 6 && int.TryParse(hp[5], out int harch))
            parts.Add(new CataPart(8, harch, hp[2].Trim() + ".nif", hairFile));
        if (parts.Count == 0)
            return null;
        // Every hair style of the race (fig3map slot 8, variant n = style n - 1); an empty one is bald.
        var hairStyles = new List<CataPart?>();
        if (fig3map.TryGetValue((figure, 8), out string[]? styles))
            foreach (string id in styles)
                hairStyles.Add(int.TryParse(id, out int sid) && fig3parts.TryGetValue(sid, out string[]? sp) && sp.Length >= 6
                    && int.TryParse(sp[5], out int sarch) ? new CataPart(8, sarch, sp[2].Trim() + ".nif", null) : null);
        while (hairStyles.Count > 0 && hairStyles[^1] is null)
            hairStyles.RemoveAt(hairStyles.Count - 1);
        var slotVariants = new Dictionary<int, IReadOnlyList<CataPart?>>();
        var defaults = new Dictionary<int, int>();
        foreach (int slot in WornSlots)
        {
            if (!fig3map.TryGetValue((figure, slot), out string[]? ids))
                continue;
            var list = ids.Select(id => int.TryParse(id, out int vid) && fig3parts.TryGetValue(vid, out string[]? vp) && vp.Length >= 6
                && int.TryParse(vp[5], out int varch) ? new CataPart(slot, varch, vp[2].Trim() + ".nif", null) : null).ToList();
            while (list.Count > 0 && list[^1] is null)
                list.RemoveAt(list.Count - 1);
            if (list.Count == 0)
                continue;
            slotVariants[slot] = list;
            CataPart? worn = parts.FirstOrDefault(p => p.Slot == slot);
            defaults[slot] = worn is null ? 0
                : Math.Max(1, list.FindIndex(v => v is not null && v.Nif.Equals(worn.Nif, StringComparison.OrdinalIgnoreCase)) + 1);
        }
        CataPart? rowHair = parts.FirstOrDefault(p => p.Slot == 8);
        int defaultHair = rowHair is null ? 0
            : Math.Max(0, hairStyles.FindIndex(h => h is not null && h.Nif.Equals(rowHair.Nif, StringComparison.OrdinalIgnoreCase)));
        float scale = monster.Length > 81 && float.TryParse(monster[81], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0
            ? s / 100f : 1f;
        return new CataSpec(int.Parse(monster[0], CultureInfo.InvariantCulture), n[1].Trim(), skeletonArchive,
            n[2].Trim() + "_skeleton.nif", parts, scale)
        { Clips = clips(n), Figure = figure, HairStyles = hairStyles, DefaultHair = defaultHair, Variants = slotVariants, DefaultVariants = defaults };
    }

    // A head's morph feature codes come in pairs, one feature each (surveyed over all 36 race heads,
    // whose labels "FM1=3 ..." match their race's slider names in 35): 1/2 nose, 3/4 eyes, 5/6 lips,
    // 7/8 jaw, 9/10 ears, 11/12 nose width, 13/14 tusks, 15/16 sharpness, 17/18 tines, 19/20 spines;
    // 21-24 are moods. Which end of a pair is the slider's low end is unconfirmed (odd taken as low).
    private static readonly (string Label, int Code)[] FeatureCodes =
    [
        ("Nose Length", 1), ("Nose Width", 11), ("Jaw Length", 7), ("Nose", 1), ("Eyes", 3), ("Lips", 5),
        ("Jaw", 7), ("Ears", 9), ("Tusks", 13), ("Sharpness", 15), ("Tines", 17), ("Spines", 19),
    ];

    /// <summary>
    /// The head's face sliders: each label's feature pair by name; a label whose feature the head lacks
    /// (the elf man's "Jaw", where his head carries lips) takes the head's next unused pair below the
    /// moods, in file order. <paramref name="codes"/> is the head mesh's <see cref="NifMesh.MorphCodes"/>.
    /// </summary>
    public static IReadOnlyList<CataSlider> Sliders(IReadOnlyList<string> labels, IReadOnlyList<int> codes)
    {
        var used = new HashSet<int>();
        var pairs = codes.Where(c => c is > 0 and < 21).Select(c => (c + 1) / 2).Distinct().ToList();
        var sliders = new List<CataSlider>();
        foreach (string label in labels)
        {
            int code = FeatureCodes.FirstOrDefault(f => f.Label.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase)).Code;
            int pair = code > 0 && pairs.Contains((code + 1) / 2) && !used.Contains((code + 1) / 2) ? (code + 1) / 2
                : pairs.FirstOrDefault(p => !used.Contains(p));
            if (pair > 0) used.Add(pair);
            sliders.Add(new CataSlider(label.Trim(), Index(2 * pair - 1), Index(2 * pair)));
        }
        return sliders;

        int Index(int c) => c > 0 ? codes.ToList().IndexOf(c) : -1;
    }

    // "CthBody01_ss_bri_M.dds" -> 1, "chnBody05_..." -> 5; 1 when the name carries none.
    internal static int VariantOf(string skin)
    {
        Match m = PartNumber().Match(skin);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) && v > 0 ? v : 1;
    }

    [GeneratedRegex(@"(?:body|arms|legs|boots?|gloves?)(\d\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PartNumber();

    /// <summary>
    /// Binds body parts to a skeleton by bone name. A part's skin is either named bones over vertices
    /// already in the rest pose (most parts) or a self-contained skin over a copy of the skeleton (hair);
    /// both end up as rest-pose vertices weighted to the skeleton's joints, whose inverse bind is the
    /// inverse of the joint's rest world transform (the parts' bind pose is the skeleton's rest pose).
    /// A rigid shape follows the root bone. <paramref name="parts"/> pairs each part file with the skin
    /// it wears (null: its own texture).
    /// </summary>
    public static NifModel Assemble(IReadOnlyList<NifJoint> skeleton, IEnumerable<(NifModel Part, string? Skin)> parts) =>
        Assemble(skeleton, parts.Select(p => (p.Part, p.Skin, "")));

    /// <summary>As above; each part's meshes are named <c>Label</c>, <c>Label_1</c>, ... (empty: the
    /// shape's own name), so the client can find a slot (<c>head</c>) or a hair style (<c>hair2</c>).</summary>
    public static NifModel Assemble(IReadOnlyList<NifJoint> skeleton, IEnumerable<(NifModel Part, string? Skin, string Label)> parts)
    {
        var skel = new NifModel { Version = default, Meshes = [], Joints = skeleton };
        Matrix4x4[] world = skel.JointWorlds();
        var jointOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int j = 0; j < skeleton.Count; j++)
            jointOf.TryAdd(skeleton[j].Name.Trim(), j);
        int root = jointOf.TryGetValue("Bip01", out int r) ? r : 0;

        var meshes = new List<NifMesh>();
        NifVersion version = default;
        foreach ((NifModel part, string? skin, string label) in parts)
        {
            version = part.Version;
            int shapeNo = 0;
            Matrix4x4[] partWorld = part.JointWorlds();
            foreach (NifMesh mesh in part.Meshes)
            {
                if (mesh.Helper)
                    continue;
                Vector3[] verts = mesh.Vertices;
                Vector3[]? normals = mesh.Normals;
                Vector3[][]? morphs = mesh.MorphTargets;
                string[] names;
                if (mesh.SkinBoneNames is not null)
                    names = mesh.SkinBoneNames;
                else if (mesh.SkinJoints is { } own && mesh.Skin is not null)
                {
                    // A self-contained skin: pose it in its own file's rest pose.
                    names = own.Select(b => part.Joints[b.Joint].Name).ToArray();
                    var m = own.Select(b => b.InverseBind * partWorld[b.Joint]).ToArray();
                    // Morph offsets move with the skin's linear part, weighted like the vertices.
                    if (mesh.MorphTargets is not null)
                        morphs = mesh.MorphTargets.Select(target => target.Select((d, v) =>
                            mesh.Skin[v].Length == 0 ? d
                                : mesh.Skin[v].Aggregate(Vector3.Zero, (acc, bw) => acc + Vector3.TransformNormal(d, m[bw.Bone]) * bw.Weight)).ToArray()).ToArray();
                    verts = new Vector3[mesh.Vertices.Length];
                    normals = mesh.Normals is null ? null : new Vector3[mesh.Vertices.Length];
                    for (int v = 0; v < verts.Length; v++)
                    {
                        foreach ((int bone, float w) in mesh.Skin[v])
                        {
                            verts[v] += Vector3.Transform(mesh.Vertices[v], m[bone]) * w;
                            if (normals is not null)
                                normals[v] += Vector3.TransformNormal(mesh.Normals![v], m[bone]) * w;
                        }
                        if (mesh.Skin[v].Length == 0)
                            verts[v] = mesh.Vertices[v];
                        if (normals is not null)
                            normals[v] = normals[v] == Vector3.Zero ? mesh.Normals![v] : Vector3.Normalize(normals[v]);
                    }
                }
                else
                    names = [];

                // The skeleton joints this mesh uses, and each vertex's influences re-indexed to them.
                var used = new List<int>();
                var slotOf = new Dictionary<int, int>();
                int Slot(int joint)
                {
                    if (!slotOf.TryGetValue(joint, out int s))
                    {
                        s = used.Count;
                        used.Add(joint);
                        slotOf[joint] = s;
                    }
                    return s;
                }
                var weights = new (int, float)[verts.Length][];
                for (int v = 0; v < verts.Length; v++)
                {
                    var list = new List<(int, float)>();
                    if (mesh.Skin is not null && names.Length > 0)
                        foreach ((int bone, float w) in mesh.Skin[v])
                            if (bone < names.Length && jointOf.TryGetValue(names[bone].Trim(), out int j))
                                list.Add((Slot(j), w));
                    float sum = list.Sum(x => x.Item2);
                    weights[v] = sum > 0 ? list.Select(x => (x.Item1, x.Item2 / sum)).ToArray() : [(Slot(root), 1f)];
                }
                string shape = label.Length == 0 ? mesh.ShapeName : shapeNo == 0 ? label : $"{label}_{shapeNo}";
                shapeNo++;
                meshes.Add(mesh with
                {
                    Name = $"{mesh.Name}_{meshes.Count}",
                    ShapeName = shape,
                    Vertices = verts,
                    Normals = normals,
                    MorphTargets = morphs,
                    TextureFile = skin ?? mesh.TextureFile,
                    SkinBoneNames = null,
                    SkinJoints = used.Select(j => (j, Invert(world[j]))).ToArray(),
                    Skin = weights,
                });
            }
        }
        return new NifModel { Version = version, Joints = skeleton, Meshes = meshes };
    }

    private static Matrix4x4 Invert(Matrix4x4 m) =>
        Matrix4x4.Invert(m, out Matrix4x4 inv) ? inv : Matrix4x4.Identity;

    /// <summary>
    /// A clip for a Catacombs skeleton: rotations on every bone, translations on the root (Bip01) only.
    /// The Catacombs clips are shared across races and some were keyed on other proportions; their
    /// translations would stretch each skeleton's bones (OfflineDAoC's finding: Cat_troll_idle moves the
    /// troll's eyelids 2.8 units).
    /// </summary>
    public static KfaClip RotationsOnly(KfaClip clip) => clip with
    {
        Tracks = clip.Tracks.Select(t => t.Bone.Trim().Equals("Bip01", StringComparison.OrdinalIgnoreCase) ? t
            : t with { Translations = [] }).ToList(),
    };
}
