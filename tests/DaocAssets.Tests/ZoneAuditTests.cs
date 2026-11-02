// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocAssets.Zone;
using Xunit;

namespace DaocAssets.Tests;

/// <summary>The world-data inventory behind `mpk worldaudit`.</summary>
public class ZoneAuditTests
{
    [Theory]
    [InlineData("river00", "river#")]
    [InlineData("tex03-00.BMP", "tex#-#.bmp")]
    [InlineData("SECTOR.DAT", "sector.dat")]
    public void PatternsFoldDigitsAndCase(string name, string pattern) =>
        Assert.Equal(pattern, ZoneAudit.Pattern(name));

    [Fact]
    public void KindComesFromTheDataArchive()
    {
        Assert.Equal("outdoor (heightmap)", ZoneAudit.Kind(["SECTOR.DAT", "terrain.pcx", "nifs.csv"]));
        Assert.Equal("dungeon (chunks)", ZoneAudit.Kind(["sector.dat", "dungeon.chunk", "dungeon.place"]));
        Assert.Equal("city (skycity chunks)", ZoneAudit.Kind(["skycity.chunk", "dungeon.chunk"]));
    }

    [Fact]
    public void ReportsSectorKeysCsvHeadersAndWhatTheConverterUses()
    {
        string sector = "; comment\n[terrain]\nscalefactor=8\nhorizon=horizon.tga\n[river00]\ntype=RIVER\nheight=1916\n[river01]\ntype=LAKE\n";
        string csv = "Fixtures,,\nID,NIF #,X\n1,401,100\n2,401,200\n";
        var files = new Dictionary<string, byte[]>
        {
            ["SECTOR.DAT"] = Encoding.ASCII.GetBytes(sector),
            ["fixtures.csv"] = Encoding.ASCII.GetBytes(csv),
            ["lights.csv"] = Encoding.ASCII.GetBytes("1,2,3,4\n"),
        };
        var archives = new Dictionary<string, IReadOnlyList<string>> { ["dat000.mpk"] = files.Keys.ToList() };
        var audit = new ZoneAudit();
        audit.AddZone("zone000", archives, (_, m) => files[m], ["sounds.dat"]);
        string md = audit.ToMarkdown("t");

        Assert.Equal(1, audit.Zones);
        Assert.Contains("| terrain.scalefactor | yes | 1 | `8` |", md);
        Assert.Contains("| terrain.horizon | no | 1 | `horizon.tga` |", md);
        Assert.Contains("| river#.type | no | 1 | `RIVER` · `LAKE` |", md);   // river00 and river01 grouped
        Assert.Contains("| fixtures.csv | yes | 1 | dat#.mpk |", md);
        Assert.Contains("2 rows; `Fixtures,, / ID,NIF #,X` ×1", md);
        Assert.Contains("| lights.csv | no | 1 |", md);
        Assert.Contains("- sounds.dat: 1 zone(s)", md);
    }
}
