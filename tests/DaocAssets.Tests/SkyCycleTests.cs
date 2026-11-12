// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>The sky's canopy blend and sun path through the game day (retail's hours and angles, daoc-re RE/79; see SkyCycle).</summary>
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
    public void TheSunDiskRisesEastStandsNinetyLessTheEclipticAtNoonAndSetsWest()
    {
        var rise = SkyCycle.Disk(At(6), 15);
        var noon = SkyCycle.Disk(At(12), 15);
        var set = SkyCycle.Disk(At(18), 15);
        Assert.True(rise.East > 0.95f && MathF.Abs(rise.Up) < 0.01f);
        Assert.True(set.East < -0.95f && MathF.Abs(set.Up) < 0.01f);
        Assert.Equal(0f, noon.East, 0.01f);
        Assert.Equal(75f, MathF.Asin(noon.Up) * 180 / MathF.PI, 0.1f);   // 90 - ecliptic
        Assert.True(noon.South > 0);
        Assert.True(SkyCycle.Disk(At(0), 15).Up < -0.9f);   // under the ground at midnight
        Assert.True(SkyCycle.SunUp(At(12)) && !SkyCycle.SunUp(At(18)) && !SkyCycle.SunUp(At(3)));
    }

    [Theory]
    [InlineData(6, 45)]
    [InlineData(9, 67.5)]
    [InlineData(12, 90)]
    [InlineData(15, 112.5)]
    [InlineData(17.99, 134.9)]
    [InlineData(18, 135)]
    [InlineData(23.9, 135)]
    [InlineData(0, 45)]
    [InlineData(5.9, 45)]
    public void TheSunlightNeverDropsBelowFortyFiveDegreesAndHoldsAtNight(float hour, float angle)
    {
        Assert.Equal(angle, SkyCycle.LightAngle(At(hour)), 0.1f);
        var light = SkyCycle.Light(At(hour), 0);
        Assert.Equal(MathF.Sin(angle * MathF.PI / 180), light.Up, 0.01f);
        Assert.True(light.Up >= MathF.Sin(MathF.PI / 4) - 0.01f);
    }
}
