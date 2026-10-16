// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk: list and extract members of a DAoC MPK/NPK client archive. Owner-run tooling — point it
// at your own client install. The extracted files are the client's content, not ours; they
// never belong in this repo or a release (see CLAUDE.md, "Game client and assets").

using DaocAssets.Mpk;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
            return Usage();

        string command = args[0];
        string archivePath = args[1];
        if (!File.Exists(archivePath))
        {
            Console.Error.WriteLine($"No such file: {archivePath}");
            return 1;
        }

        try
        {
            using var mpk = MpkArchive.Open(archivePath);
            return command switch
            {
                "list" => List(mpk),
                "extract" => Extract(mpk, args),
                _ => Usage(),
            };
        }
        catch (MpkFormatException e)
        {
            Console.Error.WriteLine($"{archivePath}: {e.Message}");
            return 1;
        }
    }

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
        string? member = null;
        string outDir = ".";
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outDir = args[++i];
            else if (!args[i].StartsWith('-'))
                member = args[i];
            else
                return Usage();
        }

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

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              mpk list    <archive.mpk>
              mpk extract <archive.mpk> [member] [--out DIR]

            With no member, extract writes every member. Default --out is the current directory.
            """);
        return 2;
    }
}
