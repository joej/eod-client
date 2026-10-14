// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zone lookup as OpenDAoC-Core 2026.09.36 (f0aaa1e) does it: WorldMgr.RegisterZone scales the
// database offsets and sizes by 8192, Zone.IsPointInZone tests the half-open rectangle, and
// Region.GetZone returns the first match. Data: zones.tsv (see its header for provenance).

using System.Globalization;

namespace DaocProtocol.World;

/// <param name="X">Region coordinate of the zone's west edge.</param>
/// <param name="Y">Region coordinate of the zone's north edge.</param>
public sealed record Zone(ushort Id, ushort RegionId, int X, int Y, int Width, int Height, string Name)
{
    public const int Unit = 8192;

    public bool Contains(float x, float y) =>
        X <= (int)x && Y <= (int)y && X + Width > (int)x && Y + Height > (int)y;
}

/// <summary>
/// Which zone a region-global position is in. The 1.127 client reports its zone in every position
/// update (0xA9); the real client gets it from its map data, we get it from this table.
/// </summary>
public static class Zones
{
    private static readonly Lazy<IReadOnlyList<Zone>> Table = new(Load);

    public static IReadOnlyList<Zone> All => Table.Value;

    /// <summary>The zone containing (x, y) in <paramref name="regionId"/>, or null if none does.</summary>
    public static Zone? Find(ushort regionId, float x, float y) =>
        Table.Value.FirstOrDefault(z => z.RegionId == regionId && z.Contains(x, y));

    private static IReadOnlyList<Zone> Load()
    {
        using Stream stream = typeof(Zones).Assembly.GetManifestResourceStream("DaocProtocol.World.zones.tsv")
            ?? throw new InvalidOperationException("zones.tsv is not embedded.");
        using var reader = new StreamReader(stream);
        var zones = new List<Zone>();
        while (reader.ReadLine() is string line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] f = line.Split('\t');
            int Int(int i) => int.Parse(f[i], CultureInfo.InvariantCulture);
            zones.Add(new Zone((ushort)Int(0), (ushort)Int(1), Int(2) * Zone.Unit, Int(3) * Zone.Unit,
                Int(4) * Zone.Unit, Int(5) * Zone.Unit, f[6]));
        }

        return zones;
    }
}
