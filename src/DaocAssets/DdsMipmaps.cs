// SPDX-License-Identifier: GPL-3.0-or-later
//
// Some of the install's DDS textures carry a partial mipmap chain: wood_no_planks_256x512.dds is 512 x 256 DXT1
// with 3 levels where a full chain has 10. Godot's DDS loader expects either one level or the full chain, and
// rejects the file ("Expected Image data size … with 9 mipmaps"), which left every model using it untextured
// (the tutorial's Constantine's Sound, 2026-11-08). For such a file this gives the top level alone, which the
// client loads and builds its own mipmaps from.

namespace DaocAssets;

public static class DdsMipmaps
{
    private const int HeaderSize = 128;          // "DDS " + the 124-byte header
    private const uint MipmapCountFlag = 0x20000; // DDSD_MIPMAPCOUNT in dwFlags
    private const uint CapsComplex = 0x8, CapsMipmap = 0x400000;

    /// <summary>
    /// For a block-compressed DDS (DXT1/3/5) with more than one level but fewer than the full chain: the same
    /// file with the top level only. Anything else (one level, a full chain, uncompressed, not a DDS) comes back
    /// unchanged, the same array.
    /// </summary>
    public static byte[] TopLevelIfPartial(byte[] dds)
    {
        if (dds.Length < HeaderSize || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ')
            return dds;
        uint flags = U32(dds, 8), height = U32(dds, 12), width = U32(dds, 16), mips = U32(dds, 28);
        string fourCc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        int block = fourCc switch { "DXT1" => 8, "DXT3" or "DXT5" => 16, _ => 0 };
        if (block == 0 || width == 0 || height == 0 || (flags & MipmapCountFlag) == 0 || mips <= 1)
            return dds;
        int full = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        if (mips >= full)
            return dds;
        long top = (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block;
        if (HeaderSize + top > dds.Length)
            return dds;
        byte[] result = new byte[HeaderSize + top];
        Array.Copy(dds, result, result.Length);
        Put(result, 8, flags & ~MipmapCountFlag);
        Put(result, 28, 1);
        Put(result, 108, U32(dds, 108) & ~(CapsComplex | CapsMipmap));
        return result;
    }

    private static uint U32(byte[] b, int at) => BitConverter.ToUInt32(b, at);

    private static void Put(byte[] b, int at, uint value) => BitConverter.GetBytes(value).CopyTo(b, at);
}
