// SPDX-License-Identifier: GPL-3.0-or-later
//
// Turns the static meshes read from a NIF into a glTF binary (.glb) that Godot imports natively.
// Uses SharpGLTF (MIT). The only non-trivial part is the coordinate conversion: NIF is Z-up and
// left-handed-ish for DAoC's data; glTF/Godot are Y-up, right-handed, metres. We map
// (x, y, z) -> (x, z, -y), which is the standard Z-up -> Y-up rotation, and flip triangle winding
// to keep faces front-facing after the handedness change. Scale is left at 1 for now; one DAoC
// world unit in metres is still unknown (open question W1), so models come out in NIF units and
// can be rescaled once that's measured.

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
            scene.AddRigidMesh(BuildMesh(mesh), Matrix4x4.Identity);
        return scene;
    }

    private static IMeshBuilder<MaterialBuilder> BuildMesh(NifMesh m)
    {
        var material = new MaterialBuilder(m.Name).WithMetallicRoughnessShader();

        bool hasNormals = m.Normals is not null && m.Normals.Length == m.Vertices.Length;
        bool hasUv = m.TexCoords is not null && m.TexCoords.Length == m.Vertices.Length;

        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1>(m.Name);
        var prim = builder.UsePrimitive(material);

        for (int t = 0; t < m.Triangles.Length; t++)
        {
            var (a, b, c) = m.Triangles[t];
            // Winding flipped (a, c, b) because the Z-up -> Y-up map changes handedness.
            prim.AddTriangle(
                Vertex(m, a, hasNormals, hasUv),
                Vertex(m, c, hasNormals, hasUv),
                Vertex(m, b, hasNormals, hasUv));
        }
        return builder;
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(
        NifMesh m, int i, bool hasNormals, bool hasUv)
    {
        Vector3 pos = ToYUp(m.Vertices[i]);
        Vector3 nrm = hasNormals ? ToYUp(m.Normals![i]) : Vector3.UnitY;
        Vector2 uv = hasUv ? m.TexCoords![i] : Vector2.Zero;
        return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(pos, nrm), new VertexTexture1(uv));
    }

    // NIF (Z-up) -> glTF/Godot (Y-up): (x, y, z) -> (x, z, -y).
    private static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);
}
