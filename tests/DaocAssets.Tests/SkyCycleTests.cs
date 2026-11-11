// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>The sky's canopy blend and sun path through the game day (stand-in hours; see SkyCycle).</summary>
public sealed class SkyCycleTests
{
    private static uint At(float hour) => (uint)(hour * 3_600_000);

    [Theory]
    [InlineData(2f, "night", "night", 0.444f)]
    [InlineData(5.25f, "night", "dawn", 0.5f)]
    [InlineData(6f, "dawn", "day", 0f)]        // pure dawn at 6
    [InlineData(7f, "dawn", "day", 0.6667f)]    // day from 7:30 (retail's slot), not 8
    [InlineData(12f, "day", "day", 0.5f)]      // day held from 7:30 to 16:30
    [InlineData(17.25f, "day", "dusk", 0.5f)]
    [InlineData(18f, "dusk", "night", 0f)]     // pure dusk at 18
    [InlineData(23.9f, "night", "night", 0.983f)]
    public void BlendsTheCanopies(float hour, string from, string to, float t)
    {
        var b = SkyCycle.Blend(At(hour));
        Assert.Equal((from, to), (b.From, b.To));
        Assert.Equal(t, b.T, 0.01f);
    }

    [Fact]
    public void TheSunRisesEastPeaksSouthAndSetsWest()
    {
        var rise = SkyCycle.Toward(At(6));
        var noon = SkyCycle.Toward(At(12));
        var set = SkyCycle.Toward(At(18 - 0.001f));
        Assert.Equal(1f, rise.East, 0.01f);
        Assert.Equal(0f, rise.Up, 0.01f);
        Assert.Equal(0f, noon.East, 0.01f);
        Assert.Equal(MathF.Sin(MathF.PI / 3), noon.Up, 0.01f);
        Assert.True(noon.South > 0);
        Assert.Equal(-1f, set.East, 0.01f);
        Assert.True(SkyCycle.SunUp(At(12)) && !SkyCycle.SunUp(At(18)) && !SkyCycle.SunUp(At(3)));
        // The moon takes the same arc by night: high at midnight.
        Assert.Equal(noon.Up, SkyCycle.Toward(At(0)).Up, 0.01f);
    }
}
