// SPDX-License-Identifier: GPL-3.0-or-later
//
// Tree clusters (zones/trees/tree_clusters.mpk, tree_clusters.csv): a zone's nifs.csv can name a model that
// is no file at all, e.g. "Elm1CL5.nif", five elm1 trees. The table gives each such name its tree and up to
// ten offsets: "name,tree,x,y,z,x,y,z,…" (unused slots 0,0,0). The client builds the group from the tree;
// the converter places one tree per offset instead. Used by the tutorial zones (27: 51 placements) and
// zone 335. The file holds the table twice; the first row of a name wins.
//
// Where each tree goes: retail's own rule for a cluster's points (daoc-re RE/66, from the code that stamps the trees'
// shadows into shadow.pcx; ZoneShadow): with θ = 180° − the fixture's angle and s its scale,
//     x' = X + s·(cos θ·x + sin θ·y),   y' = Y + s·(cos θ·y − sin θ·x)        (z ignored)
// That code turns the offsets; that the trees themselves are placed the same way is assumed, so their shadows land
// under them (open question W53). Each tree stands on the ground at its own position (also unconfirmed).

using System.Globalization;

namespace DaocAssets.Zone;

/// <summary>Grouped trees: a cluster name → its tree and the trees' offsets.</summary>
public sealed class TreeClusters
{
    private readonly Dictionary<string, (string Tree, (float X, float Y, float Z)[] Offsets)> _byName;

    private TreeClusters(Dictionary<string, (string, (float, float, float)[])> byName) => _byName = byName;

    /// <summary>No clusters (an install without the table).</summary>
    public static TreeClusters None { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    public int Count => _byName.Count;

    public static TreeClusters Parse(string csv)
    {
        var byName = new Dictionary<string, (string, (float, float, float)[])>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in csv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 5 || c[0].Length == 0 || c[1].Length == 0 || c[0].Equals("name", StringComparison.OrdinalIgnoreCase)
                || byName.ContainsKey(Bare(c[0])))
                continue;
            var offsets = new List<(float, float, float)>();
            for (int k = 2; k + 2 < c.Length; k += 3)
            {
                if (!float.TryParse(c[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(c[k + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    || !float.TryParse(c[k + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    break;
                if (x == 0 && y == 0 && z == 0)
                    continue;   // an unused slot
                offsets.Add((x, y, z));
            }
            if (offsets.Count > 0)
                byName[Bare(c[0])] = (Bare(c[1]), offsets.ToArray());
        }
        return new TreeClusters(byName);
    }

    /// <summary>Reads the table from the install's <c>zones/trees/tree_clusters.mpk</c>; <see cref="None"/> without it.</summary>
    public static TreeClusters Load(string installRoot)
    {
        string? zones = Directory.Exists(installRoot)
            ? Directory.EnumerateDirectories(installRoot).FirstOrDefault(d => Path.GetFileName(d).Equals("zones", StringComparison.OrdinalIgnoreCase))
            : null;
        string? trees = zones is null ? null
            : Directory.EnumerateDirectories(zones).FirstOrDefault(d => Path.GetFileName(d).Equals("trees", StringComparison.OrdinalIgnoreCase));
        string? mpk = trees is null ? null
            : Directory.EnumerateFiles(trees).FirstOrDefault(f => Path.GetFileName(f).Equals("tree_clusters.mpk", StringComparison.OrdinalIgnoreCase));
        if (mpk is null)
            return None;
        using Mpk.MpkArchive archive = Mpk.MpkArchive.Open(mpk);
        return archive.TryGetEntry("tree_clusters.csv", out Mpk.MpkEntry e)
            ? Parse(System.Text.Encoding.Latin1.GetString(archive.Extract(e)))
            : None;
    }

    public bool IsCluster(string nifName) => _byName.ContainsKey(Bare(nifName));

    /// <summary>The layout with each cluster placement replaced by its trees; other placements unchanged.</summary>
    public ZoneLayout Expand(ZoneLayout layout)
    {
        if (_byName.Count == 0)
            return layout;
        var fixtures = new List<ZoneFixture>(layout.Fixtures.Count);
        foreach (ZoneFixture f in layout.Fixtures)
        {
            if (!_byName.TryGetValue(Bare(f.NifName), out var cluster))
            {
                fixtures.Add(f);
                continue;
            }
            foreach ((float ox, float oy, float oz) in cluster.Offsets)
            {
                (float x, float y) = Place(f, ox, oy);
                fixtures.Add(f with
                {
                    NifName = cluster.Tree,
                    X = x,
                    Y = y,
                    Z = f.Z + f.Scale * oz,
                    // Each tree stands on the ground at its own spot: kept at the cluster's one height, trees
                    // on a slope floated up to 44 m or sank 6 m (tutorial zones, 2026-11-08).
                    OnGround = true,
                });
            }
        }
        return new ZoneLayout { Fixtures = fixtures };
    }

    /// <summary>
    /// The points where a layout's clusters (not yet expanded) darken the ground: one per tree, in the layout's
    /// coordinates (ZoneShadow.Stamp). A single tree that is no cluster leaves none, as in retail.
    /// </summary>
    public IEnumerable<(float X, float Y)> ShadowPoints(ZoneLayout layout)
    {
        foreach (ZoneFixture f in layout.Fixtures)
            if (_byName.TryGetValue(Bare(f.NifName), out var cluster))
                foreach ((float ox, float oy, float _) in cluster.Offsets)
                    yield return Place(f, ox, oy);
    }

    // Retail's turn of a cluster offset (see the header): θ = 180° − angle, as a heading.
    private static (float X, float Y) Place(ZoneFixture f, float ox, float oy)
    {
        double t = (180 - f.AngleDeg) * Math.PI / 180;
        float cos = (float)Math.Cos(t), sin = (float)Math.Sin(t);
        return (f.X + f.Scale * (cos * ox + sin * oy), f.Y + f.Scale * (cos * oy - sin * ox));
    }

    private static string Bare(string name) => name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
}
