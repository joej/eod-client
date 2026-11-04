// SPDX-License-Identifier: GPL-3.0-or-later
//
// Turns the static meshes read from a NIF into a glTF binary (.glb) that Godot imports natively.
// Uses SharpGLTF (MIT). The only non-trivial part is the coordinate conversion: NIF is Z-up and
// left-handed-ish for DAoC's data; glTF/Godot are Y-up, right-handed, metres. We map
// (x, y, z) -> (x, z, -y), which is the standard Z-up -> Y-up rotation, and flip triangle winding
// to keep faces front-facing after the handedness change. We also scale by UNITS_TO_METRES so the
// model comes out in metres, Godot's unit (open question W1): one DAoC world unit = 0.025 m. That
// figure is from OfflineDAoC's pipeline and we confirmed it against our own data — a Gamebryo
// humanoid fixture (sample-strips-1010) measures 71.8 NIF units tall = 1.79 m at 0.025, and the
// keep comes out ~16 m, a plausible 3-4 storey tower.

using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace DaocAssets.Nif;

public static class GltfExporter
{
    /// <summary>Writes the model's meshes to a .glb file at <paramref name="path"/>.</summary>
    public static void SaveGlb(NifModel model, string path, IReadOnlyDictionary<string, KfaClip>? clips = null)
    {
        SceneBuilder scene = BuildScene(model, clips);
        scene.ToGltf2().SaveGLB(path);
    }

    /// <summary>Builds a glTF scene from the model (exposed for tests that inspect it in memory).
    /// <paramref name="clips"/> (a skinned model only) become glTF animations of that name, each
    /// driving the joints its tracks name.</summary>
    public static SceneBuilder BuildScene(NifModel model, IReadOnlyDictionary<string, KfaClip>? clips = null)
    {
        // A NaN vertex would be dropped silently by the glTF writer, leaving an empty or holed model
        // that looks like a successful conversion. Fail loudly instead (the library reports it).
        foreach (NifMesh mesh in model.Meshes)
            if (!mesh.Helper && mesh.Vertices.Any(v => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)))
                throw new InvalidOperationException($"{mesh.Name} ({mesh.ShapeName}) has NaN vertex positions.");
        if (model.Joints.Count > 0)
            return BuildSkinnedScene(model, clips);
        var scene = new SceneBuilder();
        foreach (NifMesh mesh in model.Meshes)
        {
            if (mesh.Helper)
                continue; // collision/climb/shadow-caster proxy — the client never draws it
            // Name the node after the mesh, so the client can tell meshes apart (water surfaces are
            // matched to their water.csv row by it; the inspect readout shows it).
            scene.AddRigidMesh(BuildMesh(mesh, nifSpace: false), Matrix4x4.Identity).WithName(NodeName(mesh));
        }
        return scene;
    }

    // A model with a skeleton (a 10.1 figure or creature). The skeleton stays in NIF space under one
    // root node that turns NIF (Z-up, NIF units) into glTF (Y-up, metres): (x, y, z) -> (x, z, -y)
    // scaled by UnitsToMetres. Every NIF node becomes a glTF node with its own local transform;
    // skinned shapes keep their skin-space vertices and bind to their bones with the inverse bind
    // matrices from NiSkinData (glTF ignores a skinned mesh's own node transform, and the root's
    // conversion reaches the vertices through the joints' world transforms). Rigid shapes in such a
    // file are already baked to NIF world space and hang off the root.
    private static SceneBuilder BuildSkinnedScene(NifModel model, IReadOnlyDictionary<string, KfaClip>? clips)
    {
        var scene = new SceneBuilder();
        var root = new NodeBuilder("nif_root") { LocalMatrix = NifToGltf };
        var nodes = new NodeBuilder[model.Joints.Count];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int j = 0; j < nodes.Length; j++)
        {
            NifJoint joint = model.Joints[j];
            NodeBuilder parent = joint.Parent < 0 ? root : nodes[joint.Parent];
            // glTF tolerates repeated node names but SharpGLTF refuses them among joints; NIF files
            // repeat helper names (several "collidee" nodes), so keep the first and number the rest.
            string name = joint.Name;
            for (int k = 2; !used.Add(name); k++)
                name = $"{joint.Name}#{k}";
            nodes[j] = parent.CreateNode(name);
            nodes[j].LocalMatrix = joint.Local;
        }
        foreach (NifMesh mesh in model.Meshes)
        {
            if (mesh.Helper)
                continue;
            if (mesh.SkinJoints is { Length: > 0 } bones && mesh.Skin is not null)
            {
                var joints = bones.Select(b => (nodes[b.Joint], b.InverseBind)).ToArray();
                scene.AddSkinnedMesh(BuildSkinnedMesh(mesh), joints).WithName(NodeName(mesh));
            }
            else
            {
                scene.AddRigidMesh(BuildMesh(mesh, nifSpace: true), root).WithName(NodeName(mesh));
            }
        }
        if (model.Meshes.All(m => m.Helper || m.SkinJoints is not { Length: > 0 }))
            scene.AddNode(root);   // no skin pulled the skeleton in; keep it in the scene anyway

        // Animation clips: keys are bone-local in NIF space, like the joints' own transforms, so each
        // track drives its joint directly (the root's NIF -> glTF conversion applies above them).
        if (clips is not null)
        {
            var byName = new Dictionary<string, NodeBuilder>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < nodes.Length; j++)
                byName.TryAdd(model.Joints[j].Name, nodes[j]);
            foreach ((string clip, KfaClip kfa) in clips)
                foreach (KfaTrack track in kfa.Tracks)
                {
                    if (!byName.TryGetValue(track.Bone, out NodeBuilder? node))
                        continue;
                    if (track.Rotations.Length > 0)
                    {
                        var curve = node.UseRotation(clip);
                        foreach ((float t, Quaternion q) in track.Rotations) curve.SetPoint(t, q);
                    }
                    if (track.Translations.Length > 0)
                    {
                        var curve = node.UseTranslation(clip);
                        foreach ((float t, Vector3 v) in track.Translations) curve.SetPoint(t, v);
                    }
                    if (track.Scales.Length > 0)
                    {
                        var curve = node.UseScale(clip);
                        foreach ((float t, float v) in track.Scales) curve.SetPoint(t, new Vector3(v));
                    }
                }
        }
        return scene;
    }

    // NIF (Z-up, NIF units) -> glTF (Y-up, metres), as a row-vector matrix: p' = p * M.
    private static readonly Matrix4x4 NifToGltf = new(
        UnitsToMetres, 0, 0, 0,
        0, 0, -UnitsToMetres, 0,
        0, UnitsToMetres, 0, 0,
        0, 0, 0, 1);

    // The glTF node name: the NIF shape's own name when it has one (a figure's part variants, e.g.
    // "Body1:0", are chosen by it), else the mesh name (water00, mesh12).
    private static string NodeName(NifMesh m) => string.IsNullOrEmpty(m.ShapeName) ? m.Name : m.ShapeName;

    private static IMeshBuilder<MaterialBuilder> BuildSkinnedMesh(NifMesh m)
    {
        MaterialBuilder material = Material(m);
        bool hasNormals = m.Normals is not null && m.Normals.Length == m.Vertices.Length;
        bool hasUv = m.TexCoords is not null && m.TexCoords.Length == m.Vertices.Length;
        // A second texture layer (a figure's face decal) rides on TEXCOORD_1, as on unskinned meshes.
        Vector2[]? uv2 = m.DarkTexCoords ?? m.DetailTexCoords ?? m.DecalTexCoords;
        if (uv2 is not null && uv2.Length == m.Vertices.Length)
        {
            var layered = new MeshBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4>(m.Name);
            var lp = layered.UsePrimitive(material);
            VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4> LV(int i) => new(
                new VertexPositionNormal(m.Vertices[i], hasNormals ? m.Normals![i] : Vector3.UnitZ),
                new VertexTexture2(hasUv ? m.TexCoords![i] : Vector2.Zero, uv2[i]),
                new VertexJoints4(m.Skin![i].Length > 0 ? m.Skin[i] : [(0, 1f)]));
            foreach (var (a, b, c) in m.Triangles)
                lp.AddTriangle(LV(a), LV(b), LV(c));
            AddMorphs(layered, m);
            return layered;
        }
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(m.Name);
        var prim = builder.UsePrimitive(material);
        VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(int i)
        {
            Vector3 nrm = hasNormals ? m.Normals![i] : Vector3.UnitZ;
            Vector2 uv = hasUv ? m.TexCoords![i] : Vector2.Zero;
            (int, float)[] w = m.Skin![i].Length > 0 ? m.Skin[i] : [(0, 1f)];
            return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(
                new VertexPositionNormal(m.Vertices[i], nrm), new VertexTexture1(uv), new VertexJoints4(w));
        }
        foreach (var (a, b, c) in m.Triangles)
            prim.AddTriangle(V(a), V(b), V(c));
        AddMorphs(builder, m);
        return builder;
    }

    // The shape's morph targets (a Catacombs head's face sliders and moods), in file order: target i is
    // the file's morph i + 1. SharpGLTF keys a delta by the base vertex's position, so vertices sharing a
    // position share a delta (the file gives them the same one: seam copies of one point).
    private static void AddMorphs(IMeshBuilder<MaterialBuilder> builder, NifMesh m)
    {
        if (m.MorphTargets is null)
            return;
        for (int t = 0; t < m.MorphTargets.Length; t++)
        {
            Vector3[] deltas = m.MorphTargets[t];
            var target = builder.UseMorphTarget(t);
            for (int i = 0; i < m.Vertices.Length && i < deltas.Length; i++)
                target.SetVertexDelta(m.Vertices[i], new VertexGeometryDelta(deltas[i], Vector3.Zero, Vector3.Zero));
        }
    }

    private static IMeshBuilder<MaterialBuilder> BuildMesh(NifMesh m, bool nifSpace)
    {
        MaterialBuilder material = Material(m);
        bool hasNormals = m.Normals is not null && m.Normals.Length == m.Vertices.Length;
        bool hasUv = m.TexCoords is not null && m.TexCoords.Length == m.Vertices.Length;

        // A second texture layer (the dark map, else the detail map) rides on TEXCOORD_1, and vertex
        // colours the NIF says to use on COLOR_0; a plain shape keeps the smaller vertex format.
        Vector2[]? uv2 = m.DarkTexCoords ?? m.DetailTexCoords ?? m.DecalTexCoords;
        bool hasUv2 = uv2 is not null && uv2.Length == m.Vertices.Length;
        bool hasColors = m.VertexColors is not null && m.VertexColors.Length == m.Vertices.Length;
        if (hasUv2 || hasColors)
        {
            var layered = new MeshBuilder<VertexPositionNormal, VertexColor1Texture2>(m.Name);
            var lp = layered.UsePrimitive(material);
            VertexBuilder<VertexPositionNormal, VertexColor1Texture2, VertexEmpty> LV(int i) => new(
                Vertex(m, i, hasNormals, hasUv, nifSpace).Geometry,
                new VertexColor1Texture2(hasColors ? m.VertexColors![i] : Vector4.One,
                    hasUv ? m.TexCoords![i] : Vector2.Zero, hasUv2 ? uv2![i] : Vector2.Zero));
            foreach (var (a, b, c) in m.Triangles)
                lp.AddTriangle(LV(a), LV(b), LV(c));
            return layered;
        }

        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1>(m.Name);
        var prim = builder.UsePrimitive(material);

        for (int t = 0; t < m.Triangles.Length; t++)
        {
            var (a, b, c) = m.Triangles[t];
            // Keep the NIF's own winding (a, b, c). Although the Z-up→Y-up map (x,z,-y) is a
            // handedness change that would normally invert front/back, the exporter also carries the
            // NIF's real per-vertex normals through the same map, and we bind them as the glTF NORMAL
            // attribute. Measured against those normals on the keep, the NIF winding is the one that
            // faces OUT (an extra flip to (a,c,b) left ~99% of faces inverted, so back-face culling
            // removed the outside and the model read inside-out — see-through tower walls). So we do
            // not flip here; winding agrees with the normals and front faces point outward.
            prim.AddTriangle(
                Vertex(m, a, hasNormals, hasUv, nifSpace),
                Vertex(m, b, hasNormals, hasUv, nifSpace),
                Vertex(m, c, hasNormals, hasUv, nifSpace));
        }
        return builder;
    }

    private static MaterialBuilder Material(NifMesh m)
    {
        // Name the glTF material after the NIF's texture file (without extension) when there is one,
        // so the Godot side can resolve it to <name>.dds in the owner's extracted-texture folder and
        // bind it at load time. No texture bytes go into the .glb — it stays a distributable,
        // geometry-only artifact; the client's pixels never leave the owner's machine.
        string materialName = string.IsNullOrEmpty(m.TextureFile)
            ? m.Name
            : System.IO.Path.GetFileNameWithoutExtension(m.TextureFile);
        var material = new MaterialBuilder(materialName).WithMetallicRoughnessShader();
        material.WithMetallicRoughness(0f, 1f); // items read as diffuse; no PBR metalness

        // Apply the shape's NiMaterialProperty colour as the glTF base-colour factor. On an untextured
        // shape (no TextureFile) this IS the surface colour — the keep's tan stone bodies and other
        // flat material-coloured surfaces (open question W3a). On a textured shape it tints the texture
        // (DAoC keep skins are white, so no visible tint).
        //
        // The subtlety that caused the in-world MAGENTA: an UNtextured shape whose colour is the
        // glTF default white (1,1,1,1) — either because it carries no NiMaterialProperty, or carries
        // one that IS white — writes NO baseColorFactor at all, because SharpGLTF omits a factor equal
        // to the default. A glTF material with neither a texture nor any factor is what Godot's importer
        // paints with its magenta "no material" placeholder (seen on keep towers, house walls, bridge,
        // forge, ruins). So for an untextured shape we force an EXPLICIT factor: the real colour if it
        // is non-white, else a neutral grey, so a factor is always serialised and the surface never
        // falls through to magenta. A textured shape is left alone (default-white correctly shows the
        // texture unmodulated).
        if (!string.IsNullOrEmpty(m.TextureFile))
        {
            if (m.BaseColor is { } tint)
                material.WithBaseColor(tint);
        }
        else
        {
            Vector4 colour = m.BaseColor ?? new Vector4(1f, 1f, 1f, 1f);
            if (IsDefaultWhite(colour))
                colour = new Vector4(0.6f, 0.6f, 0.6f, 1f); // neutral stone-grey, never magenta
            material.WithBaseColor(colour);
        }

        // Transparency and sidedness from the NIF's NiAlphaProperty/NiStencilProperty: DAoC's trees are
        // crossed planes with alpha-tested leaf textures, which only read as foliage when the
        // transparent pixels are cut out and both faces draw.
        switch (m.Alpha)
        {
            case SurfaceAlpha.Cutout:
                material.WithAlpha(SharpGLTF.Materials.AlphaMode.MASK, m.AlphaCutoff);
                break;
            case SurfaceAlpha.Blend:
                material.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND);
                break;
        }
        if (m.DoubleSided)
            material.WithDoubleSide(true);

        // The extra layers, by texture name (no image goes into the file): the client binds the dark
        // map (else the detail map) on the second UV set, and multiplies in the vertex colours.
        var extras = new System.Text.Json.Nodes.JsonObject();
        if (!string.IsNullOrEmpty(m.DarkTextureFile))
            extras["dark"] = System.IO.Path.GetFileNameWithoutExtension(m.DarkTextureFile);
        if (!string.IsNullOrEmpty(m.DetailTextureFile))
            extras["detail"] = System.IO.Path.GetFileNameWithoutExtension(m.DetailTextureFile);
        if (!string.IsNullOrEmpty(m.DecalTextureFile))
            extras["decal"] = System.IO.Path.GetFileNameWithoutExtension(m.DecalTextureFile);
        if (m.VertexColors is not null)
            extras["vertexColors"] = true;
        if (extras.Count > 0)
            material.Extras = extras;
        return material;
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(
        NifMesh m, int i, bool hasNormals, bool hasUv, bool nifSpace)
    {
        Vector3 pos = nifSpace ? m.Vertices[i] : ToYUp(m.Vertices[i]);
        Vector3 nrm = hasNormals ? (nifSpace ? m.Normals![i] : DirToYUp(m.Normals![i])) : (nifSpace ? Vector3.UnitZ : Vector3.UnitY);
        Vector2 uv = hasUv ? m.TexCoords![i] : Vector2.Zero;
        return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(pos, nrm), new VertexTexture1(uv));
    }

    // One DAoC world unit in metres (open question W1). Measured: a Gamebryo humanoid is ~72 units
    // tall = ~1.8 m, matching OfflineDAoC's own constant. Positions are scaled by this; normals are
    // directions, so they are mapped by ToYUp but NOT scaled.
    private const float UnitsToMetres = 0.025f;

    // A base colour that equals glTF's default white, which SharpGLTF omits from the output (so it
    // would leave an untextured surface with no factor at all -> Godot's magenta placeholder).
    private static bool IsDefaultWhite(Vector4 c) =>
        c.X >= 0.999f && c.Y >= 0.999f && c.Z >= 0.999f && c.W >= 0.999f;

    // NIF (Z-up) -> glTF/Godot (Y-up): (x, y, z) -> (x, z, -y), scaled to metres. For positions.
    private static Vector3 ToYUp(Vector3 v) => new(v.X * UnitsToMetres, v.Z * UnitsToMetres, -v.Y * UnitsToMetres);

    // Same axis swap for a direction (normal), WITHOUT the metre scale, so it stays unit length.
    private static Vector3 DirToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);
}
