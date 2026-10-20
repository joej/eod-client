// SPDX-License-Identifier: GPL-3.0-or-later
//
// A minimal reader for the 8-bit, single-plane, RLE-encoded PCX images DAoC uses for zone data
// (terrain.pcx heightmap, offset.pcx). We need only the raw byte-per-pixel grid, not the palette.
// Format: ZSoft PCX (manufacturer 0x0A), 128-byte header, then PCX run-length encoding.

using System.Buffers.Binary;

namespace DaocAssets.Zone;

/// <summary>An 8-bit PCX image as a width×height grid of byte values (index 0..255).</summary>
public sealed class Pcx
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Row-major pixel values, length Width*Height.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>The value at (x, y), clamped to the image bounds.</summary>
    public byte At(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return Pixels[y * Width + x];
    }

    /// <summary>Parses an 8-bit single-plane RLE PCX. Throws on anything else.</summary>
    public static Pcx Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 128 || d[0] != 0x0A)
            throw new FormatException("Not a PCX file (bad manufacturer byte).");
        int encoding = d[2];
        int bpp = d[3];
        int xmin = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
        int ymin = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
        int xmax = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
        int ymax = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
        int planes = d[65];
        int bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(d[66..]);

        if (bpp != 8 || planes != 1 || encoding != 1)
            throw new FormatException($"Unsupported PCX (bpp={bpp}, planes={planes}, enc={encoding}); need 8-bit, 1-plane, RLE.");

        int width = xmax - xmin + 1;
        int height = ymax - ymin + 1;
        if (width <= 0 || height <= 0 || bytesPerLine < width)
            throw new FormatException($"Bad PCX dimensions ({width}x{height}, bpl={bytesPerLine}).");

        // RLE-decode exactly bytesPerLine * height bytes, starting after the 128-byte header.
        int scanlineTotal = bytesPerLine * height;
        var raw = new byte[scanlineTotal];
        int p = 128, o = 0;
        while (o < scanlineTotal)
        {
            if (p >= d.Length)
                throw new FormatException("PCX data ended before the image was complete.");
            byte b = d[p++];
            if ((b & 0xC0) == 0xC0)
            {
                int count = b & 0x3F;
                if (p >= d.Length)
                    throw new FormatException("PCX RLE run has no value byte.");
                byte val = d[p++];
                for (int i = 0; i < count && o < scanlineTotal; i++)
                    raw[o++] = val;
            }
            else
            {
                raw[o++] = b;
            }
        }

        // Keep the first `width` bytes of each `bytesPerLine` scanline (bpl may be padded).
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            Array.Copy(raw, y * bytesPerLine, pixels, y * width, width);

        return new Pcx { Width = width, Height = height, Pixels = pixels };
    }
}
