// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>
/// Held items and helms from objects.csv (column 2 NIF, 17 On Belt, 18 Shield, 19 Combat Stance, 20 Left
/// Hand, 22 Apply Texture, 79-90 Cata helm NIFs, 91 Cata Helm Texture), items.csv (NIF names, column 23
/// Cata Helm Hair) and pskins.csv (texture names); rows shaped like the install's, cut down.
/// </summary>
public sealed class CataGearTests
{
    private static string Row(int id, string name, (int Col, string Value)[] cells)
    {
        var r = Enumerable.Repeat("", 98).ToArray();
        (r[0], r[1]) = (id.ToString(), name);
        foreach ((int col, string value) in cells) r[col] = value;
        return string.Join(',', r);
    }

    private static Dictionary<int, GearItem> Gear() => CataGear.Parse(
        "Information,,NIF\nID,name,#\n" + string.Join('\n',
            Row(3, "Briton short sword", [(2, "167"), (17, "1"), (19, "1")]),
            Row(59, "Round wooden shield", [(2, "28"), (18, "1"), (22, "760")]),
            Row(327, "Norse staff", [(2, "155"), (19, "4"), (20, "1")]),
            Row(824, "Albion Studded Helm", [(2, "403"), (22, "315"), (79, "1242"), (80, "1246"), (81, "1222")]),
            Row(57, "Cloak", [(11, "362")])),
        "2500 Limit,,NIF\nID,Textual Name,Name\n167,s,b_sl_shortsword01\n28,s,sh_rounda\n155,s,n_2h_staff01\n403,h,helm1\n" +
        "1242,h,albHelm01_Bri_m,0,4,,,,,,,,,,,,,,,,,,,2\n1246,h,albHelm01_brit_f,0,4,,,,,,,,,,,,,,,,,,,2\n1222,h,albHelm01_sar_m,0,4,,,,,,,,,,,,,,,,,,,2",
        "Information,,Skin\nID,Textual Name,Name\n760,shield wood,sh_wood01.tga\n315,Padded Leather,b_lea2_helm1.tga");

    [Fact]
    public void HeldItemsCarryTheirNifAndHowTheyAreCarried()
    {
        Dictionary<int, GearItem> gear = Gear();
        GearItem sword = gear[3];
        Assert.Equal(("b_sl_shortsword01.nif", true, false, 1, false), (sword.Nif, sword.Belt, sword.Shield, sword.Stance, sword.LeftHand));
        Assert.Empty(sword.Helms);
        Assert.True(gear[59].Shield);
        Assert.Equal("sh_wood01.tga", gear[59].Texture);           // over the emblem pattern the NIF names
        Assert.False(gear[327].Belt);                              // a staff stays in the hand
        Assert.DoesNotContain(57, gear.Keys);                      // a cloak is worn, not held
    }

    [Fact]
    public void AHelmNamesItsModelPerRaceAndSex()
    {
        GearItem helm = Gear()[824];
        Assert.Null(helm.Nif);                                     // no stance, shield or belt flag: not held
        Assert.Null(helm.Texture);
        Assert.Equal(2, helm.HelmHair);
        Assert.Equal(new Dictionary<string, string>
            { ["bri_m"] = "albHelm01_Bri_m.nif", ["bri_f"] = "albHelm01_brit_f.nif", ["sar_m"] = "albHelm01_sar_m.nif" }, helm.Helms);
    }

    [Theory]
    [InlineData("albHelm01_Bri_m.nif", "bri_m")]
    [InlineData("albHelm01_brit_f.nif", "bri_f")]
    [InlineData("midHelm01_kob_F.nif", "kob_f")]
    [InlineData("toaHelm_Winged_lur_m.nif", "lur_m")]
    [InlineData("helm1.nif", null)]
    public void AHelmFileNamesItsRaceAndSex(string nif, string? key) => Assert.Equal(key, CataGear.RaceKey(nif));
}
