// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk trees: the install's tree model names (zones/trees/treemap.csv) into the library, for the client's line of sight.

using DaocAssets.Mpk;
using DaocAssets.Zone;

internal static partial class Cli
{
    // mpk trees <client-dir> --out DIR: the install's tree model names (TreeMap) to DIR/trees.txt, one per line.
    private static int TreesCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not string outDir)
            return Usage();
        string archive = FindInstallFile(client, TreeMap.Archive) ?? throw new FileNotFoundException(TreeMap.Archive, client);
        IReadOnlySet<string> names;
        using (MpkArchive mpk = MpkArchive.Open(archive))
            names = TreeMap.Names(System.Text.Encoding.Latin1.GetString(mpk.Extract(TreeMap.Member)));
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, TreeMap.OutFile), names);
        Console.WriteLine($"trees: {names.Count} tree model names → {Path.Combine(outDir, TreeMap.OutFile)}");
        return 0;
    }
}
