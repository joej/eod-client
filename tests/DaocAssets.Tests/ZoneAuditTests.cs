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
        Assert.Contains("| river#.type | yes | 1 | `RIVER` · `LAKE` |", md);   // river00 and river01 grouped
        Assert.Contains("| fixtures.csv | yes | 1 | dat#.mpk |", md);
        Assert.Contains("2 rows; `Fixtures,, / ID,NIF #,X` ×1", md);
        Assert.Contains("| lights.csv | no | 1 |", md);
        Assert.Contains("- sounds.dat: 1 zone(s)", md);
    }

    [Fact]
    public void ListsIgnoredFieldsAndDegenerateValuesInFilesWeRead()
    {
        // What the review list exists for: fields of files we read that nobody reads (fixtures.csv's On
        // Ground column, unread until 0478b56; [terrain] flip_y), and degenerate values in fields we do
        // read (a [sectorsize] sizex=0 read literally made a zone 0 units wide).
        string sector = "[sectorsize]\nsizex=0\nsizey=8\n[terrain]\nscalefactor=0\nflip_y=1\n";
        string csv = "Fixtures,,,,,,,,,,,On.\n" +
                     "ID,NIF #,Textual Name,X,Y,Z,A,Scale,Collide,Radius,Animate,Ground,Flip\n" +
                     "1,407,Keep,25344,47104,2880,0,100,5120,0,0,1,0\n" +
                     "2,412,Elm1,14848,13568,0,291,90,0,8,0,1,0\n";
        var files = new Dictionary<string, byte[]>
        {
            ["SECTOR.DAT"] = Encoding.ASCII.GetBytes(sector),
            ["fixtures.csv"] = Encoding.ASCII.GetBytes(csv),
        };
        var audit = new ZoneAudit();
        audit.AddZone("zone000", new Dictionary<string, IReadOnlyList<string>> { ["dat000.mpk"] = files.Keys.ToList() },
            (_, m) => files[m], []);
        string md = audit.ToMarkdown("t");

        Assert.Contains("| 11 | Ground | reads: On Ground | `1` ×2 |", md);
        Assert.Contains("| 12 | Flip | **ignored** | `0` ×2 |", md);
        Assert.Contains("| 5 | Z | reads: z (unless On Ground) **check:** `0` ×1 |", md);
        Assert.Contains("| terrain.flip_y | 1 | **ignored** | `1` ×1 |", md);
        // A degenerate value in a field we read is called out ([terrain] scalefactor=0 here).
        Assert.Contains("| terrain.scalefactor | 1 | reads **check:** `0` ×1 | `0` ×1 |", md);
    }
}
