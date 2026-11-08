// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk maps: the client's map pictures and layout (ui/maps) into the library, for the map window.

internal static partial class Cli
{
    // maps <client-dir> --out DIR: every file of ui/maps (z<NNN>.dds zone maps, r<NNN>.dds region maps, their
    // area insets, regions.dat and areas.dat) copied as is, names lower-cased; regions.dat is checked by parsing it.
    private static int MapsCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not string outDir)
            return Usage();
        if (FindInstallFile(client, "ui/maps/regions.dat") is not string regions)
        {
            Console.Error.WriteLine("maps: no ui/maps/regions.dat in the install");
            return 2;
        }
        Directory.CreateDirectory(outDir);
        string source = Path.GetDirectoryName(regions)!;
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(outDir, Path.GetFileName(file).ToLowerInvariant()), overwrite: true);
            copied++;
        }
        var layout = DaocAssets.Zone.MapLayout.Parse(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(regions)));
        Console.WriteLine($"maps: {copied} files; {layout.Count} region maps placing {layout.Sum(m => m.Zones.Count)} zones");
        return 0;
    }
}
