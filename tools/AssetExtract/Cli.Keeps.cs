// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk keeps: the keep packets a server would send for a region, from keep tables (DaocProtocol.World.KeepReplay), for
// the client to replay with --keeps. Not install data: the tables are a server's (Eve of Darkness db-public, MIT).

using DaocProtocol.World;

internal static partial class Cli
{
    // keeps <Keep.json> <KeepComponent.json> --region N [--look old|new] [--level L] [--health H] [--raised yes] --out FILE
    private static int KeepsCmd(string[] args)
    {
        if (Options.Parse(args, 1, ["--region", "--look", "--level", "--health", "--raised", "--out"], positional: true) is not { } o
            || o.Positional.Count != 2 || o["--region"] is not string regionText || !int.TryParse(regionText, out int region)
            || o["--out"] is not string outFile)
            return Usage();
        bool newKeeps = (o["--look"] ?? "new") switch { "new" => true, "old" => false, _ => throw new ArgumentException("--look old|new") };
        int? level = o["--level"] is string l ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : null;

        List<KeepRow> keeps = KeepReplay.ReadKeeps(File.ReadAllText(o.Positional[0]));
        List<KeepComponentRow> parts = KeepReplay.ReadComponents(File.ReadAllText(o.Positional[1]));
        byte health = o["--health"] is string h ? byte.Parse(h, System.Globalization.CultureInfo.InvariantCulture) : (byte)100;
        bool raised = o["--raised"] is "yes" or "1" or "true";
        var packets = KeepReplay.Build(keeps, parts, region, newKeeps, level, health, raised);
        int count = keeps.Count(k => k.Region == region);
        string header = $"Keep packets for region {region}: {count} keeps, {packets.Count - count} components ({(newKeeps ? "new" : "old")} look"
            + (level is null ? "" : $", every keep at level {level}") + (health == 100 ? "" : $", every piece at {health} % health")
            + (raised ? ", towers raised" : "") + $"),\nas OpenDAoC would send them, from {Path.GetFileName(o.Positional[0])} and "
            + $"{Path.GetFileName(o.Positional[1])}. Made by mpk keeps.";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, KeepReplay.ToText(packets, header));
        Console.WriteLine($"Keeps: region {region}, {count} keeps, {packets.Count - count} components -> {outFile}");
        return 0;
    }
}

internal static partial class Cli
{
    // Keep pieces are looked up in the frontiers' own folder first (frontiers/NIFS), then the default order.
    internal static readonly string[] KeepFolders = ["frontiers/"];

    // keepmodels <client-dir> --index index.json --library LIBDIR: every keep piece model (fr_tiers × fr_pieces) into the
    // library, and LIBDIR/keeps/pieces.csv: skin,name,tier,glb for each one that converted.
    private static int KeepModelsCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--index", "--library"]) is not { } o || o["--index"] is not string indexPath
            || o["--library"] is not string libDir)
            return Usage();
        string frontiers = FindInstallFile(client, "frontiers/frontiers.mpk") ?? throw new FileNotFoundException("frontiers.mpk", client);
        Dictionary<int, DaocAssets.Keeps.KeepPieceType> types;
        DaocAssets.Keeps.KeepMaterials materials;
        using (MpkArchive mpk = MpkArchive.Open(frontiers))
        {
            string Text(string name) => System.Text.Encoding.Latin1.GetString(mpk.Extract(name));
            types = DaocAssets.Keeps.KeepPieces.Parse(Text("fr_tiers.csv"), Text("fr_pieces.csv"));
            materials = DaocAssets.Keeps.KeepMaterials.Parse(Text("fr_materials.csv"), Text("fr_textures.csv"));
        }
        var library = new AssetLibrary(AssetIndex.Load(indexPath), libDir).Scoped("keeps", KeepFolders);
        string modelsDir = Path.Combine(libDir, "models");
        var stems = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int absent = 0, failed = 0;
        foreach (string file in types.Values.SelectMany(t => t.Tiers).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ModelResult r = library.GetModel(file + ".nif");
            stems[file] = r.GlbPath is null ? null : ModelStem(modelsDir, r.GlbPath);
            if (r.Origin == AssetOrigin.Missing) { absent++; Console.Error.WriteLine($"  {file}: not in the install"); }
            else if (r.GlbPath is null) { failed++; Console.Error.WriteLine($"  {file}: {r.Error}"); }
        }
        var table = new System.Text.StringBuilder("skin,name,tier,glb\n");
        int rows = 0;
        foreach (DaocAssets.Keeps.KeepPieceType t in types.Values.OrderBy(t => t.Skin))
            for (int i = 0; i < t.Tiers.Count; i++)
                if (t.Tiers[i] is string f && stems[f] is string stem)
                {
                    table.Append($"{t.Skin},{t.Name},{i + 1},{stem}\n");
                    rows++;
                }
        string outDir = Path.Combine(libDir, "keeps");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "pieces.csv"), table.ToString());

        // The keep textures: every fr_textures file (frontiers/nifs/<file>.dds) into textures/keeps/, and the material table.
        string texDir = Path.Combine(libDir, "textures", "keeps");
        Directory.CreateDirectory(texDir);
        int copied = 0, missing = 0;
        foreach (string file in materials.Files)
        {
            if (FindInstallFile(client, $"frontiers/nifs/{file}.dds") is string src)
            {
                File.Copy(src, Path.Combine(texDir, file.ToLowerInvariant() + ".dds"), overwrite: true);   // the library's names are lower case
                copied++;
            }
            else
            {
                missing++;
                Console.Error.WriteLine($"  texture {file}.dds: not in frontiers/nifs");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "materials.csv"), materials.ToCsv());
        // The damage overlays by damage state (fr_textures' Damage column: 1 broken … 4 lightly damaged).
        File.WriteAllText(Path.Combine(outDir, "damage.csv"), "state,texture\n" + string.Concat(materials.DamageOverlays
            .OrderBy(d => d.Key).Select(d => $"{d.Key},{d.Value}\n")));
        Console.WriteLine($"keepmodels: {materials.Materials.Count} materials ({materials.GroupWidth} textures a group in this install's table), " +
            $"{copied} textures copied, {missing} missing");
        Console.WriteLine($"keepmodels: {types.Count} component types, {rows} type/tier models ({stems.Count} files: {absent} not in the install, {failed} failed)");
        return 0;
    }
}
