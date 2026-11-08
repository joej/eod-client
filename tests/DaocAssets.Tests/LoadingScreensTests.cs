// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>loading.dat (gamedata.mpk): each region's name and loading pictures, shaped like the install's file.</summary>
public sealed class LoadingScreensTests
{
    private const string Dat = """
        [region001]
        name=Albion
        screen01=archive://data/loading/alb1.mpk:alb0
        screen02=archive://data/loading/alb1.mpk:alb1
        screens=2

        [region010]
        name=Camelot City
        screen01=archive://data/loading/camcity.mpk:camelot1
        screen02=not a picture
        screen03=archive://data/loading/camcity.mpk:camelot3
        screens=3
        instance=1
        widestyle=1

        [region099]
        name=Nowhere

        [settings]
        screens=1
        """;

    [Fact]
    public void EachRegionGivesItsNameAndPictures()
    {
        IReadOnlyList<LoadingRegion> regions = LoadingScreens.Parse(Dat);
        Assert.Equal([1, 10, 99], regions.Select(r => r.Region));
        LoadingRegion city = regions[1];
        Assert.Equal("Camelot City", city.Name);
        Assert.True(city.Instance);
        Assert.True(city.WideStyle);
        Assert.Equal([new LoadingPicture("data/loading/camcity.mpk", "camelot1"), new LoadingPicture("data/loading/camcity.mpk", "camelot3")],
            city.Pictures);
        Assert.Equal("camcity_camelot1", city.Pictures[0].Stem);
        Assert.Empty(regions[2].Pictures);
        Assert.False(regions[0].Instance);
    }

    [Fact]
    public void TheLibraryTableListsOnlyPicturesThatWereFound()
    {
        IReadOnlyList<LoadingRegion> regions = LoadingScreens.Parse(Dat);
        string csv = LoadingScreens.ToCsv(regions, new HashSet<string> { "alb1_alb0", "alb1_alb1", "camcity_camelot3" });
        var table = LoadingScreens.ReadCsv(csv);
        Assert.Equal(["alb1_alb0", "alb1_alb1"], table[1].Stems);
        Assert.Equal(["camcity_camelot3"], table[10].Stems);
        Assert.Equal("Camelot City", table[10].Name);
        Assert.True(table[10].Instance);
        Assert.Empty(table[99].Stems);
    }

    [Theory]
    [InlineData("archive://data/loading/alb1.mpk:alb0", "data/loading/alb1.mpk", "alb0")]
    [InlineData("archive://phousing\\data\\loading\\housing.mpk:ahouse01", "phousing/data/loading/housing.mpk", "ahouse01")]
    public void APictureReferenceNamesItsArchiveAndMember(string reference, string archive, string member) =>
        Assert.Equal(new LoadingPicture(archive, member), LoadingScreens.ParsePicture(reference));

    [Theory]
    [InlineData("data/loading/alb1.mpk:alb0")]
    [InlineData("archive://alb1.mpk:")]
    [InlineData("archive://alb1.mpk")]
    public void AnythingElseIsNoPicture(string reference) => Assert.Null(LoadingScreens.ParsePicture(reference));
}
