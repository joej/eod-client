// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// A zone's files as the client reads them: the csv archive's copy first, then the dat archive's; the layout's two
/// tables from one archive; an unusable csv archive ignored. Crafted archives.
/// </summary>
public sealed class ZoneArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "daoc-zonearc-" + Guid.NewGuid().ToString("N"));

    public ZoneArchiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Nifs = "Grid Nifs,,,,\r\nNIF #,Textual Name,Filename,\r\n{0},Tower,{1},\r\n";
    private const string Fixtures = "Fixtures,,\r\nID,NIF #,Textual Name,X,Y,Z,A,Scale,Collide,Radius,Animate,Ground,Flip,Cave,Unique ID\r\n" +
                                    "1,{0},Tower,100,200,300,0,100,1,0,0,0,0,0,1\r\n";

    private string Dat(params (string, string)[] members)
    {
        string path = Path.Combine(_dir, "dat001.mpk");
        File.WriteAllBytes(path, MpkBuilder.Build(members.Select(m => (m.Item1, System.Text.Encoding.Latin1.GetBytes(m.Item2))).ToArray()));
        return path;
    }

    private void Csv(params (string, string)[] members) =>
        File.WriteAllBytes(Path.Combine(_dir, "csv001.mpk"),
            MpkBuilder.Build(members.Select(m => (m.Item1, System.Text.Encoding.Latin1.GetBytes(m.Item2))).ToArray()));

    [Fact]
    public void TheCsvArchivesCopyWinsAndTheDatArchiveFillsIn()
    {
        string dat = Dat(("SECTOR.DAT", "[water]\r\ntype=SWAMP"), ("terrain.pcx", "pcx"));
        Csv(("SECTOR.DAT", "[water]\r\ntype=OCEAN"));
        using ZoneArchive zone = ZoneArchive.Open(dat);
        Assert.Equal("[water]\r\ntype=OCEAN", zone.Text("SECTOR.DAT"));   // zone 10's water type, the csv copy
        Assert.Equal("pcx", zone.Text("terrain.pcx"));                   // only in the dat archive
        Assert.Null(zone.Text("lights.csv"));
    }

    [Fact]
    public void TheLayoutTakesBothTablesFromOneArchive()
    {
        // Zone 1's Ruined Tower: model 423 in the dat copy, 421 in the csv copy, each with its own nifs.csv.
        string dat = Dat(("nifs.csv", string.Format(Nifs, 423, "old.nif")), ("fixtures.csv", string.Format(Fixtures, 423)));
        Csv(("nifs.csv", string.Format(Nifs, 421, "tower.nif")), ("fixtures.csv", string.Format(Fixtures, 421)));
        using (ZoneArchive zone = ZoneArchive.Open(dat))
            Assert.Equal("tower.nif", ZoneLayout.Read(zone, 0, 0).Fixtures.Single().NifName);

        // A csv archive with fixtures.csv but no nifs.csv: both from the dat archive, never one of each.
        Csv(("fixtures.csv", string.Format(Fixtures, 421)));
        using (ZoneArchive zone = ZoneArchive.Open(dat))
            Assert.Equal("old.nif", ZoneLayout.Read(zone, 0, 0).Fixtures.Single().NifName);
    }

    [Fact]
    public void AnEmptyCsvArchiveIsIgnored()
    {
        string dat = Dat(("SECTOR.DAT", "dat"));
        File.WriteAllBytes(Path.Combine(_dir, "csv001.mpk"), []);   // zone 9's
        using ZoneArchive zone = ZoneArchive.Open(dat);
        Assert.Null(zone.Csv);
        Assert.Equal("dat", zone.Text("SECTOR.DAT"));
    }
}
