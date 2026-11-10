// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>The menu bar meters' levels at retail's thresholds (0 good, 1 middling, 2 bad).</summary>
public class MetersTests
{
    [Theory]
    [InlineData(60, 0)]
    [InlineData(11, 0)]
    [InlineData(10.9, 1)]
    [InlineData(5, 1)]
    [InlineData(4.9, 2)]
    public void FrameRate(double fps, int level) => Assert.Equal(level, Meters.FrameRate(fps));

    [Theory]
    [InlineData(40, 0, true, 0)]
    [InlineData(499, 0, true, 0)]
    [InlineData(500, 0, true, 1)]
    [InlineData(1499, 0, true, 1)]
    [InlineData(1500, 0, true, 2)]
    [InlineData(40, 400, true, 0)]     // a ping out under 500 ms doesn't count yet
    [InlineData(40, 800, true, 1)]     // out 800 ms without a reply: yellow
    [InlineData(40, 2000, true, 2)]    // a stalled link: red
    [InlineData(40, 0, false, 2)]      // not connected
    public void Lag(int roundTrip, int waiting, bool connected, int level) =>
        Assert.Equal(level, Meters.Lag(roundTrip, waiting, connected));

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(15, true, 1)]
    [InlineData(39, true, 1)]
    [InlineData(40, true, 2)]
    [InlineData(0, false, 2)]
    public void Performance(int lost, bool traffic, int level) =>
        Assert.Equal(level, Meters.Performance(lost, traffic));
}
