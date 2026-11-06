// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk list, extract, index: archive members and the install index.

internal static partial class Cli
{
    private static int List(MpkArchive mpk)
    {
        Console.WriteLine($"{mpk.Entries.Count} member(s):");
        foreach (MpkEntry e in mpk.Entries)
            Console.WriteLine($"  {e.UncompressedSize,10}  {e.Name}");
        return 0;
    }

    private static int Extract(MpkArchive mpk, string[] args)
    {
        // extract <archive> [member] [--out DIR]
        if (Options.Parse(args, 2, ["--out"], positional: true) is not { } o)
            return Usage();
        string? member = o.Positional.LastOrDefault();
        string outDir = o["--out"] ?? ".";

        Directory.CreateDirectory(outDir);
        IEnumerable<MpkEntry> targets = member is null
            ? mpk.Entries
            : mpk.TryGetEntry(member, out MpkEntry one) ? [one] : [];
        if (member is not null && !targets.Any())
        {
            Console.Error.WriteLine($"No member '{member}' in archive.");
            return 1;
        }

        int n = 0;
        foreach (MpkEntry e in targets)
        {
            // Member names have no path separators (the directory is flat), but guard anyway so a
            // crafted name can't write outside outDir.
            string safe = Path.GetFileName(e.Name);
            File.WriteAllBytes(Path.Combine(outDir, safe), mpk.Extract(e));
            n++;
        }
        Console.WriteLine($"Extracted {n} file(s) to {Path.GetFullPath(outDir)}.");
        return 0;
    }

    private static int IndexCmd(string[] args)
    {
        // index <client-dir> --out index.json
        if (args.Length < 2)
            return Usage();
        string clientDir = args[1];
        if (Options.Parse(args, 2, ["--out"]) is not { } o)
            return Usage();
        if (o["--out"] is not { } outPath)
        {
            Console.Error.WriteLine("index: --out index.json is required.");
            return 2;
        }

        try
        {
            int last = 0;
            AssetIndex index = AssetIndex.Build(clientDir, n =>
            {
                if (n - last >= 200) { Console.Error.Write($"\r  indexed {n} archives…"); last = n; }
            });
            Console.Error.WriteLine();
            index.Save(outPath);
            (int loose, int archived) = index.Breakdown();
            Console.WriteLine($"Indexed {index.Count} assets ({loose} loose, {archived} in archives) " +
                $"-> {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (DirectoryNotFoundException e)
        {
            Console.Error.WriteLine($"index: {e.Message}");
            return 1;
        }
    }
}
