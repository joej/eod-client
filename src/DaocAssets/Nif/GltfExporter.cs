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
    public static void SaveGlb(NifModel model, string path)
    {
        SceneBuilder scene = BuildScene(model);
        scene.ToGltf2().SaveGLB(path);
    }

    /// <summary>Builds a glTF scene from the model (exposed for tests that inspect it in memory).</summary>
    public static SceneBuilder BuildScene(NifModel model)
    {
        var scene = new SceneBuilder();
        foreach (NifMesh mesh in model.Meshes)
        {
            if (mesh.Helper)
                continue; // collision/climb/shadow-caster proxy — the client never draws it
            // Name the node after the mesh, so the client can tell meshes apart (water surfaces are
            // matched to their water.csv row by it; the inspect readout shows it).
            scene.AddRigidMesh(BuildMesh(mesh), Matrix4x4.Identity).WithName(mesh.Name);
        }
        return scene;
    }

    private static IMeshBuilder<MaterialBuilder> BuildMesh(NifMesh m)
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

        bool hasNormals = m.Normals is not null && m.Normals.Length == m.Vertices.Length;
        bool hasUv = m.TexCoords is not null && m.TexCoords.Length == m.Vertices.Length;

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
                Vertex(m, a, hasNormals, hasUv),
                Vertex(m, b, hasNormals, hasUv),
                Vertex(m, c, hasNormals, hasUv));
        }
        return builder;
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(
        NifMesh m, int i, bool hasNormals, bool hasUv)
    {
        Vector3 pos = ToYUp(m.Vertices[i]);
        Vector3 nrm = hasNormals ? DirToYUp(m.Normals![i]) : Vector3.UnitY;
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
