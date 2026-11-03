// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>Headings against OpenDAoC's Point2D: heading 0 faces +Y (south), 1024 faces -X (west).</summary>
public sealed class HeadingTests
{
    [Theory]
    [InlineData(0, 0, 1)]        // south
    [InlineData(1024, -1, 0)]    // west
    [InlineData(2048, 0, -1)]    // north
    [InlineData(3072, 1, 0)]     // east
    public void AHeadingFacesTheServersDirection(ushort heading, double dx, double dy)
    {
        (double x, double y) = Heading.ToDirection(heading);
        Assert.Equal((dx, dy), (Math.Round(x, 6) + 0.0, Math.Round(y, 6) + 0.0));
        Assert.Equal(heading, Heading.FromDirection(dx, dy));
    }

    [Fact]
    public void DirectionAndHeadingRoundTrip()
    {
        for (ushort h = 0; h < 4096; h += 7)
        {
            (double dx, double dy) = Heading.ToDirection(h);
            Assert.Equal(h, Heading.FromDirection(dx * 250, dy * 250));
        }
    }
}
