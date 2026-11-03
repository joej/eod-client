// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// The converted-zone check: each kind of fault it reports, and the clean case it must not report.
/// Ground is flat at 1000 except where a test says otherwise; the model is a 2 m cube centred on its
/// origin horizontally, from 0 to 2 m up (glTF metres, Y up). 40 region units = 1 m.
/// </summary>
public sealed class ZoneCheckTests
{
    private static readonly ModelBounds Cube = new(new Vector3(-1, 0, -1), new Vector3(1, 2, 1));

    private static ZoneCheckReport Run(Func<float, float, float> ground, params ZonePlacement[] p) =>
        ZoneCheck.Check(p, ground, glb => glb == "missing.glb" ? null : Cube, 0, 0, 8192, 8192);

    private static ZonePlacement At(float z, string glb = "cube.glb", float x = 4000, float angle = 0, float scale = 1) =>
        new(glb, x, 4000, z, angle, scale);

    [Fact]
    public void AnObjectStandingOnTheGroundIsClean() =>
        Assert.Empty(Run((_, _) => 1000, At(1000)).Findings);

    [Fact]
    public void ReportsEachKindOfFault()
    {
        ZoneCheckReport r = Run((_, _) => 1000,
            At(0),                       // the 546 Camelot Hills objects at z = 0: 1,000 units under
            At(1000 - 300, scale: 5),    // a 10 m cube, origin 7.5 m under, top above the ground: sunk
            At(1000 + 120),              // base 3 m above the ground: floating
            At(1000, glb: "missing.glb"),
            At(1000, x: 9000),           // outside the 8192-unit zone
            At(1000, scale: 200));       // a 400 m cube
        Assert.Equal(
            ["under the ground", "sunk into the ground", "floating", "missing or empty model", "outside its zone", "implausible size"],
            r.Findings.Select(f => f.Kind));
        Assert.Equal(2, r.Errors);
    }

    [Fact]
    public void GeometryBelowTheOriginIsArtNotAPlacementFault()
    {
        // A 40 m tree whose trunk reaches 10 m below its origin (DAoC trees do, to stay planted on
        // slopes), standing with its origin on the ground: clean. The same tree with its origin 5 m
        // down: sunk.
        var tree = new ModelBounds(new Vector3(-3, -10, -3), new Vector3(3, 30, 3));
        Assert.Empty(ZoneCheck.Check([At(1000)], (_, _) => 1000, _ => tree, 0, 0, 8192, 8192).Findings);
        Assert.Equal("sunk into the ground",
            Assert.Single(ZoneCheck.Check([At(800)], (_, _) => 1000, _ => tree, 0, 0, 8192, 8192).Findings).Kind);
    }

    [Fact]
    public void FloatingIsJudgedAgainstTheHighestGroundUnderTheRotatedFootprint()
    {
        // A 20 m long plank; the ground rises 200 units (5 m) from 7.5 m east of its origin. Unrotated
        // it reaches the rise, so its base (at 1000) is not above the highest ground (1200) under it;
        // turned 90 degrees it lies north-south over the low ground only, and floats if raised.
        var plank = new ModelBounds(new Vector3(-10, 0, -0.5f), new Vector3(10, 1, 0.5f));
        float Ground(float x, float y) => x > 4000 + 300 ? 1200 : 1000;
        ZoneCheckReport R(float z, float angle) => ZoneCheck.Check([At(z, angle: angle)], Ground, _ => plank, 0, 0, 8192, 8192);
        Assert.Empty(R(1000, 0).Findings);
        Assert.Empty(R(1000, 90).Findings);
        Assert.Empty(R(1100, 0).Findings);                                  // under the 1200 rise: not floating
        Assert.Equal("floating", Assert.Single(R(1100, 90).Findings).Kind); // 2.5 m over flat 1000
    }

    [Fact]
    public void ReadsTheManifestTheExporterWrites()
    {
        var p = ZoneCheck.ReadManifest("glb,x,y,z,angle,scale\nkeep.glb,574208,530432,2880,90,1\nbad\n");
        Assert.Equal(new ZonePlacement("keep.glb", 574208, 530432, 2880, 90, 1), Assert.Single(p));
    }

    [Fact]
    public void ReviewedFindingsAreReportedAsKnownAndNotCounted()
    {
        var known = ZoneCheck.ReadKnown("# comment\nRAFT1.glb | floating | raft on water\n");
        ZoneCheckReport r = ZoneCheck.ApplyKnown(
            Run((_, _) => 1000, At(1120, glb: "raft1.glb"), At(1120)), known);   // both float 3 m up
        Assert.Equal(1, r.Warnings);                 // the cube still counts
        Assert.Equal(1, r.KnownCount);               // the raft (case-insensitive name) is known
        Assert.EndsWith("; 1 known", r.Summary());
        // A known model with a different kind of fault is still reported.
        Assert.Equal(1, ZoneCheck.ApplyKnown(Run((_, _) => 1000, At(0, glb: "raft1.glb")), known).Errors);
    }
}
