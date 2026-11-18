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
        // 150 units a step (retail). Heading 0: x east, y north.
        (double x, double y, double deg) = KeepLayout.Place(1000, 1000, 0, 2, 1, 1);
        Assert.Equal((1300, 850, 90), (Math.Round(x), Math.Round(y), deg));
        // Heading 90: the same offsets turned a quarter.
        (x, y, deg) = KeepLayout.Place(1000, 1000, 90, 2, 1, 3);
        Assert.Equal((1150, 1300, 0), (Math.Round(x), Math.Round(y), deg));
    }

    [Theory]
    [InlineData(0, 1, false, 1)]
    [InlineData(3, 1, false, 4)]
    [InlineData(4, 1, false, 5)]
    [InlineData(9, 1, false, 5)]        // clamped to 4
    [InlineData(1, 0x162, true, 5)]     // a raised tower (keep id with a high byte): height 4
    [InlineData(1, 0x62, true, 2)]      // the flag on a keep is no tower's
    public void TheTierColumnIsTheHeightPlusOne(int height, int keepId, bool raised, int column) =>
        Assert.Equal(column, KeepLayout.TierColumn(height, keepId, raised));

    [Fact]
    public void AMissingTierFallsToTheOneBelow()
    {
        Assert.Equal(4, KeepLayout.Tier(["a", "b", "c", "d"], 5));
        Assert.Equal(1, KeepLayout.Tier(["a", null, null, null, "e"], 3));
        Assert.Equal(5, KeepLayout.Tier(["a", null, null, null, "e"], 5));
        Assert.Null(KeepLayout.Tier([], 3));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(2, 0)] [InlineData(3, 1)] [InlineData(5, 1)] [InlineData(6, 2)] [InlineData(8, 2)]
    [InlineData(9, 3)] [InlineData(10, 3)]
    public void TheLevelTierGoesByThrees(int level, int tier) => Assert.Equal(tier, KeepLayout.LevelTier(level));

    [Theory]
    [InlineData(null, 3, 2, false, false, true)]
    [InlineData("!visible_static", 3, 2, true, false, true)]
    [InlineData("!visible_undamaged", 3, 2, false, false, true)]
    [InlineData("!visible_undamaged", 3, 2, true, false, false)]
    [InlineData("!visible_damaged", 3, 2, false, false, false)]
    [InlineData("!visible_damaged", 3, 2, true, false, true)]
    [InlineData("!sclimb_visible_Tier3_hib;!sclimb_visible_Tier3_hib:0", 3, 2, false, true, true)]
    [InlineData("!sclimb_visible_Tier3_hib;!sclimb_visible_Tier3_hib:0", 3, 2, false, false, false)]   // not climbable
    [InlineData("!sclimb_visible_Tier3_hib", 1, 2, false, true, false)]   // another realm
    [InlineData("!sclimb_visible_Tier2_hib", 3, 2, false, true, false)]   // another level tier
    [InlineData("!hib_cake_undamaged", 3, 2, false, false, true)]
    [InlineData("!alb_cake_undamaged", 3, 2, false, false, false)]       // another realm's topping
    [InlineData("!alb_cake_damaged", 1, 2, true, false, true)]
    public void ABranchShowsForItsStateLevelAndRealm(string? branches, int realm, int levelTier, bool broken, bool climbable, bool shows) =>
        Assert.Equal(shows, KeepLayout.Shows(branches, realm, levelTier, broken, climbable));

    [Fact]
    public void ThePieceTableReadsEachTier()
    {
        Dictionary<int, string?[]> p = KeepLayout.ReadPieces("skin,name,tier,glb\n31,outpost,1,keeps/outpost_lvl1\n31,outpost,5,5-outpost_tower\n");
        Assert.Equal(new string?[] { "keeps/outpost_lvl1", null, null, null, "5-outpost_tower" }, p[31]);
    }
}

/// <summary>Keep textures: fr_materials by realm and group, in either table width.</summary>
public sealed class KeepMaterialsTests
{
    private const string Textures = """
        141,,,,,,,
        ,Desc,Filename,Damage,Clamp,,,
        1,Hib Wall,hib_wall,,
        2,Hib Wall 4,hib_wall_lvl4,,
        3,Alb Wall,alb_wall,,
        4,Dark,wall_dark,,
        5,75-51% hps,damage_75,4,0
        """;

    [Fact]
    public void AThreeWideTableGivesEachRealmAndTierItsTextures()
    {
        // id, name, Alb/Mid/Hib plain (3 each), Alb T1-4, Mid T1-4, Hib T1-4 (3 each), Damage, Emmitence: 49 columns.
        string[] row = new string[49];
        System.Array.Fill(row, "");
        (row[0], row[1]) = ("1", "!nf_main_wall");
        (row[2], row[3]) = ("3", "4");     // Albion plain: base 3, dark 4
        row[8] = "1";                      // Hibernia plain
        row[2 + 9 + (2 * 4 + 3) * 3] = "2";   // Hibernia Tier 4
        (row[47], row[48]) = ("1", "128");
        string header = "109,Search" + string.Concat(Enumerable.Repeat(",x", 47));
        string names = ",String" + string.Concat(Enumerable.Repeat(",Base", 47));
        KeepMaterials m = KeepMaterials.Parse($"{header}\n{names}\n{string.Join(',', row)}\n", Textures);

        Assert.Equal(3, m.GroupWidth);
        KeepMaterial wall = m.Materials["!NF_MAIN_WALL"];
        Assert.Equal(new KeepTextureSet("alb_wall", "wall_dark", null, null), wall.Sets[0, 0]);
        Assert.Equal("hib_wall", wall.Sets[2, 0]?.Base);
        Assert.Equal("hib_wall_lvl4", wall.Sets[2, 4]?.Base);
        Assert.Null(wall.Sets[1, 0]);
        Assert.Equal((true, 128), (wall.Damage, wall.Emittance));
        Assert.Equal("damage_75", m.DamageOverlays[4]);

        var table = KeepMaterials.ReadCsv(m.ToCsv());
        Assert.Equal("hib_wall_lvl4", table[("!nf_main_wall", 3, 4)].Set.Base);
    }

    [Theory]
    [InlineData("!nf_main_wall", 0, 4, 0)]
    [InlineData("!nf_main_wall", 3, 1, 3)]
    [InlineData("!nf_door", 0, 4, 0)]      // "!nf_door" doesn't contain "!door": by the level tier
    [InlineData("!door_postern", 0, 4, 4)] // a door by the piece's tier
    [InlineData("!door", 3, 1, 1)]
    [InlineData("!door", 3, 5, 4)]         // TIER 5 clamped to Tier 4 (ours)
    public void AGroupIsTheLevelTierOrForADoorThePiecesTier(string name, int levelTier, int column, int group) =>
        Assert.Equal(group, KeepMaterials.Group(name, levelTier, column));
}
