// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>Con colours against CapnBry's table (as OpenDAoC-Core's CharacterConLevelGrid has it).</summary>
public class ConColorTests
{
    [Theory]
    // A level 1 player: 0 blue, 1 yellow, 2 orange, 3 red, 4 and up purple.
    [InlineData(1, 0, ConColor.Blue)]
    [InlineData(1, 1, ConColor.Yellow)]
    [InlineData(1, 2, ConColor.Orange)]
    [InlineData(1, 3, ConColor.Red)]
    [InlineData(1, 4, ConColor.Purple)]
    // Level 10: grey to 6, green 7, blue 8-9, yellow 10, orange 11, red 12-13, purple 14.
    [InlineData(10, 6, ConColor.Grey)]
    [InlineData(10, 7, ConColor.Green)]
    [InlineData(10, 9, ConColor.Blue)]
    [InlineData(10, 10, ConColor.Yellow)]
    [InlineData(10, 11, ConColor.Orange)]
    [InlineData(10, 13, ConColor.Red)]
    [InlineData(10, 14, ConColor.Purple)]
    // Level 50: grey to 35, green 36-40, blue 41-45, yellow 46-50, orange 51-55, red 56-60, purple 61.
    [InlineData(50, 35, ConColor.Grey)]
    [InlineData(50, 36, ConColor.Green)]
    [InlineData(50, 50, ConColor.Yellow)]
    [InlineData(50, 60, ConColor.Red)]
    [InlineData(50, 61, ConColor.Purple)]
    // Above 50 the grey span grows by one per level (OpenDAoC ConLevelUtility.BuildGrid).
    [InlineData(51, 36, ConColor.Grey)]
    [InlineData(51, 62, ConColor.Purple)]
    public void ACreatureConsByItsLevelAgainstYours(int player, int creature, ConColor expected) =>
        Assert.Equal(expected, ConColors.Of(player, creature));
}
