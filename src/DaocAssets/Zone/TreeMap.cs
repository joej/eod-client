// SPDX-License-Identifier: GPL-3.0-or-later
//
// zones/trees/treemap.csv: the zone models that are trees. Each row maps a classic NIF name to its SpeedTree replacement
// (open question W5): "NIF Name,Replacement Tree,Bark Texture,Leaf Texture,zoffset,scale,…,Expansion". The client uses
// the names alone here: retail never tests a tree in its line-of-sight ray, whatever the fixture's Collide value (daoc-re
// RE/93, both tree modes), while a solid tree still stops the player walking into it.

namespace DaocAssets.Zone;

public static class TreeMap
{
    /// <summary>Where the table is in an install, and the file the client reads (one model name per line).</summary>
    public const string Archive = "zones/trees/treemap.mpk", Member = "treemap.csv", OutFile = "trees.txt";

    /// <summary>The tree model names, lower case and without ".nif" (as zone.csv names its models, less ".glb").</summary>
    public static IReadOnlySet<string> Names(string csv)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in csv.Split('\n'))
        {
            string first = line.Split(',')[0].Trim();
            if (first.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && first.Length > 4)
                names.Add(first[..^4].ToLowerInvariant());
        }
        return names;
    }
}
