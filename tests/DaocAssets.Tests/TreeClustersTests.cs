// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// Tree clusters (zones/trees/tree_clusters.csv): a placed "Elm1CL5.nif" is no file but five elm1 trees at
/// the table's offsets, turned and scaled with the placement as one model would be. Crafted rows in the
/// file's own layout.
/// </summary>
public sealed class TreeClustersTests
{
    private const string Csv =
        "name,tree,x,y,z,x,y,z,x,y,z\n" +
        "Elm1CL2.nif,elm1.NIF,100.000000,0.000000,0.000000,0.000000,200.000000,0.000000,0,0,0\n" +
        // The shipped file holds its table twice; the first row of a name wins.
        "Elm1CL2.nif,other.NIF,5,5,0,0,0,0,0,0,0\n";

    private static ZoneFixture At(string nif, int angle, float scale = 1, float z = 500) =>
        new(nif, "tree", 10000, 20000, z, angle, scale, OnGround: true);

    [Fact]
    public void AClusterBecomesItsTreesAtTheOffsets()
    {
        TreeClusters clusters = TreeClusters.Parse(Csv);
        Assert.Equal(1, clusters.Count);
        Assert.True(clusters.IsCluster("zones/elm1cl2.nif"));

        ZoneLayout layout = clusters.Expand(new ZoneLayout { Fixtures = [At("Elm1CL2.nif", 0), At("keep.nif", 0)] });

        // Two trees (the third slot is unused), then the keep unchanged.
        Assert.Equal(["elm1.NIF", "elm1.NIF", "keep.nif"], layout.Fixtures.Select(f => f.NifName));
        // At angle 0 an offset is in the model's axes: X as the world's, Y mirrored (NIF Y is world -Y).
        Assert.Equal((10100f, 20000f), (layout.Fixtures[0].X, layout.Fixtures[0].Y));
        Assert.Equal((10000f, 19800f), (layout.Fixtures[1].X, layout.Fixtures[1].Y));
        Assert.All(layout.Fixtures.Take(2), f => Assert.True(f.OnGround));
        Assert.Equal(["elm1.NIF", "keep.nif"], layout.Models);
    }

    [Fact]
    public void AClusterTurnsAndScalesAsOneModel()
    {
        ZoneLayout layout = TreeClusters.Parse(Csv).Expand(new ZoneLayout { Fixtures = [At("Elm1CL2.nif", 90, scale: 2)] });

        // (100, 0) at 90° and scale 2: dx = 2·(100·cos 90 + 0) = 0, dy = 2·(100·sin 90 − 0) = 200.
        Assert.Equal(10000f, layout.Fixtures[0].X, 0.01f);
        Assert.Equal(20200f, layout.Fixtures[0].Y, 0.01f);
        // (0, 200): dx = 2·(0 + 200·sin 90) = 400, dy = 2·(0 − 200·cos 90) = 0.
        Assert.Equal(10400f, layout.Fixtures[1].X, 0.01f);
        Assert.Equal(20000f, layout.Fixtures[1].Y, 0.01f);
        Assert.All(layout.Fixtures, f => Assert.Equal(2f, f.Scale));
    }

    [Fact]
    public void EachTreeOfAClusterStandsOnTheGround()
    {
        // A cluster placed without "On Ground" still grounds its trees, each where it stands.
        var cluster = At("Elm1CL2.nif", 0) with { OnGround = false };
        ZoneLayout layout = TreeClusters.Parse(Csv).Expand(new ZoneLayout { Fixtures = [cluster] });
        Assert.All(layout.Fixtures, f => Assert.True(f.OnGround));
    }

    [Fact]
    public void WithoutTheTableNothingChanges()
    {
        var layout = new ZoneLayout { Fixtures = [At("Elm1CL2.nif", 0)] };
        Assert.Same(layout, TreeClusters.None.Expand(layout));
    }
}
