// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk: list and extract members of a DAoC MPK/NPK client archive. Owner-run tooling — point it
// at your own client install. The extracted files are the client's content, not ours; they
// never belong in this repo or a release (see CLAUDE.md, "Game client and assets").

using DaocAssets;
using DaocAssets.Mpk;
using DaocAssets.Nif;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
            return Usage();

        string command = args[0];

        if (command == "textures")
            return Textures(args);

        string inputPath = args[1];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"No such file: {inputPath}");
            return 1;
        }

        if (command == "convert")
            return Convert(inputPath, args);

        try
        {
            using var mpk = MpkArchive.Open(inputPath);
            return command switch
            {
                "list" => List(mpk),
                "extract" => Extract(mpk, args),
                _ => Usage(),
            };
        }
        catch (MpkFormatException e)
        {
            Console.Error.WriteLine($"{inputPath}: {e.Message}");
            return 1;
        }
    }

    private static int Convert(string nifPath, string[] args)
    {
        // convert <model.nif> [--out FILE.glb]
        string outPath = Path.ChangeExtension(nifPath, ".glb");
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else
                return Usage();
        }

        try
        {
            NifModel model = NifFile.Read(File.ReadAllBytes(nifPath));
            GltfExporter.SaveGlb(model, outPath);
            int verts = model.Meshes.Sum(m => m.Vertices.Length);
            int tris = model.Meshes.Sum(m => m.Triangles.Length);
            Console.WriteLine($"Converted NIF {model.Version}: {model.Meshes.Count} mesh(es), " +
                $"{verts} verts, {tris} tris -> {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (NifFormatException e)
        {
            Console.Error.WriteLine($"{nifPath}: {e.Message}");
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

    private static int Textures(string[] args)
    {
        // textures <client-dir> <model.nif | dir-of-nifs> --out DIR
        if (args.Length < 3)
            return Usage();
        string clientDir = args[1];
        string modelArg = args[2];
        string? outDir = null;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outDir = args[++i];
            else
                return Usage();
        }
        if (outDir is null)
        {
            Console.Error.WriteLine("textures: --out DIR is required.");
            return 2;
        }

        string[] nifs =
            Directory.Exists(modelArg)
                ? Directory.GetFiles(modelArg, "*.nif", SearchOption.AllDirectories)
                : File.Exists(modelArg) ? [modelArg]
                : [];
        if (nifs.Length == 0)
        {
            Console.Error.WriteLine($"No .nif model found at: {modelArg}");
            return 1;
        }

        TextureResolver resolver;
        try
        {
            resolver = new TextureResolver(clientDir);
        }
        catch (DirectoryNotFoundException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        // Collect the unique texture names across all models first (one model's skin is often shared).
        var wanted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string nif in nifs)
        {
            try
            {
                foreach (string name in TextureResolver.TextureNames(nif))
                    wanted.Add(name);
            }
            catch (NifFormatException e)
            {
                Console.Error.WriteLine($"  (skipping {Path.GetFileName(nif)}: {e.Message})");
            }
        }

        if (wanted.Count == 0)
        {
            Console.WriteLine("No texture names referenced by the model(s); nothing to gather.");
            return 0;
        }

        int loose = 0, archived = 0, missing = 0;
        foreach (string name in wanted)
        {
            TextureResult r = resolver.Resolve(name, outDir);
            switch (r.Source)
            {
                case TextureSource.Loose: loose++; Console.WriteLine($"  loose    {r.Name}"); break;
                case TextureSource.Archive: archived++; Console.WriteLine($"  archive  {r.Name}  ({Path.GetFileName(r.FoundIn)})"); break;
                default: missing++; Console.WriteLine($"  MISSING  {r.Name}"); break;
            }
        }
        Console.WriteLine($"Gathered {loose + archived} of {wanted.Count} texture(s) " +
            $"({loose} loose, {archived} from archives, {missing} missing) -> {Path.GetFullPath(outDir)}");
        return missing == 0 ? 0 : 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              mpk list     <archive.mpk>
              mpk extract  <archive.mpk> [member] [--out DIR]
              mpk convert  <model.nif> [--out FILE.glb]
              mpk textures <client-dir> <model.nif | dir-of-nifs> --out DIR

            With no member, extract writes every member. Default --out is the current directory.
            convert writes a glTF binary next to the .nif unless --out is given.
            textures gathers the .dds a model references (loose on disk or packed in an .mpk) into
            one folder, for the Godot client to load. Owner-run: point it at your own install.
            """);
        return 2;
    }
}
