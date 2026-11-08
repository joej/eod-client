// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// Zone edge walls (bound.csv): parsed in the file's own layout ("closed, count, x1, y1, ..."), and the
/// retail client's collision: a step into a wall slides along it (v - 1.02 (v.n) n), a corner stops, and
/// moving away is free. Crafted rows.
/// </summary>
public sealed class ZoneBoundsTests
{
    [Fact]
    public void ALineIsItsSegmentsAndAClosedOneJoinsUp()
    {
        const string csv =
            "1, 3, 0, 0, 100, 0, 100, 100\r\n" +   // closed triangle: 3 segments
            "0, 3, 0, 0, 10, 0, 10, 10\r\n" +      // open: 2
            "0, 99, 0, 0, 1, 1\r\n" +              // count out of range: skipped
            "0, 3, 0, 0, 1\r\n";                    // too few numbers: skipped
        List<BoundSegment> s = ZoneBounds.Parse(csv, 1000, 2000);
        Assert.Equal(5, s.Count);
        Assert.Equal(new BoundSegment(1000, 2000, 1100, 2000), s[0]);
        Assert.Equal(new BoundSegment(1100, 2100, 1000, 2000), s[2]);   // the closing segment, offset applied
        Assert.Equal(s, ZoneBounds.ReadCsv(ZoneBounds.ToCsv(s)));
    }

    [Fact]
    public void TheWallsComeFromTheCsvArchiveElseTheDatOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "daoc-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dat = Path.Combine(dir, "dat009.mpk"), csv = Path.Combine(dir, "CSV009.mpk");
            File.WriteAllBytes(dat, MpkBuilder.Build(("bound.csv", "0, 2, 0, 0, 5, 5"u8.ToArray())));
            File.WriteAllBytes(csv, MpkBuilder.Build(("bound.csv", "0, 2, 0, 0, 7, 7"u8.ToArray())));
            Assert.Equal([new BoundSegment(0, 0, 7, 7)], ZoneExport.ReadBounds(dat, 0, 0));
            // Zone 9 ships a 0-byte csv archive: the dat archive's walls then.
            File.WriteAllBytes(csv, []);
            Assert.Equal([new BoundSegment(0, 0, 5, 5)], ZoneExport.ReadBounds(dat, 0, 0));
        }
        finally { Directory.Delete(dir, true); }
    }

    // A wall along y = 100 from x = 0 to 1000; the player south of it (y < 100).
    private static readonly BoundSegment[] Wall = [new(0, 100, 1000, 100)];

    [Fact]
    public void AStepThatMissesTheWallsIsTaken()
    {
        Assert.Equal((510f, 60f), ZoneBounds.Step(Wall, 500, 50, 510, 60));
        Assert.Equal((1200f, 150f), ZoneBounds.Step(Wall, 1100, 50, 1200, 150));   // past the wall's end
    }

    [Fact]
    public void AStepIntoAWallSlidesAlongItAndEasesAway()
    {
        // (500, 90) → (510, 110): v = (10, 20), n = (0, -1), v·n = -20 → slide (10, 20 - 1.02·20) = (10, -0.4).
        (float x, float y) = ZoneBounds.Step(Wall, 500, 90, 510, 110);
        Assert.Equal(510f, x, 0.001f);
        Assert.Equal(89.6f, y, 0.001f);
    }

    [Fact]
    public void AStepAwayFromAWallIsFree()
    {
        // Standing on the wall's line and stepping back south.
        Assert.Equal((500f, 80f), ZoneBounds.Step(Wall, 500, 100, 500, 80));
    }

    [Fact]
    public void ACornerStops()
    {
        // The wall plus one along x = 600 (y 0-100): heading north-east into the corner from (590, 90),
        // each slide runs into the other wall, so the player stays put.
        BoundSegment[] corner = [Wall[0], new(600, 0, 600, 100)];
        Assert.Equal((590f, 90f), ZoneBounds.Step(corner, 590, 90, 610, 110));
    }

    [Fact]
    public void AHeadOnStepStops()
    {
        // Straight into the wall: the slide has no movement along it, only the 2% ease back.
        (float x, float y) = ZoneBounds.Step(Wall, 500, 95, 500, 105);
        Assert.Equal(500f, x, 0.001f);
        Assert.True(y < 100, $"y {y} went through the wall");
    }
}
