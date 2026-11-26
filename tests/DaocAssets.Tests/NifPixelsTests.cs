// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>Textures stored inside a NIF (NiPixelData): read, decoded and written out as a DDS.</summary>
public sealed class NifPixelsTests
{
    [Fact]
    public void TwentyFourBitPixelsAreReadThroughTheirMasks()
    {
        // Two pixels, stored R G B (the masks say so): red, then a grey-green.
        var p = new NifPixelData(0, 0x0000FF, 0x00FF00, 0xFF0000, 0, 24, -1, [(2, 1, 0)], 3, [255, 0, 0, 90, 110, 80]);
        EmbeddedTexture t = NifPixels.Decode(p, null)!;
        Assert.Equal((2, 1), (t.Width, t.Height));
        Assert.Equal(new byte[] { 0, 0, 255, 255, 80, 110, 90, 255 }, t.Pixels);   // BGRA, opaque
    }

    [Fact]
    public void ThirtyTwoBitPixelsKeepTheirAlpha()
    {
        var p = new NifPixelData(1, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000, 32, -1, [(1, 1, 0)], 4, [10, 20, 30, 128]);
        Assert.Equal(new byte[] { 30, 20, 10, 128 }, NifPixels.Decode(p, null)!.Pixels);
    }

    [Fact]
    public void PaletteIndicesTakeTheirColourAndAPaletteWithoutAlphaIsOpaque()
    {
        var palette = new byte[256 * 4];
        palette[4] = 200; palette[5] = 100; palette[6] = 50; palette[7] = 0;   // entry 1, alpha 0 as stored
        var p = new NifPixelData(2, 0, 0, 0, 0, 8, 0, [(1, 1, 0)], 1, [1]);
        Assert.Equal(new byte[] { 50, 100, 200, 255 }, NifPixels.Decode(p, new NifPalette(false, palette))!.Pixels);
        Assert.Equal(new byte[] { 50, 100, 200, 0 }, NifPixels.Decode(p, new NifPalette(true, palette))!.Pixels);
        Assert.Null(NifPixels.Decode(p, null));   // no palette, nothing to draw
    }

    [Fact]
    public void ATextureIsNamedByItsContentAndFoundByThatName()
    {
        var a = new NifPixelData(0, 0xFF, 0xFF00, 0xFF0000, 0, 24, -1, [(1, 1, 0)], 3, [1, 2, 3]);
        var b = new NifPixelData(0, 0xFF, 0xFF00, 0xFF0000, 0, 24, -1, [(1, 1, 0)], 3, [1, 2, 4]);
        string nameA = NifPixels.Decode(a, null)!.Name;
        Assert.Matches("^emb_[0-9a-f]{12}\\.dds$", nameA);
        Assert.Equal(nameA, NifPixels.Decode(a, null)!.Name);
        Assert.NotEqual(nameA, NifPixels.Decode(b, null)!.Name);
        Assert.Same(NifPixels.Find(nameA), NifPixels.Find(Path.GetFileNameWithoutExtension(nameA)));
    }

    [Fact]
    public void BlockCompressedPixelsAreKeptAsStoredAndShortDataIsNot()
    {
        // 8 x 4 DXT1: two 8-byte blocks; the second mip level after them is not part of the top level.
        byte[] blocks = [.. Enumerable.Range(1, 16).Select(i => (byte)i), 99, 99];
        EmbeddedTexture t = NifPixels.Decode(new NifPixelData(4, 0, 0, 0, 0, 0, -1, [(8, 4, 0), (4, 2, 16)], 0, blocks), null)!;
        Assert.Equal("DXT1", t.Compression);
        Assert.Equal(blocks[..16], t.Pixels);
        byte[] dds = t.ToDds();
        Assert.Equal("DXT1"u8.ToArray(), dds[84..88]);
        Assert.Equal(128 + 16, dds.Length);
        Assert.Equal("DXT5", NifPixels.Decode(new NifPixelData(6, 0, 0, 0, 0, 0, -1, [(4, 4, 0)], 0, new byte[16]), null)!.Compression);
        Assert.Null(NifPixels.Decode(new NifPixelData(4, 0, 0, 0, 0, 0, -1, [(8, 8, 0)], 0, new byte[8]), null));
        Assert.Null(NifPixels.Decode(new NifPixelData(0, 0xFF, 0xFF00, 0xFF0000, 0, 24, -1, [(2, 2, 0)], 3, new byte[5]), null));
        Assert.Null(NifPixels.Decode(new NifPixelData(3, 0, 0, 0, 0, 0, -1, [(1, 1, 0)], 0, new byte[4]), null));   // not a format nif.xml names
    }

    [Fact]
    public void TheDdsIsOneUncompressedLevel()
    {
        var t = new EmbeddedTexture("emb_x.dds", 2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);
        byte[] dds = t.ToDds();
        Assert.Equal("DDS "u8.ToArray(), dds[..4]);
        uint U32(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(at, 4));
        Assert.Equal((1u, 2u, 8u, 32u), (U32(12), U32(16), U32(20), U32(88)));   // height, width, pitch, bits
        Assert.Equal((0x00FF0000u, 0xFF000000u), (U32(92), U32(104)));         // red and alpha masks: A8R8G8B8
        Assert.Equal(128 + 8, dds.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, dds[128..]);
    }

    [Fact]
    public void PixelDataReadsTheTenOneExtraWord()
    {
        // ATextureRenderData: format, 4 masks, bits, 3 + 8 unknown bytes, (10.1: one u32), palette, mips, bytes per
        // pixel, mip (w, h, offset), then the pixel count and bytes.
        byte[] Body(bool ten)
        {
            var w = new List<byte>();
            void U32(uint v) => w.AddRange(BitConverter.GetBytes(v));
            U32(0); U32(0xFF); U32(0xFF00); U32(0xFF0000); U32(0);
            w.Add(24); w.AddRange(new byte[11]);
            if (ten) U32(0);
            U32(0xFFFFFFFF); U32(1); U32(3); U32(1); U32(1); U32(0); U32(3); w.AddRange([7, 8, 9]);
            return [.. w];
        }
        foreach (bool ten in new[] { false, true })
        {
            var c = new NifCursor(Body(ten));
            NifPixelData p = NifPixels.ReadPixelData(ref c, ten);
            Assert.Equal(c.Length, c.Pos);
            Assert.Equal((-1, 3), (p.PaletteRef, p.BytesPerPixel));
            Assert.Equal(new byte[] { 7, 8, 9 }, p.Pixels);
        }
    }

    [LiveFact("live-nif/demontunnel2.nif")]
    public void DarknessFallsLongTunnelHasItsOwnTextures()
    {
        // Its walls' skins are inside the file (NiPixelData, 4.2.1.0); they drew white before (owner, 2026-11-25).
        NifModel model = NifFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", "demontunnel2.nif")));
        NifMesh[] drawn = model.Meshes.Where(m => !m.Helper && m.Vertices.Length > 100).ToArray();
        Assert.NotEmpty(drawn);
        Assert.All(drawn, m => Assert.StartsWith("emb_", m.TextureFile));
        Assert.All(drawn, m => Assert.NotNull(NifPixels.Find(m.TextureFile!)));
        Assert.Contains(drawn, m => m.DarkTextureFile is { } d && d.StartsWith("emb_"));
    }
}
