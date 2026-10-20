// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using DaocAssets.Nif;
using SharpGLTF.Schema2;

namespace DaocAssets.Tests;

/// <summary>
/// NIF reader tests. The live tests run against real client NIFs in git-ignored
/// private/nif-fixtures (copied to output when present) and assert the golden geometry decoded
/// by hand and cross-checked against nif.xml. They skip when the fixtures are absent.
/// </summary>
public sealed class NifFileTests
{
    // A single case when the fixture is present, none otherwise, so the test skips cleanly.
    public static IEnumerable<object[]> Bowsprit()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-422.nif");
        if (File.Exists(p))
            yield return [p];
    }

    public static IEnumerable<object[]> Ambrat()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-1010.nif");
        if (File.Exists(p))
            yield return [p];
    }

    public static IEnumerable<object[]> StripsCreature()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-strips-1010.nif");
        if (File.Exists(p))
            yield return [p];
    }

    public static IEnumerable<object[]> Strips4x()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-strips-4x.nif");
        if (File.Exists(p))
            yield return [p];
    }

    [Theory]
    [MemberData(nameof(Bowsprit))]
    public void ReadsTheBowspritGoldenGeometry(string path)
    {
        // items/bowsprit_alb_01.NIF, NetImmerse 4.2.2.0.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(4, 2, 2, 0), model.Version);
        NifMesh mesh = Assert.Single(model.Meshes);

        Assert.Equal(22, mesh.Vertices.Length);
        Assert.Equal(12, mesh.Triangles.Length);
        Assert.NotNull(mesh.Normals);
        Assert.NotNull(mesh.TexCoords);
        // The external base texture named by the NiSourceTexture block.
        Assert.Equal("PSbeamLt_grey.dds", mesh.TextureFile);
        Assert.Equal((ushort)0, mesh.Triangles[0].A);
        Assert.Equal((ushort)2, mesh.Triangles[0].C);
        Assert.All(mesh.Triangles, t => Assert.True(t.A < 22 && t.B < 22 && t.C < 22));

        // Byte-for-byte golden hashes from the verified hand-decode.
        Assert.Equal("2d5b083725f5f978", VertHash(mesh));
        Assert.Equal("a29c9f18d2b00746", TriHash(mesh));
    }

    [Theory]
    [MemberData(nameof(Ambrat))]
    public void ReadsTheAmbratGamebryoGoldenGeometry(string path)
    {
        // figures/ambrat_black.nif, Gamebryo 10.1.0.0 (a rigged creature). We take its bind-pose
        // geometry; no skinning/animation (there are no .kf files). Golden values from the verified
        // hand-decode against nif.xml and the real file.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(10, 1, 0, 0), model.Version);
        NifMesh mesh = Assert.Single(model.Meshes);

        Assert.Equal(27, mesh.Vertices.Length);
        Assert.Equal(30, mesh.Triangles.Length);
        Assert.NotNull(mesh.Normals);
        Assert.NotNull(mesh.TexCoords);
        Assert.Equal("AmbientCreaturesTexture.dds", mesh.TextureFile);
        Assert.Equal((12, 14, 15), (mesh.Triangles[0].A, mesh.Triangles[0].B, mesh.Triangles[0].C));
        Assert.All(mesh.Triangles, t => Assert.True(t.A < 27 && t.B < 27 && t.C < 27));

        // Byte-for-byte golden hashes from the verified hand-decode.
        Assert.Equal("1adeba4b932bf670", VertHash(mesh));
        Assert.Equal("6178f8385ce0c558", TriHash(mesh));
    }

    [Theory]
    [MemberData(nameof(StripsCreature))]
    public void ReadsTheTriStripsGoldenGeometry(string path)
    {
        // figures/Skel01.NIF, Gamebryo 10.1.0.0, NiTriStripsData (triangle strips) and multi-mesh
        // (two body parts). Strips are converted to a triangle list (winding flipped on odd
        // triangles, degenerate stitch triangles dropped). Golden values from the verified decode.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(10, 1, 0, 0), model.Version);
        Assert.Equal(2, model.Meshes.Count);

        NifMesh m0 = model.Meshes[0];
        Assert.Equal(31, m0.Vertices.Length);
        Assert.Equal(29, m0.Triangles.Length);
        Assert.All(m0.Triangles, t => Assert.True(t.A < 31 && t.B < 31 && t.C < 31));
        Assert.All(m0.Triangles, t => Assert.True(t.A != t.B && t.B != t.C && t.A != t.C));
        Assert.Equal("4f2a52863a6b49b1", VertHash(m0));
        Assert.Equal("339d984541687ca0", TriHash(m0));

        NifMesh m1 = model.Meshes[1];
        Assert.Equal(503, m1.Vertices.Length);
        Assert.Equal(652, m1.Triangles.Length);
        Assert.Equal("b987c508e56915ab", VertHash(m1));
        Assert.Equal("7273be65c279afb2", TriHash(m1));
    }

    [Theory]
    [MemberData(nameof(Strips4x))]
    public void ReadsThe4xTriStripsGoldenGeometry(string path)
    {
        // items/b_bo_shortbow01.nif, NetImmerse 4.1.0.12, NiTriStripsData. The 4.x strip layout has
        // no Has-Points byte (that is `since 10.0.1.3`); points follow Strip Lengths directly.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(4, 1, 0, 12), model.Version);
        NifMesh mesh = Assert.Single(model.Meshes);

        Assert.Equal(74, mesh.Vertices.Length);
        Assert.Equal(60, mesh.Triangles.Length);
        Assert.All(mesh.Triangles, t => Assert.True(t.A < 74 && t.B < 74 && t.C < 74));
        Assert.All(mesh.Triangles, t => Assert.True(t.A != t.B && t.B != t.C && t.A != t.C));
        Assert.Equal("4be11ed97cecf228", VertHash(mesh));
        Assert.Equal("da58e35b38cf8ca7", TriHash(mesh));
    }

    [Fact]
    public void RejectsNonNif()
    {
        Assert.Throws<NifFormatException>(() => NifFile.Read("not a nif at all"u8.ToArray()));
    }

    [Theory]
    [MemberData(nameof(Bowsprit))]
    public void ConvertsToAValidGlbWithYUpGeometry(string path)
    {
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        var gltf = GltfExporter.BuildScene(model).ToGltf2();

        // SharpGLTF throws on an invalid scene when building; assert the geometry survived.
        var prim = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        var positions = prim.GetVertexAccessor("POSITION").AsVector3Array();
        Assert.Equal(22, positions.Count);
        Assert.Equal(36, prim.GetIndices().Count); // 12 triangles
        Assert.NotNull(prim.GetVertexAccessor("NORMAL"));
        Assert.NotNull(prim.GetVertexAccessor("TEXCOORD_0"));

        // The NIF's first vertex (0, -492.73, 56) maps Z-up -> Y-up as (x, z, -y) = (0, 56, 492.73).
        var v0 = positions[0];
        Assert.Equal(0f, v0.X, 0.01);
        Assert.Equal(56f, v0.Y, 0.01);
        Assert.Equal(492.73f, v0.Z, 0.01);

        // The material is named for the texture (sans extension) so Godot can resolve <name>.dds from
        // the owner's extracted folder, and NO texture bytes are embedded: the .glb stays a clean,
        // distributable, geometry-only artifact (assets policy).
        Assert.Equal("PSbeamLt_grey", Assert.Single(gltf.LogicalMaterials).Name);
        Assert.Empty(gltf.LogicalImages);
    }

    private static string VertHash(NifMesh m)
    {
        byte[] buf = new byte[m.Vertices.Length * 12];
        int o = 0;
        foreach (var v in m.Vertices)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.X); o += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.Y); o += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.Z); o += 4;
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    private static string TriHash(NifMesh m)
    {
        byte[] buf = new byte[m.Triangles.Length * 6];
        int o = 0;
        foreach (var (a, b, c) in m.Triangles)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), a); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), b); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), c); o += 2;
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }
}
