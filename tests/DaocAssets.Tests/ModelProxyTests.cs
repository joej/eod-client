// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Library;

namespace DaocAssets.Tests;

/// <summary>A dungeon's <c>nifproxy.csv</c>: rows <c>old,new,0</c> of whole file names, the new piece loaded in place of the old.</summary>
public sealed class ModelProxyTests
{
    [Fact]
    public void TheWholeFileNameIsSwappedKeepingItsFolder()
    {
        ModelProxy p = ModelProxy.Parse("STONEHall01.NIF,ALTSTONEHall01.NIF,0\r\n");
        Assert.Equal("ALTSTONEHall01.NIF", p.Apply("stonehall01.nif"));
        Assert.Equal(@"zones\Dnifs\ALTSTONEHall01.NIF", p.Apply(@"zones\Dnifs\STONEHall01.NIF"));
        Assert.Equal("STONEHall01", p.Apply("STONEHall01"));   // a stem alone isn't the row's name (inferred)
        Assert.Equal("STONEHall02.NIF", p.Apply("STONEHall02.NIF"));
    }

    [Fact]
    public void ASwappedNameIsNotSwappedAgain()
    {
        ModelProxy p = ModelProxy.Parse("a.nif,b.nif,0\nb.nif,c.nif,0\n");
        Assert.Equal("b.nif", p.Apply("a.nif"));
    }

    [LiveTheory("live-mpk/dat060.mpk", "live-mpk/dat079.mpk")]
    [InlineData("dat060", 26, "STONERoom05.NIF", "ALTSTONERoom05.NIF")]
    [InlineData("dat079", 35, "OceanusHall01.NIF", "Alt_OceanusHall01.NIF")]
    public void ADungeonsTableIsReadFromItsZoneArchive(string dat, int rows, string old, string swapped)
    {
        ModelProxy p = ModelProxy.FromZoneArchive(Path.Combine(AppContext.BaseDirectory, $"live-mpk/{dat}.mpk"));
        Assert.Equal(rows, p.Count);
        Assert.Equal(swapped, p.Apply(old));
    }

    [LiveFact("live-mpk/dat012.mpk")]
    public void AZoneWithoutOneSwapsNothing() =>
        Assert.Equal(0, ModelProxy.FromZoneArchive(Path.Combine(AppContext.BaseDirectory, "live-mpk/dat012.mpk")).Count);
}
