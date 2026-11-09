// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's data as the client reads it: its csvNNN.mpk first, then its datNNN.mpk. Both archives carry the
// zone's CSVs and SECTOR.DAT, and where both have a file the client uses the csv archive's (daoc-re RE/62, RE/69).
// The copies differ a lot in the owner's install (2026-11-09, the 76 converted outdoor zones and their
// neighbours): fixtures.csv in 49 zones (other model numbers, the 3D-angle columns), SECTOR.DAT in 43 (water
// types, whole river tables, terrain settings), bound.csv in 35. The dat archive alone holds the heightmaps and
// a city's city.csv. An unusable csv archive (zone 9's is 0 bytes; csv212.mpk lists fixtures.csv twice, which
// our reader refuses) leaves the dat archive's files.

using DaocAssets.Mpk;

namespace DaocAssets.Zone;

public sealed class ZoneArchive : IDisposable
{
    public MpkArchive Dat { get; }
    public MpkArchive? Csv { get; }

    private ZoneArchive(MpkArchive dat, MpkArchive? csv) => (Dat, Csv) = (dat, csv);

    /// <summary>Opens a zone's datNNN.mpk and, when usable, the csvNNN.mpk beside it.</summary>
    public static ZoneArchive Open(string zoneDatMpk)
    {
        MpkArchive dat = MpkArchive.Open(zoneDatMpk);
        MpkArchive? csv = null;
        if (ZoneExport.CsvArchive(zoneDatMpk) is string path && new FileInfo(path).Length > 0)
        {
            try { csv = MpkArchive.Open(path); }
            catch (MpkFormatException) { }   // csv212.mpk: a duplicate member; the dat archive's files then
        }
        return new ZoneArchive(dat, csv);
    }

    /// <summary>Whether either archive has the member (any case).</summary>
    public bool Has(string name) => (Csv?.TryGetEntry(name, out _) ?? false) || Dat.TryGetEntry(name, out _);

    /// <summary>The member's bytes, from the csv archive when it has it, else the dat archive; null if neither.</summary>
    public byte[]? Read(string name)
    {
        if (Csv is not null && Csv.TryGetEntry(name, out MpkEntry c))
            return Csv.Extract(c);
        return Dat.TryGetEntry(name, out MpkEntry d) ? Dat.Extract(d) : null;
    }

    /// <summary>The member as text (Latin-1), or null.</summary>
    public string? Text(string name) => Read(name) is byte[] b ? System.Text.Encoding.Latin1.GetString(b) : null;

    /// <summary>
    /// Members that must come from the same archive (fixtures.csv's model numbers index nifs.csv): the csv
    /// archive's when it has them all, else the dat archive's.
    /// </summary>
    public MpkArchive SourceOf(params string[] names) =>
        Csv is not null && names.All(n => Csv.TryGetEntry(n, out _)) ? Csv : Dat;

    public void Dispose()
    {
        Csv?.Dispose();
        Dat.Dispose();
    }
}
