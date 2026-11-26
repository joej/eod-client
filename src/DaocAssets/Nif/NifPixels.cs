// SPDX-License-Identifier: GPL-3.0-or-later
//
// Textures stored inside a NIF instead of in a file beside it: a NiSourceTexture with Use External 0 points at a
// NiPixelData holding the pixels (and, for 8-bit ones, a NiPalette). Darkness Falls' long tunnel, Tir na Nog's houses
// and some items and figures carry their skins this way; without these their surfaces drew white. Layouts from
// NifTools' nif.xml (GPL-3.0) for 4.x and 10.1.0.0 (ATextureRenderData, NiPixelData, NiPalette); the channel masks and
// the opaque reading of a palette without alpha agree with the Offline DAoC pipeline's decoder (GPL-3.0,
// nif_static.py embedded_image).
//
// A decoded texture is named by its content ("emb_" + 12 hex digits of its SHA-1, ".dds"), so the same pixels in two
// models are one texture and a name can never mean two images. The converter registers each one here when it reads
// the model; AssetLibrary.GatherTexture writes it into the library (a single-level DDS, uncompressed or DXT as stored:
// the client builds the mipmaps) when a model or figure asks for that name.

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DaocAssets.Nif;

/// <summary>
/// A texture from a NIF's own pixel data: its name and its top level, rows top first: BGRA bytes when
/// <paramref name="Compression"/> is null, else the DXT1 or DXT5 blocks as stored.
/// </summary>
public sealed record EmbeddedTexture(string Name, int Width, int Height, byte[] Pixels, string? Compression = null)
{
    /// <summary>The texture as a DDS file: one level, 32-bit A8R8G8B8 or the stored DXT blocks.</summary>
    public byte[] ToDds()
    {
        var dds = new byte[128 + Pixels.Length];
        void Put(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(at, 4), v);
        "DDS "u8.CopyTo(dds);
        Put(4, 124);                                   // header size
        Put(12, (uint)Height);
        Put(16, (uint)Width);
        Put(76, 32);                                   // pixel format size
        Put(108, 0x1000);                              // caps: TEXTURE
        if (Compression is null)
        {
            Put(8, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000);    // CAPS, HEIGHT, WIDTH, PITCH, PIXELFORMAT
            Put(20, (uint)Width * 4);                  // pitch
            Put(80, 0x1 | 0x40);                       // ALPHAPIXELS, RGB
            Put(88, 32);                               // bits
            Put(92, 0x00FF0000); Put(96, 0x0000FF00); Put(100, 0x000000FF); Put(104, 0xFF000000);
        }
        else
        {
            Put(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // CAPS, HEIGHT, WIDTH, PIXELFORMAT, LINEARSIZE
            Put(20, (uint)Pixels.Length);              // linear size of the level
            Put(80, 0x4);                              // FOURCC
            System.Text.Encoding.ASCII.GetBytes(Compression).CopyTo(dds, 84);
        }
        Pixels.CopyTo(dds, 128);
        return dds;
    }
}

/// <summary>NiPixelData as stored (4.x and 10.1.0.0).</summary>
internal sealed record NifPixelData(uint Format, uint RedMask, uint GreenMask, uint BlueMask, uint AlphaMask,
    int BitsPerPixel, int PaletteRef, (int Width, int Height, int Offset)[] Mipmaps, int BytesPerPixel, byte[] Pixels);

/// <summary>NiPalette as stored: its entries as RGBA bytes.</summary>
internal sealed record NifPalette(bool HasAlpha, byte[] Rgba);

/// <summary>Reads and decodes NIF-embedded textures, and holds the ones decoded so far by name.</summary>
public static class NifPixels
{
    private static readonly ConcurrentDictionary<string, EmbeddedTexture> Decoded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A texture decoded from a model read in this process, by its name with or without the extension.</summary>
    public static EmbeddedTexture? Find(string name) =>
        Decoded.TryGetValue(Path.GetFileNameWithoutExtension(name), out EmbeddedTexture? t) ? t : null;

    // NiPixelData (ATextureRenderData + pixels). 10.1.0.0 adds one u32 after the eight unknown bytes.
    internal static NifPixelData ReadPixelData(ref NifCursor c, bool gamebryo10)
    {
        uint format = c.U32();
        uint red = c.U32(), green = c.U32(), blue = c.U32(), alpha = c.U32();
        int bits = c.U8();
        for (int i = 0; i < 3 + 8; i++) c.U8();        // Unknown 3 Bytes, Unknown 8 Bytes
        if (gamebryo10) c.U32();                       // Unknown Int
        int palette = c.I32();
        uint mips = c.U32();
        if (mips > 32) throw new NifFormatException($"Implausible mipmap count {mips}.");
        int bytesPerPixel = (int)c.U32();
        var levels = new (int, int, int)[mips];
        for (int i = 0; i < mips; i++)
            levels[i] = ((int)c.U32(), (int)c.U32(), (int)c.U32());
        uint size = c.U32();
        if (size > c.Length - c.Pos) throw new NifFormatException("Pixel data runs past the end of the file.");
        var pixels = new byte[size];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = c.U8();
        return new NifPixelData(format, red, green, blue, alpha, bits, palette, levels, bytesPerPixel, pixels);
    }

    internal static NifPalette ReadPalette(ref NifCursor c)
    {
        bool hasAlpha = c.U8() != 0;
        uint entries = c.U32();
        var rgba = new byte[(entries == 16 ? 16 : 256) * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = c.U8();
        return new NifPalette(hasAlpha, rgba);
    }

    /// <summary>
    /// The texture's top level, registered under its content name; null for a format not read or data too short for its
    /// own description. Formats (nif.xml PixelFormat): 0 RGB and 1 RGBA through their masks, 2 palette indices, 4 DXT1,
    /// 5 and 6 DXT5 (the 10.1 files' own skins are mostly 4 and 6), kept compressed.
    /// </summary>
    internal static EmbeddedTexture? Decode(NifPixelData p, NifPalette? palette)
    {
        if (p.Mipmaps.Length == 0)
            return null;
        (int w, int h, int off) = p.Mipmaps[0];
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || off < 0)
            return null;
        if (p.Format is 4 or 5 or 6)
        {
            int block = p.Format == 4 ? 8 : 16;
            int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
            if (off + size > p.Pixels.Length)
                return null;
            return Register(w, h, p.Pixels.AsSpan(off, size).ToArray(), p.Format == 4 ? "DXT1" : "DXT5");
        }
        var bgra = new byte[w * h * 4];
        if (p.Format == 2 || p.BitsPerPixel == 8)
        {
            // 8-bit palette indices. A palette that says it has no alpha draws opaque.
            if (palette is null || off + w * h > p.Pixels.Length)
                return null;
            int count = palette.Rgba.Length / 4;
            for (int i = 0; i < w * h; i++)
            {
                int e = Math.Min(p.Pixels[off + i], count - 1) * 4;
                bgra[i * 4] = palette.Rgba[e + 2];
                bgra[i * 4 + 1] = palette.Rgba[e + 1];
                bgra[i * 4 + 2] = palette.Rgba[e];
                bgra[i * 4 + 3] = palette.HasAlpha ? palette.Rgba[e + 3] : (byte)255;
            }
        }
        else if (p.Format is 0 or 1)
        {
            int bpp = p.BytesPerPixel is 3 or 4 ? p.BytesPerPixel : p.BitsPerPixel / 8;
            if (bpp is not (3 or 4) || off + w * h * bpp > p.Pixels.Length)
                return null;
            for (int i = 0; i < w * h; i++)
            {
                uint word = 0;
                for (int k = 0; k < bpp; k++)
                    word |= (uint)p.Pixels[off + i * bpp + k] << (8 * k);
                bgra[i * 4] = Channel(word, p.BlueMask, 255);
                bgra[i * 4 + 1] = Channel(word, p.GreenMask, 255);
                bgra[i * 4 + 2] = Channel(word, p.RedMask, 255);
                bgra[i * 4 + 3] = bpp == 4 ? Channel(word, p.AlphaMask, 255) : (byte)255;
            }
        }
        else
            return null;

        return Register(w, h, bgra, null);
    }

    private static EmbeddedTexture Register(int w, int h, byte[] pixels, string? compression)
    {
        byte[] hash = SHA1.HashData([.. BitConverter.GetBytes(w), .. BitConverter.GetBytes(h),
            .. System.Text.Encoding.ASCII.GetBytes(compression ?? "BGRA"), .. pixels]);
        string name = "emb_" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        return Decoded.GetOrAdd(name, new EmbeddedTexture(name + ".dds", w, h, pixels, compression));
    }

    // One channel through its mask, scaled to 8 bits; an empty mask gives the default.
    private static byte Channel(uint word, uint mask, byte none)
    {
        if (mask == 0)
            return none;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint v = (word & mask) >> shift;
        return max == 255 ? (byte)v : (byte)(v * 255 / max);
    }
}
