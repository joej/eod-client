// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Nif;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;

namespace DaocAssets.Tests;

/// <summary>
/// Transparency and sidedness from NiAlphaProperty / NiStencilProperty: DAoC's trees are crossed
/// planes whose leaf textures are alpha-tested, so they only read as foliage when the transparent
/// pixels are cut out and both faces draw (roadmap M7a).
/// </summary>
public class NifAlphaTests(ITestOutputHelper output)
{
    [Theory]
    // DAoC's trees (elm1, bpinea, bwillow, oak1): blend ONE/ZERO + test GREATER 75 → cut-out at 75/255.
    [InlineData(0x1221, 75, SurfaceAlpha.Cutout, 75 / 255f)]
    // nif.xml's default flags 0x00ED: blend SRC_ALPHA / INV_SRC_ALPHA, no test → cut-out at 0.5.
    [InlineData(0x00ED, 0, SurfaceAlpha.Cutout, 0.5f)]
    // Blend onto ONE (dest bits 0) = additive glow; onto DESTALPHA (8); onto INVSRCCOLOR (3): blend.
    [InlineData(0x0001, 0, SurfaceAlpha.Blend, 0.5f)]
    [InlineData(0x0101, 0, SurfaceAlpha.Blend, 0.5f)]
    [InlineData(0x0061, 0, SurfaceAlpha.Blend, 0.5f)]
    // A test with a zero reference still cuts something: floor at 0.05.
    [InlineData(0x0200, 0, SurfaceAlpha.Cutout, 0.05f)]
    // Neither blend nor test.
    [InlineData(0x0000, 128, SurfaceAlpha.Opaque, 0.5f)]
    public void MapsAlphaFlagsToAGltfMode(int flags, int threshold, SurfaceAlpha mode, float cutoff)
    {
        (SurfaceAlpha m, float c) = NifFile.AlphaMode((ushort)flags, (byte)threshold);
        Assert.Equal(mode, m);
        Assert.Equal(cutoff, c, 3);
    }

    public static IEnumerable<object[]> Trees()
    {
        foreach (string name in new[] { "elm1-421.nif", "oak1-4101.nif" })
        {
            string p = Path.Combine(AppContext.BaseDirectory, "live-nif", name);
            if (File.Exists(p))
                yield return [p];
        }
    }

    [LiveTheory("live-nif/elm1-421.nif", "live-nif/oak1-4101.nif")]
    [MemberData(nameof(Trees))]
    public void TreeLeavesAreTwoSidedCutouts(string path)
    {
        // zones/Nifs elm1 (4.2.1.0) and oak1 (4.1.0.12) from the owner's client (git-ignored fixtures).
        // Each has one NiAlphaProperty, flags 0x1221 threshold 75.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        foreach (NifMesh m in model.Meshes)
            output.WriteLine(m.ToString());

        NifMesh[] cut = model.Meshes.Where(m => m.Alpha == SurfaceAlpha.Cutout).ToArray();
        Assert.NotEmpty(cut);
        Assert.All(cut, m =>
        {
            Assert.Equal(75 / 255f, m.AlphaCutoff, 3);
            Assert.True(m.DoubleSided);
            Assert.NotNull(m.TextureFile);
        });
        Assert.DoesNotContain(model.Meshes, m => m.Alpha == SurfaceAlpha.Blend);
    }

    [Fact]
    public void ExportWritesMaskCutoffAndDoubleSided()
    {
        var tri = new NifMesh
        {
            Name = "leaf",
            Vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            TexCoords = [new(0, 0), new(1, 0), new(0, 1)],
            Triangles = [(0, 1, 2)],
            TextureFile = "leaves.dds",
            Alpha = SurfaceAlpha.Cutout,
            AlphaCutoff = 75 / 255f,
            DoubleSided = true,
        };
        var opaque = tri with { Name = "trunk", TextureFile = "bark.dds", Alpha = SurfaceAlpha.Opaque, DoubleSided = false };
        var model = new NifModel { Version = new NifVersion(4, 2, 1, 0), Meshes = [tri, opaque] };

        ModelRoot gltf = GltfExporter.BuildScene(model).ToGltf2();
        Material leaves = gltf.LogicalMaterials.Single(m => m.Name == "leaves");
        Material bark = gltf.LogicalMaterials.Single(m => m.Name == "bark");

        Assert.Equal(AlphaMode.MASK, leaves.Alpha);
        Assert.Equal(75 / 255f, leaves.AlphaCutoff, 3);
        Assert.True(leaves.DoubleSided);
        Assert.Equal(AlphaMode.OPAQUE, bark.Alpha);
        Assert.False(bark.DoubleSided);
    }
}
