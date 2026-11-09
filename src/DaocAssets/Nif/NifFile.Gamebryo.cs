// SPDX-License-Identifier: GPL-3.0-or-later
//
// Builds a model from a fully decoded Gamebryo 10.1 file (Gamebryo1010.Parse): the node tree with
// its transforms, per-shape textures, material colours and alpha, and skins. This replaces the 10.1
// geometry scan for every file that decodes completely; a file that doesn't (a block type we don't
// decode yet) still falls back to the scan.
//
// Rigid shapes are baked to world space exactly as the 4.x path does (same helper-name, Hidden-flag
// and property-inheritance rules). Skinned shapes (NiSkinInstance) keep their vertices in skin space,
// and the model carries the skeleton: every NiNode in the tree becomes a joint. A skinned vertex is
// placed as  v_world = v * BoneBind_i * BoneWorld_i  (row vectors, summed by weight), BoneBind_i being
// NiSkinData's per-bone transform; so the glTF inverse bind matrix of bone i is BoneBind_i. In the
// bind pose BoneBind_i * BoneWorld_i equals the skinned shape's own world transform for every bone,
// and NiSkinData's overall Skin Transform is that transform's inverse (measured on bcommonm.nif:
// Body1's vertices are stored around the origin, its shape sits at z 53.3, and every bone's
// bind * world lands on (0, 2.0, 53.3); including the Skin Transform cancels the placement and drops
// the torso, head and hands to the feet). BindPoseError measures that, and settled both this and the
// matrix convention below.

using System.Numerics;

namespace DaocAssets.Nif;

public static partial class NifFile
{
    /// <summary>
    /// A NiAVObject/skin transform as a row-vector matrix (p' = p * M). The 3x3 is stored so that
    /// p' = R * p with R read row-major (the convention the 4.x path uses, verified on assembled
    /// buildings), i.e. the row-vector matrix is its transpose. Measured on skinned figures: this
    /// reading makes every bone's bind chain agree (see <see cref="BindPoseError"/>).
    /// </summary>
    internal static Matrix4x4 ToMatrix(NifTransform t)
    {
        float[] r = t.Rot;
        float s = t.Scale;
        return new Matrix4x4(
            r[0] * s, r[3] * s, r[6] * s, 0,
            r[1] * s, r[4] * s, r[7] * s, 0,
            r[2] * s, r[5] * s, r[8] * s, 0,
            t.Trans.X, t.Trans.Y, t.Trans.Z, 1);
    }

    // The tree walk over a parsed 10.1 file. Throws if the file doesn't decode completely or holds no
    // geometry: without block sizes there is no safe way to skip what we can't read.
    private static NifModel ReadGamebryoTree(ReadOnlySpan<byte> bytes, NifVersion version)
    {
        Gb10File g = Gamebryo1010.Parse(bytes);
        if (!g.Complete)
            throw new NifFormatException(
                $"10.1 NIF stops at {g.StopType ?? "an undecoded block"} ({g.StopReason ?? g.Result.ToString()}); not supported.");
        Gb10Block[] blocks = g.Blocks!;
        var b = new Gb10Builder(blocks);
        foreach (int root in g.Roots.Length > 0 ? g.Roots : [0])
            b.Walk(root, -1, Matrix4x4.Identity, helper: false, alpha: -1, stencil: -1);
        if (b.Meshes.Count == 0)
            throw new NifFormatException("10.1 NIF has no geometry.");
        // A skeleton only when something is skinned to it: a static 10.1 object exports as baked
        // meshes like a 4.x one (its nodes would otherwise become joints, and repeated helper names
        // such as "collidee" made the glTF writer refuse the file).
        bool skinned = b.Meshes.Any(m => m.SkinJoints is { Length: > 0 });
        var nodeWorlds = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach ((int i, Matrix4x4 w) in WorldTransforms(g))
            if (!string.IsNullOrEmpty(blocks[i].Name) && blocks[i].Type is "NiNode" or "NiLODNode" or "NiBillboardNode" or "NiTriShape" or "NiTriStrips")
                nodeWorlds.TryAdd(blocks[i].Name.Trim().ToLowerInvariant(), w);
        return new NifModel { Version = version, Meshes = b.Meshes, Joints = skinned ? b.Joints : [], NodeWorlds = nodeWorlds };
    }

    /// <summary>
    /// The node tree of a 10.1 file as joints (parents first), for a Catacombs skeleton
    /// (<c>figures/fig3/sfigNNN.mpk</c>, e.g. <c>Bri_m_skeleton.nif</c>): it holds bones and no skinned
    /// shapes, so <see cref="Read"/> gives it no joints. Null if the file doesn't decode completely.
    /// </summary>
    public static IReadOnlyList<NifJoint>? ReadSkeleton(ReadOnlySpan<byte> bytes)
    {
        Gb10File g = Gamebryo1010.Parse(bytes);
        if (!g.Complete)
            return null;
        var b = new Gb10Builder(g.Blocks!);
        foreach (int root in g.Roots.Length > 0 ? g.Roots : [0])
            b.Walk(root, -1, Matrix4x4.Identity, helper: false, alpha: -1, stencil: -1);
        return b.Joints;
    }

    /// <summary>
    /// For a 10.1 file with self-contained skins: the largest difference (max matrix element, NIF
    /// units for the translation part) between BoneBind_i * BoneWorld_i and the skinned shape's own
    /// world transform, over every bone of every skin. Near zero when the file's skeleton is in its
    /// bind pose and our skinning formula and matrix convention are right. Null if the file doesn't
    /// decode completely or has no self-contained skin.
    /// </summary>
    public static float? BindPoseError(ReadOnlySpan<byte> bytes)
    {
        Gb10File g = Gamebryo1010.Parse(bytes);
        if (!g.Complete)
            return null;
        Gb10Block[] blocks = g.Blocks!;
        Dictionary<int, Matrix4x4> world = WorldTransforms(g);
        float? worst = null;
        for (int s = 0; s < blocks.Length; s++)
        {
            Gb10Block shape = blocks[s];
            if (shape.SkinRef < 0 || !SelfContained(blocks, blocks[shape.SkinRef]) || !world.TryGetValue(s, out Matrix4x4 shapeWorld))
                continue;
            Gb10Block skin = blocks[shape.SkinRef];
            Gb10Block data = blocks[skin.SkinDataRef];
            float err = 0;
            for (int i = 0; i < skin.Bones.Length && i < data.BoneBinds.Length; i++)
                if (world.TryGetValue(skin.Bones[i], out Matrix4x4 bw))
                    err = Math.Max(err, MaxDiff(ToMatrix(data.BoneBinds[i]) * bw, shapeWorld));
            worst = Math.Max(worst ?? 0, err);
        }
        return worst;
    }

    // Every block's world transform from the file's roots down (row vectors).
    private static Dictionary<int, Matrix4x4> WorldTransforms(Gb10File g)
    {
        Gb10Block[] blocks = g.Blocks!;
        var world = new Dictionary<int, Matrix4x4>();
        var stack = new Stack<(int, Matrix4x4)>();
        foreach (int r in g.Roots.Length > 0 ? g.Roots : [0])
            stack.Push((r, Matrix4x4.Identity));
        while (stack.Count > 0)
        {
            (int i, Matrix4x4 parent) = stack.Pop();
            if (i < 0 || i >= blocks.Length || world.ContainsKey(i)) continue;
            Matrix4x4 w = ToMatrix(blocks[i].Local) * parent;
            world[i] = w;
            foreach (int c in blocks[i].Children) stack.Push((c, w));
        }
        return world;
    }

    /// <summary>Diagnostic lines for each skinned shape of a 10.1 file: the shape's world transform,
    /// the skin transform, and per bone the bone's world and bind transforms (translations).</summary>
    public static IEnumerable<string> SkinReport(byte[] bytes)
    {
        Gb10File g = Gamebryo1010.Parse(bytes);
        if (!g.Complete)
            yield break;
        Gb10Block[] blocks = g.Blocks!;
        Dictionary<int, Matrix4x4> world = WorldTransforms(g);
        static string T(Matrix4x4 m) => $"({m.M41:F1},{m.M42:F1},{m.M43:F1})";
        for (int s = 0; s < blocks.Length; s++)
        {
            Gb10Block shape = blocks[s];
            if (shape.SkinRef < 0) continue;
            Gb10Block skin = blocks[shape.SkinRef], data = blocks[skin.SkinDataRef];
            yield return $"{shape.Name}: shape world {T(world.GetValueOrDefault(s))} local {T(ToMatrix(shape.Local))}" +
                $" skin T {T(ToMatrix(data.SkinTransform))} root {(skin.SkeletonRoot >= 0 ? blocks[skin.SkeletonRoot].Name : "-")}";
            for (int i = 0; i < Math.Min(3, skin.Bones.Length); i++)
            {
                Matrix4x4 bw = world.GetValueOrDefault(skin.Bones[i]);
                Matrix4x4 bind = ToMatrix(data.BoneBinds[i]);
                yield return $"    {blocks[skin.Bones[i]].Name}: world {T(bw)} bind {T(bind)}" +
                    $" skinT*bind*world {T(ToMatrix(data.SkinTransform) * bind * bw)} bind*world {T(bind * bw)}";
            }
        }
    }

    private static float MaxDiff(Matrix4x4 a, Matrix4x4 b)
    {
        Matrix4x4 d = a - b;
        float m = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                m = Math.Max(m, Math.Abs(d[r, c]));
        return m;
    }

    // A skin whose bones are distinct NiNodes in this file (a classic figure or creature). A
    // Catacombs part's bone pointers repeat: its bones are in a separate skeleton, found by name.
    private static bool SelfContained(Gb10Block[] blocks, Gb10Block skin) =>
        skin.Bones.Length > 0
        && skin.Bones.All(i => i >= 0 && i < blocks.Length && blocks[i].Type == "NiNode")
        && skin.Bones.Distinct().Count() == skin.Bones.Length;

    private sealed class Gb10Builder(Gb10Block[] blocks)
    {
        public readonly List<NifMesh> Meshes = [];
        public readonly List<NifJoint> Joints = [];
        private readonly Dictionary<int, int> _jointOf = [];       // node block -> joint index
        private readonly Dictionary<int, Matrix4x4> _worldOf = [];
        private readonly List<(int Shape, int Joint)> _pendingSkins = [];
        private readonly HashSet<int> _seen = [];

        public void Walk(int index, int parentJoint, Matrix4x4 parentWorld, bool helper, int alpha, int stencil,
            bool numberedHelper = false, int vertexColor = -1, bool collision = false)
        {
            if (index < 0 || index >= blocks.Length || !_seen.Add(index))
                return;
            Gb10Block b = blocks[index];
            Matrix4x4 local = ToMatrix(b.Local);
            Matrix4x4 world = local * parentWorld;
            bool underHelper = helper || IsHelperNodeName(b.Name) || IsHidden(b.Flags);
            bool underNumbered = numberedHelper || IsNumberedHelperName(b.Name);
            bool underCollision = collision || IsCollisionName(b.Name);
            if (b.Type is "NiNode" or "NiBillboardNode" or "NiLODNode")
            {
                int joint = Joints.Count;
                Joints.Add(new NifJoint { Name = b.Name, Parent = parentJoint, Local = local });
                _jointOf[index] = joint;
                _worldOf[index] = world;
                int nodeAlpha = Find(b.Props, "NiAlphaProperty", alpha);
                int nodeStencil = Find(b.Props, "NiStencilProperty", stencil);
                int nodeVertexColor = Find(b.Props, "NiVertexColorProperty", vertexColor);
                foreach (int child in b.Type == "NiLODNode" ? NearLevel(b) : b.Children)
                    Walk(child, joint, world, underHelper, nodeAlpha, nodeStencil, underNumbered, nodeVertexColor, underCollision);
                if (parentJoint < 0)
                    BindSkins();
            }
            else if (b.Type is "NiTriShape" or "NiTriStrips")
            {
                NifMesh? mesh = Shape(index, b, world, alpha, stencil, vertexColor);
                if (mesh is null)
                    return;
                Meshes.Add(mesh with { Helper = underHelper || (underNumbered && mesh.TextureFile is null), Collision = underCollision });
            }
        }

        // An LOD node draws one child at a time. Keep the nearest level, as the 4.x path does: from
        // NiRangeLODData the child whose near extent is smallest, otherwise (screen-size LOD, or no
        // data) child 0, the highest detail. A billboard node is walked like a plain node: it keeps
        // its stored orientation instead of turning to the camera.
        private int[] NearLevel(Gb10Block lod)
        {
            if (lod.Children.Length <= 1)
                return lod.Children;
            float[] near = lod.LodDataRef >= 0 ? blocks[lod.LodDataRef].LodNear : [];
            if (near.Length != lod.Children.Length)
                return [lod.Children[0]];
            int best = 0;
            for (int i = 1; i < near.Length; i++)
                if (near[i] < near[best]) best = i;
            return [lod.Children[best]];
        }

        private NifMesh? Shape(int index, Gb10Block shape, Matrix4x4 world, int alpha, int stencil, int vertexColor)
        {
            if (shape.DataRef < 0 || blocks[shape.DataRef] is not { Vertices.Length: > 0 } data
                || data.Triangles.Length == 0)
                return null;

            string? texture = null, dark = null, detail = null;
            uint uvSet = 0, darkUv = 0, detailUv = 0;
            Vector4? color = null;
            foreach (int p in shape.Props)
            {
                Gb10Block prop = blocks[p];
                if (prop.Type == "NiTexturingProperty")
                {
                    if (prop.BaseSourceRef >= 0)
                        (texture, uvSet) = (blocks[prop.BaseSourceRef].TextureFile, prop.BaseUvSet);
                    if (prop.DarkSourceRef >= 0)
                        (dark, darkUv) = (blocks[prop.DarkSourceRef].TextureFile, prop.DarkUvSet);
                    if (prop.DetailSourceRef >= 0)
                        (detail, detailUv) = (blocks[prop.DetailSourceRef].TextureFile, prop.DetailUvSet);
                }
                else if (prop.Type == "NiMaterialProperty")
                    color = prop.MaterialColor;
            }
            Vector2[]? Set(uint i) => i < data.UvSets.Length ? data.UvSets[i] : null;
            int vc = Find(shape.Props, "NiVertexColorProperty", vertexColor);
            Vector4[]? colors = vc >= 0 && blocks[vc].VertexMode == 2 && data.Colors?.Length == data.Vertices.Length
                ? data.Colors : null;
            Vector2[]? uvs = data.UvSets.Length == 0 ? null
                : data.UvSets[uvSet < data.UvSets.Length ? (int)uvSet : 0];

            int a = Find(shape.Props, "NiAlphaProperty", alpha);
            int st = Find(shape.Props, "NiStencilProperty", stencil);
            (SurfaceAlpha mode, float cutoff) = a < 0 ? (SurfaceAlpha.Opaque, 0.5f)
                : AlphaMode(blocks[a].AlphaFlags, blocks[a].AlphaThreshold);
            bool bothSides = mode != SurfaceAlpha.Opaque || (st >= 0 && blocks[st].StencilDrawMode == 3);

            // A Catacombs part names its bones in a bone list (NiStringsExtraData), one per skin bone; its
            // bone pointers can still look self-contained (dwa_f_Hair01's one bone points at the file's own
            // "Scene Root"), so the list decides: such a skin is bound by name.
            bool named = shape.SkinRef >= 0 && blocks.FirstOrDefault(x => x.Type == "NiStringsExtraData") is { } list
                && list.Strings.Length >= blocks[shape.SkinRef].Bones.Length;
            bool skinned = shape.SkinRef >= 0 && !named && SelfContained(blocks, blocks[shape.SkinRef]);
            Vector3[] verts = data.Vertices;
            Vector3[]? normals = data.Normals;
            if (!skinned)
            {
                // Rigid (or a Catacombs part, whose skeleton is elsewhere): bake to world.
                verts = verts.Select(v => Vector3.Transform(v, world)).ToArray();
                normals = normals?.Select(n => Vector3.Normalize(Vector3.TransformNormal(n, world))).ToArray();
            }
            else
            {
                _pendingSkins.Add((Meshes.Count, index));
            }
            (string[] Names, (int, float)[][] Weights)? byName = shape.SkinRef >= 0 && !skinned
                ? NamedSkin(blocks[shape.SkinRef], verts.Length) : null;
            (Vector3[][] Targets, int[] Codes)? morphs = Morphs(shape, data.Vertices.Length, skinned ? Matrix4x4.Identity : world);
            return new NifMesh
            {
                Name = $"mesh{index}",
                ShapeName = shape.Name,
                Vertices = verts,
                Normals = normals,
                TexCoords = uvs,
                Triangles = data.Triangles,
                TextureFile = texture,
                UvSets = data.UvSets,
                VertexColors = colors,
                DarkTextureFile = Set(darkUv) is null ? null : dark,
                DarkTexCoords = dark is null ? null : Set(darkUv),
                DetailTextureFile = Set(detailUv) is null ? null : detail,
                DetailTexCoords = detail is null ? null : Set(detailUv),
                BaseColor = color,
                Alpha = mode,
                Additive = a >= 0 && Additive(blocks[a].AlphaFlags),
                AlphaCutoff = cutoff,
                DoubleSided = bothSides,
                SkinBoneNames = byName?.Names,
                Skin = byName?.Weights,
                MorphTargets = morphs?.Targets,
                MorphCodes = morphs?.Codes,
            };
        }

        // The shape's morph targets (its NiGeomMorpherController's NiMorphData), as offsets from the base
        // shape in the same space as the exported vertices (only the linear part of `space` applies to an
        // offset), and their feature codes from the file's label string: "FM1=3\r\nFM2=4..." numbers the
        // morphs from 1 after the base. Null when the shape has no morphs for its vertices.
        private (Vector3[][], int[])? Morphs(Gb10Block shape, int vertexCount, Matrix4x4 space)
        {
            if (shape.Controller < 0 || shape.Controller >= blocks.Length || blocks[shape.Controller].Type != "NiGeomMorpherController")
                return null;
            int dataRef = blocks[shape.Controller].DataRef;
            if (dataRef < 0 || dataRef >= blocks.Length || blocks[dataRef].Type != "NiMorphData")
                return null;
            Gb10Block data = blocks[dataRef];
            if (data.MorphVectors.Length < 2 || data.MorphVectors.Any(m => m.Length != vertexCount))
                return null;
            Vector3[] baseShape = data.MorphVectors[0];
            var targets = new Vector3[data.MorphVectors.Length - 1][];
            for (int m = 1; m < data.MorphVectors.Length; m++)
                targets[m - 1] = data.MorphVectors[m].Select((v, i) =>
                    Vector3.TransformNormal(data.RelativeMorphs ? v : v - baseShape[i], space)).ToArray();
            var codes = new int[targets.Length];
            string label = blocks.Where(x => x.Type == "NiStringExtraData").SelectMany(x => x.Strings)
                .FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Z]+1\s*=")) ?? "";
            foreach (string line in label.Split('\n', '\r'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^[A-Z]+(\d+)\s*=\s*(-?\d+)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= codes.Length)
                    codes[n - 1] = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            return (targets, codes);
        }

        // A Catacombs part's skin: its bones are named by the file's bone list (a NiStringsExtraData
        // with one name per skin bone), or, past the list's end, by the skin's own bone node
        // (hig_f_Hair02's list is shorter than its bones; OfflineDAoC's GPL pipeline does the same).
        private (string[], (int, float)[][])? NamedSkin(Gb10Block skin, int vertexCount)
        {
            if (skin.SkinDataRef < 0 || skin.SkinDataRef >= blocks.Length)
                return null;
            Gb10Block data = blocks[skin.SkinDataRef];
            string[] list = blocks.FirstOrDefault(x => x.Type == "NiStringsExtraData")?.Strings ?? [];
            int count = Math.Min(skin.Bones.Length, data.BoneWeights.Length);
            var names = new string[count];
            var influences = new List<(int Bone, float Weight)>[vertexCount];
            for (int i = 0; i < count; i++)
            {
                int node = skin.Bones[i];
                names[i] = i < list.Length && list[i].Length > 0 ? list[i]
                    : node >= 0 && node < blocks.Length ? blocks[node].Name : "";
                foreach ((ushort v, float w) in data.BoneWeights[i])
                    if (v < vertexCount && w > 0)
                        (influences[v] ??= []).Add((i, w));
            }
            var weights = new (int, float)[vertexCount][];
            for (int v = 0; v < vertexCount; v++)
                weights[v] = TopFour(influences[v]);
            return (names, weights);
        }

        // Once the whole tree is walked (every bone has a joint), attaches each skinned shape's
        // weights and sets its bones' inverse bind matrices.
        private void BindSkins()
        {
            foreach ((int meshIndex, int shapeIndex) in _pendingSkins)
            {
                Gb10Block skin = blocks[blocks[shapeIndex].SkinRef];
                Gb10Block data = blocks[skin.SkinDataRef];
                NifMesh mesh = Meshes[meshIndex];
                var bones = new List<(int Joint, Matrix4x4 InverseBind)>();
                var influences = new List<(int Bone, float Weight)>[mesh.Vertices.Length];
                for (int i = 0; i < skin.Bones.Length && i < data.BoneBinds.Length; i++)
                {
                    if (!_jointOf.TryGetValue(skin.Bones[i], out int joint))
                        continue;
                    int bone = bones.Count;
                    bones.Add((joint, ToMatrix(data.BoneBinds[i])));
                    foreach ((ushort v, float w) in data.BoneWeights[i])
                        if (v < influences.Length && w > 0)
                            (influences[v] ??= []).Add((bone, w));
                }
                var weights = new (int, float)[mesh.Vertices.Length][];
                for (int v = 0; v < weights.Length; v++)
                    weights[v] = TopFour(influences[v]);
                Meshes[meshIndex] = mesh with { SkinJoints = [.. bones], Skin = weights };
            }
            _pendingSkins.Clear();
        }

        // glTF takes four influences per vertex: keep the strongest four, renormalised. A vertex no
        // bone weights is left empty (the exporter pins it to the skin's first joint).
        private static (int, float)[] TopFour(List<(int Bone, float Weight)>? list)
        {
            if (list is null || list.Count == 0)
                return [];
            var top = list.OrderByDescending(x => x.Weight).Take(4).ToArray();
            float sum = top.Sum(x => x.Weight);
            return top.Select(x => (x.Bone, x.Weight / sum)).ToArray();
        }

        private int Find(int[] props, string type, int inherited)
        {
            foreach (int p in props)
                if (p >= 0 && p < blocks.Length && blocks[p].Type == type)
                    return p;
            return inherited;
        }
    }
}
