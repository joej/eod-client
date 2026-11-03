// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocAssets.Nif;
using Xunit;
using Xunit.Abstractions;

namespace DaocAssets.Tests;

/// <summary>
/// The coverage survey behind `mpk audit`: block types, the byte-exact decode check, and features.
/// </summary>
public class NifSurveyTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PrivateNifs()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "live-nif");
        if (!Directory.Exists(dir))
            yield break;
        foreach (string p in Directory.GetFiles(dir, "*.nif").Order())
            yield return [Path.GetFileName(p)];
    }

    [Theory]
    [MemberData(nameof(PrivateNifs))]
    public void EveryDecodedBlockEndsExactlyAtTheNextOne(string name)
    {
        // Every real fixture (git-ignored; from the owner's client): the 4.x block scan finds the
        // header's block count, and every block of a type we decode is consumed byte for byte.
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", name));
        NifSurvey s = NifSurvey.Read(bytes);
        output.WriteLine($"{name}: {s.Version}, {s.BlockTypes.Count}/{s.DeclaredBlocks} blocks");
        foreach (var g in s.BlockTypes.GroupBy(t => t))
            output.WriteLine($"  {g.Key} x{g.Count()}  exact {s.ExactDecodes.GetValueOrDefault(g.Key)}" +
                $"  failed {s.FailedDecodes.GetValueOrDefault(g.Key)}");

        Assert.True(s.BoundariesMatchHeader);
        Assert.Empty(s.FailedDecodes);
    }

    [Fact]
    public void ADecodeThatOverrunsOrFallsShortIsReported()
    {
        // One NiAlphaProperty, decoded exactly; then the same with a stray byte in the body.
        NifSurvey good = NifSurvey.Read(OneAlphaBlock(extraByte: false));
        Assert.Equal(1, good.ExactDecodes["NiAlphaProperty"]);
        Assert.Empty(good.FailedDecodes);
        Assert.Equal(1, good.Features["NiAlphaProperty: flags 0x1221 threshold 75 -> Cutout"]);

        NifSurvey bad = NifSurvey.Read(OneAlphaBlock(extraByte: true));
        Assert.Equal(1, bad.FailedDecodes["NiAlphaProperty"]);
        Assert.Empty(bad.ExactDecodes);
    }

    [Fact]
    public void EveryTypeTheConverterUsesIsOneTheSurveyDecodes()
    {
        // A type the converter claims to handle must be one whose layout the audit can verify.
        var v4 = new NifVersion(4, 2, 2, 0);
        string[] all = [.. NifSurvey.DecodedTypes4x, "NiKeyframeController", "NiStringExtraData"];
        foreach (string t in all)
            if (NifSurvey.Coverage(t, v4) != NifCoverage.Unhandled)
                Assert.Contains(t, NifSurvey.DecodedTypes4x);
        Assert.Equal(NifCoverage.Unhandled, NifSurvey.Coverage("NiKeyframeController", v4));
        Assert.NotEmpty(NifSurvey.CoverageNote("NiTexturingProperty", v4));
    }

    [Fact]
    public void AuditReportListsTypesAndIntegrity()
    {
        var audit = new NifAudit();
        audit.Add("zones", "a.nif", NifSurvey.Read(OneAlphaBlock(extraByte: false)));
        audit.Add("items", "b.nif", NifSurvey.Read(OneAlphaBlock(extraByte: true)));
        audit.AddUnreadable("items", "c.nif", "Not a NIF file");
        string md = audit.ToMarkdown("test");

        Assert.Contains("| NiAlphaProperty | 4.x Handled | 2 | 2 | 4.x | 1/2 |", md);
        Assert.Contains("**NiAlphaProperty**: 1 block(s) did not decode exactly, e.g. b.nif", md);
        Assert.Contains("- **Not a NIF file** (1): c.nif", md);
    }

    // A minimal NetImmerse 4.2.2.0 file: header line, version, one NiAlphaProperty block (empty name,
    // no extra data, no controller, flags 0x1221, threshold 75), then the footer (one root, block 0).
    private static byte[] OneAlphaBlock(bool extraByte)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.2.0\n"));
        w.Write(0x04020200u);
        w.Write(1u);
        w.Write((uint)"NiAlphaProperty".Length);
        w.Write(Encoding.ASCII.GetBytes("NiAlphaProperty"));
        w.Write(0u);          // Name (empty)
        w.Write(-1);          // Extra Data
        w.Write(-1);          // Controller
        w.Write((ushort)0x1221);
        w.Write((byte)75);
        if (extraByte)
            w.Write((byte)0);
        w.Write(1u);          // Num Roots
        w.Write(0);           // root: block 0
        return ms.ToArray();
    }
}

/// <summary>
/// The Gamebryo 10.1 sequential decode: blocks have no sizes, so every block is decoded in order and
/// the last must end exactly at the footer. Synthetic files, so each way it can go wrong is pinned.
/// </summary>
public class NifSurvey1010Tests
{
    // A 10.1.0.0 file: header, type table, Unknown Int 2, blocks (each after the u32 zero marker), footer.
    private static byte[] File1010(string[] types, Action<BinaryWriter, int>[] bodies, bool marker = true)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 10.1.0.0\n"));
        w.Write(0x0A010000u);
        w.Write(0u);                                    // User Version
        w.Write((uint)types.Length);                    // Num Blocks
        string[] distinct = types.Distinct().ToArray();
        w.Write((ushort)distinct.Length);
        foreach (string t in distinct) { w.Write((uint)t.Length); w.Write(Encoding.ASCII.GetBytes(t)); }
        foreach (string t in types) w.Write((ushort)Array.IndexOf(distinct, t));
        w.Write(0u);                                    // Unknown Int 2
        for (int i = 0; i < bodies.Length; i++)
        {
            if (marker) w.Write(0u);
            bodies[i](w, i);
        }
        w.Write(1u); w.Write(0);                        // footer: one root, block 0
        return ms.ToArray();
    }

    // NiNode body at 10.1: name, extra-data list, controller, flags, transform, properties, collision,
    // children, effects.
    private static void Node(BinaryWriter w, string name, int[] children)
    {
        w.Write((uint)name.Length); w.Write(Encoding.ASCII.GetBytes(name));
        w.Write(0u); w.Write(-1);                       // extra data list, controller
        w.Write((ushort)0x000C);
        for (int k = 0; k < 3 + 9; k++) w.Write(k is 3 or 7 or 11 ? 1f : 0f);   // translation, identity rotation
        w.Write(1f);                                    // scale
        w.Write(0u); w.Write(-1);                       // properties, collision object
        w.Write((uint)children.Length); foreach (int c in children) w.Write(c);
        w.Write(0u);                                    // effects
    }

    private static void Alpha(BinaryWriter w)
    {
        w.Write(0u); w.Write(0u); w.Write(-1);          // name, extra data list, controller
        w.Write((ushort)0x1221); w.Write((byte)75);
    }

    [Fact]
    public void AFileWhoseBlocksDecodeToTheFooterIsComplete()
    {
        byte[] f = File1010(["NiNode", "NiAlphaProperty"], [(w, _) => Node(w, "Scene Root", [1]), (w, _) => Alpha(w)]);
        NifSurvey s = NifSurvey.Read(f);
        Assert.Equal(NifSurvey.SequentialDecode.Complete, s.Sequential);
        Assert.Equal(1, s.ExactDecodes["NiNode"]);
        Assert.Equal(1, s.ExactDecodes["NiAlphaProperty"]);
        Assert.Equal(1, s.Features["[10.1] NiAlphaProperty: flags 0x1221 threshold 75 -> Cutout"]);
    }

    [Fact]
    public void AStrayByteMakesTheDecodeMissTheFooter()
    {
        byte[] f = File1010(["NiNode", "NiAlphaProperty"],
            [(w, _) => Node(w, "Scene Root", [1]), (w, _) => { Alpha(w); w.Write((byte)0); }]);
        NifSurvey s = NifSurvey.Read(f);
        Assert.Equal(NifSurvey.SequentialDecode.Failed, s.Sequential);
        Assert.Equal("(footer)", s.StopType);
        Assert.Empty(s.ExactDecodes);
        Assert.Empty(s.Features);   // features from a broken decode are not reported
    }

    [Fact]
    public void AMissingBlockMarkerOrABadReferenceFailsAtThatBlock()
    {
        byte[] noMarker = File1010(["NiAlphaProperty"], [(w, _) => Alpha(w)], marker: false);
        NifSurvey a = NifSurvey.Read(noMarker);
        Assert.Equal(NifSurvey.SequentialDecode.Failed, a.Sequential);
        Assert.Equal("NiAlphaProperty", a.StopType);

        byte[] badChild = File1010(["NiNode"], [(w, _) => Node(w, "Scene Root", [7])]);
        NifSurvey b = NifSurvey.Read(badChild);
        Assert.Equal(NifSurvey.SequentialDecode.Failed, b.Sequential);
        Assert.Contains("Reference 7", b.StopReason);
    }

    // NiTriShape at 10.1: NiAVObject (as Node, without children), data, skin, no shader.
    private static void Shape(BinaryWriter w, string name, int data)
    {
        w.Write((uint)name.Length); w.Write(Encoding.ASCII.GetBytes(name));
        w.Write(0u); w.Write(-1);
        w.Write((ushort)0x000C);
        for (int k = 0; k < 3 + 9; k++) w.Write(k is 3 or 7 or 11 ? 1f : 0f);
        w.Write(1f);
        w.Write(0u); w.Write(-1);
        w.Write(data); w.Write(-1); w.Write((byte)0);
    }

    // NiTriShapeData at 10.1: one triangle, no normals, colours or UVs.
    private static void Triangle(BinaryWriter w)
    {
        w.Write((ushort)3); w.Write((byte)0); w.Write((byte)0);
        w.Write((byte)1); foreach (float v in new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 }) w.Write(v);
        w.Write((ushort)0); w.Write((byte)0);           // data flags (no UVs), no normals
        for (int k = 0; k < 4; k++) w.Write(0f);        // bounding sphere
        w.Write((byte)0);                               // no vertex colours
        w.Write((ushort)0);                             // consistency flags
        w.Write((ushort)1); w.Write(3u); w.Write((byte)1);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
        w.Write((ushort)0);                             // match groups
    }

    [Fact]
    public void AnLodNodeKeepsItsNearestLevelAndParticlesAreReadPast()
    {
        // Root -> LOD node (range data lists the far level first) -> far and near child nodes, each
        // with one triangle; a particle modifier sits in the file too. Before 2026-11-03 the decode
        // stopped at the LOD node and the whole file fell back to the geometry scan.
        string[] types = ["NiNode", "NiLODNode", "NiRangeLODData", "NiNode", "NiTriShape", "NiTriShapeData",
            "NiNode", "NiTriShape", "NiTriShapeData", "NiParticleGrowFade"];
        byte[] f = File1010(types,
        [
            (w, _) => Node(w, "Scene Root", [1]),
            (w, _) =>
            {
                Node(w, "lod", [3, 6]);
                w.Write((ushort)0); w.Write(0u); w.Write(2);   // switch flags, index, LOD data
            },
            (w, _) =>
            {
                for (int k = 0; k < 3; k++) w.Write(0f);       // centre
                w.Write(2u);
                w.Write(500f); w.Write(10000f);                 // level 0: far
                w.Write(0f); w.Write(500f);                     // level 1: near
            },
            (w, _) => Node(w, "level a", [4]),
            (w, _) => Shape(w, "far shape", 5),
            (w, _) => Triangle(w),
            (w, _) => Node(w, "level b", [7]),
            (w, _) => Shape(w, "near shape", 8),
            (w, _) => Triangle(w),
            (w, _) => { w.Write(-1); w.Write(-1); w.Write(1f); w.Write(2f); },   // next, controller, grow, fade
        ]);

        Assert.Equal(NifSurvey.SequentialDecode.Complete, NifSurvey.Read(f).Sequential);
        NifModel model = NifFile.Read(f);
        NifMesh mesh = Assert.Single(model.Meshes, m => !m.Helper);
        Assert.Equal("near shape", mesh.ShapeName);
    }

    [Fact]
    public void ATypeWeDontDecodeStopsTheDecodeWithoutPassingAnything()
    {
        byte[] f = File1010(["NiNode", "NiMeshParticleSystem"], [(w, _) => Node(w, "Scene Root", [1]), (w, _) => w.Write(0u)]);
        NifSurvey s = NifSurvey.Read(f);
        Assert.Equal(NifSurvey.SequentialDecode.Stopped, s.Sequential);
        Assert.Equal("NiMeshParticleSystem", s.StopType);
        Assert.Empty(s.ExactDecodes);
    }

    [Fact]
    public void ASeparatePartsRepeatedBonePointersAreReportedAsBoundByName()
    {
        // A fig3 body part: two skin bones, both pointing at the file's only node (its bones live in a
        // separate skeleton file and are matched by name).
        byte[] f = File1010(["NiNode", "NiSkinInstance"], [(w, _) => Node(w, "Scene Root", []), (w, _) =>
        {
            w.Write(-1); w.Write(0);                    // Data, Skeleton Root
            w.Write(2u); w.Write(0); w.Write(0);        // Bones
        }]);
        NifSurvey s = NifSurvey.Read(f);
        Assert.Equal(NifSurvey.SequentialDecode.Complete, s.Sequential);
        Assert.Equal(1, s.Features["[10.1] NiSkinInstance: bone pointers repeat (bones bound by name to a separate skeleton)"]);
    }

    [Fact]
    public void EveryTenOneTypeTheConverterUsesIsOneTheSurveyDecodes()
    {
        var v10 = new NifVersion(10, 1, 0, 0);
        foreach (string t in new[] { "NiTriShapeData", "NiTriStripsData", "NiSourceTexture", "NiSkinData", "NiNode" })
            if (NifSurvey.Coverage(t, v10) != NifCoverage.Unhandled)
                Assert.Contains(t, NifSurvey.DecodedTypes1010);
    }
}

public class NifBlockScanTests
{
    [Fact]
    public void TextStartingWithNiInsideABodyIsNotABlock()
    {
        // NiStringExtraData whose text is "NiMAXPSysConverter flag" (as 3ds Max writes it), then a real
        // NiAlphaProperty. Accepting the text as a type name used to end the 2-block scan early and
        // drop the NiAlphaProperty.
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.2.0\n"));
        w.Write(0x04020200u);
        w.Write(2u);
        void Sized(string text) { w.Write((uint)text.Length); w.Write(Encoding.ASCII.GetBytes(text)); }
        Sized("NiStringExtraData");
        w.Write(-1);                     // Next Extra Data
        w.Write(0u);                     // Bytes Remaining
        Sized("NiMAXPSysConverter flag");
        Sized("NiAlphaProperty");
        w.Write(0u); w.Write(-1); w.Write(-1); w.Write((ushort)0x1221); w.Write((byte)75);
        w.Write(1u); w.Write(0);         // footer

        // The converter's own scan, capped at the header's block count as NifFile.Read uses it.
        Assert.Equal(["NiStringExtraData", "NiAlphaProperty"],
            NifFile.IndexBlocks(ms.ToArray(), 2).Select(b => b.Type));

        NifSurvey s = NifSurvey.Read(ms.ToArray());
        Assert.Equal(["NiStringExtraData", "NiAlphaProperty"], s.BlockTypes);
        Assert.True(s.BoundariesMatchHeader);
        Assert.Equal(1, s.ExactDecodes["NiAlphaProperty"]);
    }
}

/// <summary>
/// Skinned 10.1 figures: the tree reader, skin weights and inverse bind matrices, checked on a classic
/// figure from the owner's client (git-ignored fixture; skipped when absent).
/// </summary>
public class NifSkinTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "live-nif", "bcommonm-1010.nif");

    [Fact]
    public void AClassicFigureStandsAssembledInItsBindPose()
    {
        if (!File.Exists(Fixture))
            return;   // private fixture (the owner's figures/bcommonm.nif) not present
        byte[] bytes = File.ReadAllBytes(Fixture);
        NifModel model = NifFile.Read(bytes);
        Assert.True(model.Joints.Count > 100);
        Assert.Contains(model.Joints, j => j.Name == "Bip01 Head");

        // Every bone's bind * world lands on its skinned shape's world transform (NIF units).
        Assert.InRange(NifFile.BindPoseError(bytes)!.Value, 0f, 0.5f);

        // Parts stored around the origin are placed on the body by their skin: the torso and head
        // must come out at body height, not at the feet (which is what including NiSkinData's overall
        // Skin Transform in the inverse bind matrix did).
        float MinZ(string part) => model.Posed(model.Meshes.Single(m => m.ShapeName.StartsWith(part + ":"))).Min(v => v.Z);
        float MaxZ(string part) => model.Posed(model.Meshes.Single(m => m.ShapeName.StartsWith(part + ":"))).Max(v => v.Z);
        Assert.True(model.Meshes.Single(m => m.ShapeName.StartsWith("Body1:")).Vertices.Min(v => v.Z) < 0);
        Assert.InRange(MinZ("Body1"), 40f, 50f);
        Assert.InRange(MaxZ("HeadA1"), 65f, 75f);
        Assert.InRange(MinZ("Boots1"), -1f, 2f);

        // Weights: at most four per vertex, normalised.
        foreach (NifMesh m in model.Meshes.Where(m => m.Skin is not null))
            foreach (var w in m.Skin!)
            {
                Assert.InRange(w.Length, 1, 4);
                Assert.InRange(w.Sum(x => x.Weight), 0.999f, 1.001f);
            }
    }

    [Fact]
    public void TheExportedSkinPosesTheFigureToHumanHeight()
    {
        if (!File.Exists(Fixture))
            return;
        NifModel model = NifFile.Read(File.ReadAllBytes(Fixture));
        // Keep one variant per slot, as the viewer does, and measure through glTF's own skinning.
        string[] parts = ["Body1:", "LBody1:", "Legs1:", "Boots1:", "Arms1:", "Gloves1:", "HeadA1:"];
        var one = new NifModel
        {
            Version = model.Version,
            Joints = model.Joints,
            Meshes = model.Meshes.Where(m => parts.Any(p => m.ShapeName.StartsWith(p))).ToList(),
        };
        var gltf = GltfExporter.BuildScene(one).ToGltf2();
        Assert.Equal(parts.Length, gltf.LogicalSkins.Count);
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var (a, b, c, _) in SharpGLTF.Schema2.Toolkit.EvaluateTriangles(gltf.DefaultScene))
            foreach (var v in new[] { a.GetGeometry().GetPosition(), b.GetGeometry().GetPosition(), c.GetGeometry().GetPosition() })
            {
                minY = Math.Min(minY, v.Y);
                maxY = Math.Max(maxY, v.Y);
            }
        Assert.InRange(minY, -0.05f, 0.05f);          // feet on the ground
        Assert.InRange(maxY - minY, 1.6f, 2.0f);      // a ~1.8 m Briton
    }
}

/// <summary>Every private fixture converts to finite geometry: a NaN vertex is dropped silently by the
/// glTF writer, so it has to be caught here (ncarcass-421.nif has a node with a NaN transform).</summary>
public class NifFiniteGeometryTests
{
    [Theory]
    [MemberData(nameof(NifSurveyTests.PrivateNifs), MemberType = typeof(NifSurveyTests))]
    public void EveryFixtureConvertsToFiniteVertices(string name)
    {
        NifModel model = NifFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", name)));
        foreach (NifMesh m in model.Meshes.Where(m => !m.Helper))
            Assert.All(m.Vertices, v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), $"{m.Name}: {v}"));
        var gltf = GltfExporter.BuildScene(model).ToGltf2();
        Assert.Equal(model.Meshes.Count(m => !m.Helper), gltf.LogicalMeshes.Count);   // nothing dropped
    }
}
