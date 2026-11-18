// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Keeps;

namespace DaocAssets.Tests;

/// <summary>The keep piece tables: a component type's model at each build tier.</summary>
public sealed class KeepPiecesTests
{
    // The install's layout, cut down: two title lines, fr_tiers' TIER columns counting fr_pieces' rows from 0.
    private const string Pieces = """
        117,,,effects
        ID,Description,File,,
        1,1-15degree-left,1-15degree-left,476
        2,1gate,1gate,
        3,2gate,2gate,
        4,outpost_lvl1,outpost_lvl1,
        5,outpost_lvl2,outpost_lvl2,
        6,5-outpost_tower,5-outpost_tower,
        """;

    private const string Tiers = """
        35,,,,,,
        ;#, info, TIER 1, TIER 2, TIER 3, TIER 4,TIER 5
        0,Gate,1,2,,,
        15,,0,0,0,0
        31,outpost,3,4,,,5
        """;

    [Fact]
    public void ATiersNumbersCountThePiecesFromZero()
    {
        Dictionary<int, KeepPieceType> types = KeepPieces.Parse(Tiers, Pieces);
        Assert.Equal(["1gate", "2gate"], types[0].Tiers);
        Assert.Equal(["outpost_lvl1", "outpost_lvl2", null, null, "5-outpost_tower"], types[31].Tiers);
        Assert.Empty(types[15].Tiers);   // the unnamed row is no type
    }
}

/// <summary>Where a keep component stands and what of it shows (provisional rules: KeepLayout).</summary>
public sealed class KeepLayoutTests
{
    [Fact]
    public void AComponentStandsItsOffsetFromTheKeepTurnedByTheKeepsHeading()
    {
        // Heading 0: x east, y north (the server subtracts y).
        (double x, double y, double deg) = KeepLayout.Place(1000, 1000, 0, 2, 1, 1);
        Assert.Equal((1296, 852, 90), (Math.Round(x), Math.Round(y), deg));
        // Heading 90: the same offsets turned a quarter.
        (x, y, deg) = KeepLayout.Place(1000, 1000, 90, 2, 1, 3);
        Assert.Equal((1148, 1296, 0), (Math.Round(x), Math.Round(y), deg));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(3, 3)] [InlineData(4, 4)] [InlineData(5, 4)]
    public void TheTierIsTheBuildHeight(int height, int tier) =>
        Assert.Equal(tier, KeepLayout.Tier(["a", "b", "c", "d"], height));

    [Fact]
    public void AMissingTierFallsToTheOneBelow()
    {
        Assert.Equal(1, KeepLayout.Tier(["a", null, null, null, "e"], 3));
        Assert.Equal(5, KeepLayout.Tier(["a", null, null, null, "e"], 5));
        Assert.Null(KeepLayout.Tier([], 3));
    }

    [Theory]
    [InlineData(null, 3, 3, false, true)]
    [InlineData("!visible_static", 3, 3, true, true)]
    [InlineData("!visible_undamaged", 3, 3, false, true)]
    [InlineData("!visible_undamaged", 3, 3, true, false)]
    [InlineData("!visible_damaged", 3, 3, false, false)]
    [InlineData("!visible_damaged", 3, 3, true, true)]
    [InlineData("!sclimb_visible_Tier3_hib;!sclimb_visible_Tier3_hib:0", 3, 3, false, true)]
    [InlineData("!sclimb_visible_Tier3_hib;!sclimb_visible_Tier3_hib:0", 1, 3, false, false)]
    [InlineData("!sclimb_visible_Tier2_hib", 3, 3, false, false)]
    public void ABranchShowsForItsStateTierAndRealm(string? branches, int realm, int tier, bool broken, bool shows) =>
        Assert.Equal(shows, KeepLayout.Shows(branches, realm, tier, broken));

    [Fact]
    public void ThePieceTableReadsEachTier()
    {
        Dictionary<int, string?[]> p = KeepLayout.ReadPieces("skin,name,tier,glb\n31,outpost,1,keeps/outpost_lvl1\n31,outpost,5,5-outpost_tower\n");
        Assert.Equal(new string?[] { "keeps/outpost_lvl1", null, null, null, "5-outpost_tower" }, p[31]);
    }
}
