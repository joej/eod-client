// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk nifinfo, convert, textures, model: one model at a time.

internal static partial class Cli
{
    // nifinfo <model.nif>: what the converter reads from one model: meshes (shape name, texture,
    // skinned or not, helper), the skeleton, and for skinned 10.1 files the bind-pose check.
    private static int NifInfo(string nifPath)
    {
        byte[] bytes = File.ReadAllBytes(nifPath);
        try
        {
            NifModel model = NifFile.Read(bytes);
            Console.WriteLine($"NIF {model.Version}: {model.Meshes.Count} mesh(es), {model.Joints.Count} joint(s)");
            foreach (NifMesh m in model.Meshes)
                Console.WriteLine($"  {m.ShapeName,-28} {m}" + (m.SkinJoints is { } sj ? $" ({sj.Length} bones)" : ""));
            for (int j = 0; j < model.Joints.Count; j++)
            {
                NifJoint joint = model.Joints[j];
                int depth = 0;
                for (int p = joint.Parent; p >= 0; p = model.Joints[p].Parent) depth++;
                Console.WriteLine($"  joint {j,3} {new string(' ', depth * 2)}{joint.Name}");
            }
            // Per skinned mesh: where the skin puts its vertices in the file's rest pose, against the
            // stored vertices. A part whose skinned box is far from its stored box binds wrongly.
            foreach (NifMesh m in model.Meshes.Where(m => m.SkinJoints is not null))
            {
                System.Numerics.Vector3[] skinned = model.Posed(m);
                string Box(System.Numerics.Vector3[] p) =>
                    $"({p.Min(q => q.X):F0},{p.Min(q => q.Y):F0},{p.Min(q => q.Z):F0})..({p.Max(q => q.X):F0},{p.Max(q => q.Y):F0},{p.Max(q => q.Z):F0})";
                Console.WriteLine($"  skin {m.ShapeName,-14} stored {Box(m.Vertices)} skinned {Box(skinned)}" +
                    $" unweighted {m.Skin!.Count(x => x.Length == 0)}");
            }
            if (model.Joints.Count == 0)
                foreach ((string name, System.Numerics.Matrix4x4 w) in model.NodeWorlds)
                    Console.WriteLine($"  node {name,-28} at ({w.M41:F1},{w.M42:F1},{w.M43:F1})");
            if (Environment.GetEnvironmentVariable("NIFINFO_SKINS") == "1")
                foreach (string line in NifFile.SkinReport(bytes))
                    Console.WriteLine("  " + line);
            if (NifFile.BindPoseError(bytes) is { } err)
                Console.WriteLine($"Bind-pose check: max difference {err:G4} (near 0 = skeleton in bind pose)");
            return 0;
        }
        catch (NifFormatException e)
        {
            Console.Error.WriteLine($"{nifPath}: {e.Message}");
            return 1;
        }
    }

    private static int Convert(string nifPath, string[] args)
    {
        // convert <model.nif> [--out FILE.glb]
        if (Options.Parse(args, 2, ["--out"]) is not { } o)
            return Usage();
        string outPath = o["--out"] ?? Path.ChangeExtension(nifPath, ".glb");

        try
        {
            NifModel model = NifFile.Read(File.ReadAllBytes(nifPath));
            GltfExporter.SaveGlb(model, outPath);
            int verts = model.Meshes.Sum(m => m.Vertices.Length);
            int tris = model.Meshes.Sum(m => m.Triangles.Length);
            Console.WriteLine($"Converted NIF {model.Version}: {model.Meshes.Count} mesh(es), " +
                $"{verts} verts, {tris} tris -> {Path.GetFullPath(outPath)}");
            WarnDropped(File.ReadAllBytes(nifPath));
            return 0;
        }
        catch (NifFormatException e)
        {
            Console.Error.WriteLine($"{nifPath}: {e.Message}");
            return 1;
        }
    }

    // Says which of the file's block types the converter skips or only partly uses, so a gap shows
    // up at conversion time instead of as a wrong-looking model in the world.
    private static void WarnDropped(byte[] nif)
    {
        NifSurvey s = NifSurvey.Read(nif);
        foreach (var g in s.BlockTypes.GroupBy(t => t).OrderBy(g => g.Key))
        {
            NifCoverage c = NifSurvey.Coverage(g.Key, s.Version);
            if (c == NifCoverage.Handled)
                continue;
            string note = NifSurvey.CoverageNote(g.Key, s.Version);
            Console.WriteLine($"  {(c == NifCoverage.Unhandled ? "skipped" : "partial")} {g.Key} x{g.Count()}" +
                (note.Length > 0 ? $": {note}" : ""));
        }
    }

    private static int Textures(string[] args)
    {
        // textures <client-dir> <model.nif | dir-of-nifs> --out DIR
        if (args.Length < 3)
            return Usage();
        string clientDir = args[1];
        string modelArg = args[2];
        if (Options.Parse(args, 3, ["--out"]) is not { } o)
            return Usage();
        if (o["--out"] is not { } outDir)
        {
            Console.Error.WriteLine("textures: --out DIR is required.");
            return 2;
        }

        string[] nifs =
            Directory.Exists(modelArg)
                ? Directory.GetFiles(modelArg, "*.nif", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
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

    // model <name.nif> --index index.json --library LIBDIR: converts one model (and its textures)
    // into the library, always afresh. One step, one result, for the Makefile: prints one status
    // line; exit 0 converted / override / unsupported (a NIF form the reader doesn't take: recorded,
    // not an error), 1 failed (a converter bug), 3 not in the install.
    private static int ModelCmd(string[] args)
    {
        string name = args[1];
        if (Options.Parse(args, 2, ["--index", "--library"]) is not { } o
            || o["--index"] is not { } indexPath || o["--library"] is not { } libDir)
            return Usage();
        var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir);
        ModelResult r = library.GetModel(name, reconvert: true);
        switch (r.Origin)
        {
            case AssetOrigin.Converted:
            case AssetOrigin.Override:
                Console.WriteLine($"{r.Origin.ToString().ToLowerInvariant()} {Path.GetFileName(r.GlbPath)}");
                return 0;
            case AssetOrigin.Unsupported:
                Console.WriteLine($"unsupported {r.Error}");
                return 0;
            case AssetOrigin.Missing:
                Console.Error.WriteLine($"model: {name} is not in the install index");
                return 3;
            default:
                Console.Error.WriteLine($"model: FAILED {r.Error}");
                return 1;
        }
    }
}
