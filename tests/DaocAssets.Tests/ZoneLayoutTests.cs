// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// Zone layout parsing: join fixtures.csv to nifs.csv by NIF index, apply the zone's region offset
/// to each position, and carry angle/scale. Uses small in-test CSV text (the real files are the
/// same shape, verified by hand against Camelot Hills).
/// </summary>
public sealed class ZoneLayoutTests
{
    private const string Nifs =
        "Grid Nifs,,,Ambient,Merlin Data\n" +
        "NIF,Textual Name,Filename,Only,Shadow\n" +
        "407,Keep,keep.nif,0,0\n" +
        "403,Pine,bpinea.nif,1,1\n";

    private const string Fixtures =
        "Fixtures,,,,,,,\n" +
        "ID,NIF #,Textual Name,X,Y,Z,A,Scale\n" +
        "1,407,Keep,25344,47104,2880,90,100\n" +
        "2,403,Pine,1000,2000,300,0,50\n" +
        "3,999,Unknown,5,5,5,0,100\n";   // NIF index not in nifs.csv -> skipped

    [Fact]
    public void JoinsFixturesToModelsAndAppliesOffset()
    {
        // Camelot Hills offset: 67 * 8192, 59 * 8192.
        ZoneLayout layout = ZoneLayout.Parse(Nifs, Fixtures, 67 * 8192, 59 * 8192);

        Assert.Equal(2, layout.Fixtures.Count); // the unknown-NIF row is dropped

        ZoneFixture keep = layout.Fixtures[0];
        Assert.Equal("keep.nif", keep.NifName);
        Assert.Equal("Keep", keep.Label);
        Assert.Equal(25344 + 67 * 8192, keep.X);   // 574208 — matches the verified region-global value
        Assert.Equal(47104 + 59 * 8192, keep.Y);   // 530432
        Assert.Equal(2880f, keep.Z);
        Assert.Equal(90, keep.AngleDeg);
        Assert.Equal(1f, keep.Scale);              // 100% -> 1.0
    }

    [Fact]
    public void ScaleIsPerCentConvertedToRatio()
    {
        ZoneLayout layout = ZoneLayout.Parse(Nifs, Fixtures, 0, 0);
        Assert.Equal(0.5f, layout.Fixtures[1].Scale); // 50 -> 0.5
    }

    [Fact]
    public void ModelsListsDistinctReferencedFiles()
    {
        ZoneLayout layout = ZoneLayout.Parse(Nifs, Fixtures, 0, 0);
        Assert.Equal(new[] { "keep.nif", "bpinea.nif" }, layout.Models);
    }

    [Fact]
    public void SkipsHeaderAndMalformedRows()
    {
        // Only the two well-formed data rows survive; headers and the bad-index row do not.
        ZoneLayout layout = ZoneLayout.Parse(Nifs, Fixtures, 0, 0);
        Assert.All(layout.Fixtures, f => Assert.False(string.IsNullOrEmpty(f.NifName)));
        Assert.DoesNotContain(layout.Fixtures, f => f.Label == "Unknown");
    }
}
