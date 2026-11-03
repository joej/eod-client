// SPDX-License-Identifier: GPL-3.0-or-later
//
// DAoC headings as OpenDAoC-Core 2026.09.36 (f0aaa1e) computes them, GameServer/world/Point2D.cs:
// GetHeading(x, y) = atan2(-dx, dy) scaled to 0..4095, and GetPointFromHeading moves by
// (-sin a, +cos a). So heading 0 faces +Y (south: region Y grows southward), 1024 faces -X (west),
// 2048 north, 3072 east.

namespace DaocProtocol.World;

public static class Heading
{
    /// <summary>The unit direction (dx, dy) in region coordinates that <paramref name="heading"/> faces.</summary>
    public static (double Dx, double Dy) ToDirection(ushort heading)
    {
        double a = (heading & 0x0FFF) * (2 * Math.PI / 4096);
        return (-Math.Sin(a), Math.Cos(a));
    }

    /// <summary>The heading (0..4095) facing along (dx, dy) in region coordinates.</summary>
    public static ushort FromDirection(double dx, double dy)
    {
        double h = Math.Atan2(-dx, dy) * (4096 / (2 * Math.PI));
        return (ushort)((int)Math.Round(h < 0 ? h + 4096 : h) & 0x0FFF);
    }
}
