// SPDX-License-Identifier: GPL-3.0-or-later

namespace DaocAssets.Tests;

/// <summary>
/// A DDS with a partial mipmap chain (wood_no_planks_256x512.dds: 512 x 256 DXT1, 3 levels of 10) is cut to its
/// top level, which Godot loads; one level or a full chain is left alone. Crafted headers in the file's layout.
/// </summary>
public sealed class DdsMipmapsTests
{
    private static byte[] Dds(uint width, uint height, uint mips, string fourCc, int dataBytes)
    {
        byte[] b = new byte[128 + dataBytes];
        "DDS "u8.CopyTo(b);
        BitConverter.GetBytes(124u).CopyTo(b, 4);
        BitConverter.GetBytes(0xA1007u).CopyTo(b, 8);        // as the file: caps, height, width, pixel format, mipmap count, linear size
        BitConverter.GetBytes(height).CopyTo(b, 12);
        BitConverter.GetBytes(width).CopyTo(b, 16);
        BitConverter.GetBytes(mips).CopyTo(b, 28);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(b, 84);
        BitConverter.GetBytes(0x401008u).CopyTo(b, 108);      // texture, complex, mipmap
        for (int i = 128; i < b.Length; i++)
            b[i] = (byte)i;
        return b;
    }

    [Fact]
    public void APartialChainIsCutToItsTopLevel()
    {
        // 512 x 256 DXT1: levels of 65,536, 16,384 and 4,096 bytes.
        byte[] file = Dds(512, 256, 3, "DXT1", 86016);
        byte[] top = DdsMipmaps.TopLevelIfPartial(file);

        Assert.Equal(128 + 65536, top.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(top, 28));
        Assert.Equal(0u, BitConverter.ToUInt32(top, 8) & 0x20000);       // no mipmap count
        Assert.Equal(0x1000u, BitConverter.ToUInt32(top, 108));          // a plain texture
        Assert.Equal(file.AsSpan(128, 65536).ToArray(), top.AsSpan(128).ToArray());
    }

    [Fact]
    public void OneLevelAFullChainOrUncompressedStayAsTheyAre()
    {
        byte[] one = Dds(512, 256, 1, "DXT1", 65536);
        byte[] full = Dds(512, 256, 10, "DXT5", 174776);
        byte[] plain = Dds(512, 256, 3, "\0\0\0\0", 100);
        Assert.Same(one, DdsMipmaps.TopLevelIfPartial(one));
        Assert.Same(full, DdsMipmaps.TopLevelIfPartial(full));
        Assert.Same(plain, DdsMipmaps.TopLevelIfPartial(plain));
        Assert.Same(Array.Empty<byte>(), DdsMipmaps.TopLevelIfPartial([]));
    }
}
