// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Library;

namespace DaocAssets.Tests;

/// <summary>A dungeon's <c>texproxy.csv</c>: rows <c>old,new,0</c>, a texture name's stem renamed before it is looked up.</summary>
public sealed class TextureProxyTests
{
    [Fact]
    public void TheStemIsRenamedKeepingItsFolderAndExtension()
    {
        TextureProxy p = TextureProxy.Parse("dstonewall04,dstonewall03,0\r\nEyestatsheild,bstatsheild,0\r\n");
        Assert.Equal("dstonewall03.dds", p.Apply("dstonewall04.dds"));
        Assert.Equal(@"maps\dstonewall03.TGA", p.Apply(@"maps\DSTONEWALL04.TGA"));
        Assert.Equal("bstatsheild", p.Apply("eyestatsheild"));
        Assert.Equal("other.dds", p.Apply("other.dds"));
    }

    [Fact]
    public void ARenamedNameIsNotRenamedAgain()
    {
        // Zone 060: RockCaveFloor01 -> dstonerockfloor01, and dstonerockfloor01 -> wetstones.
        TextureProxy p = TextureProxy.Parse("dstonerockfloor01,wetstones,0\nRockCaveFloor01,dstonerockfloor01,0\n");
        Assert.Equal("dstonerockfloor01.dds", p.Apply("RockCaveFloor01.dds"));
        Assert.Equal("wetstones.dds", p.Apply("dstonerockfloor01.dds"));
    }

    [Fact]
    public void RowsWithoutTwoNamesAreSkipped()
    {
        TextureProxy p = TextureProxy.Parse("\nonlyone\n,missing,0\na,b,0\n");
        Assert.Equal(1, p.Count);
    }

    [LiveFact("live-mpk/dat060.mpk")]
    public void ADungeonsTableIsReadFromItsZoneArchive()
    {
        TextureProxy p = TextureProxy.FromZoneArchive(Path.Combine(AppContext.BaseDirectory, "live-mpk/dat060.mpk"));
        Assert.Equal(15, p.Count);
        Assert.Equal("dstonewall03.dds", p.Apply("dstonewall04.dds"));
    }

    [LiveFact("live-mpk/dat012.mpk")]
    public void AZoneWithoutOneRenamesNothing() =>
        Assert.Equal(0, TextureProxy.FromZoneArchive(Path.Combine(AppContext.BaseDirectory, "live-mpk/dat012.mpk")).Count);
}
