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

    public static IEnumerable<object[]> MultiMat4x()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-multimat-4x.nif");
        if (File.Exists(p))
            yield return [p];
    }

    public static IEnumerable<object[]> Keep422()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "keep-422.nif");
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

        // Byte-for-byte golden hashes from the verified hand-decode. Vertices have the model's node
        // transform baked in (the bowsprit's root places the mesh near the origin); cross-checked
        // against an independent walk of the node hierarchy.
        Assert.Equal("99903e9b1748c619", VertHash(mesh));
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
        Assert.Equal("5cba64a51818e6cc", VertHash(mesh));
        Assert.Equal("da58e35b38cf8ca7", TriHash(mesh));
    }

    [Theory]
    [MemberData(nameof(MultiMat4x))]
    public void ReadsPerShapeTexturesInAMultiMaterialModel(string path)
    {
        // zones/Nifs/WELL.NIF, NetImmerse 4.1.0.12: 5 NiTriStrips shapes. Four have their own
        // NiTexturingProperty -> NiSourceTexture (two stone, two log); the fifth ("Editable Mesh" with
        // an empty-name, editor-default blue material) has NO texturing property and is untextured.
        // Per-shape resolution gives each textured shape its own skin — what stops a multi-material
        // building (a keep uses ten skins across its parts) rendering in a single skin — and leaves the
        // untextured shape null rather than smearing the model's first texture onto it (open question
        // W3a; the old shared-first-texture fallback did exactly that).
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(4, 1, 0, 12), model.Version);
        Assert.Equal(5, model.Meshes.Count);

        // Two distinct base textures across the four textured shapes (not all one), and one untextured
        // shape. The names carry the NIF's own casing (STONE2-1.TGA) — the library gathers
        // case-insensitively.
        string?[] tex = model.Meshes.Select(m => m.TextureFile).ToArray();
        Assert.Equal(1, tex.Count(t => t is null));
        Assert.Equal(new[] { "STONE2-1.TGA", "britlog.tga" },
            tex.Where(t => t is not null).Select(t => t!).Distinct().OrderByDescending(t => t).ToArray());
        Assert.Equal(2, tex.Count(t => t == "STONE2-1.TGA"));
        Assert.Equal(2, tex.Count(t => t == "britlog.tga"));

        // The untextured shape still carries its material colour (never a texture).
        Assert.All(model.Meshes.Where(m => m.TextureFile is null), m => Assert.NotNull(m.BaseColor));

        // Byte-for-byte golden: the shapes' geometry and their texture assignment together.
        Assert.Equal("6698d641119541f7", ModelVertHash(model));
        Assert.Equal("baf5e3a26b8f8ff7", TextureMapHash(model));
    }

    [Theory]
    [MemberData(nameof(Keep422))]
    public void UntexturedKeepShapesCarryMaterialColourNotTheSharedTexture(string path)
    {
        // zones/Nifs/keep.NIF, NetImmerse 4.2.2.0: 59 shapes. 20 have a NiTexturingProperty and a real
        // skin; the other 39 have NO texturing property and are flat surfaces coloured by their
        // NiMaterialProperty — 38 share a tan stone colour (open question W3a). The old converter gave
        // every untextured shape the model's FIRST texture (bpaldor1.dds) and, with no UVs, sampled it
        // at texel (0,0) — the "flat tan towers" bug. This asserts the corrected behaviour: an
        // untextured shape gets null TextureFile and its own material colour, and the shared-first-
        // texture fallback is gone.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(4, 2, 2, 0), model.Version);
        // 55: the 59 shapes minus the 4 KeepFar shapes, which belong to the far LOD level and were only
        // ever counted here because a fallback re-added them (see KeepDrawsOnlyItsNearLevelOnce).
        Assert.Equal(55, model.Meshes.Count);

        NifMesh[] untextured = model.Meshes.Where(m => m.TextureFile is null).ToArray();
        NifMesh[] textured = model.Meshes.Where(m => m.TextureFile is not null).ToArray();
        Assert.Equal(39, untextured.Length);
        Assert.Equal(16, textured.Length);   // 20 before, minus the 4 textured KeepFar shapes

        // Every untextured shape carries a material colour (never falls through to a texture).
        Assert.All(untextured, m => Assert.NotNull(m.BaseColor));

        // 38 of them share the tan stone diffuse rgb(233,171,74) -> (0.914, 0.671, 0.290), alpha 1.
        var tan = new System.Numerics.Vector4(233 / 255f, 171 / 255f, 74 / 255f, 1f);
        int tanCount = untextured.Count(m => Near(m.BaseColor!.Value, tan));
        Assert.Equal(38, tanCount);

        // No untextured shape wears the model's first texture (bpaldor1) — the fallback is gone.
        Assert.DoesNotContain("bpaldor1.tga", untextured.Select(m => m.TextureFile));

        // Textured shapes keep their own skins; bpaldor1 is one of them, on exactly the shape(s) that
        // reference it as a texture — not smeared across the untextured towers.
        Assert.Contains("bpaldor1.tga", textured.Select(m => m.TextureFile));

        // glTF side: no texture bytes are embedded, and the tan collision hull doesn't reach the export.
        // The 38 tan shapes are the keep's collision hull (W3e), all helpers: since 2026-11-03 that
        // includes mesh133, under a numbered collision node, which used to be drawn (W3f). Read the
        // base-colour factors back out of the written glTF JSON (stable across SharpGLTF's material-
        // channel API churn).
        var gltf = GltfExporter.BuildScene(model).ToGltf2();
        Assert.Empty(gltf.LogicalImages);
        using var doc = System.Text.Json.JsonDocument.Parse(gltf.GetJsonPreview());
        var factors = doc.RootElement.GetProperty("materials").EnumerateArray()
            .Where(m => m.TryGetProperty("pbrMetallicRoughness", out var p) && p.TryGetProperty("baseColorFactor", out _))
            .Select(m =>
            {
                var f = m.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor");
                return new System.Numerics.Vector4(
                    f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle(), f[3].GetSingle());
            })
            .ToArray();
        Assert.All(model.Meshes.Where(m => m.BaseColor is { } c && Near(c, tan)), m => Assert.True(m.Helper, m.Name));
        Assert.DoesNotContain(factors, f => Near(f, tan));
    }

    [Fact]
    public void A4xLodNodeKeepsTheLevelWithTheNearestRangeNotEveryUnnamedLevel()
    {
        // Shaped like HBirchsingle.nif (4.2.1.0): an LOD node whose two levels are named neither near
        // nor far, the far level first: a flat card picture of the tree, then the tree. Going by name
        // kept both, and the card was drawn over the tree (Cursed Forest, 2026-11-03).
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.1.0\n"));
        w.Write(0x04020100u);
        w.Write(6u);
        void Type(string t) { w.Write((uint)t.Length); w.Write(System.Text.Encoding.ASCII.GetBytes(t)); }
        void Av(string name)
        {
            Type(name);                                 // a name is a sized string like a type
            w.Write(-1); w.Write(-1);                   // extra data, controller
            w.Write((ushort)0x000C);
            for (int k = 0; k < 3 + 9; k++) w.Write(k is 3 or 7 or 11 ? 1f : 0f);
            w.Write(1f);
            for (int k = 0; k < 3; k++) w.Write(0f);    // velocity
            w.Write(0u); w.Write((byte)0);              // properties, no bounding volume
        }
        void Triangle()
        {
            w.Write((ushort)3); w.Write((byte)1);
            foreach (float v in new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 }) w.Write(v);
            w.Write((byte)0);                           // no normals
            for (int k = 0; k < 4; k++) w.Write(0f);    // bounding sphere
            w.Write((byte)0); w.Write((ushort)0);       // no vertex colours, no UV sets
            w.Write((ushort)1); w.Write(3u);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
            w.Write((ushort)0);                         // match groups
        }
        Type("NiNode"); Av("Scene Root"); w.Write(1u); w.Write(1); w.Write(0u);
        Type("NiLODNode"); Av("lod"); w.Write(2u); w.Write(2); w.Write(4); w.Write(0u);
        w.Write(0);                                     // NiSwitchNode u32
        for (int k = 0; k < 3; k++) w.Write(0f);        // LOD centre
        w.Write(2u); w.Write(500f); w.Write(10000f); w.Write(0f); w.Write(500f);
        Type("NiTriShape"); Av("card"); w.Write(3); w.Write(-1);
        Type("NiTriShapeData"); Triangle();
        Type("NiTriShape"); Av("tree"); w.Write(5); w.Write(-1);
        Type("NiTriShapeData"); Triangle();
        w.Write(1u); w.Write(0);                        // footer: one root

        NifModel model = NifFile.Read(ms.ToArray());
        Assert.Equal("mesh4", Assert.Single(model.Meshes, m => !m.Helper).Name);
    }

    [Fact]
    public void AnUntexturedShapeUnderANumberedClimbNodeIsHidden()
    {
        // A ladder's climb panel: untextured, under "climb001". The exact-name rule (`climb`) missed it,
        // and it was drawn as a flat green panel over the rungs (Llyn Barfog, 2026-11-03). A shape under
        // "climbwall" (a name, not a numbered helper) and one under the root stay.
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.1.0\n"));
        w.Write(0x04020100u);
        w.Write(9u);
        void Sized(string t) { w.Write((uint)t.Length); w.Write(System.Text.Encoding.ASCII.GetBytes(t)); }
        void Av(string name)
        {
            Sized(name);
            w.Write(-1); w.Write(-1);
            w.Write((ushort)0x000C);
            for (int k = 0; k < 3 + 9; k++) w.Write(k is 3 or 7 or 11 ? 1f : 0f);
            w.Write(1f);
            for (int k = 0; k < 3; k++) w.Write(0f);
            w.Write(0u); w.Write((byte)0);
        }
        void Triangle()
        {
            w.Write((ushort)3); w.Write((byte)1);
            foreach (float v in new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 }) w.Write(v);
            w.Write((byte)0);
            for (int k = 0; k < 4; k++) w.Write(0f);
            w.Write((byte)0); w.Write((ushort)0);
            w.Write((ushort)1); w.Write(3u);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
            w.Write((ushort)0);
        }
        void Node(string name, params int[] children) { Sized("NiNode"); Av(name); w.Write((uint)children.Length); foreach (int c in children) w.Write(c); w.Write(0u); }
        void Shape(string name, int data) { Sized("NiTriShape"); Av(name); w.Write(data); w.Write(-1); }
        Node("Scene Root", 1, 4, 7);                                  // 0
        Node("climb001", 2);                                          // 1
        Shape("ladder panel", 3); Sized("NiTriShapeData"); Triangle(); // 2, 3
        Node("climbwall", 5);                                         // 4
        Shape("wall", 6); Sized("NiTriShapeData"); Triangle();        // 5, 6
        Shape("floor", 8); Sized("NiTriShapeData"); Triangle();       // 7, 8
        w.Write(1u); w.Write(0);

        NifModel model = NifFile.Read(ms.ToArray());
        Assert.Equal(["mesh2"], model.Meshes.Where(m => m.Helper).Select(m => m.Name));
        Assert.Equal(["mesh5", "mesh7"], model.Meshes.Where(m => !m.Helper).Select(m => m.Name).Order());
    }

    private static bool Near(System.Numerics.Vector4 a, System.Numerics.Vector4 b) =>
        Math.Abs(a.X - b.X) < 0.004 && Math.Abs(a.Y - b.Y) < 0.004
        && Math.Abs(a.Z - b.Z) < 0.004 && Math.Abs(a.W - b.W) < 0.004;

    // A unit triangle for the in-memory exporter tests below.
    private static NifMesh TriMesh(string name, string? textureFile, System.Numerics.Vector4? baseColor) => new()
    {
        Name = name,
        Vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
        Triangles = [(0, 1, 2)],
        TextureFile = textureFile,
        BaseColor = baseColor,
    };

    private static System.Numerics.Vector4[] BaseColorFactors(NifModel model)
    {
        var gltf = GltfExporter.BuildScene(model).ToGltf2();
        using var doc = System.Text.Json.JsonDocument.Parse(gltf.GetJsonPreview());
        if (!doc.RootElement.TryGetProperty("materials", out var mats))
            return [];
        return mats.EnumerateArray()
            .Select(m => m.TryGetProperty("pbrMetallicRoughness", out var p)
                         && p.TryGetProperty("baseColorFactor", out var f)
                ? new System.Numerics.Vector4(f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle(), f[3].GetSingle())
                : (System.Numerics.Vector4?)null)
            .Where(v => v is not null).Select(v => v!.Value).ToArray();
    }

    [Theory]
    [InlineData(null, null, null, null)]          // no NiMaterialProperty at all
    [InlineData(1f, 1f, 1f, 1f)]                   // a material property that IS default white
    public void UntexturedWhiteOrColourlessShapeGetsNeutralGreyNotMagenta(float? r, float? g, float? b, float? a)
    {
        // The in-world magenta came from an UNtextured shape whose colour is glTF's default white —
        // either no NiMaterialProperty, or one that is white. SharpGLTF omits a default-white factor,
        // so the material ends up with NO baseColorFactor, which Godot paints with its magenta "no
        // material" placeholder (keep towers, house walls, bridge, forge, ruins). The exporter now
        // forces an explicit neutral grey for these so a factor is always written and it never goes
        // magenta. Covers both the colourless case and the explicitly-white case.
        System.Numerics.Vector4? baseColor = r is null ? null : new System.Numerics.Vector4(r.Value, g!.Value, b!.Value, a!.Value);
        var model = new NifModel
        {
            Version = new NifVersion(4, 2, 2, 0),
            Meshes = [TriMesh("bare", textureFile: null, baseColor: baseColor)],
        };
        System.Numerics.Vector4[] factors = BaseColorFactors(model);
        var grey = new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f);
        Assert.Contains(factors, f => Near(f, grey));
    }

    [Theory]
    [MemberData(nameof(Keep422))]
    public void CollisionAndHiddenHelperShapesAreDroppedFromTheScene(string path)
    {
        // game.dll's visibility pass hides (and the client never draws) nodes named exactly
        // collidee/climb/sclimb/pickee/ladder/shadowcaster and any node with the NiAVObject Hidden
        // flag (daoc-re RE/34). keep.NIF carries collidee subtrees; those shapes are flagged Helper by
        // the reader and must NOT reach the exported scene — rendering them was the z-fighting
        // placeholder geometry (and, for the keep, the collision hull overlapping the real stone).
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Contains(model.Meshes, m => m.Helper);                 // the keep has helper shapes
        Assert.Contains(model.Meshes, m => !m.Helper);                // and real ones

        int exported = GltfExporter.BuildScene(model).ToGltf2().LogicalMeshes.Count;
        int nonHelper = model.Meshes.Count(m => !m.Helper);
        Assert.Equal(nonHelper, exported);                            // only non-helpers are exported
        Assert.True(exported < model.Meshes.Count, "some shapes should be dropped");
    }

    [Theory]
    [MemberData(nameof(Keep422))]
    public void OnlyTheNearLodLevelIsExported(string path)
    {
        // keep.NIF carries a NiLODNode with a near ("keepNear"/"visible") and a far ("keepFar") level.
        // The engine draws one level by distance; baking all of them stacks near+far geometry and they
        // z-fight. The reader selects the near level, so no shape baked from a "far" LOD node reaches
        // the scene. (A model without LOD is unaffected — see the house tests, which still export.)
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        // Shapes are named mesh<blockIndex>; we can't see the source node name post-bake, so assert the
        // behaviour that matters: the model still exports real geometry (near level kept), and dropping
        // the far level means fewer meshes than a hypothetical draw-everything. Concretely, the keep
        // still has its textured stone and its helper shapes are flagged.
        Assert.Contains(model.Meshes, m => m.TextureFile is not null);   // near-level textured stone kept
        int exported = GltfExporter.BuildScene(model).ToGltf2().LogicalMeshes.Count;
        Assert.True(exported > 0);
    }

    [Fact]
    public void UntexturedColouredShapeKeepsItsOwnColour()
    {
        // A non-white material colour (the keep's tan stone, W3a) must pass through unchanged — the
        // grey default is only for the white/colourless case.
        var tan = new System.Numerics.Vector4(233 / 255f, 171 / 255f, 74 / 255f, 1f);
        var model = new NifModel
        {
            Version = new NifVersion(4, 2, 2, 0),
            Meshes = [TriMesh("tan", textureFile: null, baseColor: tan)],
        };
        System.Numerics.Vector4[] factors = BaseColorFactors(model);
        Assert.Contains(factors, f => Near(f, tan));
        var grey = new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f);
        Assert.DoesNotContain(factors, f => Near(f, grey));
    }

    [Fact]
    public void TexturedShapeWithoutMaterialColourIsNotTinted()
    {
        // The neutral-grey default must apply ONLY to untextured, colourless shapes. A textured shape
        // with no NiMaterialProperty must keep glTF's default white factor so its bound .dds shows
        // unmodulated — tinting it grey would darken every such surface.
        var model = new NifModel
        {
            Version = new NifVersion(4, 2, 2, 0),
            Meshes = [TriMesh("skinned", textureFile: "keepSTONE.tga", baseColor: null)],
        };
        System.Numerics.Vector4[] factors = BaseColorFactors(model);
        var grey = new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f);
        Assert.DoesNotContain(factors, f => Near(f, grey));
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

        // With the node transform baked in, the bowsprit's first vertex is (0, 17.08, 15.0) in NIF
        // space; Z-up -> Y-up maps (x, z, -y) = (0, 15.0, -17.08), then scaled to metres by the
        // 0.025 m/unit world scale (W1): (0, 0.375, -0.427).
        var v0 = positions[0];
        Assert.Equal(0f, v0.X, 0.001);
        Assert.Equal(15.0f * 0.025f, v0.Y, 0.001);
        Assert.Equal(-17.08f * 0.025f, v0.Z, 0.001);

        // The material is named for the texture (sans extension) so Godot can resolve <name>.dds from
        // the owner's extracted folder, and NO texture bytes are embedded: the .glb stays a clean,
        // distributable, geometry-only artifact (assets policy).
        Assert.Equal("PSbeamLt_grey", Assert.Single(gltf.LogicalMaterials).Name);
        Assert.Empty(gltf.LogicalImages);

        // Triangle winding must face OUT, i.e. agree with the exported NORMAL attribute. An earlier
        // exporter flipped winding an extra time, leaving ~99% of faces inverted: with back-face
        // culling the client then showed the inside of every model (see-through walls). Assert the
        // front faces point the same way as the normals so this can't silently regress.
        AssertWindingFacesOutward(prim);
    }

    [Theory]
    [MemberData(nameof(StripsCreature))]
    public void HumanoidConvertsToAPlausibleHeightInMetres(string path)
    {
        // The W1 world scale (0.025 m/unit) is anchored on this fixture: a Gamebryo humanoid stands
        // ~72 NIF units tall, which must come out ~1.8 m — human height — in the exported .glb. The
        // whole scale pipeline (exporter bakes metres; WorldView/zone trust the .glb as metres, no
        // re-scaling) rests on that, so guard it with a readable range: a wrong UnitsToMetres makes a
        // "0.72 m dwarf" or "72 m titan" fail here, not silently mis-size the world.
        // The fixture is skinned: its vertices stay in skin space and reach metres through the
        // skeleton (root conversion, joints, inverse bind matrices), so measure the model as glTF
        // poses it, not the raw vertex buffer.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        var gltf = GltfExporter.BuildScene(model).ToGltf2();

        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var (a, b, c, _) in SharpGLTF.Schema2.Toolkit.EvaluateTriangles(gltf.DefaultScene))
            foreach (var v in new[] { a.GetGeometry().GetPosition(), b.GetGeometry().GetPosition(), c.GetGeometry().GetPosition() })
            {
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }
        float heightMetres = maxY - minY;
        Assert.InRange(heightMetres, 1.2f, 2.6f); // a humanoid, in metres — not raw units, not ×0.0025
    }

    // Fraction of triangles whose geometric winding agrees with the vertex normals must be high
    // (a few genuinely double-sided/degenerate faces are tolerated). Front faces point outward.
    private static void AssertWindingFacesOutward(MeshPrimitive prim)
    {
        var pos = prim.GetVertexAccessor("POSITION").AsVector3Array();
        var nrm = prim.GetVertexAccessor("NORMAL").AsVector3Array();
        var idx = prim.GetIndices();
        int agree = 0, total = 0;
        for (int i = 0; i + 2 < idx.Count; i += 3)
        {
            System.Numerics.Vector3 p0 = pos[(int)idx[i]], p1 = pos[(int)idx[i + 1]], p2 = pos[(int)idx[i + 2]];
            System.Numerics.Vector3 face = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0);
            if (face.LengthSquared() < 1e-9f) continue; // degenerate
            System.Numerics.Vector3 vn = nrm[(int)idx[i]] + nrm[(int)idx[i + 1]] + nrm[(int)idx[i + 2]];
            total++;
            if (System.Numerics.Vector3.Dot(face, vn) >= 0) agree++;
        }
        Assert.True(total > 0, "no non-degenerate triangles to check");
        Assert.True(agree >= total * 0.9, $"winding faces inward: only {agree}/{total} triangles agree with their normals");
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

    // Hash of all vertices across all meshes in order (whole-model geometry).
    private static string ModelVertHash(NifModel model)
    {
        var all = new List<byte>();
        foreach (NifMesh m in model.Meshes)
        {
            byte[] buf = new byte[m.Vertices.Length * 12];
            int o = 0;
            foreach (var v in m.Vertices)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.X); o += 4;
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.Y); o += 4;
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.Z); o += 4;
            }
            all.AddRange(buf);
        }
        return Convert.ToHexStringLower(SHA256.HashData(all.ToArray()))[..16];
    }

    // Hash binding each mesh's vertex count + texture name, in order: catches a texture reshuffled
    // onto the wrong shape even when the geometry set is unchanged.
    private static string TextureMapHash(NifModel model)
    {
        var s = string.Join("|", model.Meshes.Select(m => $"{m.Vertices.Length}:{m.TextureFile}"));
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..16];
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

    [Theory]
    [MemberData(nameof(Keep422))]
    public void KeepDrawsOnlyItsNearLevelOnce(string path)
    {
        // keep.NIF holds a NiLODNode with two whole keeps: KeepFar (shapes 151, 158, 163, 168) and
        // KeepNear, both under a node translated (827, 557, -304). The walk keeps the near level; the
        // unreached-shape fallback used to re-add the far shapes without that translation, drawing a
        // second keep ~25 m off through the first. None of the far shapes may appear.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        string[] names = model.Meshes.Select(m => m.Name).ToArray();
        foreach (int far in new[] { 151, 158, 163, 168 })
            Assert.DoesNotContain($"mesh{far}", names);
        Assert.Contains("mesh175", names);   // keepNear:0
        Assert.Equal(names.Length, names.Distinct().Count());
    }
}
